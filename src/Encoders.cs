using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace StillsAndReels
{
    /// <summary>
    /// The formats the editor can export and the FFmpeg options for each: software encoders (always there, best
    /// quality for the size) and the graphics card's own encoders (NVIDIA, Intel, AMD: several times faster,
    /// somewhat bigger files). Which hardware encoders really work on this PC is found by trying each one on a
    /// few black frames; the answer is kept for the rest of the session.
    /// </summary>
    internal static class Encoders
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static bool IsAudio(string format) { return format == "mp3" || format == "m4a" || format == "wav"; }
        public static bool IsVideo(string format) { return format == "mp4" || format == "hevc" || format == "webm"; }

        public static string Extension(string format)
        {
            switch (format)
            {
                case "hevc": return ".mp4";
                case "webm": return ".webm";
                case "gif": return ".gif";
                case "mp3": return ".mp3";
                case "m4a": return ".m4a";
                case "wav": return ".wav";
                default: return ".mp4";
            }
        }

        public static string Describe(string format)
        {
            switch (format)
            {
                case "hevc": return "HEVC (H.265) with AAC sound";
                case "webm": return "VP9 with Opus sound";
                case "gif": return "animated GIF, no sound";
                case "mp3": return "MP3";
                case "m4a": return "AAC (M4A)";
                case "wav": return "uncompressed WAV";
                default: return "H.264 with AAC sound";
            }
        }

        /// <summary>
        /// Output options for pictures arriving as RGB. crf is on x264's scale (14 best … 28 small) and is translated
        /// for the other encoders. hardware: null for the software encoder, or "nvenc", "qsv", "amf".
        /// </summary>
        public static string VideoArgs(string format, string hardware, int crf, string preset)
        {
            string filter = VideoQuality.FromRgbFilter;
            string tags = VideoQuality.ColourTags;
            bool hevc = format == "hevc";
            if (format == "webm")
                return string.Format(Inv, "-vf \"{0}\" -c:v libvpx-vp9 -crf {1} -b:v 0 -row-mt 1 -deadline good -cpu-used 3 -pix_fmt yuv420p ", filter, (int)Math.Round(crf * 1.43)) + tags;
            switch (hardware)
            {
                case "nvenc":
                    return string.Format(Inv, "-vf \"{0}\" -c:v {1} -preset p5 -tune hq -rc vbr -cq {2} -b:v 0 -pix_fmt yuv420p {3}",
                        filter, hevc ? "hevc_nvenc" : "h264_nvenc", crf + (hevc ? 5 : 3), hevc ? "-tag:v hvc1 " : "-profile:v high ") + tags;
                case "qsv":
                    return string.Format(Inv, "-vf \"{0}\" -c:v {1} -preset medium -global_quality {2} -pix_fmt nv12 {3}",
                        filter.Replace("format=yuv420p", "format=nv12"), hevc ? "hevc_qsv" : "h264_qsv", crf + (hevc ? 5 : 3), hevc ? "-tag:v hvc1 " : "") + tags;
                case "amf":
                    // AMD's HEVC encoder has no B-frames, so no quantiser for them
                    return string.Format(Inv, "-vf \"{0}\" -c:v {1} -quality quality -rc cqp -qp_i {2} -qp_p {3} {4}-pix_fmt yuv420p {5}",
                        filter, hevc ? "hevc_amf" : "h264_amf", crf + 2, crf + 4, hevc ? "" : "-qp_b " + (crf + 6) + " ", hevc ? "-tag:v hvc1 " : "") + tags;
            }
            if (hevc)
                return string.Format(Inv, "-vf \"{0}\" -c:v libx265 -preset {1} -crf {2} -pix_fmt yuv420p -tag:v hvc1 -x265-params log-level=error ", filter, preset, crf + 4) + tags;
            return VideoQuality.H264FromRgb(crf, preset);
        }

        public static string AudioArgs(string format, int kbps)
        {
            if (format == "webm") return string.Format(Inv, "-c:a libopus -b:a {0}k ", Math.Min(256, kbps));
            return string.Format(Inv, "-c:a aac -b:a {0}k ", kbps);
        }

        public static string HardwareName(string hardware)
        {
            return hardware == "nvenc" ? "NVIDIA" : hardware == "qsv" ? "Intel" : hardware == "amf" ? "AMD" : "";
        }

        /// <summary>Rough size of a finished file in megabytes.</summary>
        public static double EstimateMb(string format, bool hardware, int w, int h, double fps, int crf, int audioKbps, double seconds)
        {
            if (format == "wav") return 48000 * 4 * seconds / 1048576;
            if (IsAudio(format)) return audioKbps * 1000.0 * seconds / 8 / 1048576;
            if (format == "gif") return w * (double)h * fps * seconds * 0.22 / 1048576;   // LZW on dithered video: about a fifth of a byte per pixel
            // bits per pixel per frame for H.264 by quality, measured on ordinary footage
            double bpp = crf <= 14 ? 0.22 : crf <= 18 ? 0.13 : crf <= 23 ? 0.065 : 0.035;
            if (format == "hevc") bpp *= 0.62;
            if (format == "webm") bpp *= 0.68;
            if (hardware) bpp *= 1.35;
            return (w * (double)h * fps * bpp + audioKbps * 1000.0) * seconds / 8 / 1048576;
        }

        // ------------------------------------------------------------------ what this PC's graphics card can do

        static readonly object gate = new object();      // guards the answers
        static readonly object probing = new object();   // one probe at a time; never held while the window asks for an answer
        static readonly Dictionary<string, string> found = new Dictionary<string, string>();

        /// <summary>The detected hardware encoder for a format if Detect has already run for it; null when none or not yet known.</summary>
        public static string Known(string format, out bool tested)
        {
            lock (gate) { string hw; tested = found.TryGetValue(format, out hw); return tested ? hw : null; }
        }

        /// <summary>
        /// Finds a hardware encoder that really works for the format ("mp4" or "hevc") by encoding a few frames with
        /// each. Takes up to a few seconds the first time; call it off the UI thread. Returns null when there is none.
        /// </summary>
        public static string Detect(string format)
        {
            if (format != "mp4" && format != "hevc") return null;
            lock (probing)
            {
                string hw;
                lock (gate) if (found.TryGetValue(format, out hw)) return hw;
                hw = null;
                if (FFmpeg.IsWindows)
                    foreach (var candidate in new[] { "nvenc", "qsv", "amf" })
                        if (Works(format, candidate)) { hw = candidate; break; }
                lock (gate) found[format] = hw;
                return hw;
            }
        }

        static bool Works(string format, string hardware)
        {
            // the same options an export uses, on a size every encoder accepts
            string args = "-hide_banner -nostdin -loglevel error -f lavfi -i color=c=black:s=1280x720:r=30 -frames:v 8 " +
                          VideoArgs(format, hardware, 20, "medium") + "-f null -";
            Process p = null;
            try
            {
                p = FFmpeg.Start(args);
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (!p.WaitForExit(12000)) return false;
                return p.ExitCode == 0;
            }
            catch { return false; }
            finally { FFmpeg.KillQuietly(p); }
        }
    }
}
