using System.Globalization;
using System.Text;

namespace StillsAndReels
{
    /// <summary>
    /// FFmpeg settings that decide picture quality: how videos are decoded to pixels, and how pixels
    /// are encoded back into H.264. Kept in one place so recording, the editor and export agree on
    /// colours (BT.709, limited range, tagged) and never lose detail to cheap scaling.
    /// </summary>
    internal static class VideoQuality
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Lanczos scaling with full-resolution chroma and exact rounding (export, still frames).</summary>
        public const string SharpScale = "lanczos+accurate_rnd+full_chroma_int";
        /// <summary>Faster scaling for live playback; still uses full-resolution chroma.</summary>
        public const string FastScale = "bilinear+full_chroma_int";

        /// <summary>
        /// Filter that scales a decoded video to w × h and converts it to BGRA. Colours follow the file's
        /// own tags; untagged HD video is read as BT.709 (what cameras, phones and screen recorders use),
        /// where FFmpeg would otherwise assume the SD standard and shift reds and greens.
        /// </summary>
        public static string ToBgra(int w, int h, bool sharp, MediaInfo info)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(Inv, "scale={0}:{1}:flags={2}", w, h, sharp ? SharpScale : FastScale);
            if (info != null && !info.ColourTagged && (info.Width >= 1280 || info.Height >= 720))
                sb.Append(":in_color_matrix=bt709");
            sb.Append(",format=bgra");
            return sb.ToString();
        }

        /// <summary>Filter for RGB pictures going into the H.264 encoder: BT.709 limited range 4:2:0, sharp chroma.</summary>
        public const string FromRgbFilter = "scale=flags=" + SharpScale + ":out_color_matrix=bt709:out_range=tv,format=yuv420p";

        /// <summary>Colour tags written into every H.264 file so players show the colours that were encoded.</summary>
        public const string ColourTags = "-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv ";

        /// <summary>
        /// H.264 output options for pictures that arrive as RGB (screen recordings, editor export).
        /// crf: 0 = lossless … 51 = worst; preset: x264 speed/size trade-off.
        /// </summary>
        public static string H264FromRgb(int crf, string preset)
        {
            return string.Format(Inv, "-vf \"{0}\" -c:v libx264 -preset {1} -crf {2} -pix_fmt yuv420p -profile:v high ", FromRgbFilter, preset, crf) +
                   ColourTags;
        }

        /// <summary>
        /// Lossless RGB capture for recording segments: exact screen pixels, cheap enough to keep up with
        /// the screen. The finished recording is encoded once from these (see ScreenRecorder.Finish).
        /// </summary>
        public const string LosslessCapture = "-c:v libx264rgb -preset ultrafast -qp 0 -pix_fmt bgr0 ";

        /// <summary>Quality of finished screen recordings: visually lossless for text and UI.</summary>
        public const int RecordingCrf = 16;
        public const string RecordingPreset = "fast";
        public const int RecordingAudioKbps = 192;
    }
}
