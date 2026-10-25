using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;

namespace StillsAndReels
{
    /// <summary>
    /// Captures what the PC is playing (the default speakers or headphones) with WASAPI loopback and
    /// streams it to FFmpeg through a local named pipe. The bundled FFmpeg can't capture PC sound itself.
    /// Silence is filled in by the clock, because Windows sends nothing while nothing plays, and the
    /// sound would otherwise drift ahead of the picture.
    /// </summary>
    /// <summary>Fetches the next waiting packet of sound into buf (growing it), returning its frame count, or 0 if none.</summary>
    internal delegate int SoundPacketSource(ref byte[] buf);

    /// <summary>
    /// Streams loopback packets to FFmpeg in real time. Windows sends nothing while nothing plays, so silence
    /// is added by the clock; FFmpeg times raw sound by its sample count, so a gap would pull later sound early.
    /// </summary>
    internal static class SoundPump
    {
        /// <summary>
        /// Silence added at the start of the sound, in seconds, i.e. how much later the sound is placed. The
        /// screen input is opened before the sound pipe, so the picture's clock starts about one frame before the
        /// sound's; without this the sound led the picture by 35–50 ms in tests/AvSyncTest.cs.
        /// </summary>
        public const double StartOffset = 0.04;

        /// <summary>
        /// The picture's lead over the sound in a normal start (sound pipe connecting after the "Input #0" line), on top
        /// of what StartOffset and the pre-roll already cover; measured as 0 in tests/AvSyncTest.cs, so any lead counts.
        /// A longer lead (a slow microphone or pipe open on some PCs) is corrected live by StartGapMeter.
        /// </summary>
        public const double BuiltInLead = 0.0;

        /// <summary>
        /// extraSilence: called each loop; silence of that many seconds is inserted (once) when it returns more than
        /// 0, which moves all later sound that much later. Used to correct a measured start gap.
        /// </summary>
        public static void Run(Stream output, int rate, int blockAlign, SoundPacketSource next, Func<bool> stopping, Func<double> extraSilence)
        {
            var buf = new byte[0];
            // one raw block (1024 samples) plus the probe, so FFmpeg's input opens at once and the picture isn't held up
            long written = 1024 + Math.Max(1, 32 / blockAlign + 1);
            output.Write(new byte[written * blockAlign], 0, (int)written * blockAlign);
            output.Flush();
            var clock = Stopwatch.StartNew();
            double lastPacket = -1;
            double shifted = 0; // total silence inserted for start-gap correction
            while (!stopping())
            {
                Thread.Sleep(5);
                double extra = extraSilence != null ? extraSilence() : 0;
                if (extra > 0.001)
                {
                    long n = (long)(extra * rate);
                    int bytes = (int)n * blockAlign;
                    if (buf.Length < bytes) buf = new byte[bytes];
                    Array.Clear(buf, 0, bytes);
                    output.Write(buf, 0, bytes);
                    written += n;
                    shifted += extra;
                }
                int frames;
                while (!stopping() && (frames = next(ref buf)) > 0)
                {
                    output.Write(buf, 0, frames * blockAlign);
                    written += frames;
                    lastPacket = clock.Elapsed.TotalSeconds;
                }
                // only while no sound is arriving (none for 60 ms), so playing sound is never interrupted
                double now = clock.Elapsed.TotalSeconds + StartOffset + shifted;
                long due = (long)(now * rate);
                if (now - lastPacket > 0.06 && due - written > rate / 50)
                {
                    long fill = due - written;
                    int bytes = (int)fill * blockAlign;
                    if (buf.Length < bytes) buf = new byte[bytes];
                    Array.Clear(buf, 0, bytes);
                    output.Write(buf, 0, bytes);
                    written += fill;
                }
                output.Flush();
            }
            // Stopping: hand over any sound still waiting, then silence up to this moment and a little beyond,
            // so the sound track lasts as long as the picture (FFmpeg gets "q" right after this returns).
            int more;
            while ((more = next(ref buf)) > 0) { output.Write(buf, 0, more * blockAlign); written += more; }
            long end = (long)((clock.Elapsed.TotalSeconds + StartOffset + shifted + 0.08) * rate);
            if (end > written)
            {
                int bytes = (int)(end - written) * blockAlign;
                if (buf.Length < bytes) buf = new byte[bytes];
                Array.Clear(buf, 0, bytes);
                output.Write(buf, 0, bytes);
            }
            output.Flush();
        }
    }

    /// <summary>
    /// Measures how far the picture's clock starts ahead of the sound's. FFmpeg opens the screen input first, grabs
    /// its first frame while probing and prints "Input #0, …" straight after; the sound's clock starts when FFmpeg
    /// connects to the sound pipe, after the inputs in between (a microphone) have opened. Both moments are timed on
    /// one clock; the difference beyond the normal lead is inserted as silence once, so the sound no longer runs
    /// ahead and ends with the picture. (FFmpeg's own progress line can't show this: it follows the lagging stream.)
    /// </summary>
    internal sealed class StartGapMeter
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        double videoAt = double.NaN, connectAt = double.NaN, pending;
        /// <summary>Picture lead in seconds (sound pipe connected this long after the first frame); NaN until known.</summary>
        public double Lead = double.NaN;
        /// <summary>Silence inserted to correct the lead, in seconds (0 when the lead was normal).</summary>
        public double Inserted;

        /// <summary>Feed every FFmpeg stderr line.</summary>
        public void OnFfmpegLine(string line)
        {
            if (line == null || !double.IsNaN(videoAt) || !line.StartsWith("Input #0,", StringComparison.Ordinal)) return;
            videoAt = clock.Elapsed.TotalSeconds; Evaluate();
        }
        /// <summary>Call when FFmpeg connects to the sound pipe.</summary>
        public void OnConnected() { connectAt = clock.Elapsed.TotalSeconds; Evaluate(); }

        void Evaluate()
        {
            if (double.IsNaN(videoAt) || double.IsNaN(connectAt) || !double.IsNaN(Lead)) return;
            Lead = connectAt - videoAt;
            double extra = Lead - SoundPump.BuiltInLead;
            if (extra > 0.06) lock (this) { pending = extra; Inserted = extra; }
        }
        /// <summary>For SoundPump.Run's extraSilence: the correction, once.</summary>
        public double Take() { lock (this) { double v = pending; pending = 0; return v; } }
    }

    internal sealed class PcSoundCapture : IDisposable
    {
        public string PipePath { get; private set; }
        /// <summary>FFmpeg input options describing the raw sound in the pipe (go before -i PipePath).</summary>
        public string InputFormatArgs { get; private set; }
        public event Action<string> Failed; // capture thread
        readonly Stopwatch sinceConnect = new Stopwatch();
        /// <summary>Times the picture's lead over the sound; feed it FFmpeg's stderr lines through OnFfmpegLine.</summary>
        public readonly StartGapMeter Gap = new StartGapMeter();

        /// <summary>Seconds since FFmpeg opened the sound pipe (the sound's clock start); 0 before that.</summary>
        public double SecondsSinceConnect { get { return sinceConnect.IsRunning ? sinceConnect.Elapsed.TotalSeconds : 0; } }

        /// <summary>Feed FFmpeg's stderr lines here (see StartGapMeter).</summary>
        public void OnFfmpegLine(string line) { Gap.OnFfmpegLine(line); }

        readonly NamedPipeServerStream pipe;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        Thread thread;
        string openError;
        volatile bool stopping;
        int rate, blockAlign;

        public PcSoundCapture()
        {
            string name = "StillsAndReels-pcsound-" + Guid.NewGuid().ToString("N");
            PipePath = @"\\.\pipe\" + name;
            pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        }

        /// <summary>Prepares the speakers' loopback stream and learns its format. Throws if there is no sound device.</summary>
        public void Open()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "PC sound" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            if (!ready.WaitOne(5000)) openError = openError ?? "The sound device didn't respond.";
            if (openError != null) { Dispose(); throw new IOException("PC sound can't be recorded: " + openError); }
        }

        void Run()
        {
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice device;
                Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device), "no speakers or headphones were found");
                object o;
                var iidClient = typeof(IAudioClient).GUID;
                Check(device.Activate(ref iidClient, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o), "the sound device couldn't be opened");
                client = (IAudioClient)o;
                IntPtr fmt;
                Check(client.GetMixFormat(out fmt), "the sound format couldn't be read");
                try
                {
                    InputFormatArgs = DescribeFormat(fmt);
                    Check(client.Initialize(0 /* shared */, 0x00020000 /* LOOPBACK */, 10000000 /* 1 s buffer */, 0, fmt, IntPtr.Zero), "loopback recording isn't available");
                }
                finally { Marshal.FreeCoTaskMem(fmt); }
                var iidCapture = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref iidCapture, out o), "loopback recording isn't available");
                capture = (IAudioCaptureClient)o;
            }
            catch (Exception ex)
            {
                openError = ex.Message;
                ready.Set();
                Release(capture); Release(client);
                return;
            }
            ready.Set();

            try
            {
                // FFmpeg opens the pipe once the other inputs are open, i.e. just as the picture starts
                var wait = pipe.BeginWaitForConnection(null, null);
                while (!wait.AsyncWaitHandle.WaitOne(50)) if (stopping) return;
                pipe.EndWaitForConnection(wait);
                if (stopping) return;
                sinceConnect.Start();
                Gap.OnConnected();
                Check(client.Start(), "the sound device couldn't start");
                var cap = capture;
                SoundPump.Run(pipe, rate, blockAlign, (ref byte[] buf) =>
                {
                    uint packet;
                    if (cap.GetNextPacketSize(out packet) != 0 || packet == 0) return 0;
                    IntPtr data; uint frames, flags; ulong devPos, qpc;
                    if (cap.GetBuffer(out data, out frames, out flags, out devPos, out qpc) != 0) return 0;
                    int bytes = (int)frames * blockAlign;
                    if (buf.Length < bytes) buf = new byte[bytes];
                    if ((flags & 0x2) != 0) Array.Clear(buf, 0, bytes); // AUDCLNT_BUFFERFLAGS_SILENT
                    else Marshal.Copy(data, buf, 0, bytes);
                    cap.ReleaseBuffer(frames);
                    return (int)frames;
                }, () => stopping, Gap.Take);
                try { pipe.Flush(); } catch { }
            }
            catch (Exception ex)
            {
                if (!stopping) { var h = Failed; if (h != null) h("PC sound stopped: " + ex.Message); }
            }
            finally
            {
                try { if (client != null) client.Stop(); } catch { }
                Release(capture); Release(client);
                try { pipe.Dispose(); } catch { }
            }
        }

        string DescribeFormat(IntPtr fmt)
        {
            int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
            int channels = Marshal.ReadInt16(fmt, 2);
            rate = Marshal.ReadInt32(fmt, 4);
            blockAlign = Marshal.ReadInt16(fmt, 12);
            int bits = Marshal.ReadInt16(fmt, 14);
            bool isFloat = tag == 3;
            if (tag == 0xFFFE) isFloat = Marshal.ReadInt32(fmt, 24) == 3; // SubFormat GUID starts with the format tag
            else if (tag != 1 && tag != 3) throw new IOException("the sound format isn't supported");
            string f = isFloat ? (bits == 64 ? "f64le" : "f32le") : bits == 16 ? "s16le" : bits == 24 ? "s24le" : bits == 32 ? "s32le" : null;
            if (f == null || channels < 1 || rate < 8000 || blockAlign < 1) throw new IOException("the sound format isn't supported");
            return string.Format(CultureInfo.InvariantCulture, "-f {0} -ar {1} -ac {2} ", f, rate, channels);
        }

        static void Check(int hr, string what) { if (hr < 0) throw new IOException(what + " (0x" + hr.ToString("X8") + ")"); }

        static void Release(object o) { try { if (o != null) Marshal.ReleaseComObject(o); } catch { } }

        /// <summary>Ends the sound (FFmpeg sees the end of the pipe) and frees the device.</summary>
        public void Stop()
        {
            stopping = true;
            if (thread != null && thread.IsAlive) thread.Join(3000);
        }

        public void Dispose()
        {
            Stop();
            try { pipe.Dispose(); } catch { }
        }

        // ------------------------------------------------------------------ Core Audio COM

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
