using System;
using System.Collections.Generic;
using System.IO;

namespace StillsAndReels
{
    internal enum FileKind
    {
        Other,        // not an image - ignored, the last image stays on screen
        Image,        // an image this app can show
        Incompatible, // an image format this app doesn't support
        Video         // a video (or audio-in-video container) for Videos mode
    }

    internal static class FileTypes
    {
        static readonly HashSet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico",
            // read by the bundled FFmpeg
            ".webp", ".heic", ".heif", ".avif", ".tga", ".psd", ".jp2", ".j2k", ".jpf", ".jpx", ".dds", ".qoi", ".pcx",
            ".exr", ".hdr", ".pbm", ".pgm", ".ppm", ".pnm",
            // the app's own layered picture
            ".srpic"
        };

        /// <summary>Pictures Windows can't read by itself; the bundled FFmpeg reads them.</summary>
        static readonly HashSet<string> ViaFfmpeg = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".webp", ".heic", ".heif", ".avif", ".tga", ".psd", ".jp2", ".j2k", ".jpf", ".jpx", ".dds", ".qoi", ".pcx",
            ".exr", ".hdr", ".pbm", ".pgm", ".ppm", ".pnm"
        };

        public static bool NeedsFfmpeg(string path)
        {
            try { return ViaFfmpeg.Contains(Path.GetExtension(path) ?? ""); } catch { return false; }
        }

        static readonly HashSet<string> OtherImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".svg", ".svgz", ".psb", ".raw", ".cr2", ".cr3", ".crw",
            ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef", ".x3f",
            ".3fr", ".erf", ".kdc", ".mrw", ".jxl", ".jxr", ".wdp", ".hdp",
            ".xcf", ".emf", ".wmf", ".ai", ".eps", ".kra",
            ".ora", ".pdn", ".cur", ".ani", ".icns", ".apng"
        };

        static readonly HashSet<string> Videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".qt", ".avi", ".mkv", ".webm", ".wmv", ".asf", ".flv", ".f4v", ".mpg", ".mpeg",
            ".mpe", ".m2v", ".ts", ".mts", ".m2ts", ".3gp", ".3g2", ".ogv", ".vob", ".divx", ".mxf", ".dv", ".y4m"
        };

        public static bool IsVideo(string path)
        {
            try { return Videos.Contains(Path.GetExtension(path) ?? ""); } catch { return false; }
        }

        public const string VideoFilter = "Videos|*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.webm;*.wmv;*.flv;*.mpg;*.mpeg;*.ts;*.mts;*.m2ts;*.3gp;*.ogv";

        public const string SaveFilter =
            "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg|WebP image (*.webp)|*.webp|Bitmap image (*.bmp)|*.bmp|GIF image (*.gif)|*.gif|TIFF image (*.tif)|*.tif;*.tiff|" +
            "Picture with layers, to keep editing later (*.srpic)|*.srpic";

        const string PictureList = "*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff;*.ico;*.webp;*.heic;*.heif;*.avif;*.tga;*.psd;*.jp2;*.j2k;*.dds;*.qoi;*.pcx;*.exr;*.hdr;*.ppm;*.pgm;*.pbm;*.pnm;*.srpic";

        public const string OpenFilter =
            "Pictures and videos|" + PictureList + ";*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.webm;*.wmv;*.flv;*.mpg;*.mpeg;*.ts;*.mts;*.m2ts;*.3gp;*.ogv|" +
            "Pictures (*.jpg;*.png;*.webp;*.heic;*.gif;*.tif;*.bmp;*.srpic and more)|" + PictureList + "|" +
            VideoFilter + "|All files (*.*)|*.*";

        public static FileKind Classify(string path)
        {
            if (string.IsNullOrEmpty(path)) return FileKind.Other;
            string ext;
            try { ext = Path.GetExtension(path); }
            catch (ArgumentException) { return FileKind.Other; }
            if (string.IsNullOrEmpty(ext)) return FileKind.Other;
            if (Supported.Contains(ext)) return FileKind.Image;
            if (Videos.Contains(ext)) return FileKind.Video;
            if (OtherImages.Contains(ext)) return FileKind.Incompatible;
            return FileKind.Other;
        }
    }
}
