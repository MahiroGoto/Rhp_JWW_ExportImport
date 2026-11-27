// JWW (Jw_cad for Windows) reader.
// Layout: LibreCAD jwwlib (jwwdoc.cpp Read / jwwdoc.h Serialize), MFC CArchive serialisation,
// checked against real Jw_cad 7.00 files. Coordinates are paper mm (図寸); the layer group scale
// gives the real size. Strings are Shift-JIS, or UTF-16 when MFC writes the 0xFF 0xFFFE marker.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jww
{
    public class JwEntity
    {
        public int Group, Layer, Color, Style, PenWidth, Flags;
        public uint CurveAttr;
    }
    public class JwLine : JwEntity { public double X1, Y1, X2, Y2; }
    public class JwArc : JwEntity { public double X, Y, R, Start, Sweep, Tilt, Flat; public bool Full; }
    public class JwPoint : JwEntity { public double X, Y, Rotation, Scale; public bool Temporary; public uint Code; }
    public class JwText : JwEntity
    {
        public double X1, Y1, X2, Y2, SizeX, SizeY, Spacing, Angle;
        public uint Kind;                     // 文字種 (+10000 italic, +20000 bold)
        public string Font, Value;
    }
    public class JwDimension : JwEntity
    {
        public JwLine Line; public JwText Text;
        public List<JwLine> ExtraLines = new List<JwLine>();
        public List<JwPoint> ExtraPoints = new List<JwPoint>();
    }
    public class JwSolid : JwEntity { public double X1, Y1, X2, Y2, X3, Y3, X4, Y4; public uint Rgb; }
    public class JwBlockRef : JwEntity { public double X, Y, ScaleX, ScaleY, Rotation; public uint Number; }
    public class JwBlockDef : JwEntity { public uint Number, Referenced, Time; public string Name; public List<JwEntity> Members = new List<JwEntity>(); }

    public class JwwDocument
    {
        public uint Version;
        public string Memo = "";
        public int Paper;
        public int WriteGroup;
        public int[] GroupState = new int[16];
        public double[] GroupScale = new double[16];
        public string[] GroupName = new string[16];
        public int[,] LayerState = new int[16, 16];
        public string[,] LayerName = new string[16, 16];
        public uint[] PenColor = new uint[10], PenWidth = new uint[10];          // screen colour (COLORREF), dots
        public uint[] PrtColor = new uint[10], PrtWidth = new uint[10];          // print colour, width
        public uint[] SxfColor = new uint[257], SxfPrtWidth = new uint[257];      // pen colours 100..356
        public List<JwEntity> Entities = new List<JwEntity>();
        public Dictionary<uint, JwBlockDef> Blocks = new Dictionary<uint, JwBlockDef>();
        public long EndOfData, FileLength;
        public List<string> Warnings = new List<string>();

        public static string PaperName(int code)
        {
            string[] a = { "A0", "A1", "A2", "A3", "A4" };
            if (code >= 0 && code < 5) return a[code];
            if (code >= 8 && code <= 11) return (code - 6) + "A";
            return "code " + code;
        }

        /// <summary>RGB of a JWW pen colour (1-9, SXF 100+), screen colours; near-white becomes black.</summary>
        public int[] Rgb(int pen, uint rgbIfSolid)
        {
            uint c;
            if (pen == 10) c = rgbIfSolid;
            else if (pen >= 100 && pen - 100 < 257) c = SxfColor[pen - 100];
            else if (pen >= 0 && pen < 10) c = PenColor[pen];
            else c = 0;
            int r = (int)(c & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)((c >> 16) & 0xFF);
            if (r > 230 && g > 230 && b > 230) { r = g = b = 0; }
            return new[] { r, g, b };
        }

        public double PrintWidthMm(int pen)
        {
            uint w = 0;
            if (pen >= 100 && pen - 100 < 257) w = SxfPrtWidth[pen - 100];
            else if (pen >= 0 && pen < 10) w = PrtWidth[pen];
            return w / 100.0;
        }
    }

    public class JwwReader
    {
        static Encoding s_sjis;
        public static Encoding Sjis
        {
            get
            {
                if (s_sjis != null) return s_sjis;
                try { s_sjis = Encoding.GetEncoding(932); }
                catch
                {
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

        BinaryReader r;
        JwwDocument doc;
        uint ver;
        readonly Dictionary<int, string> classes = new Dictionary<int, string>();
        readonly Dictionary<int, JwEntity> objects = new Dictionary<int, JwEntity>();
        int index = 1;

        public static JwwDocument Read(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                var rd = new JwwReader();
                rd.r = br;
                rd.doc = new JwwDocument();
                rd.doc.FileLength = fs.Length;
                rd.ReadAll();
                rd.doc.EndOfData = fs.Position;
                return rd.doc;
            }
        }

        uint DW() { return r.ReadUInt32(); }
        double D() { return r.ReadDouble(); }
        ushort W() { return r.ReadUInt16(); }
        byte B() { return r.ReadByte(); }

        string Str()
        {
            bool unicode = false;
            long n = B();
            if (n == 0xFF)
            {
                n = W();
                if (n == 0xFFFE)
                {
                    unicode = true;
                    n = B();
                    if (n == 0xFF) { n = W(); if (n == 0xFFFF) n = DW(); }
                }
                else if (n == 0xFFFF) n = DW();
            }
            if (n == 0) return "";
            if (unicode) return Encoding.Unicode.GetString(r.ReadBytes((int)n * 2));
            return Sjis.GetString(r.ReadBytes((int)n));
        }

        uint Count()
        {
            uint n = W();
            if (n == 0xFFFF) n = DW();
            return n;
        }

        void ReadAll()
        {
            byte[] head = r.ReadBytes(8);
            if (Encoding.ASCII.GetString(head) != "JwwData.") throw new InvalidDataException("Not a JWW file");
            Header();
            uint n = Count();
            for (uint i = 0; i < n; i++)
            {
                JwEntity e = ReadObject();
                if (e != null && !(e is JwBlockDef)) doc.Entities.Add(e);
            }
            // block definitions
            if (r.BaseStream.Position >= r.BaseStream.Length) return;
            try
            {
                uint nb = Count();
                for (uint i = 0; i < nb; i++)
                {
                    var def = ReadObject() as JwBlockDef;
                    if (def == null) throw new InvalidDataException("block definition expected");
                    uint m = Count();
                    for (uint k = 0; k < m; k++)
                    {
                        JwEntity e = ReadObject();
                        if (e != null) def.Members.Add(e);
                    }
                    doc.Blocks[def.Number] = def;
                }
            }
            catch (Exception ex)
            {
                doc.Warnings.Add("Block definitions could not be read completely: " + ex.Message);
            }
        }

        void Header()
        {
            ver = DW(); doc.Version = ver;
            doc.Memo = Str();
            doc.Paper = (int)DW();
            doc.WriteGroup = (int)DW();
            for (int g = 0; g < 16; g++)
            {
                doc.GroupState[g] = (int)DW(); DW(); doc.GroupScale[g] = D(); DW();
                for (int l = 0; l < 16; l++) { doc.LayerState[g, l] = (int)DW(); DW(); }
            }
            for (int i = 0; i < 14; i++) DW();
            for (int i = 0; i < 5; i++) DW();
            DW(); DW();
            D(); D(); D(); DW(); DW();
            for (int i = 0; i < 5; i++) D();
            for (int g = 0; g < 16; g++) for (int l = 0; l < 16; l++) doc.LayerName[g, l] = Str();
            for (int g = 0; g < 16; g++) doc.GroupName[g] = Str();
            D(); D(); DW(); D();
            if (ver >= 300) { D(); D(); }
            DW();
            D(); D(); D();
            D(); D(); D();
            if (ver >= 300) { for (int i = 0; i < 8; i++) { D(); D(); D(); DW(); } }
            else { for (int i = 0; i < 4; i++) { D(); D(); D(); } }
            if (ver >= 300) { D(); D(); D(); DW(); D(); D(); D(); DW(); }
            for (int i = 0; i < 10; i++) D();
            D();
            for (int i = 0; i < 10; i++) { doc.PenColor[i] = DW(); doc.PenWidth[i] = DW(); }
            for (int i = 0; i < 10; i++) { doc.PrtColor[i] = DW(); doc.PrtWidth[i] = DW(); D(); }
            for (int i = 2; i <= 9; i++) { DW(); DW(); DW(); DW(); }
            for (int i = 11; i <= 15; i++) { DW(); DW(); DW(); DW(); DW(); }
            for (int i = 16; i <= 19; i++) { DW(); DW(); DW(); DW(); }
            for (int i = 0; i < 11; i++) DW();
            DW(); DW(); DW(); DW(); DW();
            for (int i = 0; i < 9; i++) D();
            DW(); DW();
            if (ver >= 420)
            {
                for (int n = 0; n < 257; n++) { doc.SxfColor[n] = DW(); DW(); }
                for (int n = 0; n < 257; n++) { Str(); DW(); doc.SxfPrtWidth[n] = DW(); D(); }
                for (int n = 0; n < 33; n++) { DW(); DW(); DW(); DW(); }
                for (int n = 0; n < 33; n++) { Str(); DW(); for (int j = 0; j < 10; j++) D(); }
            }
            for (int i = 0; i < 10; i++) { D(); D(); D(); DW(); }
            D(); D(); D(); DW(); DW();
            D(); D(); DW();
            for (int i = 0; i < 6; i++) D();
        }

        void Base(JwEntity e)
        {
            e.CurveAttr = DW();
            e.Style = B();
            e.Color = W();
            if (ver >= 351) e.PenWidth = W();
            e.Layer = W();
            e.Group = W();
            e.Flags = W();
        }

        JwEntity ReadObject()
        {
            ushort tag = W();
            string name;
            if (tag == 0xFFFF)
            {
                W();                                    // schema
                ushort len = W();
                name = Encoding.ASCII.GetString(r.ReadBytes(len));
                classes[index++] = name;
            }
            else if (tag == 0x7FFF)
            {
                uint big = DW();
                if ((big & 0x80000000) == 0) { JwEntity o; objects.TryGetValue((int)big, out o); return o; }
                name = classes[(int)(big & 0x7FFFFFFF)];
            }
            else if ((tag & 0x8000) != 0)
            {
                name = classes[tag & 0x7FFF];
            }
            else
            {
                JwEntity o; objects.TryGetValue(tag, out o); return o;   // reference to an object already read
            }
            int myIndex = index++;
            JwEntity e = ReadBody(name);
            objects[myIndex] = e;
            return e;
        }

        JwLine ReadLine() { var e = new JwLine(); Base(e); e.X1 = D(); e.Y1 = D(); e.X2 = D(); e.Y2 = D(); return e; }

        JwPoint ReadPoint()
        {
            var e = new JwPoint(); Base(e);
            e.X = D(); e.Y = D(); e.Temporary = DW() != 0; e.Scale = 1;
            if (e.Style == 100) { e.Code = DW(); e.Rotation = D(); e.Scale = D(); }
            return e;
        }

        JwText ReadText()
        {
            var e = new JwText(); Base(e);
            e.X1 = D(); e.Y1 = D(); e.X2 = D(); e.Y2 = D(); e.Kind = DW();
            e.SizeX = D(); e.SizeY = D(); e.Spacing = D(); e.Angle = D();
            e.Font = Str(); e.Value = Str();
            return e;
        }

        JwEntity ReadBody(string name)
        {
            switch (name)
            {
                case "CDataSen": return ReadLine();
                case "CDataEnko":
                    {
                        var e = new JwArc(); Base(e);
                        e.X = D(); e.Y = D(); e.R = D(); e.Start = D(); e.Sweep = D(); e.Tilt = D(); e.Flat = D(); e.Full = DW() != 0;
                        return e;
                    }
                case "CDataTen": return ReadPoint();
                case "CDataMoji": return ReadText();
                case "CDataSunpou":
                    {
                        var e = new JwDimension(); Base(e);
                        e.Line = ReadLine();
                        e.Text = ReadText();
                        if (ver >= 420)
                        {
                            W();                                // SXF mode
                            e.ExtraLines.Add(ReadLine()); e.ExtraLines.Add(ReadLine());
                            for (int i = 0; i < 4; i++) e.ExtraPoints.Add(ReadPoint());
                        }
                        return e;
                    }
                case "CDataSolid":
                    {
                        var e = new JwSolid(); Base(e);
                        // stored order: point 1, point 4, point 2, point 3
                        e.X1 = D(); e.Y1 = D(); e.X4 = D(); e.Y4 = D(); e.X2 = D(); e.Y2 = D(); e.X3 = D(); e.Y3 = D();
                        if (e.Color == 10) e.Rgb = DW();
                        return e;
                    }
                case "CDataBlock":
                    {
                        var e = new JwBlockRef(); Base(e);
                        e.X = D(); e.Y = D(); e.ScaleX = D(); e.ScaleY = D(); e.Rotation = D(); e.Number = DW();
                        return e;
                    }
                case "CDataList":
                    {
                        var e = new JwBlockDef(); Base(e);
                        e.Number = DW(); e.Referenced = DW(); e.Time = DW(); e.Name = Str();
                        return e;
                    }
                default:
                    throw new InvalidDataException("Unknown JWW object class '" + name + "' at byte " + r.BaseStream.Position);
            }
        }
    }
}
