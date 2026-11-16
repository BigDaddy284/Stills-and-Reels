using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;

namespace StillsAndReels
{
    internal enum TrackKind { Video, Audio }
    internal enum ClipKind { Video, Image, Audio, Text }

    /// <summary>A transform keyframe. T is seconds from the start of the clip (timeline time).</summary>
    [DataContract(Namespace = "")]
    internal sealed class Keyframe
    {
        [DataMember] public double T;
        [DataMember] public double X, Y;
        [DataMember] public double Scale = 1, Rotation, Opacity = 1;
        [DataMember] public bool Ease = true;
        /// <summary>
        /// How the values travel to this keyframe from the one before. 0 = as Ease says (what 2.6 saved: smooth or
        /// straight), 1 straight, 2 smooth, 3 slow start, 4 slow finish, 5 stay then jump, 6 overshoot and settle, 7 bounce.
        /// </summary>
        [DataMember] public int Curve;
        public Keyframe Clone() { return (Keyframe)MemberwiseClone(); }

        public int CurveKind { get { return Curve >= 1 && Curve <= 7 ? Curve : Ease ? 2 : 1; } }

        public static readonly string[] CurveNames = { "", "Straight", "Smooth", "Slow start", "Slow finish", "Stay, then jump", "Overshoot", "Bounce" };

        /// <summary>Progress along the way to this keyframe (0..1) shaped by its curve.</summary>
        public double Shape(double u)
        {
            switch (CurveKind)
            {
                case 2: return u * u * (3 - 2 * u);
                case 3: return u * u * u;
                case 4: { double v = 1 - u; return 1 - v * v * v; }
                case 5: return u >= 1 - 1e-9 ? 1 : 0;
                case 6: { const double c1 = 1.70158, c3 = c1 + 1; double v = u - 1; return 1 + c3 * v * v * v + c1 * v * v; }
                case 7:
                    {
                        const double n1 = 7.5625, d1 = 2.75;
                        if (u < 1 / d1) return n1 * u * u;
                        if (u < 2 / d1) { u -= 1.5 / d1; return n1 * u * u + 0.75; }
                        if (u < 2.5 / d1) { u -= 2.25 / d1; return n1 * u * u + 0.9375; }
                        u -= 2.625 / d1; return n1 * u * u + 0.984375;
                    }
                default: return u;
            }
        }
    }

    /// <summary>A named point on the timeline to line things up with.</summary>
    [DataContract(Namespace = "")]
    internal sealed class Marker
    {
        [DataMember] public double T;
        [DataMember] public string Name;
    }

    /// <summary>Transform values at one moment.</summary>
    internal struct Placement
    {
        public double X, Y, Scale, Rotation, Opacity;
    }

    [DataContract(Namespace = "")]
    internal sealed class Clip
    {
        [DataMember] public int Id;
        [DataMember] public ClipKind Kind;
        [DataMember] public int MediaId;          // 0 for text clips
        [DataMember] public double Start;         // timeline seconds
        [DataMember] public double Duration;      // timeline seconds
        [DataMember] public double In;            // source seconds (video/audio)
        [DataMember] public double Speed = 1;

        // ---- picture
        [DataMember] public double X, Y;          // offset from the centre, as a fraction of the canvas size
        [DataMember] public double Scale = 1;     // 1 = fits the canvas
        [DataMember] public double Rotation;      // degrees
        [DataMember] public double Opacity = 1;
        [DataMember] public bool FlipH, FlipV;
        [DataMember] public double CropL, CropT, CropR, CropB; // fractions of the source
        [DataMember] public double Brightness, Contrast, Saturation, Exposure, Temperature, Tint, Hue;
        [DataMember] public string Filter = "";
        [DataMember] public double FilterAmount = 1;
        [DataMember] public double Blur, Sharpen, Vignette, Grain;
        [DataMember] public bool ChromaKey;
        [DataMember] public int KeyColor = unchecked((int)0xFF00FF00);
        [DataMember] public double KeySimilarity = 30, KeySmoothness = 10;
        [DataMember] public bool Stabilize;
        [DataMember] public double FadeIn, FadeOut;           // picture fades (seconds)
        [DataMember] public string Transition = "";            // transition into this clip
        [DataMember] public double TransitionDuration = 0.6;
        // New in 3.0. A project saved by 2.6 has none of these, and a missing value reads as 0, false or null,
        // so every one of them must mean "as before" when it is empty.
        [DataMember] public string Blend = "";                 // how the clip mixes with what is under it ("" = normal)
        [DataMember] public int MaskKind;                      // 0 none, 1 box, 2 oval, 3 split
        [DataMember] public double MaskX, MaskY;               // centre, fraction of the picture from its middle
        [DataMember] public double MaskW, MaskH;               // size, fraction of the picture (0 reads as 0.6)
        [DataMember] public double MaskRotation, MaskFeather, MaskRound;
        [DataMember] public bool MaskInvert;
        [DataMember] public int BackFill;                      // 1 = a blurred copy of the clip fills the canvas behind it
        [DataMember] public double Corner;                     // rounded corners, 0..1
        [DataMember] public double BorderWidth;                // 0..1 of half the shorter side
        [DataMember] public int BorderColor;                   // 0 reads as white
        [DataMember] public double DropShadow;                 // 0..1
        [DataMember] public double Pixelate, Glow, RgbSplit;   // effects, 0..100
        [DataMember] public int Mirror;                        // 1 left half onto the right, 2 right onto left, 3 top onto bottom, 4 bottom onto top
        [DataMember] public double Highlights, Shadows;        // -100..100
        [DataMember] public string Motion = "";                // movement over the whole clip: zoomin, zoomout, panleft, …
        [DataMember] public double MotionAmount;               // strength; 0 reads as 1
        [DataMember] public string AnimIn = "", AnimOut = "";  // how the clip arrives and leaves: fade, slideup, pop, type, …
        [DataMember] public double AnimInTime, AnimOutTime;    // seconds; 0 reads as 0.5
        [DataMember] public int TextColor2;                    // second colour of a top-to-bottom gradient; 0 = none
        [DataMember] public double LineSpacing;                // extra line spacing, -0.5..2 of the normal gap

        /// <summary>Text only, not saved: how many characters show while a typewriter animation runs (0 = all).</summary>
        public int RevealChars;

        // ---- sound
        [DataMember] public double Volume = 1;
        [DataMember] public bool Muted;
        [DataMember] public double AudioFadeIn, AudioFadeOut;
        // New in 3.0 (see the note above: empty must mean "as before").
        [DataMember] public double Denoise;                    // noise reduction, 0..100
        [DataMember] public bool VoiceBoost;                   // clearer speech: rumble cut, presence lift, gentle compression
        [DataMember] public double Bass, Treble;               // -100..100 (±12 dB)
        [DataMember] public double Pitch;                      // semitones, -12..12
        [DataMember] public bool EvenLoudness;                 // brought to one steady loudness
        [DataMember] public bool Duck;                         // turned down while other clips are sounding (music under speech)
        [DataMember] public double DuckAmount;                 // how far down, 0..1; 0 reads as 0.75
        [DataMember] public List<LevelKey> Levels = new List<LevelKey>(); // volume keyframes; when there are any they replace Volume

        // ---- text
        [DataMember] public string Text = "";
        [DataMember] public string Font = "Segoe UI";
        [DataMember] public double FontSize = 0.08;  // fraction of the canvas height
        [DataMember] public int TextColor = unchecked((int)0xFFFFFFFF);
        [DataMember] public bool Bold, Italic;
        [DataMember] public int Align = 1;           // 0 left, 1 centre, 2 right
        [DataMember] public int OutlineColor = unchecked((int)0xFF000000);
        [DataMember] public double OutlineWidth;     // fraction of the font size
        [DataMember] public bool Shadow;
        [DataMember] public int BoxColor = unchecked((int)0xFF000000);
        [DataMember] public double BoxOpacity;
        [DataMember] public string TextStyle = "plain";

        [DataMember] public List<Keyframe> Keys = new List<Keyframe>();

        public double End { get { return Start + Duration; } }
        public bool IsVisual { get { return Kind != ClipKind.Audio; } }

        public Clip Clone()
        {
            var c = (Clip)MemberwiseClone();
            c.Keys = new List<Keyframe>();
            if (Keys != null) foreach (var k in Keys) c.Keys.Add(k.Clone());
            c.Levels = new List<LevelKey>();
            if (Levels != null) foreach (var k in Levels) c.Levels.Add(new LevelKey { T = k.T, V = k.V });
            return c;
        }

        [OnDeserialized]
        void Fix(StreamingContext ctx)
        {
            if (Keys == null) Keys = new List<Keyframe>();
            if (Levels == null) Levels = new List<LevelKey>();
            if (Filter == null) Filter = "";
            if (Transition == null) Transition = "";
            if (Blend == null) Blend = "";
            if (Motion == null) Motion = "";
            if (AnimIn == null) AnimIn = "";
            if (AnimOut == null) AnimOut = "";
            if (Text == null) Text = "";
            if (string.IsNullOrEmpty(Font)) Font = "Segoe UI";
            if (string.IsNullOrEmpty(TextStyle)) TextStyle = "plain";
            if (Speed <= 0) Speed = 1;
        }

        /// <summary>Source time (seconds) shown at timeline time t.</summary>
        public double SourceTime(double t) { return In + (t - Start) * Speed; }

        public bool HasKeys { get { return Keys != null && Keys.Count > 0; } }

        public Placement PlacementAt(double t)
        {
            var p = new Placement { X = X, Y = Y, Scale = Scale, Rotation = Rotation, Opacity = Opacity };
            if (HasKeys)
            {
                double lt = t - Start;
                Keyframe a = null, b = null;
                foreach (var k in Keys)
                {
                    if (k.T <= lt + 1e-9) a = k;
                    else { b = k; break; }
                }
                if (a == null) a = Keys[0];
                if (b == null) b = a;
                if (ReferenceEquals(a, b)) { p.X = a.X; p.Y = a.Y; p.Scale = a.Scale; p.Rotation = a.Rotation; p.Opacity = a.Opacity; }
                else
                {
                    double u = (lt - a.T) / Math.Max(1e-6, b.T - a.T);
                    u = Math.Max(0, Math.Min(1, u));
                    u = b.Shape(u);
                    p.X = a.X + (b.X - a.X) * u;
                    p.Y = a.Y + (b.Y - a.Y) * u;
                    p.Scale = a.Scale + (b.Scale - a.Scale) * u;
                    p.Rotation = a.Rotation + (b.Rotation - a.Rotation) * u;
                    p.Opacity = Math.Max(0, Math.Min(1, a.Opacity + (b.Opacity - a.Opacity) * u));
                    if (p.Scale < 0.01) p.Scale = 0.01;   // an overshooting curve mustn't turn the clip inside out
                }
            }
            return p;
        }

        public double MaxScale
        {
            get
            {
                double m = Scale;
                if (HasKeys) foreach (var k in Keys) m = Math.Max(m, k.Scale);
                if (!string.IsNullOrEmpty(Motion)) m *= 1 + 0.2 * MotionStrength;
                return Math.Max(0.05, m);
            }
        }

        public double MotionStrength { get { return MotionAmount > 0.001 ? MotionAmount : 1; } }
        public double AnimInLength { get { return Math.Min(AnimInTime > 0.001 ? AnimInTime : 0.5, Duration / 2); } }
        public double AnimOutLength { get { return Math.Min(AnimOutTime > 0.001 ? AnimOutTime : 0.5, Duration / 2); } }
        public bool IsAnimated { get { return HasKeys || !string.IsNullOrEmpty(Motion) || !string.IsNullOrEmpty(AnimIn) || !string.IsNullOrEmpty(AnimOut); } }

        /// <summary>
        /// Where the clip is actually drawn at timeline time t: its own placement (and keyframes) plus the chosen
        /// movement and the way it arrives and leaves.
        /// </summary>
        public Placement ShownAt(double t)
        {
            var p = PlacementAt(t);
            if (!string.IsNullOrEmpty(Motion))
            {
                double u = Duration > 1e-6 ? Math.Max(0, Math.Min(1, (t - Start) / Duration)) : 0;
                double k = MotionStrength, lt = t - Start;
                switch (Motion)
                {
                    case "zoomin": p.Scale *= 1 + 0.18 * k * u; break;
                    case "zoomout": p.Scale *= 1 + 0.18 * k * (1 - u); break;
                    // pans enlarge the clip a little so its edges stay off the canvas while it travels
                    case "panleft": p.Scale *= 1 + 0.12 * k; p.X += (0.5 - u) * 0.1 * k * p.Scale; break;
                    case "panright": p.Scale *= 1 + 0.12 * k; p.X -= (0.5 - u) * 0.1 * k * p.Scale; break;
                    case "panup": p.Scale *= 1 + 0.12 * k; p.Y += (0.5 - u) * 0.1 * k * p.Scale; break;
                    case "pandown": p.Scale *= 1 + 0.12 * k; p.Y -= (0.5 - u) * 0.1 * k * p.Scale; break;
                    case "shake":
                        p.Scale *= 1 + 0.04 * k;
                        p.X += (Math.Sin(lt * 31.1) + 0.6 * Math.Sin(lt * 17.3 + 1.3)) * 0.006 * k;
                        p.Y += (Math.Sin(lt * 27.7 + 0.7) + 0.6 * Math.Sin(lt * 13.9 + 2.1)) * 0.008 * k;
                        p.Rotation += Math.Sin(lt * 23.3 + 0.4) * 0.5 * k;
                        break;
                    case "pulse": p.Scale *= 1 + 0.05 * k * Math.Pow(0.5 + 0.5 * Math.Sin(lt * Math.PI * 2 * 1.2), 3); break;
                    case "float": p.Y += Math.Sin(lt * Math.PI * 2 / 3.2) * 0.012 * k; p.Rotation += Math.Sin(lt * Math.PI * 2 / 4.6 + 0.8) * 1.2 * k; break;
                    case "spin": p.Rotation += 360 * u * Math.Round(Math.Max(1, k)); break;
                    case "swing": p.Rotation += Math.Sin(lt * Math.PI * 2 / 2.4) * 6 * k; break;
                }
            }
            if (!string.IsNullOrEmpty(AnimIn))
            {
                double d = AnimInLength;
                if (d > 1e-6 && t - Start < d) Arrive(ref p, AnimIn, (t - Start) / d);
            }
            if (!string.IsNullOrEmpty(AnimOut))
            {
                double d = AnimOutLength;
                if (d > 1e-6 && End - t < d) Arrive(ref p, AnimOut, (End - t) / d);
            }
            return p;
        }

        /// <summary>Applies an arrival animation at progress u (0 = just appearing, 1 = in place). Leaving is the same played backwards.</summary>
        static void Arrive(ref Placement p, string kind, double u)
        {
            u = Math.Max(0, Math.Min(1, u));
            double e = 1 - (1 - u) * (1 - u) * (1 - u);   // fast at first, settling gently
            double q = 1 - e;
            switch (kind)
            {
                case "fade": p.Opacity *= u; break;
                case "slideup": p.Y += 0.14 * q; p.Opacity *= Math.Min(1, u * 2.5); break;
                case "slidedown": p.Y -= 0.14 * q; p.Opacity *= Math.Min(1, u * 2.5); break;
                case "slideleft": p.X += 0.18 * q; p.Opacity *= Math.Min(1, u * 2.5); break;
                case "slideright": p.X -= 0.18 * q; p.Opacity *= Math.Min(1, u * 2.5); break;
                case "pop":
                    {
                        // grows past its size and settles back
                        double c1 = 1.70158, c3 = c1 + 1, v = u - 1;
                        double back = 1 + c3 * v * v * v + c1 * v * v;
                        p.Scale *= Math.Max(0.01, 0.4 + 0.6 * back); p.Opacity *= Math.Min(1, u * 4);
                        break;
                    }
                case "grow": p.Scale *= 0.6 + 0.4 * e; p.Opacity *= u; break;
                case "shrink": p.Scale *= 1 + 0.6 * q; p.Opacity *= u; break;
                case "spin": p.Rotation -= 120 * q; p.Scale *= 0.4 + 0.6 * e; p.Opacity *= Math.Min(1, u * 2); break;
                case "drop":
                    {
                        // falls from above and bounces
                        double b = u, n1 = 7.5625, d1 = 2.75;
                        if (b < 1 / d1) b = n1 * b * b;
                        else if (b < 2 / d1) { b -= 1.5 / d1; b = n1 * b * b + 0.75; }
                        else if (b < 2.5 / d1) { b -= 2.25 / d1; b = n1 * b * b + 0.9375; }
                        else { b -= 2.625 / d1; b = n1 * b * b + 0.984375; }
                        p.Y -= 0.6 * (1 - b); p.Opacity *= Math.Min(1, u * 5);
                        break;
                    }
            }
        }

        /// <summary>For a typewriter animation: the share of the text showing at time t (1 = all of it).</summary>
        public double RevealAt(double t)
        {
            double r = 1;
            if (AnimIn == "type") { double d = AnimInLength; if (d > 1e-6) r = Math.Min(r, (t - Start) / d); }
            if (AnimOut == "type") { double d = AnimOutLength; if (d > 1e-6) r = Math.Min(r, (End - t) / d); }
            return Math.Max(0, Math.Min(1, r));
        }

        /// <summary>Picture fade multiplier at timeline time t.</summary>
        public double FadeAt(double t)
        {
            double f = 1;
            if (FadeIn > 0.001) f = Math.Min(f, (t - Start) / FadeIn);
            if (FadeOut > 0.001) f = Math.Min(f, (End - t) / FadeOut);
            return Math.Max(0, Math.Min(1, f));
        }

        public void SortKeys() { Keys.Sort((a, b) => a.T.CompareTo(b.T)); }

        // ---- sound

        public bool HasLevels { get { return Levels != null && Levels.Count > 0; } }
        public double DuckDepth { get { return DuckAmount > 0.001 ? Math.Min(1, DuckAmount) : 0.75; } }

        /// <summary>Volume at lt seconds into the clip: the slider's value, or the line through the volume keyframes.</summary>
        public double VolumeAt(double lt)
        {
            if (!HasLevels) return Volume;
            LevelKey a = null, b = null;
            foreach (var k in Levels)
            {
                if (k.T <= lt + 1e-9) a = k;
                else { b = k; break; }
            }
            if (a == null) return Levels[0].V;
            if (b == null) return a.V;
            double u = (lt - a.T) / Math.Max(1e-6, b.T - a.T);
            return a.V + (b.V - a.V) * Math.Max(0, Math.Min(1, u));
        }

        public LevelKey LevelAt(double t, double tolerance)
        {
            double lt = t - Start;
            foreach (var k in Levels) if (Math.Abs(k.T - lt) <= tolerance) return k;
            return null;
        }

        /// <summary>True when any volume this clip reaches is above silence.</summary>
        public bool Sounds
        {
            get
            {
                if (Muted) return false;
                if (!HasLevels) return Volume > 0.0001;
                foreach (var k in Levels) if (k.V > 0.0001) return true;
                return false;
            }
        }

        /// <summary>
        /// The slow part of the clip's sound work, as an FFmpeg filter chain run over the whole file once and kept
        /// ("" when there is none). Bass, treble, voice boost, volume and ducking are done live by the mixer.
        /// </summary>
        public string SoundChain()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (Denoise > 0.5)
            {
                double d = Math.Min(100, Denoise);
                parts.Add("highpass=f=70");
                parts.Add("afftdn=nr=" + (6 + d * 0.24).ToString("0.#", inv) + ":nf=" + (-50 + d * 0.25).ToString("0.#", inv));
            }
            if (Math.Abs(Pitch) > 0.01)
            {
                int rate = (int)Math.Round(48000 * Math.Pow(2, Math.Max(-12, Math.Min(12, Pitch)) / 12.0));
                parts.Add("asetrate=" + rate + ",aresample=48000," + MediaLibrary.Tempo(48000.0 / rate));
            }
            if (EvenLoudness) parts.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            return string.Join(",", parts);
        }

        /// <summary>Moves every keyframe (picture and volume) by delta seconds, e.g. when the clip's start is trimmed.</summary>
        public void ShiftKeys(double delta)
        {
            foreach (var k in Keys) k.T += delta;
            foreach (var k in Levels) k.T += delta;
        }

        /// <summary>Stretches keyframe times, e.g. when the clip's speed changes.</summary>
        public void ScaleKeys(double factor)
        {
            foreach (var k in Keys) k.T *= factor;
            foreach (var k in Levels) k.T *= factor;
        }

        public Keyframe KeyAt(double t, double tolerance)
        {
            double lt = t - Start;
            foreach (var k in Keys) if (Math.Abs(k.T - lt) <= tolerance) return k;
            return null;
        }

        public bool HasColorWork
        {
            get
            {
                return Math.Abs(Brightness) > 0.01 || Math.Abs(Contrast) > 0.01 || Math.Abs(Saturation) > 0.01 ||
                       Math.Abs(Exposure) > 0.01 || Math.Abs(Temperature) > 0.01 || Math.Abs(Tint) > 0.01 || Math.Abs(Hue) > 0.01 ||
                       Math.Abs(Highlights) > 0.01 || Math.Abs(Shadows) > 0.01 ||
                       (!string.IsNullOrEmpty(Filter) && FilterAmount > 0.001);
            }
        }

        public bool HasMask { get { return MaskKind >= 1 && MaskKind <= 3; } }

        /// <summary>The clip's mask as the renderer wants it, or null.</summary>
        public MaskShape Mask()
        {
            if (!HasMask) return null;
            return new MaskShape
            {
                Kind = MaskKind, X = MaskX, Y = MaskY, W = MaskW > 0.001 ? MaskW : 0.6, H = MaskH > 0.001 ? MaskH : 0.6,
                Rotation = MaskRotation, Feather = Math.Max(0, MaskFeather), Round = MaskRound, Invert = MaskInvert,
            };
        }

        public bool HasCrop { get { return CropL > 0.0005 || CropT > 0.0005 || CropR > 0.0005 || CropB > 0.0005; } }
    }

    /// <summary>A volume keyframe: V is the volume (1 = 100%) at T seconds into the clip.</summary>
    [DataContract(Namespace = "")]
    internal sealed class LevelKey
    {
        [DataMember] public double T, V;
    }

    [DataContract(Namespace = "")]
    internal sealed class Track
    {
        [DataMember] public int Id;
        [DataMember] public TrackKind Kind;
        [DataMember] public bool Muted, Hidden, Locked;
        [DataMember] public List<Clip> Clips = new List<Clip>();

        public Track Clone()
        {
            var t = (Track)MemberwiseClone();
            t.Clips = new List<Clip>();
            foreach (var c in Clips) t.Clips.Add(c.Clone());
            return t;
        }

        [OnDeserialized]
        void Fix(StreamingContext ctx) { if (Clips == null) Clips = new List<Clip>(); }

        public void Sort() { Clips.Sort((a, b) => a.Start.CompareTo(b.Start)); }

        public double End
        {
            get
            {
                double e = 0;
                foreach (var c in Clips) e = Math.Max(e, c.End);
                return e;
            }
        }

        public Clip ClipAt(double t)
        {
            foreach (var c in Clips) if (t >= c.Start - 1e-9 && t < c.End - 1e-9) return c;
            return null;
        }

        /// <summary>True if [start, end) is free (ignoring the given clip).</summary>
        public bool IsFree(double start, double end, Clip ignore = null)
        {
            foreach (var c in Clips)
            {
                if (ReferenceEquals(c, ignore)) continue;
                if (start < c.End - 1e-6 && end > c.Start + 1e-6) return false;
            }
            return true;
        }
    }

    /// <summary>A file used by the project. Saved with its original location; the working copy lives in the cache.</summary>
    [DataContract(Namespace = "")]
    internal sealed class MediaRef
    {
        [DataMember] public int Id;
        [DataMember] public string Path;
        [DataMember] public string Name;
    }

    [DataContract(Namespace = "")]
    internal sealed class Project
    {
        [DataMember] public int Version = 1;
        [DataMember] public int Width = 1920, Height = 1080;
        [DataMember] public double Fps = 30;
        [DataMember] public int Background = unchecked((int)0xFF000000);
        [DataMember] public List<Track> Tracks = new List<Track>();
        [DataMember] public List<MediaRef> Media = new List<MediaRef>();
        [DataMember] public int NextId = 1;
        // New in 3.0
        [DataMember] public double MarkIn, MarkOut;                        // the marked range; MarkOut at or before MarkIn means none
        [DataMember] public List<Marker> Markers = new List<Marker>();

        public bool HasRange { get { return MarkOut > MarkIn + 0.01; } }

        public Project()
        {
            Tracks.Add(new Track { Id = NewId(), Kind = TrackKind.Video });
        }

        public int NewId() { return NextId++; }

        public Project Clone()
        {
            var p = (Project)MemberwiseClone();
            p.Tracks = new List<Track>();
            foreach (var t in Tracks) p.Tracks.Add(t.Clone());
            p.Media = new List<MediaRef>();
            foreach (var m in Media) p.Media.Add(new MediaRef { Id = m.Id, Path = m.Path, Name = m.Name });
            p.Markers = new List<Marker>();
            foreach (var m in Markers) p.Markers.Add(new Marker { T = m.T, Name = m.Name });
            return p;
        }

        [OnDeserialized]
        void Fix(StreamingContext ctx)
        {
            if (Tracks == null) Tracks = new List<Track>();
            if (Media == null) Media = new List<MediaRef>();
            if (Markers == null) Markers = new List<Marker>();
            if (Tracks.Count == 0 || Tracks[0].Kind != TrackKind.Video) Tracks.Insert(0, new Track { Id = NextId++, Kind = TrackKind.Video });
            if (Width < 16 || Height < 16) { Width = 1920; Height = 1080; }
            if (Fps < 1 || Fps > 120) Fps = 30;
        }

        public Track Main { get { return Tracks[0]; } }

        public double Duration
        {
            get
            {
                double d = 0;
                foreach (var t in Tracks) d = Math.Max(d, t.End);
                return d;
            }
        }

        public bool IsEmpty
        {
            get
            {
                foreach (var t in Tracks) if (t.Clips.Count > 0) return false;
                return true;
            }
        }

        public int ClipCount
        {
            get
            {
                int n = 0;
                foreach (var t in Tracks) n += t.Clips.Count;
                return n;
            }
        }

        public IEnumerable<Clip> AllClips()
        {
            foreach (var t in Tracks) foreach (var c in t.Clips) yield return c;
        }

        public Track TrackOf(Clip c)
        {
            foreach (var t in Tracks) if (t.Clips.Contains(c)) return t;
            return null;
        }

        public Clip FindClip(int id)
        {
            foreach (var t in Tracks) foreach (var c in t.Clips) if (c.Id == id) return c;
            return null;
        }

        public Track FindTrack(int id)
        {
            foreach (var t in Tracks) if (t.Id == id) return t;
            return null;
        }

        public bool UsesMedia(int mediaId)
        {
            foreach (var c in AllClips()) if (c.MediaId == mediaId && c.Kind != ClipKind.Text) return true;
            return false;
        }

        public MediaRef FindMedia(int id) { return Media.Find(m => m.Id == id); }

        public double FrameDuration { get { return 1.0 / Math.Max(1, Fps); } }

        public double Snap(double t) { double f = Math.Max(1, Fps); return Math.Round(t * f) / f; }

        // ------------------------------------------------------------------ saving

        static readonly DataContractSerializer serializer = new DataContractSerializer(typeof(Project));

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = XmlWriter.Create(fs, settings))
                serializer.WriteObject(w, this);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static Project Load(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var r = XmlReader.Create(fs, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                var p = serializer.ReadObject(r) as Project;
                if (p == null) throw new InvalidDataException("This isn't a Stills & Reels video project.");
                return p;
            }
        }
    }

    /// <summary>Named looks ("filters") as colour matrices, plus the transitions offered by the editor.</summary>
    internal static class Looks
    {
        public static readonly string[] FilterIds =
        {
            "mono", "noir", "sepia", "vintage", "warm", "cool", "vivid", "fade", "cinematic", "dramatic",
            "tealorange", "golden", "sunset", "arctic", "forest", "rose", "pastel", "matte", "bleach", "punch", "technicolor", "night", "invert",
        };

        public static string FilterName(string id)
        {
            switch (id)
            {
                case "mono": return "Mono";
                case "noir": return "Noir";
                case "sepia": return "Sepia";
                case "vintage": return "Vintage";
                case "warm": return "Warm";
                case "cool": return "Cool";
                case "vivid": return "Vivid";
                case "fade": return "Faded";
                case "cinematic": return "Cinematic";
                case "dramatic": return "Dramatic";
                case "invert": return "Invert";
                case "tealorange": return "Teal & orange";
                case "golden": return "Golden hour";
                case "sunset": return "Sunset";
                case "arctic": return "Arctic";
                case "forest": return "Forest";
                case "rose": return "Rose";
                case "pastel": return "Pastel";
                case "matte": return "Matte";
                case "bleach": return "Bleach";
                case "punch": return "Punch";
                case "technicolor": return "Technicolor";
                case "night": return "Night";
                default: return "None";
            }
        }

        public static readonly string[] MotionIds = { "zoomin", "zoomout", "panleft", "panright", "panup", "pandown", "float", "swing", "pulse", "shake", "spin" };

        public static string MotionName(string id)
        {
            switch (id)
            {
                case "zoomin": return "Slow zoom in";
                case "zoomout": return "Slow zoom out";
                case "panleft": return "Pan left";
                case "panright": return "Pan right";
                case "panup": return "Pan up";
                case "pandown": return "Pan down";
                case "float": return "Float";
                case "swing": return "Swing";
                case "pulse": return "Pulse";
                case "shake": return "Shake";
                case "spin": return "Spin";
                default: return "None";
            }
        }

        /// <summary>Ways a clip can arrive (and, played backwards, leave). "type" is for text only.</summary>
        public static readonly string[] AnimIds = { "fade", "slideup", "slidedown", "slideleft", "slideright", "pop", "grow", "shrink", "spin", "drop", "type" };

        public static string AnimName(string id, bool leaving)
        {
            switch (id)
            {
                case "fade": return "Fade";
                case "slideup": return leaving ? "Slide down" : "Slide up";
                case "slidedown": return leaving ? "Slide up" : "Slide down";
                case "slideleft": return leaving ? "Slide right" : "Slide left";
                case "slideright": return leaving ? "Slide left" : "Slide right";
                case "pop": return "Pop";
                case "grow": return leaving ? "Shrink away" : "Grow";
                case "shrink": return leaving ? "Grow away" : "Shrink";
                case "spin": return "Spin";
                case "drop": return leaving ? "Fly up" : "Drop";
                case "type": return leaving ? "Untype" : "Typewriter";
                default: return "None";
            }
        }

        public static readonly string[] BlendIds = { "screen", "multiply", "overlay", "softlight", "hardlight", "add", "darken", "lighten", "difference" };

        public static string BlendName(string id)
        {
            switch (id)
            {
                case "screen": return "Screen";
                case "multiply": return "Multiply";
                case "overlay": return "Overlay";
                case "softlight": return "Soft light";
                case "hardlight": return "Hard light";
                case "add": return "Add";
                case "darken": return "Darken";
                case "lighten": return "Lighten";
                case "difference": return "Difference";
                default: return "Normal";
            }
        }

        public static Blend BlendMode(string id)
        {
            switch (id)
            {
                case "screen": return StillsAndReels.Blend.Screen;
                case "multiply": return StillsAndReels.Blend.Multiply;
                case "overlay": return StillsAndReels.Blend.Overlay;
                case "softlight": return StillsAndReels.Blend.SoftLight;
                case "hardlight": return StillsAndReels.Blend.HardLight;
                case "add": return StillsAndReels.Blend.Add;
                case "darken": return StillsAndReels.Blend.Darken;
                case "lighten": return StillsAndReels.Blend.Lighten;
                case "difference": return StillsAndReels.Blend.Difference;
                default: return StillsAndReels.Blend.Normal;
            }
        }

        public static readonly string[] TransitionIds =
        {
            "dissolve", "black", "white", "blur", "wipeleft", "wiperight", "wipeup", "wipedown", "softleft", "softright", "diagonal",
            "slideleft", "slideright", "slideup", "slidedown", "coverleft", "coverright", "coverup", "coverdown",
            "circle", "diamond", "clock", "barnh", "barnv", "blinds", "zoom", "zoomout", "spin", "pixelate", "glitch",
        };

        public static string TransitionName(string id)
        {
            switch (id)
            {
                case "dissolve": return "Dissolve";
                case "black": return "Fade to black";
                case "white": return "Fade to white";
                case "wipeleft": return "Wipe left";
                case "wiperight": return "Wipe right";
                case "wipeup": return "Wipe up";
                case "wipedown": return "Wipe down";
                case "slideleft": return "Slide left";
                case "slideright": return "Slide right";
                case "slideup": return "Slide up";
                case "slidedown": return "Slide down";
                case "circle": return "Circle";
                case "zoom": return "Zoom";
                case "blur": return "Blur";
                case "softleft": return "Soft wipe left";
                case "softright": return "Soft wipe right";
                case "diagonal": return "Diagonal wipe";
                case "coverleft": return "Cover left";
                case "coverright": return "Cover right";
                case "coverup": return "Cover up";
                case "coverdown": return "Cover down";
                case "diamond": return "Diamond";
                case "clock": return "Clock";
                case "barnh": return "Doors";
                case "barnv": return "Shutters";
                case "blinds": return "Blinds";
                case "zoomout": return "Zoom out";
                case "spin": return "Spin";
                case "pixelate": return "Pixelate";
                case "glitch": return "Glitch";
                default: return "None";
            }
        }
    }
}
