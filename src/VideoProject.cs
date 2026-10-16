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
        public Keyframe Clone() { return (Keyframe)MemberwiseClone(); }
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

        // ---- sound
        [DataMember] public double Volume = 1;
        [DataMember] public bool Muted;
        [DataMember] public double AudioFadeIn, AudioFadeOut;

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
            return c;
        }

        [OnDeserialized]
        void Fix(StreamingContext ctx)
        {
            if (Keys == null) Keys = new List<Keyframe>();
            if (Filter == null) Filter = "";
            if (Transition == null) Transition = "";
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
                    if (b.Ease) u = u * u * (3 - 2 * u);
                    p.X = a.X + (b.X - a.X) * u;
                    p.Y = a.Y + (b.Y - a.Y) * u;
                    p.Scale = a.Scale + (b.Scale - a.Scale) * u;
                    p.Rotation = a.Rotation + (b.Rotation - a.Rotation) * u;
                    p.Opacity = a.Opacity + (b.Opacity - a.Opacity) * u;
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
                return Math.Max(0.05, m);
            }
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
                       (!string.IsNullOrEmpty(Filter) && FilterAmount > 0.001);
            }
        }

        public bool HasCrop { get { return CropL > 0.0005 || CropT > 0.0005 || CropR > 0.0005 || CropB > 0.0005; } }
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
            return p;
        }

        [OnDeserialized]
        void Fix(StreamingContext ctx)
        {
            if (Tracks == null) Tracks = new List<Track>();
            if (Media == null) Media = new List<MediaRef>();
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
        public static readonly string[] FilterIds = { "mono", "noir", "sepia", "vintage", "warm", "cool", "vivid", "fade", "cinematic", "dramatic", "invert" };

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
                default: return "None";
            }
        }

        public static readonly string[] TransitionIds = { "dissolve", "black", "white", "wipeleft", "wiperight", "wipeup", "wipedown", "slideleft", "slideright", "slideup", "slidedown", "circle", "zoom" };

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
                default: return "None";
            }
        }
    }
}
