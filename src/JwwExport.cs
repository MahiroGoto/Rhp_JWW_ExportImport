// JwwExport - Rhino 8 plug-in: export a layout (or selected objects) to Jw_cad JWW.
// C# 5 (builds with the .NET Framework csc.exe; see build.ps1). Written for Sofarch, Nov 2026.
//
// Command  ExportJww   (-ExportJww asks for the file path on the command line)
//   * Active view is a layout: the whole sheet is written: page-space objects, plus the model objects
//     seen through every parallel detail, clipped to the detail and placed at the detail scale.
//   * Active view is a model view: the selected objects (or all visible) are written at a chosen scale.
// File > Export / Save As also offer "Jw_cad (*.jww)".
//
// JWW layout follows LibreCAD jwwlib (jwwdoc.cpp / jwwdoc.h): MFC CArchive serialisation, version 600,
// coordinates in PAPER mm (図寸) around the paper centre, Shift-JIS strings.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.PlugIns;

[assembly: System.Runtime.InteropServices.Guid("5b7c1d2e-8f3a-4c6b-9e1d-2a4f6b8c0d17")]
[assembly: System.Reflection.AssemblyTitle("JwwExport")]
[assembly: System.Reflection.AssemblyDescription("Export Rhino layouts and drawings to Jw_cad (JWW)")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: PlugInDescription(DescriptionType.Organization, "Sofarch")]
[assembly: PlugInDescription(DescriptionType.WebSite, "")]

namespace JwwExport
{
    // ------------------------------------------------------------------ plug-in (also a file exporter)
    public class JwwExportPlugIn : FileExportPlugIn
    {
        public static JwwExportPlugIn Instance { get; private set; }
        public JwwExportPlugIn() { Instance = this; }

        protected override FileTypeList AddFileTypes(Rhino.FileIO.FileWriteOptions options)
        {
            var list = new FileTypeList();
            list.AddFileType("Jw_cad (*.jww)", "jww", true);
            return list;
        }

        protected override WriteFileResult WriteFile(string filename, int index, RhinoDoc doc, Rhino.FileIO.FileWriteOptions options)
        {
            try
            {
                var ex = new Exporter(doc);
                RhinoPageView pv = doc.Views.ActiveView as RhinoPageView;
                string msg;
                if (pv != null) msg = ex.ExportLayout(pv, filename);
                else msg = ex.ExportModel(Exporter.ModelObjects(doc, options.WriteSelectedObjectsOnly), 100.0, filename);
                RhinoApp.WriteLine(msg);
                return WriteFileResult.Success;
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine("JWW export failed: " + e.Message);
                return WriteFileResult.Failure;
            }
        }
    }

    // ------------------------------------------------------------------ command
    [System.Runtime.InteropServices.Guid("a3e1f0c4-6d2b-4b8e-9a51-7c3d2e1f4b60")]
    public class ExportJwwCommand : Command
    {
        public override string EnglishName { get { return "ExportJww"; } }
        static double s_scale = 100.0;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var ex = new Exporter(doc);
            RhinoPageView pv = doc.Views.ActiveView as RhinoPageView;
            List<RhinoObject> objs = null;
            if (pv == null)
            {
                var go = new GetObject();
                go.SetCommandPrompt("Select objects to export to JWW (Enter = all visible)");
                go.EnablePreSelect(true, true);
                go.AcceptNothing(true);
                go.GroupSelect = true;
                go.SubObjectSelect = false;
                go.GetMultiple(0, 0);
                if (go.CommandResult() != Result.Success && go.CommandResult() != Result.Nothing) return go.CommandResult();
                objs = new List<RhinoObject>();
                if (go.ObjectCount > 0) { for (int i = 0; i < go.ObjectCount; i++) objs.Add(go.Object(i).Object()); }
                else objs = Exporter.ModelObjects(doc, false);
                double sc = s_scale;
                if (RhinoGet.GetNumber("Drawing scale 1:", false, ref sc, 1.0, 100000.0) != Result.Success) return Result.Cancel;
                s_scale = sc;
            }

            string baseName = string.IsNullOrEmpty(doc.Name) ? "drawing" : Path.GetFileNameWithoutExtension(doc.Name);
            if (pv != null) baseName += "_" + Exporter.SafeName(pv.PageName);
            string dir = string.IsNullOrEmpty(doc.Path) ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(doc.Path);
            string path;
            if (mode == RunMode.Interactive)
            {
                var dlg = new Rhino.UI.SaveFileDialog();
                dlg.Filter = "Jw_cad (*.jww)|*.jww";
                dlg.InitialDirectory = dir;
                dlg.FileName = baseName + ".jww";
                dlg.Title = "Export JWW";
                if (!dlg.ShowSaveDialog()) return Result.Cancel;
                path = dlg.FileName;
            }
            else
            {
                path = Path.Combine(dir, baseName + ".jww");
                if (RhinoGet.GetString("JWW file path", false, ref path) != Result.Success) return Result.Cancel;
                path = path.Trim().Trim('"');
            }
            if (!path.ToLowerInvariant().EndsWith(".jww")) path += ".jww";

            string msg = pv != null ? ex.ExportLayout(pv, path) : ex.ExportModel(objs, s_scale, path);
            RhinoApp.WriteLine(msg);
            return Result.Success;
        }
    }

    // ------------------------------------------------------------------ collecting Rhino geometry
    public class Exporter
    {
        readonly RhinoDoc m_doc;
        JwwFile m_jww;
        readonly Dictionary<string, int> m_layerKeys = new Dictionary<string, int>();   // "group|layerIndex" -> layer no
        readonly List<double> m_groupScales = new List<double>();
        int m_lines, m_arcs, m_texts, m_points;

        public Exporter(RhinoDoc doc) { m_doc = doc; }

        public static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        public static List<RhinoObject> ModelObjects(RhinoDoc doc, bool selectedOnly)
        {
            var s = new ObjectEnumeratorSettings();
            s.HiddenObjects = false; s.LockedObjects = true; s.NormalObjects = true;
            s.IncludeLights = false; s.IncludeGrips = false; s.ReferenceObjects = true;
            s.SelectedObjectsFilter = selectedOnly;
            var list = new List<RhinoObject>();
            foreach (var o in doc.Objects.GetObjectList(s))
                if (o.Attributes.Space == ActiveSpace.ModelSpace && o.Visible && doc.Layers[o.Attributes.LayerIndex].IsVisible) list.Add(o);
            return list;
        }

        static int PaperCode(double w, double h)
        {
            double a = Math.Max(w, h), b = Math.Min(w, h);
            double[,] sizes = { { 1189, 841 }, { 841, 594 }, { 594, 420 }, { 420, 297 }, { 297, 210 } };
            for (int i = 0; i < 5; i++)
                if (Math.Abs(a - sizes[i, 0]) < 5 && Math.Abs(b - sizes[i, 1]) < 5) return i;
            return 3;
        }

        int Group(double scale)
        {
            for (int i = 0; i < m_groupScales.Count; i++) if (Math.Abs(m_groupScales[i] - scale) < 1e-6) return i;
            if (m_groupScales.Count >= 16) return 15;
            m_groupScales.Add(scale);
            m_jww.GroupScale[m_groupScales.Count - 1] = scale;
            m_jww.GroupName[m_groupScales.Count - 1] = "1/" + scale.ToString("0.###");
            return m_groupScales.Count - 1;
        }

        int Layer(int group, int layerIndex)
        {
            string key = group + "|" + layerIndex;
            int n;
            if (m_layerKeys.TryGetValue(key, out n)) return n;
            n = 0;
            foreach (var kv in m_layerKeys) if (kv.Key.StartsWith(group + "|")) n++;
            if (n > 15) n = 15;
            m_layerKeys[key] = n;
            if (layerIndex >= 0 && layerIndex < m_doc.Layers.Count && string.IsNullOrEmpty(m_jww.LayerName[group, n]))
                m_jww.LayerName[group, n] = m_doc.Layers[layerIndex].Name;
            return n;
        }

        // nearest of the 9 JWW pens defined in JwwFile
        static int Pen(Color c, double weight)
        {
            int r = c.R, g = c.G, b = c.B;
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max - min < 40)
            {
                if (max > 150 && max < 235) return 8;                       // grey
                return weight >= 0.3 ? 3 : (weight >= 0.18 ? 2 : 1);        // black / white
            }
            if (r > 150 && g < 120 && b < 120) return 4;                    // red
            if (b > 150 && r < 120 && g < 150) return 5;                    // blue
            if (g > 120 && r < 120 && b < 120) return 6;                    // green
            if (r > 150 && b > 150 && g < 120) return 7;                    // magenta
            if (g > 120 && b > 120 && r < 120) return 9;                    // cyan
            if (r > 150 && g > 150 && b < 120) return 9;                    // yellow -> cyan pen
            return 2;
        }

        int LineStyle(ObjectAttributes a)
        {
            int li = a.LinetypeSource == ObjectLinetypeSource.LinetypeFromObject ? a.LinetypeIndex : m_doc.Layers[a.LayerIndex].LinetypeIndex;
            if (li < 0 || li >= m_doc.Linetypes.Count) return 1;
            Linetype lt = m_doc.Linetypes[li];
            string n = (lt.Name ?? "").ToUpperInvariant();
            if (lt.SegmentCount <= 1 || n.Contains("CONTINUOUS")) return 1;
            if (n.Contains("CENTER") || n.Contains("DASHDOT") || n.Contains("BORDER") || n.Contains("一点")) return 5;
            if (n.Contains("PHANTOM") || n.Contains("DIVIDE") || n.Contains("二点")) return 7;
            if (n.Contains("DOT")) return 3;
            return 2;
        }

        // ---------------------------------------------------------------- export entry points
        public string ExportLayout(RhinoPageView pv, string path)
        {
            m_jww = new JwwFile();
            m_jww.Paper = PaperCode(pv.PageWidth, pv.PageHeight);
            m_jww.Memo = (m_doc.Name ?? "") + " " + pv.PageName;
            double cx = pv.PageWidth / 2.0, cy = pv.PageHeight / 2.0;
            Transform pageXf = Transform.Translation(-cx, -cy, 0);
            Guid mainId = pv.MainViewport.Id;

            DetailViewObject[] details = pv.GetDetailViews();
            double unit = RhinoMath.UnitScale(m_doc.ModelUnitSystem, UnitSystem.Millimeters);   // model unit -> mm
            double firstScale = 1.0;
            foreach (var d in details)
                if (d.DetailGeometry.IsParallelProjection && d.DetailGeometry.PageToModelRatio > 0) { firstScale = unit / d.DetailGeometry.PageToModelRatio; break; }
            int pageGroup = Group(Math.Round(firstScale, 6));

            // page-space objects
            var s = new ObjectEnumeratorSettings();
            s.HiddenObjects = false; s.LockedObjects = true; s.IncludeLights = false; s.IncludeGrips = false;
            s.ViewportFilter = pv.MainViewport;
            foreach (var o in m_doc.Objects.GetObjectList(s))
            {
                if (o.Attributes.Space != ActiveSpace.PageSpace || o.Attributes.ViewportId != mainId) continue;
                if (o is DetailViewObject || !o.Visible || !m_doc.Layers[o.Attributes.LayerIndex].IsVisible) continue;
                AddObject(o.Geometry, o.Attributes, pageXf, 1.0, null, pageGroup, mainId);
            }

            // model space through each detail
            List<RhinoObject> model = ModelObjects(m_doc, false);
            foreach (var d in details)
            {
                var dg = d.DetailGeometry;
                if (!dg.IsParallelProjection || dg.PageToModelRatio <= 0) continue;
                double ratio = dg.PageToModelRatio;                       // paper mm per model unit
                BoundingBox rect = d.Geometry.GetBoundingBox(true);       // page coordinates
                RhinoViewport vp = d.Viewport;
                Plane cam = new Plane(vp.CameraTarget, vp.CameraX, vp.CameraY);
                Point3d pc = rect.Center;
                Transform xf = Transform.Translation(pc.X - cx, pc.Y - cy, 0)
                               * Transform.Scale(Point3d.Origin, ratio)
                               * Transform.PlaneToPlane(cam, Plane.WorldXY);
                BoundingBox clip = new BoundingBox(rect.Min.X - cx, rect.Min.Y - cy, -1e9, rect.Max.X - cx, rect.Max.Y - cy, 1e9);
                int grp = Group(Math.Round(unit / ratio, 6));             // e.g. 1:100
                foreach (var o in model)
                {
                    Layer L = m_doc.Layers[o.Attributes.LayerIndex];
                    if (!L.PerViewportIsVisible(d.Id)) continue;
                    AddObject(o.Geometry, o.Attributes, xf, ratio, clip, grp, d.Id);
                }
            }
            long n = m_jww.Save(path);
            return string.Format("JWW written: {0} ({1} bytes; {2} lines, {3} arcs, {4} texts, {5} points)", path, n, m_lines, m_arcs, m_texts, m_points);
        }

        public string ExportModel(List<RhinoObject> objs, double scale, string path)
        {
            m_jww = new JwwFile();
            m_jww.Memo = m_doc.Name ?? "";
            BoundingBox bb = BoundingBox.Empty;
            foreach (var o in objs) bb.Union(o.Geometry.GetBoundingBox(true));
            double unit = RhinoMath.UnitScale(m_doc.ModelUnitSystem, UnitSystem.Millimeters);
            double ratio = unit / scale;                                  // paper mm per model unit
            double w = (bb.Max.X - bb.Min.X) * ratio, h = (bb.Max.Y - bb.Min.Y) * ratio;
            // smallest A paper (landscape) that holds the drawing with a 10 mm margin, else A0
            int paper = 0;
            double[,] sizes = { { 1189, 841 }, { 841, 594 }, { 594, 420 }, { 420, 297 }, { 297, 210 } };
            for (int i = 4; i >= 0; i--) if (w <= sizes[i, 0] - 20 && h <= sizes[i, 1] - 20) { paper = i; break; }
            m_jww.Paper = paper;
            Point3d c = bb.IsValid ? bb.Center : Point3d.Origin;
            Transform xf = Transform.Scale(Point3d.Origin, ratio) * Transform.Translation(-c.X, -c.Y, 0);
            int grp = Group(scale);
            foreach (var o in objs) AddObject(o.Geometry, o.Attributes, xf, ratio, null, grp, Guid.Empty);
            long n = m_jww.Save(path);
            return string.Format("JWW written: {0} (1:{1}, {2} bytes; {3} lines, {4} arcs, {5} texts, {6} points)", path, scale, n, m_lines, m_arcs, m_texts, m_points);
        }

        // ---------------------------------------------------------------- geometry
        void AddObject(GeometryBase g, ObjectAttributes a, Transform xf, double ratio, BoundingBox? clip, int grp, Guid viewportId)
        {
            if (g == null) return;
            Color col = a.DrawColor(m_doc, viewportId);
            double pw = a.ComputedPlotWeight(m_doc, viewportId);
            int pen = Pen(col, pw);
            int style = LineStyle(a);
            int lay = Layer(grp, a.LayerIndex);

            var iref = g as InstanceReferenceGeometry;
            if (iref != null)
            {
                InstanceDefinition idef = m_doc.InstanceDefinitions.FindId(iref.ParentIdefId);
                if (idef == null) return;
                foreach (var sub in idef.GetObjects())
                {
                    var g2 = sub.Geometry.Duplicate();
                    g2.Transform(iref.Xform);
                    ObjectAttributes a2 = sub.Attributes.Duplicate();
                    if (a2.ColorSource == ObjectColorSource.ColorFromParent) { a2.ColorSource = a.ColorSource; a2.ObjectColor = a.ObjectColor; }
                    if (a2.LayerIndex == 0) a2.LayerIndex = a.LayerIndex;
                    AddObject(g2, a2, xf, ratio, clip, grp, viewportId);
                }
                return;
            }
            var te = g as TextEntity;
            if (te != null) { AddText(te, xf, ratio, clip, lay, pen, grp); return; }
            var dim = g as Dimension;
            if (dim != null) { foreach (var p in dim.Explode()) AddPiece(p, xf, ratio, clip, lay, pen, style, grp); return; }
            var ld = g as Leader;
            if (ld != null) { foreach (var p in ld.Explode()) AddPiece(p, xf, ratio, clip, lay, pen, style, grp); return; }
            var ht = g as Hatch;
            if (ht != null) { foreach (var c in ht.Get3dCurves(true)) AddCurve(c, xf, clip, lay, pen, 1, grp); return; }
            AddPiece(g, xf, ratio, clip, lay, pen, style, grp);
        }

        void AddPiece(GeometryBase g, Transform xf, double ratio, BoundingBox? clip, int lay, int pen, int style, int grp)
        {
            var c = g as Curve;
            if (c != null) { AddCurve(c, xf, clip, lay, pen, style, grp); return; }
            var te = g as TextEntity;
            if (te != null) { AddText(te, xf, ratio, clip, lay, pen, grp); return; }
            var pt = g as Rhino.Geometry.Point;
            if (pt != null)
            {
                Point3d p = pt.Location; p.Transform(xf);
                if (clip == null || Inside(clip.Value, p)) { m_jww.Points.Add(new JwwFile.Pt { Layer = lay, Group = grp, Color = pen, X = p.X, Y = p.Y }); m_points++; }
            }
        }

        static bool Inside(BoundingBox b, Point3d p)
        {
            return p.X >= b.Min.X - 1e-6 && p.X <= b.Max.X + 1e-6 && p.Y >= b.Min.Y - 1e-6 && p.Y <= b.Max.Y + 1e-6;
        }

        void AddCurve(Curve c0, Transform xf, BoundingBox? clip, int lay, int pen, int style, int grp)
        {
            Curve c = c0.DuplicateCurve();
            c.Transform(xf);
            // flatten to the paper plane
            c.Transform(Transform.PlanarProjection(Plane.WorldXY));
            var pieces = new List<Curve>();
            if (clip != null)
            {
                BoundingBox b = clip.Value;
                BoundingBox cb = c.GetBoundingBox(true);
                if (cb.Max.X < b.Min.X || cb.Min.X > b.Max.X || cb.Max.Y < b.Min.Y || cb.Min.Y > b.Max.Y) return;
                var rect = new Rectangle3d(Plane.WorldXY, new Point3d(b.Min.X, b.Min.Y, 0), new Point3d(b.Max.X, b.Max.Y, 0)).ToNurbsCurve();
                var ts = new List<double>();
                var ev = Rhino.Geometry.Intersect.Intersection.CurveCurve(c, rect, 1e-4, 1e-4);
                if (ev != null) foreach (var e in ev) ts.Add(e.ParameterA);
                Curve[] parts = ts.Count > 0 ? c.Split(ts) : new[] { c };
                if (parts == null) parts = new[] { c };
                foreach (var p in parts)
                    if (p != null && Inside(b, p.PointAtNormalizedLength(0.5))) pieces.Add(p);
            }
            else pieces.Add(c);
            foreach (var p in pieces) Emit(p, lay, pen, style, grp);
        }

        void Emit(Curve c, int lay, int pen, int style, int grp)
        {
            var pc = c as PolyCurve;
            if (pc != null) { for (int i = 0; i < pc.SegmentCount; i++) Emit(pc.SegmentCurve(i), lay, pen, style, grp); return; }
            Arc arc;
            if (!(c is LineCurve) && !(c is PolylineCurve) && c.TryGetArc(Plane.WorldXY, out arc, 1e-4))
            {
                double a0;
                if (arc.IsCircle) a0 = 0.0;
                else
                {
                    // counter-clockwise from the arc start (or end when the arc runs clockwise)
                    Vector3d sv = (arc.Plane.ZAxis.Z > 0 ? arc.StartPoint : arc.EndPoint) - arc.Center;
                    a0 = Math.Atan2(sv.Y, sv.X);
                }
                m_jww.Arcs.Add(new JwwFile.Arc { Layer = lay, Group = grp, Color = pen, Style = style, X = arc.Center.X, Y = arc.Center.Y,
                    R = arc.Radius, Start = a0, Sweep = arc.IsCircle ? 2 * Math.PI : arc.Angle, Full = arc.IsCircle });
                m_arcs++;
                return;
            }
            Polyline pl;
            if (!c.TryGetPolyline(out pl))
            {
                var approx = c.ToPolyline(0, 0, 0.05, 0, 0, 0.05, 0, 0, true);
                if (approx == null || !approx.TryGetPolyline(out pl)) return;
            }
            for (int i = 0; i < pl.Count - 1; i++)
            {
                if (pl[i].DistanceTo(pl[i + 1]) < 1e-9) continue;
                m_jww.Lines.Add(new JwwFile.Line { Layer = lay, Group = grp, Color = pen, Style = style, X1 = pl[i].X, Y1 = pl[i].Y, X2 = pl[i + 1].X, Y2 = pl[i + 1].Y });
                m_lines++;
            }
        }

        void AddText(TextEntity te, Transform xf, double ratio, BoundingBox? clip, int lay, int pen, int grp)
        {
            string text = te.PlainText ?? "";
            if (text.Trim().Length == 0) return;
            // model height: annotation model-space scaling multiplies by the style scale
            double h = te.TextHeight;
            if (ratio != 1.0 && m_doc.ModelSpaceAnnotationScalingEnabled && te.DimensionScale > 0) h *= te.DimensionScale;
            double size = h * ratio;                                       // paper mm
            Plane pl = te.Plane;
            Vector3d xa = pl.XAxis, ya = pl.YAxis;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            double lineStep = h * 1.6;
            double maxUnits = 0;
            foreach (var l in lines) maxUnits = Math.Max(maxUnits, JwwFile.TextUnits(l));
            double blockW = maxUnits * h, blockH = h + lineStep * (lines.Length - 1);
            double fx = 0, fy = 0;
            switch (te.TextHorizontalAlignment) { case TextHorizontalAlignment.Center: fx = -0.5; break; case TextHorizontalAlignment.Right: fx = -1.0; break; }
            switch (te.TextVerticalAlignment)
            {
                case TextVerticalAlignment.Top: case TextVerticalAlignment.MiddleOfTop: case TextVerticalAlignment.BottomOfTop: fy = -1.0; break;
                case TextVerticalAlignment.Middle: fy = -0.5; break;
                default: fy = 0.0; break;
            }
            Point3d o = pl.Origin + xa * (fx * blockW) + ya * (fy * blockH + (blockH - h));
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                double lw = JwwFile.TextUnits(lines[i]) * h;
                double dxl = (fx == -0.5) ? (blockW - lw) / 2 : (fx == -1.0 ? blockW - lw : 0);
                Point3d p0 = o + xa * dxl - ya * (lineStep * i);
                Point3d p1 = p0 + xa * lw;
                p0.Transform(xf); p1.Transform(xf);
                if (clip != null && !Inside(clip.Value, p0)) continue;
                double ang = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X) * 180.0 / Math.PI;
                string font = te.Font != null ? te.Font.FaceName : "ＭＳ ゴシック";
                m_jww.Texts.Add(new JwwFile.Text { Layer = lay, Group = grp, Color = pen, X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                    Size = Math.Round(size, 3), Angle = ang, Font = font, Value = lines[i] });
                m_texts++;
            }
        }
    }

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
