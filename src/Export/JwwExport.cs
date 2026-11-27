// Rhp_JWW_ExportImport - export part: a layout (or selected objects) to Jw_cad JWW.
// C# 5 (builds with the .NET Framework csc.exe; see build.ps1). Written for Sofarch, Nov 2026.
//
// Command  ExportJww   (-ExportJww asks for the file path on the command line)
//   * Active view is a layout: the whole sheet is written: page-space objects, plus the model objects
//     seen through every parallel detail, clipped to the detail and placed at the detail scale.
//   * Active view is a model view: the selected objects (or all visible) are written at a chosen scale.
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

using Jww;

namespace JwwExport
{
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
                // FamilyName ("ＭＳ ゴシック"), not FaceName ("Regular")
                string font = te.Font != null && !string.IsNullOrEmpty(te.Font.FamilyName) ? te.Font.FamilyName : "ＭＳ ゴシック";
                m_jww.Texts.Add(new JwwFile.Text { Layer = lay, Group = grp, Color = pen, X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                    Size = Math.Round(size, 3), Angle = ang, Font = font, Value = lines[i] });
                m_texts++;
            }
        }
    }

}
