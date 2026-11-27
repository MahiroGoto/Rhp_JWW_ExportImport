// Rhp_JWW_ExportImport - import part: Jw_cad (JWW) drawings into Rhino.
// C# 5 (builds with the .NET Framework csc.exe; see build.ps1).
//
// The plug-in class is a file importer: File > Import / Open offer "Jw_cad (*.jww)".
// Command ImportJww does the same with the option Size=Real|Paper.
// Real: JWW paper mm x layer-group scale (e.g. 1/100), converted to the model unit.
// Paper: 1:1 paper mm, so groups at different scales stay together as on the Jw_cad sheet.
// Layers: JWW <file> :: <group 0-F name> :: <group+layer 00-FF name>.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.PlugIns;
using Jww;

namespace JwwImport
{
    // The one plug-in class of Rhp_JWW_ExportImport.rhp (a file importer; export is the ExportJww command).
    public class RhpJwwPlugIn : FileImportPlugIn
    {
        public static RhpJwwPlugIn Instance { get; private set; }
        public RhpJwwPlugIn() { Instance = this; }

        protected override FileTypeList AddFileTypes(Rhino.FileIO.FileReadOptions options)
        {
            var list = new FileTypeList();
            list.AddFileType("Jw_cad (*.jww)", "jww", true);
            return list;
        }

        protected override bool ReadFile(string filename, int index, RhinoDoc doc, Rhino.FileIO.FileReadOptions options)
        {
            try
            {
                var im = new Importer(doc);
                RhinoApp.WriteLine(im.Import(filename, false));
                return true;
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine("JWW import failed: " + e.Message);
                return false;
            }
        }
    }

    [System.Runtime.InteropServices.Guid("e8b1c6f2-4d3a-4f7b-a2c9-5e6d7f8a9b01")]
    public class ImportJwwCommand : Command
    {
        public override string EnglishName { get { return "ImportJww"; } }
        static bool s_paperSize = false;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            string path = null;
            if (mode == RunMode.Interactive)
            {
                var dlg = new Rhino.UI.OpenFileDialog();
                dlg.Filter = "Jw_cad (*.jww)|*.jww";
                dlg.Title = "Import JWW";
                if (!dlg.ShowOpenDialog()) return Result.Cancel;
                path = dlg.FileName;
            }
            else
            {
                path = "";
                if (RhinoGet.GetString("JWW file path", false, ref path) != Result.Success) return Result.Cancel;
                path = path.Trim().Trim('"');
            }
            var go = new GetOption();
            go.SetCommandPrompt("Size");
            var opt = new OptionToggle(s_paperSize, "Real", "Paper");
            go.AddOptionToggle("Size", ref opt);
            go.AcceptNothing(true);
            while (true)
            {
                GetResult res = go.Get();
                if (res == GetResult.Option) continue;
                if (res == GetResult.Cancel) return Result.Cancel;
                break;
            }
            s_paperSize = opt.CurrentValue;

            uint undo = doc.BeginUndoRecord("ImportJww");
            try
            {
                RhinoApp.WriteLine(new Importer(doc).Import(path, s_paperSize));
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine("JWW import failed: " + e.Message);
                return Result.Failure;
            }
            finally { doc.EndUndoRecord(undo); }
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    public class Importer
    {
        readonly RhinoDoc m_doc;
        JwwDocument m_jw;
        double m_unit;                 // mm -> model unit
        bool m_paper;                  // true: 1:1 paper mm instead of real size
        string m_stem;
        int m_root = -1;
        readonly Dictionary<int, int> m_groupLayers = new Dictionary<int, int>();
        readonly Dictionary<int, int> m_layers = new Dictionary<int, int>();
        readonly Dictionary<uint, int> m_defs = new Dictionary<uint, int>();
        readonly HashSet<uint> m_defBusy = new HashSet<uint>();
        int m_dimStyle = -1, m_solidPattern = -1;
        int nLines, nArcs, nTexts, nPoints, nSolids, nDims, nRefs, nSkipped;

        public Importer(RhinoDoc doc) { m_doc = doc; }

        public string Import(string path, bool paperSize)
        {
            m_jw = JwwReader.Read(path);
            m_paper = paperSize;
            m_unit = RhinoMath.UnitScale(UnitSystem.Millimeters, m_doc.ModelUnitSystem);
            m_stem = Path.GetFileNameWithoutExtension(path);
            PrepareStyles();

            foreach (var e in m_jw.Entities)
            {
                var items = new List<KeyValuePair<GeometryBase, ObjectAttributes>>();
                Convert(e, items, 0);
                if (items.Count == 0) continue;
                int group = -1;
                if (items.Count > 1 && e is JwDimension) group = m_doc.Groups.Add();
                foreach (var kv in items)
                {
                    if (group >= 0) kv.Value.AddToGroup(group);
                    m_doc.Objects.Add(kv.Key, kv.Value);
                }
            }
            string warn = m_jw.Warnings.Count > 0 ? " Warnings: " + string.Join("; ", m_jw.Warnings.ToArray()) : "";
            return string.Format("JWW imported: {0} (ver {1}, {2}, 1/{3}{4}) - {5} lines, {6} arcs, {7} texts, {8} points, {9} solids, {10} dimensions, {11} block inserts ({12} definitions); {13} skipped.{14}",
                Path.GetFileName(path), m_jw.Version, JwwDocument.PaperName(m_jw.Paper), m_jw.GroupScale[0],
                m_paper ? ", paper size" : "", nLines, nArcs, nTexts, nPoints, nSolids, nDims, nRefs, m_defs.Count, nSkipped, warn);
        }

        // ---------------------------------------------------------------- styles and layers
        void PrepareStyles()
        {
            string dsName = "JWW";
            DimensionStyle ds = m_doc.DimStyles.FindName(dsName);
            if (ds == null)
            {
                var n = new DimensionStyle();
                n.Name = dsName;
                n.TextHeight = 2.5;
                n.DimensionScale = 1.0;
                n.Font = new Rhino.DocObjects.Font("ＭＳ ゴシック");
                m_doc.DimStyles.Add(n, false);
                ds = m_doc.DimStyles.FindName(dsName);
            }
            m_dimStyle = ds.Index;

            HatchPattern hp = m_doc.HatchPatterns.FindName("Solid");
            if (hp == null) { m_solidPattern = m_doc.HatchPatterns.Add(HatchPattern.Defaults.Solid); }
            else m_solidPattern = hp.Index;
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (char c in new[] { ':', ';', '[', ']', '"', '\r', '\n', '\t' }) s = s.Replace(c, '_');
            return s.Trim();
        }

        double Scale(int group)
        {
            if (m_paper) return 1.0;
            double s = (group >= 0 && group < 16) ? m_jw.GroupScale[group] : 1.0;
            return s > 0 ? s : 1.0;
        }

        int LayerIndex(int group, int layer)
        {
            group = Math.Max(0, Math.Min(15, group)); layer = Math.Max(0, Math.Min(15, layer));
            int key = group * 16 + layer, idx;
            if (m_layers.TryGetValue(key, out idx)) return idx;
            if (m_root < 0)
            {
                var root = new Layer(); root.Name = Clean("JWW " + m_stem);
                m_root = AddOrFind(root, Guid.Empty);
            }
            int g;
            if (!m_groupLayers.TryGetValue(group, out g))
            {
                var gl = new Layer();
                string gname = Clean(m_jw.GroupName[group]);
                gl.Name = group.ToString("X") + " " + (gname.Length > 0 ? gname : "1_" + m_jw.GroupScale[group].ToString("0.###"));
                gl.IsVisible = m_jw.GroupState[group] != 0;
                g = AddOrFind(gl, m_doc.Layers[m_root].Id);
                m_groupLayers[group] = g;
            }
            var l = new Layer();
            string lname = Clean(m_jw.LayerName[group, layer]);
            l.Name = group.ToString("X") + layer.ToString("X") + (lname.Length > 0 ? " " + lname : "");
            l.IsVisible = m_jw.LayerState[group, layer] != 0;
            idx = AddOrFind(l, m_doc.Layers[g].Id);
            m_layers[key] = idx;
            return idx;
        }

        int AddOrFind(Layer l, Guid parent)
        {
            l.ParentLayerId = parent;
            foreach (var ex in m_doc.Layers)
                if (!ex.IsDeleted && ex.ParentLayerId == parent && ex.Name == l.Name) return ex.Index;
            return m_doc.Layers.Add(l);
        }

        int Linetype(int style)
        {
            string[] names;
            switch (style)
            {
                case 2: names = new[] { "Dashed", "Hidden" }; break;
                case 3: names = new[] { "Dots", "Dashed" }; break;
                case 4: names = new[] { "Hidden", "Dashed" }; break;
                case 5: names = new[] { "Center", "DashDot" }; break;
                case 6: names = new[] { "DashDot", "Center" }; break;
                case 7: names = new[] { "Phantom", "Border" }; break;
                case 8: names = new[] { "Border", "Phantom" }; break;
                case 9: names = new[] { "Dots" }; break;
                default: return -1;
            }
            foreach (var n in names)
            {
                int i = m_doc.Linetypes.Find(n);
                if (i >= 0) return i;
            }
            return -1;
        }

        ObjectAttributes Attr(JwEntity e, uint rgb)
        {
            var a = new ObjectAttributes();
            a.LayerIndex = LayerIndex(e.Group, e.Layer);
            int[] c = m_jw.Rgb(e.Color, rgb);
            a.ColorSource = ObjectColorSource.ColorFromObject;
            a.ObjectColor = Color.FromArgb(c[0], c[1], c[2]);
            a.PlotColorSource = ObjectPlotColorSource.PlotColorFromObject;
            a.PlotColor = a.ObjectColor;
            double w = m_jw.PrintWidthMm(e.Color);
            if (w > 0) { a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject; a.PlotWeight = w; }
            int lt = Linetype(e.Style);
            if (lt >= 0)
            {
                a.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
                a.LinetypeIndex = lt;
                a.LinetypePatternScale = Scale(e.Group);        // dash lengths are paper mm
            }
            return a;
        }

        // ---------------------------------------------------------------- conversion
        Point3d P(double x, double y, int group)
        {
            double k = Scale(group) * m_unit;
            return new Point3d(x * k, y * k, 0);
        }

        void Convert(JwEntity e, List<KeyValuePair<GeometryBase, ObjectAttributes>> outp, int depth)
        {
            var ln = e as JwLine;
            if (ln != null)
            {
                Point3d a = P(ln.X1, ln.Y1, e.Group), b = P(ln.X2, ln.Y2, e.Group);
                if (a.DistanceTo(b) < 1e-9) { nSkipped++; return; }
                outp.Add(Pair(new LineCurve(a, b), Attr(e, 0))); nLines++;
                return;
            }
            var ar = e as JwArc;
            if (ar != null) { Curve c = ArcCurve(ar); if (c != null) { outp.Add(Pair(c, Attr(e, 0))); nArcs++; } else nSkipped++; return; }
            var tx = e as JwText;
            if (tx != null) { var t = Text(tx); if (t != null) { outp.Add(Pair(t, Attr(e, 0))); nTexts++; } return; }
            var pt = e as JwPoint;
            if (pt != null)
            {
                if (pt.Temporary) { nSkipped++; return; }          // 仮点 (construction points)
                outp.Add(Pair(new Rhino.Geometry.Point(P(pt.X, pt.Y, e.Group)), Attr(e, 0))); nPoints++;
                return;
            }
            var so = e as JwSolid;
            if (so != null)
            {
                var pts = new List<Point3d> { P(so.X1, so.Y1, e.Group), P(so.X2, so.Y2, e.Group), P(so.X3, so.Y3, e.Group), P(so.X4, so.Y4, e.Group) };
                var pl = new Polyline();
                foreach (var p in pts) if (pl.Count == 0 || pl[pl.Count - 1].DistanceTo(p) > 1e-9) pl.Add(p);
                if (pl.Count > 1 && pl[0].DistanceTo(pl[pl.Count - 1]) < 1e-9) pl.RemoveAt(pl.Count - 1);
                if (pl.Count < 3) { nSkipped++; return; }
                pl.Add(pl[0]);
                Hatch[] h = Hatch.Create(pl.ToNurbsCurve(), m_solidPattern, 0, 1, m_doc.ModelAbsoluteTolerance);
                if (h == null || h.Length == 0) { nSkipped++; return; }
                foreach (var hh in h) outp.Add(Pair(hh, Attr(e, so.Rgb)));
                nSolids++;
                return;
            }
            var dm = e as JwDimension;
            if (dm != null)
            {
                // a dimension becomes its line, text and (SXF) extension lines, grouped; empty parts are dropped
                int before = outp.Count, l0 = nLines, t0 = nTexts, k0 = nSkipped;
                if (dm.Line != null) Convert(dm.Line, outp, depth);
                if (dm.Text != null) Convert(dm.Text, outp, depth);
                foreach (var x in dm.ExtraLines) Convert(x, outp, depth);
                nLines = l0; nTexts = t0; nSkipped = k0;
                if (outp.Count > before) nDims++; else nSkipped++;
                return;
            }
            var br = e as JwBlockRef;
            if (br != null)
            {
                int def = Definition(br.Number, depth);
                if (def < 0) { nSkipped++; return; }
                Point3d o = P(br.X, br.Y, e.Group);
                Transform xf = Transform.Translation(new Vector3d(o))
                               * Transform.Rotation(br.Rotation, Vector3d.ZAxis, Point3d.Origin)
                               * Transform.Scale(Plane.WorldXY, br.ScaleX == 0 ? 1 : br.ScaleX, br.ScaleY == 0 ? 1 : br.ScaleY, 1);
                var iref = new InstanceReferenceGeometry(m_doc.InstanceDefinitions[def].Id, xf);
                outp.Add(Pair(iref, Attr(e, 0))); nRefs++;
                return;
            }
            nSkipped++;
        }

        static KeyValuePair<GeometryBase, ObjectAttributes> Pair(GeometryBase g, ObjectAttributes a)
        {
            return new KeyValuePair<GeometryBase, ObjectAttributes>(g, a);
        }

        Curve ArcCurve(JwArc a)
        {
            double k = Scale(a.Group) * m_unit;
            double r = a.R * k;
            if (r <= 0) return null;
            Point3d c = P(a.X, a.Y, a.Group);
            Plane pl = Plane.WorldXY;
            pl.Origin = c;
            if (Math.Abs(a.Tilt) > 1e-12) pl.Rotate(a.Tilt, Vector3d.ZAxis);
            double flat = (a.Flat > 0 && Math.Abs(a.Flat - 1.0) > 1e-9) ? a.Flat : 1.0;
            Curve crv;
            if (a.Full || Math.Abs(Math.Abs(a.Sweep) - 2 * Math.PI) < 1e-9)
            {
                if (flat == 1.0) return new ArcCurve(new Circle(pl, r));
                return new Ellipse(pl, r, r * flat).ToNurbsCurve();
            }
            double s0 = a.Sweep >= 0 ? a.Start : a.Start + a.Sweep;
            double sw = Math.Abs(a.Sweep);
            var arc = new Arc(new Circle(pl, r), new Interval(s0, s0 + sw));
            if (flat == 1.0) return new ArcCurve(arc);
            crv = arc.ToNurbsCurve();
            crv.Transform(Transform.Scale(pl, 1.0, flat, 1.0));
            return crv;
        }

        TextEntity Text(JwText t)
        {
            if (string.IsNullOrEmpty(t.Value)) { nSkipped++; return null; }
            double k = Scale(t.Group) * m_unit;
            double h = (t.SizeY > 0 ? t.SizeY : 2.5) * k;
            Plane pl = Plane.WorldXY;
            pl.Origin = P(t.X1, t.Y1, t.Group);
            if (Math.Abs(t.Angle) > 1e-9) pl.Rotate(t.Angle * Math.PI / 180.0, Vector3d.ZAxis);
            var te = TextEntity.Create(t.Value, pl, m_doc.DimStyles[m_dimStyle], false, 0, 0);
            if (te == null) return null;
            te.TextHeight = h;
            if (!string.IsNullOrEmpty(t.Font))
            {
                try { te.Font = new Rhino.DocObjects.Font(t.Font); } catch { }
            }
            te.TextHorizontalAlignment = TextHorizontalAlignment.Left;
            te.TextVerticalAlignment = TextVerticalAlignment.Bottom;
            return te;
        }

        int Definition(uint number, int depth)
        {
            int idx;
            if (m_defs.TryGetValue(number, out idx)) return idx;
            JwBlockDef def;
            if (!m_jw.Blocks.TryGetValue(number, out def) || depth > 20 || m_defBusy.Contains(number)) return -1;
            m_defBusy.Add(number);
            var items = new List<KeyValuePair<GeometryBase, ObjectAttributes>>();
            int l0 = nLines, a0 = nArcs, t0 = nTexts, p0 = nPoints, s0 = nSolids, d0 = nDims, r0 = nRefs;
            foreach (var m in def.Members) Convert(m, items, depth + 1);
            nLines = l0; nArcs = a0; nTexts = t0; nPoints = p0; nSolids = s0; nDims = d0; nRefs = r0;   // count inserts only
            m_defBusy.Remove(number);
            if (items.Count == 0) return -1;
            string name = Clean("JWW_" + m_stem + "_" + (string.IsNullOrEmpty(def.Name) ? number.ToString() : def.Name));
            string unique = name; int n = 2;
            while (m_doc.InstanceDefinitions.Find(unique) != null) unique = name + "_" + (n++);
            var geo = new List<GeometryBase>(); var att = new List<ObjectAttributes>();
            foreach (var kv in items) { geo.Add(kv.Key); att.Add(kv.Value); }
            idx = m_doc.InstanceDefinitions.Add(unique, "Jw_cad block " + def.Name, Point3d.Origin, geo, att);
            m_defs[number] = idx;
            return idx;
        }
    }
}
