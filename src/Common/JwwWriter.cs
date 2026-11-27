// JWW (Jw_cad for Windows) writer. Layout: LibreCAD jwwlib, MFC CArchive, version 600,
// coordinates in paper mm around the paper centre, Shift-JIS strings.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jww
{
    // ------------------------------------------------------------------ JWW writer
    public class JwwFile
    {
        public const uint Version = 600;
        public class Line { public int Layer, Group, Color, Style; public double X1, Y1, X2, Y2; }
        public class Arc { public int Layer, Group, Color, Style; public double X, Y, R, Start, Sweep; public bool Full; }
        public class Pt { public int Layer, Group, Color; public double X, Y; }
        public class Text { public int Layer, Group, Color; public double X1, Y1, X2, Y2, Size, Angle; public string Font, Value; }

        public List<Line> Lines = new List<Line>();
        public List<Arc> Arcs = new List<Arc>();
        public List<Pt> Points = new List<Pt>();
        public List<Text> Texts = new List<Text>();
        public string Memo = "";
        public int Paper = 3;                                   // 0=A0 .. 4=A4
        public double[] GroupScale = new double[16];
        public string[] GroupName = new string[16];
        public string[,] LayerName = new string[16, 16];

        static Encoding s_sjis;
        public static Encoding Sjis
        {
            get
            {
                if (s_sjis != null) return s_sjis;
                try { s_sjis = Encoding.GetEncoding(932); }
                catch
                {
                    // .NET (Core): register the code-pages provider first
                    Type t = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                    if (t != null)
                    {
                        object prov = t.GetProperty("Instance").GetValue(null, null);
                        typeof(Encoding).GetMethod("RegisterProvider").Invoke(null, new[] { prov });
                    }
                    s_sjis = Encoding.GetEncoding(932);
                }
                return s_sjis;
            }
        }

        public static double TextUnits(string s) { return Sjis.GetByteCount(s) / 2.0; }

        BinaryWriter w;
        Dictionary<string, int> classes;
        int index;

        public JwwFile() { for (int i = 0; i < 16; i++) { GroupScale[i] = 100.0; GroupName[i] = ""; } }

        void D(double v) { w.Write(v); }
        void U(uint v) { w.Write(v); }
        void S(string s)
        {
            byte[] b = string.IsNullOrEmpty(s) ? new byte[0] : Sjis.GetBytes(s);
            if (b.Length == 0) { w.Write((byte)0); return; }
            if (b.Length >= 0xFF) { w.Write((byte)0xFF); w.Write((ushort)b.Length); }
            else w.Write((byte)b.Length);
            w.Write(b);
        }
        static uint Rgb(int r, int g, int b) { return (uint)((b << 16) | (g << 8) | r); }

        void Header()
        {
            w.Write(Encoding.ASCII.GetBytes("JwwData."));
            U(Version);
            S(Memo);
            U((uint)Paper);
            U(0);                                               // write layer group
            for (int g = 0; g < 16; g++)
            {
                U(2); U(0); D(GroupScale[g]); U(0);
                for (int l = 0; l < 16; l++) { U(2); U(0); }
            }
            for (int i = 0; i < 14; i++) U(0);
            for (int i = 0; i < 5; i++) U(0);
            U(0); U(100);
            D(0); D(0); D(1); U(0); U(0);
            for (int i = 0; i < 5; i++) D(0);
            for (int g = 0; g < 16; g++) for (int l = 0; l < 16; l++) S(LayerName[g, l]);
            for (int g = 0; g < 16; g++) S(GroupName[g]);
            D(0); D(35); U(0); D(0);
            D(0); D(0);
            U(1);
            D(1); D(0); D(0);
            D(1); D(0); D(0);
            for (int i = 0; i < 8; i++) { D(1); D(0); D(0); U(0); }
            D(0); D(0); D(0); U(0); D(0); D(0); D(0); U(0);
            for (int i = 0; i < 10; i++) D(0);
            D(0);
            uint[] col = { Rgb(0, 0, 0), Rgb(0, 0, 0), Rgb(0, 0, 0), Rgb(0, 0, 0), Rgb(255, 0, 0), Rgb(0, 0, 255), Rgb(0, 160, 0), Rgb(255, 0, 255), Rgb(128, 128, 128), Rgb(0, 160, 160) };
            uint[] dot = { 1, 1, 1, 2, 1, 1, 1, 1, 1, 1 };
            uint[] prt = { 13, 13, 18, 35, 18, 18, 18, 18, 13, 13 };
            for (int i = 0; i < 10; i++) { U(col[i]); U(dot[i]); }
            for (int i = 0; i < 10; i++) { U(col[i]); U(prt[i]); D(0.3); }
            uint[] pat = { 0, 0, 0xCCCCCCCC, 0xF0F0F0F0, 0xFF00FF00, 0xFFFF1818, 0xFFF0F0FF, 0xFFFF1B1B, 0xFFF6F6FF, 0x88888888 };
            for (int i = 2; i <= 9; i++) { U(pat[i]); U(32); U(1); U(1); }
            for (int i = 11; i <= 15; i++) { U(0); U(0); U(0); U(0); U(0); }
            for (int i = 16; i <= 19; i++) { U(0xFFFF1818); U(64); U(1); U(1); }
            uint[] flags = { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0 };
            foreach (var f in flags) U(f);
            U(0); U(0); U(0); U(0); U(0);
            for (int i = 0; i < 9; i++) D(0);
            U(0); U(0);
            for (int n = 0; n < 257; n++) { U(0); U(1); }
            for (int n = 0; n < 257; n++) { S(""); U(0); U(1); D(0.3); }
            for (int n = 0; n < 33; n++) { U(0); U(32); U(1); U(1); }
            for (int n = 0; n < 33; n++) { S(""); U(0); for (int j = 0; j < 10; j++) D(0); }
            for (int i = 0; i < 10; i++) { double s = i < m_sizes.Count ? m_sizes[i] : 2.5; D(s); D(s); D(0); U(2); }
            D(2.5); D(2.5); D(0); U(2); U(1);
            D(0); D(0); U(0);
            for (int i = 0; i < 6; i++) D(0);
        }

        void Base(int layer, int group, int color, int style)
        {
            U(0); w.Write((byte)style); w.Write((ushort)color); w.Write((ushort)0);
            w.Write((ushort)layer); w.Write((ushort)group); w.Write((ushort)0);
        }

        void Tag(string name)
        {
            int i;
            if (!classes.TryGetValue(name, out i))
            {
                classes[name] = index++;
                w.Write((ushort)0xFFFF); w.Write((ushort)Version); w.Write((ushort)name.Length);
                w.Write(Encoding.ASCII.GetBytes(name));
            }
            else if (i < 0x7FFF) w.Write((ushort)(i | 0x8000));
            else { w.Write((ushort)0x7FFF); U((uint)i | 0x80000000); }
            index++;
        }

        List<double> m_sizes = new List<double>();

        public long Save(string path)
        {
            m_sizes.Clear();
            foreach (var t in Texts) if (!m_sizes.Contains(t.Size)) m_sizes.Add(t.Size);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (w = new BinaryWriter(fs))
            {
                classes = new Dictionary<string, int>(); index = 1;
                Header();
                int n = Lines.Count + Arcs.Count + Points.Count + Texts.Count;
                if (n < 0xFFFF) w.Write((ushort)n); else { w.Write((ushort)0xFFFF); U((uint)n); }
                foreach (var l in Lines) { Tag("CDataSen"); Base(l.Layer, l.Group, l.Color, l.Style); D(l.X1); D(l.Y1); D(l.X2); D(l.Y2); }
                foreach (var a in Arcs) { Tag("CDataEnko"); Base(a.Layer, a.Group, a.Color, a.Style); D(a.X); D(a.Y); D(a.R); D(a.Start); D(a.Sweep); D(0); D(1); U(a.Full ? 1u : 0u); }
                foreach (var p in Points) { Tag("CDataTen"); Base(p.Layer, p.Group, p.Color, 1); D(p.X); D(p.Y); U(0); }
                foreach (var t in Texts)
                {
                    Tag("CDataMoji"); Base(t.Layer, t.Group, t.Color, 1);
                    int k = m_sizes.IndexOf(t.Size); uint shu = (uint)(k >= 0 && k < 10 ? k + 1 : 0);
                    D(t.X1); D(t.Y1); D(t.X2); D(t.Y2); U(shu); D(t.Size); D(t.Size); D(0); D(t.Angle);
                    S(t.Font); S(t.Value);
                }
                w.Write((ushort)0);                              // block definitions: none
                w.Flush();
                return fs.Length;
            }
        }
    }
}
