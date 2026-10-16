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
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico"
        };

        static readonly HashSet<string> OtherImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".webp", ".heic", ".heif", ".avif", ".svg", ".svgz", ".psd", ".psb", ".raw", ".cr2", ".cr3", ".crw",
            ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef", ".x3f",
            ".3fr", ".erf", ".kdc", ".mrw", ".tga", ".jxl", ".jxr", ".wdp", ".hdp", ".jp2", ".j2k", ".jpf",
            ".jpx", ".exr", ".hdr", ".xcf", ".pcx", ".emf", ".wmf", ".ai", ".eps", ".dds", ".qoi", ".kra",
            ".ora", ".pdn", ".cur", ".ani", ".pbm", ".pgm", ".ppm", ".pnm", ".icns", ".apng"
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
            "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg|Bitmap image (*.bmp)|*.bmp|GIF image (*.gif)|*.gif|TIFF image (*.tif)|*.tif;*.tiff";

        public const string OpenFilter =
            "Pictures and videos|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff;*.ico;*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.webm;*.wmv;*.flv;*.mpg;*.mpeg;*.ts;*.mts;*.m2ts;*.3gp;*.ogv|" +
            "Pictures (*.jpg;*.png;*.bmp;*.gif;*.tif;*.ico)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff;*.ico|" +
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
