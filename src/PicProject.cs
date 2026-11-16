using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace StillsAndReels
{
    /// <summary>
    /// The layered picture file (.srpic): every layer with its pixels, shapes and text, so editing can go on later.
    /// One compressed stream; nothing else is needed to read it back.
    /// </summary>
    internal static class PicProject
    {
        public const string Extension = ".srpic";
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("SRPIC\n");
        const int Version = 1;

        public static bool IsProject(string path)
        {
            return path != null && string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);
        }

        public static void Save(PicState state, string path)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                fs.Write(Magic, 0, Magic.Length);
                using (var z = new DeflateStream(fs, CompressionLevel.Fastest))
                using (var w = new BinaryWriter(z, Encoding.UTF8))
                {
                    w.Write(Version);
                    w.Write(state.Size.Width); w.Write(state.Size.Height);
                    w.Write(state.Layers.Length);
                    foreach (var l in state.Layers) WriteLayer(w, l);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static PicState Load(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var head = new byte[Magic.Length];
                if (fs.Read(head, 0, head.Length) != head.Length) throw new InvalidDataException("This is not a Stills & Reels picture.");
                for (int i = 0; i < head.Length; i++) if (head[i] != Magic[i]) throw new InvalidDataException("This is not a Stills & Reels picture.");
                using (var z = new DeflateStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(z, Encoding.UTF8))
                {
                    int version = r.ReadInt32();
                    if (version < 1 || version > Version) throw new InvalidDataException("This picture was made by a newer version of Stills & Reels.");
                    int w = r.ReadInt32(), h = r.ReadInt32();
                    if (w < 1 || h < 1 || (long)w * h > PicSurface.MaxPixels) throw new InvalidDataException("The picture's size is not valid.");
                    int n = r.ReadInt32();
                    if (n < 1 || n > 10000) throw new InvalidDataException("The picture's layers are not valid.");
                    var layers = new List<PicLayer>();
                    try
                    {
                        for (int i = 0; i < n; i++) layers.Add(ReadLayer(r));
                    }
                    catch
                    {
                        foreach (var l in layers) if (l.Pixels != null) l.Pixels.Dispose();
                        throw;
                    }
                    return new PicState(new Size(w, h), layers.ToArray());
                }
            }
        }

        static void WriteColor(BinaryWriter w, Color c) { w.Write(c.IsEmpty ? 0 : c.ToArgb()); w.Write(!c.IsEmpty); }
        static Color ReadColor(BinaryReader r) { int v = r.ReadInt32(); return r.ReadBoolean() ? Color.FromArgb(v) : Color.Empty; }
        static void WritePoint(BinaryWriter w, PointF p) { w.Write(p.X); w.Write(p.Y); }
        static PointF ReadPoint(BinaryReader r) { float x = r.ReadSingle(); return new PointF(x, r.ReadSingle()); }

        static unsafe void WriteLayer(BinaryWriter w, PicLayer l)
        {
            w.Write(l.Name ?? "");
            w.Write(l.Visible);
            w.Write(l.Opacity);
            w.Write((int)l.Blend);
            w.Write(l.X); w.Write(l.Y);
            if (l.Pixels == null) { w.Write(0); w.Write(0); }
            else
            {
                w.Write(l.Pixels.Width); w.Write(l.Pixels.Height);
                var row = new byte[l.Pixels.Width * 4];
                for (int y = 0; y < l.Pixels.Height; y++)
                {
                    Marshal.Copy((IntPtr)l.Pixels.Row(y), row, 0, row.Length);
                    w.Write(row);
                }
            }
            w.Write(l.Objects.Length);
            foreach (var o in l.Objects) WriteObject(w, o);
        }

        static unsafe PicLayer ReadLayer(BinaryReader r)
        {
            string name = r.ReadString();
            bool visible = r.ReadBoolean();
            float opacity = Math.Max(0f, Math.Min(1f, r.ReadSingle()));
            int blend = r.ReadInt32();
            int x = r.ReadInt32(), y = r.ReadInt32();
            int pw = r.ReadInt32(), ph = r.ReadInt32();
            PicSurface px = null;
            try
            {
                if (pw > 0 && ph > 0)
                {
                    if ((long)pw * ph > PicSurface.MaxPixels) throw new InvalidDataException("A layer's size is not valid.");
                    px = new PicSurface(pw, ph);
                    int rowBytes = pw * 4;
                    for (int yy = 0; yy < ph; yy++)
                    {
                        var row = r.ReadBytes(rowBytes);
                        if (row.Length != rowBytes) throw new EndOfStreamException("The picture file is cut short.");
                        Marshal.Copy(row, 0, (IntPtr)px.Row(yy), rowBytes);
                    }
                }
                int n = r.ReadInt32();
                if (n < 0 || n > 1000000) throw new InvalidDataException("A layer's drawings are not valid.");
                var objs = new PicObject[n];
                for (int i = 0; i < n; i++) objs[i] = ReadObject(r);
                var layer = PicLayer.Create(name, px, x, y).WithVisible(visible).WithOpacity(opacity).WithObjects(objs);
                if (Enum.IsDefined(typeof(PicBlend), blend)) layer = layer.WithBlend((PicBlend)blend);
                return layer;
            }
            catch
            {
                if (px != null) px.Dispose();
                throw;
            }
        }

        static void WriteObject(BinaryWriter w, PicObject o)
        {
            var s = o as Stroke;
            if (s != null)
            {
                w.Write((byte)1);
                w.Write((int)s.Tool); WriteColor(w, s.Color); w.Write(s.Width);
                w.Write(s.Points.Length);
                foreach (var p in s.Points) WritePoint(w, p);
                return;
            }
            var sh = o as ShapeObject;
            if (sh != null)
            {
                w.Write((byte)2);
                w.Write((int)sh.Kind); WritePoint(w, sh.A); WritePoint(w, sh.B); WriteColor(w, sh.Color); w.Write(sh.Width); WriteColor(w, sh.Fill);
                return;
            }
            var t = o as TextObject;
            if (t != null)
            {
                w.Write((byte)3);
                w.Write(t.Text ?? ""); w.Write(t.FontName ?? ""); w.Write(t.Size); w.Write(t.Bold); w.Write(t.Italic);
                WritePoint(w, t.Location); WriteColor(w, t.Color); WriteColor(w, t.Fill);
                return;
            }
            var st = o as StepObject;
            if (st != null)
            {
                w.Write((byte)4);
                WritePoint(w, st.Center); w.Write(st.Radius); w.Write(st.Number); WriteColor(w, st.Color);
                return;
            }
            throw new NotSupportedException("Unknown drawing.");
        }

        static PicObject ReadObject(BinaryReader r)
        {
            switch (r.ReadByte())
            {
                case 1:
                    {
                        int tool = r.ReadInt32(); Color c = ReadColor(r); float width = r.ReadSingle();
                        int n = r.ReadInt32();
                        if (n < 0 || n > 10000000) throw new InvalidDataException("A stroke is not valid.");
                        var pts = new PointF[n];
                        for (int i = 0; i < n; i++) pts[i] = ReadPoint(r);
                        return new Stroke((EditTool)tool, c, width, pts);
                    }
                case 2:
                    {
                        int kind = r.ReadInt32(); var a = ReadPoint(r); var b = ReadPoint(r); Color c = ReadColor(r); float width = r.ReadSingle(); Color fill = ReadColor(r);
                        return new ShapeObject((ShapeKind)kind, a, b, c, width, fill);
                    }
                case 3:
                    {
                        string text = r.ReadString(), font = r.ReadString(); float size = r.ReadSingle(); bool bold = r.ReadBoolean(), italic = r.ReadBoolean();
                        var at = ReadPoint(r); Color c = ReadColor(r); Color back = ReadColor(r);
                        return new TextObject(text, font, size, bold, italic, at, c, back);
                    }
                case 4:
                    {
                        var at = ReadPoint(r); float radius = r.ReadSingle(); int number = r.ReadInt32(); Color c = ReadColor(r);
                        return new StepObject(at, radius, number, c);
                    }
                default:
                    throw new InvalidDataException("The picture has a drawing this version doesn't know.");
            }
        }
    }
}
