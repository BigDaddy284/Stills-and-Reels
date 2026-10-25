using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace StillsAndReels
{
    /// <summary>
    /// Temporary per-process cache. Each previewed image is copied here first and the copy is what
    /// gets read, so the original is never opened for writing or kept locked. The previous copy is
    /// deleted as soon as another image is shown, and the whole folder is removed when the app closes.
    /// </summary>
    internal static class ImageCache
    {
        static string root;
        static string dir;

        public static string Folder { get { return dir; } }

        public static void Initialize()
        {
            root = Path.Combine(Path.GetTempPath(), "StillsAndReels", "Cache");
            dir = Path.Combine(root, Process.GetCurrentProcess().Id.ToString());
            CleanupStale();
            Directory.CreateDirectory(dir);
            RemoveOldNameFolders();
        }

        /// <summary>Removes the engine and cache folders left by Picture Preview (this app's old name), unless it is still running.</summary>
        static void RemoveOldNameFolders()
        {
            try
            {
                if (Process.GetProcessesByName("PicturePreview").Length > 0) return;
                string temp = Path.Combine(Path.GetTempPath(), "PicturePreview");
                string engine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicturePreview");
                foreach (var d in new[] { temp, engine })
                    if (Directory.Exists(d)) { try { Directory.Delete(d, true); } catch { } }
            }
            catch { }
        }

        static void CleanupStale()
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var sub in Directory.GetDirectories(root))
                {
                    int pid;
                    bool alive = false;
                    if (int.TryParse(Path.GetFileName(sub), out pid))
                    {
                        try { using (var p = Process.GetProcessById(pid)) alive = !p.HasExited && p.ProcessName.IndexOf("StillsAndReels", StringComparison.OrdinalIgnoreCase) >= 0; }
                        catch { alive = false; }
                    }
                    if (!alive) { try { Directory.Delete(sub, true); } catch { } }
                }
            }
            catch { }
        }

        public static string CreateCopy(string source)
        {
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(source));
            File.Copy(source, target, false);
            try { File.SetAttributes(target, FileAttributes.Normal | FileAttributes.Temporary); } catch { }
            return target;
        }

        /// <summary>
        /// Saves a picture picked up from the screen or the clipboard as a PNG in the cache, so it is shown
        /// (and copied again) like any other file. Only the latest few are kept; all go when the app closes.
        /// </summary>
        public static string SavePicked(Bitmap bmp, string name)
        {
            string folder = Path.Combine(dir, "Picked");
            Directory.CreateDirectory(folder);
            try
            {
                var old = new DirectoryInfo(folder).GetFiles();
                Array.Sort(old, (a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                for (int i = 4; i < old.Length; i++) Delete(old[i].FullName);
            }
            catch { }
            string baseName = string.IsNullOrWhiteSpace(name) ? "Picture" : name.Trim();
            string path = Path.Combine(folder, baseName + ".png");
            for (int i = 2; File.Exists(path) && i < 10000; i++) path = Path.Combine(folder, baseName + " (" + i + ").png");
            bmp.Save(path, ImageFormat.Png);
            try { File.SetAttributes(path, FileAttributes.Normal | FileAttributes.Temporary); } catch { }
            return path;
        }

        /// <summary>True for pictures picked up from the screen or clipboard (they live in the cache, not in a real folder).</summary>
        public static bool IsPicked(string path)
        {
            if (string.IsNullOrEmpty(path) || dir == null) return false;
            return path.StartsWith(Path.Combine(dir, "Picked") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static void Delete(string cachePath)
        {
            if (string.IsNullOrEmpty(cachePath)) return;
            try
            {
                if (File.Exists(cachePath))
                {
                    File.SetAttributes(cachePath, FileAttributes.Normal);
                    File.Delete(cachePath);
                }
            }
            catch { }
        }

        public static void Shutdown()
        {
            try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    internal sealed class LoadedImage : IDisposable
    {
        public string SourcePath;
        public string CachePath;
        public Image Image;
        public bool IsAnimated;
        public long FileSize;
        MemoryStream stream; // kept open only for animated GIFs (GDI+ reads frames lazily)

        public void Dispose()
        {
            if (Image != null) { Image.Dispose(); Image = null; }
            if (stream != null) { stream.Dispose(); stream = null; }
        }

        public static LoadedImage Load(string source)
        {
            string cache = ImageCache.CreateCopy(source);
            try
            {
                byte[] data = File.ReadAllBytes(cache);
                var li = new LoadedImage { SourcePath = source, CachePath = cache, FileSize = data.LongLength };
                string ext = Path.GetExtension(source).ToLowerInvariant();

                if (ext == ".ico")
                {
                    try
                    {
                        using (var ms = new MemoryStream(data))
                        using (var icon = new Icon(ms, 256, 256))
                            li.Image = icon.ToBitmap();
                    }
                    catch { li.Image = null; }
                }

                if (li.Image == null)
                {
                    var ms = new MemoryStream(data);
                    Image img;
                    try { img = Image.FromStream(ms, true, true); }
                    catch { ms.Dispose(); throw; }

                    bool animated = false;
                    try
                    {
                        animated = Array.IndexOf(img.FrameDimensionsList, FrameDimension.Time.Guid) >= 0
                                   && img.GetFrameCount(FrameDimension.Time) > 1;
                    }
                    catch { }

                    if (animated)
                    {
                        li.Image = img;
                        li.stream = ms;
                        li.IsAnimated = true;
                    }
                    else
                    {
                        int orientation = ReadOrientation(img);
                        if (orientation <= 1) orientation = JpegOrientation(data);
                        Bitmap bmp;
                        try
                        {
                            bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
                            }
                        }
                        finally
                        {
                            img.Dispose();
                            ms.Dispose();
                        }
                        ApplyOrientation(bmp, orientation);
                        li.Image = bmp;
                    }
                }
                return li;
            }
            catch
            {
                ImageCache.Delete(cache);
                throw;
            }
        }

        static int ReadOrientation(Image img)
        {
            try
            {
                if (Array.IndexOf(img.PropertyIdList, 0x0112) < 0) return 1;
                var p = img.GetPropertyItem(0x0112);
                if (p.Value == null || p.Value.Length < 2) return 1;
                return BitConverter.ToUInt16(p.Value, 0);
            }
            catch { return 1; }
        }

        /// <summary>Reads the EXIF orientation tag straight from JPEG bytes (fallback if GDI+ doesn't report it).</summary>
        static int JpegOrientation(byte[] d)
        {
            try
            {
                if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return 1;
                int i = 2;
                while (i + 4 < d.Length && d[i] == 0xFF)
                {
                    int marker = d[i + 1];
                    int len = (d[i + 2] << 8) | d[i + 3];
                    if (marker == 0xDA || len < 2) break; // start of image data
                    int seg = i + 4;
                    if (marker == 0xE1 && seg + 14 < d.Length && d[seg] == 'E' && d[seg + 1] == 'x' && d[seg + 2] == 'i' && d[seg + 3] == 'f')
                    {
                        int tiff = seg + 6;
                        bool le = d[tiff] == 'I';
                        Func<int, int> u16 = p => le ? d[p] | (d[p + 1] << 8) : (d[p] << 8) | d[p + 1];
                        Func<int, int> u32 = p => le ? d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24)
                                                     : (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
                        int ifd = tiff + u32(tiff + 4);
                        int n = u16(ifd);
                        for (int k = 0; k < n; k++)
                        {
                            int e = ifd + 2 + k * 12;
                            if (e + 12 > d.Length) break;
                            if (u16(e) == 0x0112) return u16(e + 8);
                        }
                        return 1;
                    }
                    i += 2 + len;
                }
            }
            catch { }
            return 1;
        }

        static void ApplyOrientation(Image img, int o)
        {
            switch (o)
            {
                case 2: img.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                case 3: img.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                case 4: img.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
                case 5: img.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                case 6: img.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                case 7: img.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                case 8: img.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            }
        }
    }
}
