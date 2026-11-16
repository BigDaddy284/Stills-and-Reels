using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace StillsAndReels
{
    /// <summary>How a copy of a picture is written.</summary>
    internal sealed class SaveOptions
    {
        /// <summary>JPEG and WebP quality, 1 to 100.</summary>
        public int Quality = 92;
        /// <summary>WebP only: keep every pixel exact (bigger file).</summary>
        public bool Lossless;
        /// <summary>
        /// JPEG only: the original JPEG file whose photo details (date taken, camera, place) are carried over.
        /// Null leaves them out, which is the default.
        /// </summary>
        public string DetailsFrom;
    }

    /// <summary>
    /// Picture formats that Windows' own codecs don't read or write (WebP, HEIC, AVIF, TGA, PSD and more), handled by
    /// the bundled FFmpeg. Only local files are ever touched.
    /// </summary>
    internal static unsafe class PicCodecs
    {
        /// <summary>Reads a picture with FFmpeg and returns its pixels. Throws with a readable message if it can't.</summary>
        public static PicSurface Decode(string file)
        {
            // the first picture in the file, as a 32-bit bitmap on standard output
            string args = FFmpeg.InputArgs(file, 0) + "-an -sn -dn -frames:v 1 -f image2pipe -c:v bmp -pix_fmt bgra pipe:1";
            Process p = FFmpeg.Start(args);
            PicSurface s = null;
            try
            {
                var errTask = p.StandardError.ReadToEndAsync();
                Exception readError = null;
                try { s = ReadBmp(new BufferedStream(p.StandardOutput.BaseStream, 1 << 16)); }
                catch (Exception ex) { readError = ex; }
                if (!p.WaitForExit(60000)) FFmpeg.KillQuietly(p);
                string log = "";
                try { if (errTask.Wait(3000)) log = errTask.Result; } catch { }
                if (s == null)
                {
                    // FFmpeg's own wording is about streams and packets; say it plainly instead
                    if (readError is OutOfMemoryException) throw readError;
                    throw new InvalidDataException("This file is damaged, or it is a kind of picture Stills & Reels can't read.");
                }
                return s;
            }
            catch
            {
                if (s != null) s.Dispose();
                FFmpeg.KillQuietly(p);
                throw;
            }
            finally { p.Dispose(); }
        }

        static void ReadExact(Stream st, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = st.Read(buf, got, count - got);
                if (n <= 0) throw new EndOfStreamException("The picture is cut short.");
                got += n;
            }
        }

        static void Skip(Stream st, long count)
        {
            var junk = new byte[4096];
            while (count > 0)
            {
                int n = st.Read(junk, 0, (int)Math.Min(junk.Length, count));
                if (n <= 0) throw new EndOfStreamException("The picture is cut short.");
                count -= n;
            }
        }

        /// <summary>Reads a 24 or 32-bit uncompressed BMP from a stream (not seekable) straight into a surface.</summary>
        public static PicSurface ReadBmp(Stream st)
        {
            var head = new byte[54];
            ReadExact(st, head, 54);
            if (head[0] != 'B' || head[1] != 'M') throw new InvalidDataException("Not a bitmap.");
            int offBits = BitConverter.ToInt32(head, 10);
            int dib = BitConverter.ToInt32(head, 14);
            int w = BitConverter.ToInt32(head, 18), h = BitConverter.ToInt32(head, 22);
            int bpp = BitConverter.ToUInt16(head, 28);
            int compression = BitConverter.ToInt32(head, 30);
            bool topDown = h < 0;
            if (topDown) h = -h;
            if (dib < 40 || w < 1 || h < 1 || (bpp != 24 && bpp != 32) || (compression != 0 && compression != 3)) throw new InvalidDataException("This kind of bitmap isn't supported.");
            if ((long)w * h > PicSurface.MaxPixels) throw new OutOfMemoryException("This picture is too large to open.");
            if (offBits > 54) Skip(st, offBits - 54);

            int rowBytes = bpp == 32 ? w * 4 : (w * 3 + 3) & ~3;
            var row = new byte[rowBytes];
            var s = new PicSurface(w, h);
            try
            {
                for (int i = 0; i < h; i++)
                {
                    ReadExact(st, row, rowBytes);
                    uint* d = s.Row(topDown ? i : h - 1 - i);
                    fixed (byte* r0 = row)
                    {
                        if (bpp == 32)
                        {
                            uint* src = (uint*)r0;
                            for (int x = 0; x < w; x++)
                            {
                                uint v = src[x], a = v >> 24;
                                if (a == 255) d[x] = v;
                                else if (a == 0) d[x] = 0;
                                else
                                {
                                    uint r = ((v >> 16) & 255) * a + 127, g = ((v >> 8) & 255) * a + 127, b = (v & 255) * a + 127;
                                    d[x] = (a << 24) | (((r + (r >> 8)) >> 8) << 16) | (((g + (g >> 8)) >> 8) << 8) | ((b + (b >> 8)) >> 8);
                                }
                            }
                        }
                        else
                        {
                            byte* src = r0;
                            for (int x = 0; x < w; x++, src += 3) d[x] = 0xFF000000u | ((uint)src[2] << 16) | ((uint)src[1] << 8) | src[0];
                        }
                    }
                }
                return s;
            }
            catch { s.Dispose(); throw; }
        }

        /// <summary>Writes a bitmap as a 32-bit BMP that says it has transparency (so FFmpeg keeps it).</summary>
        public static void WriteBmp(Bitmap bmp, string path)
        {
            int w = bmp.Width, h = bmp.Height;
            const int headSize = 14 + 108; // file header + BITMAPV4HEADER
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((byte)'B'); bw.Write((byte)'M');
                bw.Write((uint)(headSize + (long)w * h * 4)); bw.Write(0); bw.Write(headSize);
                bw.Write(108); bw.Write(w); bw.Write(-h); bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write(3);                                   // BI_BITFIELDS
                bw.Write((uint)((long)w * h * 4)); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
                bw.Write(0x00FF0000u); bw.Write(0x0000FF00u); bw.Write(0x000000FFu); bw.Write(0xFF000000u);
                bw.Write(0x73524742);                          // 'sRGB'
                bw.Write(new byte[36 + 12]);                   // endpoints and gamma: unused
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        bw.Write(row);
                    }
                }
                finally { bmp.UnlockBits(data); }
            }
        }

        /// <summary>Saves a WebP picture through FFmpeg (libwebp). Transparency is kept.</summary>
        public static void SaveWebp(Bitmap bmp, string path, int quality, bool lossless)
        {
            string tmpIn = Path.Combine(ImageCache.Folder ?? Path.GetTempPath(), "save-" + Guid.NewGuid().ToString("N") + ".bmp");
            string tmpOut = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                WriteBmp(bmp, tmpIn);
                string args = FFmpeg.InputArgs(tmpIn, 0) + "-frames:v 1 -c:v libwebp " +
                              (lossless ? "-lossless 1 -compression_level 4 " : "-lossless 0 -compression_level 4 -quality " + Math.Max(1, Math.Min(100, quality)).ToString(CultureInfo.InvariantCulture) + " ") +
                              "-pix_fmt " + (HasTransparency(bmp) ? "bgra" : (lossless ? "bgra" : "yuv420p")) + " -f webp -y " + FFmpeg.Quote(tmpOut);
                using (var p = FFmpeg.Start(args))
                {
                    var errTask = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(120000)) { FFmpeg.KillQuietly(p); throw new TimeoutException("Saving the picture took too long."); }
                    if (p.ExitCode != 0 || !File.Exists(tmpOut) || new FileInfo(tmpOut).Length == 0)
                    {
                        string reason = "";
                        try { reason = FFmpeg.LastMeaningfulLine(errTask.Result); } catch { }
                        throw new IOException(string.IsNullOrEmpty(reason) ? "The WebP picture couldn't be written." : reason);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmpOut, path);
            }
            finally
            {
                try { File.Delete(tmpIn); } catch { }
                try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }
            }
        }

        static bool HasTransparency(Bitmap bmp)
        {
            if ((bmp.PixelFormat & PixelFormat.Alpha) == 0) return false;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < data.Height; y++)
                {
                    uint* p = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                    for (int x = 0; x < data.Width; x++) if (p[x] < 0xFF000000u) return true;
                }
                return false;
            }
            finally { bmp.UnlockBits(data); }
        }

        /// <summary>A JPEG as bytes, with white behind see-through parts.</summary>
        public static byte[] EncodeJpeg(Bitmap bmp, int quality)
        {
            using (var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.White);
                    g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                }
                var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var ms = new MemoryStream())
                using (var p = new EncoderParameters(1))
                {
                    p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Max(1, Math.Min(100, quality)));
                    flat.Save(ms, codec, p);
                    return ms.ToArray();
                }
            }
        }
    }

    /// <summary>
    /// Photo details (EXIF: date taken, camera, lens, place) carried from an original JPEG into a saved copy.
    /// The copy's details are made safe first: the "which way up" tag is reset because the saved pixels are already
    /// upright, and the small preview picture stored inside the details is wiped, because it still shows the
    /// picture as it was before editing (for example before something was hidden).
    /// </summary>
    internal static class JpegDetails
    {
        /// <summary>The EXIF segment of a JPEG (the whole APP1 block, starting FF E1), or null if it has none.</summary>
        public static byte[] Read(byte[] jpeg)
        {
            try
            {
                if (jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
                int i = 2;
                while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
                {
                    int marker = jpeg[i + 1];
                    if (marker == 0xDA || marker == 0xD9) break;
                    int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
                    if (len < 2 || i + 2 + len > jpeg.Length) break;
                    if (marker == 0xE1 && len >= 16 && jpeg[i + 4] == 'E' && jpeg[i + 5] == 'x' && jpeg[i + 6] == 'i' && jpeg[i + 7] == 'f' && jpeg[i + 8] == 0 && jpeg[i + 9] == 0)
                    {
                        var seg = new byte[len + 2];
                        Buffer.BlockCopy(jpeg, i, seg, 0, seg.Length);
                        return seg;
                    }
                    i += 2 + len;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Makes an EXIF segment safe to put in an edited copy (see the class notes). Returns null if the segment
        /// can't be understood, so that nothing doubtful is ever written.
        /// </summary>
        public static byte[] Clean(byte[] segment, int width = 0, int height = 0)
        {
            try
            {
                if (segment == null || segment.Length < 10 + 8) return null;
                var d = (byte[])segment.Clone();
                const int tiff = 10; // FF E1, length, "Exif\0\0"
                bool le;
                if (d[tiff] == 'I' && d[tiff + 1] == 'I') le = true;
                else if (d[tiff] == 'M' && d[tiff + 1] == 'M') le = false;
                else return null;
                Func<int, int> u16 = p => le ? d[p] | (d[p + 1] << 8) : (d[p] << 8) | d[p + 1];
                Func<int, long> u32 = p => le ? (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24))
                                              : (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
                Action<int, int> w16 = (p, v) => { if (le) { d[p] = (byte)v; d[p + 1] = (byte)(v >> 8); } else { d[p] = (byte)(v >> 8); d[p + 1] = (byte)v; } };
                Action<int, long> w32 = (p, v) =>
                {
                    if (le) { d[p] = (byte)v; d[p + 1] = (byte)(v >> 8); d[p + 2] = (byte)(v >> 16); d[p + 3] = (byte)(v >> 24); }
                    else { d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16); d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v; }
                };
                if (u16(tiff + 2) != 42) return null;
                int size = d.Length - tiff;
                long ifd0 = u32(tiff + 4);
                if (ifd0 < 8 || ifd0 + 2 > size) return null;
                int at = tiff + (int)ifd0;
                int n = u16(at);
                if (at + 2 + n * 12 + 4 > d.Length) return null;
                long exifIfd = 0;
                for (int k = 0; k < n; k++)
                {
                    int e = at + 2 + k * 12;
                    int tag = u16(e);
                    if (tag == 0x0112) SetNumber(d, e, 1, u16, w16, w32);          // orientation: upright
                    else if (tag == 0x8769) exifIfd = u32(e + 8);
                }
                // the preview picture lives in the second list: wipe its bytes and unlink the list
                int nextAt = at + 2 + n * 12;
                long ifd1 = u32(nextAt);
                if (ifd1 >= 8 && ifd1 + 2 <= size)
                {
                    int a1 = tiff + (int)ifd1;
                    int n1 = u16(a1);
                    if (a1 + 2 + n1 * 12 <= d.Length)
                    {
                        long thumbAt = 0, thumbLen = 0;
                        for (int k = 0; k < n1; k++)
                        {
                            int e = a1 + 2 + k * 12;
                            int tag = u16(e);
                            if (tag == 0x0201 || tag == 0x0111) thumbAt = u32(e + 8);
                            else if (tag == 0x0202 || tag == 0x0117) thumbLen = u32(e + 8);
                        }
                        if (thumbAt >= 8 && thumbLen > 0)
                        {
                            long from = tiff + thumbAt, to = Math.Min(d.Length, from + thumbLen);
                            for (long x = from; x < to; x++) d[x] = 0;
                        }
                        else
                        {
                            // no clear size: wipe everything after the second list, where the preview sits
                            for (int x = a1 + 2 + n1 * 12 + 4; x < d.Length; x++) d[x] = 0;
                        }
                        for (int x = a1; x < Math.Min(d.Length, a1 + 2 + n1 * 12 + 4); x++) d[x] = 0;
                    }
                    else return null;
                }
                else if (ifd1 != 0) return null;
                w32(nextAt, 0);

                // the recorded size: the copy's own
                if (exifIfd >= 8 && exifIfd + 2 <= size && width > 0 && height > 0)
                {
                    int ae = tiff + (int)exifIfd;
                    int ne = u16(ae);
                    if (ae + 2 + ne * 12 <= d.Length)
                        for (int k = 0; k < ne; k++)
                        {
                            int e = ae + 2 + k * 12;
                            int tag = u16(e);
                            if (tag == 0xA002) SetNumber(d, e, width, u16, w16, w32);
                            else if (tag == 0xA003) SetNumber(d, e, height, u16, w16, w32);
                        }
                }
                return d;
            }
            catch { return null; }
        }

        static void SetNumber(byte[] d, int entry, int value, Func<int, int> u16, Action<int, int> w16, Action<int, long> w32)
        {
            int type = u16(entry + 2);
            if (type == 3) { d[entry + 8] = d[entry + 9] = d[entry + 10] = d[entry + 11] = 0; w16(entry + 8, Math.Min(65535, value)); }
            else if (type == 4) w32(entry + 8, value);
        }

        // ------------------------------------------------------------------ reading details for the details box

        sealed class Tag { public int Type; public long Count; public int At; }

        static Dictionary<int, Tag> ReadList(byte[] d, int tiff, long offset, Func<int, int> u16, Func<int, long> u32)
        {
            var tags = new Dictionary<int, Tag>();
            if (offset < 8 || tiff + offset + 2 > d.Length) return tags;
            int at = tiff + (int)offset;
            int n = u16(at);
            int[] sizes = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8 };
            for (int k = 0; k < n; k++)
            {
                int e = at + 2 + k * 12;
                if (e + 12 > d.Length) break;
                int type = u16(e + 2);
                long count = u32(e + 4);
                if (type < 1 || type > 12 || count < 0 || count > 65536) continue;
                long bytes = sizes[type] * count;
                long where = bytes <= 4 ? e + 8 : tiff + u32(e + 8);
                if (where < 0 || where + bytes > d.Length) continue;
                tags[u16(e)] = new Tag { Type = type, Count = count, At = (int)where };
            }
            return tags;
        }

        static string Text(byte[] d, Dictionary<int, Tag> tags, int id)
        {
            Tag t;
            if (!tags.TryGetValue(id, out t) || t.Type != 2) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < t.Count; i++)
            {
                char c = (char)d[t.At + i];
                if (c == 0) break;
                if (c >= 32 && c < 127) sb.Append(c);
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>
        /// The photo details worth showing, in plain words: camera, lens, when it was taken, the exposure, and the
        /// place if the camera recorded one. An empty list when there are none.
        /// </summary>
        public static List<KeyValuePair<string, string>> Describe(byte[] segment)
        {
            var rows = new List<KeyValuePair<string, string>>();
            try
            {
                byte[] d = segment;
                const int tiff = 10;
                if (d == null || d.Length < tiff + 8) return rows;
                bool le;
                if (d[tiff] == 'I' && d[tiff + 1] == 'I') le = true;
                else if (d[tiff] == 'M' && d[tiff + 1] == 'M') le = false;
                else return rows;
                Func<int, int> u16 = p => le ? d[p] | (d[p + 1] << 8) : (d[p] << 8) | d[p + 1];
                Func<int, long> u32 = p => le ? (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24))
                                              : (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
                Func<Tag, int, double> rational = (tg, i) =>
                {
                    long a = u32(tg.At + i * 8), b = u32(tg.At + i * 8 + 4);
                    if (tg.Type == 10) { a = (int)a; b = (int)b; }
                    return b == 0 ? 0 : a / (double)b;
                };
                Action<string, string> add = (k, v) => { if (!string.IsNullOrEmpty(v)) rows.Add(new KeyValuePair<string, string>(k, v)); };
                var inv = System.Globalization.CultureInfo.InvariantCulture;

                var main = ReadList(d, tiff, u32(tiff + 4), u16, u32);
                Tag link;
                var exif = main.TryGetValue(0x8769, out link) && link.Type == 4 ? ReadList(d, tiff, u32(link.At), u16, u32) : new Dictionary<int, Tag>();
                var gps = main.TryGetValue(0x8825, out link) && link.Type == 4 ? ReadList(d, tiff, u32(link.At), u16, u32) : new Dictionary<int, Tag>();

                string make = Text(d, main, 0x010F), model = Text(d, main, 0x0110);
                if (make != null && model != null && model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) make = null;
                add("Camera", ((make ?? "") + " " + (model ?? "")).Trim());
                add("Lens", Text(d, exif, 0xA434));
                string when = Text(d, exif, 0x9003) ?? Text(d, main, 0x0132);
                if (when != null)
                {
                    DateTime dt;
                    if (DateTime.TryParseExact(when, "yyyy:MM:dd HH:mm:ss", inv, System.Globalization.DateTimeStyles.None, out dt)) when = dt.ToString("d MMMM yyyy, HH:mm");
                    add("Taken", when);
                }
                var parts = new List<string>();
                Tag t;
                if (exif.TryGetValue(0x829A, out t) && (t.Type == 5 || t.Type == 10))
                {
                    double sec = rational(t, 0);
                    if (sec > 0) parts.Add(sec >= 0.4 ? sec.ToString("0.#", inv) + " s" : "1/" + Math.Round(1 / sec).ToString(inv) + " s");
                }
                if (exif.TryGetValue(0x829D, out t) && (t.Type == 5 || t.Type == 10))
                {
                    double f = rational(t, 0);
                    if (f > 0) parts.Add("f/" + f.ToString("0.#", inv));
                }
                if (exif.TryGetValue(0x8827, out t) && (t.Type == 3 || t.Type == 4))
                {
                    long iso = t.Type == 3 ? u16(t.At) : u32(t.At);
                    if (iso > 0) parts.Add("ISO " + iso.ToString(inv));
                }
                if (exif.TryGetValue(0x920A, out t) && (t.Type == 5 || t.Type == 10))
                {
                    double mm = rational(t, 0);
                    if (mm > 0) parts.Add(mm.ToString("0.#", inv) + " mm");
                }
                add("Exposure", string.Join("   ", parts.ToArray()));

                Tag lat, lon;
                if (gps.TryGetValue(2, out lat) && gps.TryGetValue(4, out lon) && lat.Type == 5 && lon.Type == 5 && lat.Count >= 3 && lon.Count >= 3)
                {
                    double la = rational(lat, 0) + rational(lat, 1) / 60 + rational(lat, 2) / 3600;
                    double lo = rational(lon, 0) + rational(lon, 1) / 60 + rational(lon, 2) / 3600;
                    string ns = Text(d, gps, 1) ?? "N", ew = Text(d, gps, 3) ?? "E";
                    if (la != 0 || lo != 0)
                        add("Place", la.ToString("0.0000", inv) + "° " + ns + ",  " + lo.ToString("0.0000", inv) + "° " + ew + "   (stored in this file)");
                }
            }
            catch { }
            return rows;
        }

        /// <summary>Puts an EXIF segment into a JPEG that has none, right after its opening (and its JFIF block).</summary>
        public static byte[] Insert(byte[] jpeg, byte[] segment)
        {
            if (segment == null || jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
            if (Read(jpeg) != null) return jpeg;
            int at = 2;
            if (jpeg[2] == 0xFF && jpeg[3] == 0xE0 && jpeg.Length > 6)
            {
                int len = (jpeg[4] << 8) | jpeg[5];
                if (len >= 2 && 4 + len <= jpeg.Length) at = 4 + len;
            }
            var o = new byte[jpeg.Length + segment.Length];
            Buffer.BlockCopy(jpeg, 0, o, 0, at);
            Buffer.BlockCopy(segment, 0, o, at, segment.Length);
            Buffer.BlockCopy(jpeg, at, o, at + segment.Length, jpeg.Length - at);
            return o;
        }

        /// <summary>True if a JPEG file carries photo details.</summary>
        public static bool Has(string file)
        {
            try
            {
                // details sit near the start of the file
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[(int)Math.Min(fs.Length, 256 * 1024)];
                    int got = 0, n;
                    while (got < head.Length && (n = fs.Read(head, got, head.Length - got)) > 0) got += n;
                    return Read(head) != null;
                }
            }
            catch { return false; }
        }
    }
}
