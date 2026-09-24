using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    // Naming convention (no groups needed):
    //   VanyaTools_CutContour_N   — cut contour curve(s) for pack N
    //   VanyaTools_PeelMarker_N   — positioning markers for pack N (deleted on Apply)
    //
    // Any operation resolves pack N from the first selected VanyaTools_* object.
    // All objects with the same N on the same layer belong to that pack.

    internal sealed class PeelTabService
    {
        private const string ContourPrefix = "VanyaTools_CutContour_";
        private const string MarkerPrefix  = "VanyaTools_PeelMarker_";

        private const double JoinRadius = 1.5;   // mm  fillet at contour join

        // ── Pack index ────────────────────────────────────────────────────────

        // Returns next free pack index by scanning all shapes on all layers (recursive).
        public static int NextPackIndex(dynamic doc)
        {
            int max = 0;
            try
            {
                foreach (dynamic layer in doc.Layers)
                {
                    try
                    {
                        foreach (dynamic shape in layer.Shapes)
                            max = Math.Max(max, ExtractIndexRecursive(shape));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not scan a document layer for the next pack id.", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not enumerate document layers for the next pack id.", ex);
            }
            return max + 1;
        }

        private static int ExtractIndexRecursive(dynamic shape)
        {
            int best = 0;
            try
            {
                string n = (string)shape.Name ?? "";
                best = ParsePackIndex(n);
            }
            catch { }
            try
            {
                foreach (dynamic child in shape.Shapes)
                    best = Math.Max(best, ExtractIndexRecursive(child));
            }
            catch { }
            return best;
        }

        private static int ParsePackIndex(string name)
        {
            if (name == null) return 0;
            string tail = null;
            if (name.StartsWith(ContourPrefix, StringComparison.OrdinalIgnoreCase))
                tail = name.Substring(ContourPrefix.Length);
            else if (name.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                tail = name.Substring(MarkerPrefix.Length);
            if (tail == null) return 0;
            // tail may be "1" or "1_copy" etc — take leading digits
            int end = 0;
            while (end < tail.Length && char.IsDigit(tail[end])) end++;
            if (end == 0) return 0;
            int.TryParse(tail.Substring(0, end), out int idx);
            return idx;
        }

        // ── Pack resolution ───────────────────────────────────────────────────

        // Finds pack index from the current selection (any VanyaTools_* object).
        private static int ResolvePackIndex(dynamic app, bool allowSolePackFallback = false)
        {
            dynamic range = app.ActiveSelectionRange;
            if (range != null)
            {
                foreach (dynamic shape in range.Shapes)
                {
                    try
                    {
                        string name = (string)shape.Name ?? "";
                        int idx = ParsePackIndex(name);
                        if (idx > 0) return idx;
                    }
                    catch { }
                }
            }

            if (allowSolePackFallback)
            {
                // If Corel has lost the custom name, a selected magenta curve is
                // still identifiable as a generated cut contour. Give it a fresh
                // pack id so existing packs are never merged accidentally.
                List<dynamic> selectedContours = CollectSelectedCutContours(app, 0);
                if (selectedContours.Count > 0)
                {
                    int newIndex = NextPackIndex(app.ActiveDocument);
                    Log.Info("Using selected magenta cut contour(s) as new pack " + newIndex + ".");
                    return newIndex;
                }

                var indexes = new HashSet<int>();
                try
                {
                    foreach (dynamic layer in app.ActiveDocument.Layers)
                    {
                        try
                        {
                            foreach (dynamic shape in layer.Shapes)
                                CollectPackIndexes(shape, indexes);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Could not scan a document layer while resolving the pack.", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Could not enumerate document layers while resolving the pack.", ex);
                }

                if (indexes.Count == 1)
                    foreach (int idx in indexes) return idx;
                if (indexes.Count > 1)
                    throw new InvalidOperationException(
                        "Найдено несколько паков. Выделите контур нужного пака и повторите.");
            }

            if (range == null || (int)range.Count == 0)
                throw new InvalidOperationException("Ничего не выделено. Выделите контур пака.");
            throw new InvalidOperationException(
                "Объект пака не найден в выделении. Выделите контур реза или маркер язычка.");
        }
        private static void CollectPackIndexes(dynamic shape, HashSet<int> indexes)
        {
            try
            {
                int idx = ParsePackIndex((string)shape.Name ?? "");
                if (idx > 0) indexes.Add(idx);
            }
            catch { }
            try
            {
                if ((int)shape.Type == CorelConstants.CdrGroupShape)
                    foreach (dynamic child in shape.Shapes)
                        CollectPackIndexes(child, indexes);
            }
            catch { }
        }
        // Collects all contours / markers for a pack across all document layers.
        // Corel may append suffixes to duplicate object names, so match the parsed pack id.
        private static void CollectPack(dynamic app, int packIdx,
                                        out List<dynamic> contours, out List<dynamic> markers)
        {
            contours = new List<dynamic>();
            markers  = new List<dynamic>();
            try
            {
                foreach (dynamic layer in app.ActiveDocument.Layers)
                {
                    try
                    {
                        foreach (dynamic shape in layer.Shapes)
                            CollectPackFromShape(shape, packIdx, contours, markers);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not scan a document layer for pack shapes.", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not enumerate document layers while looking for pack shapes.", ex);
            }
        }

        private static void CollectPackFromShape(dynamic shape, int packIdx,
                                                  List<dynamic> contours, List<dynamic> markers)
        {
            try
            {
                string name = (string)shape.Name ?? "";
                if (HasPackName(name, ContourPrefix, packIdx))
                    { contours.Add(shape); return; }
                if (HasPackName(name, MarkerPrefix, packIdx))
                    { markers.Add(shape); return; }
            }
            catch { }
            try
            {
                if ((int)shape.Type == CorelConstants.CdrGroupShape)
                    foreach (dynamic child in shape.Shapes)
                        CollectPackFromShape(child, packIdx, contours, markers);
            }
            catch { }
        }

        private static bool HasPackName(string name, string prefix, int packIdx)
        {
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && ParsePackIndex(name) == packIdx;
        }
        // Corel sometimes omits a selected curve from the layer shape collection.
        // Recover generated contours from the active selection using their magenta outline.
        private static List<dynamic> CollectSelectedCutContours(dynamic app, int packIdx)
        {
            var contours = new List<dynamic>();
            try
            {
                dynamic range = app.ActiveSelectionRange;
                if (range != null)
                    foreach (dynamic shape in range.Shapes)
                        CollectSelectedCutContoursFromShape(shape, packIdx, contours);
            }
            catch (Exception ex)
            {
                Log.Error("Could not inspect the current selection for cut contours.", ex);
            }
            return contours;
        }

        private static void CollectSelectedCutContoursFromShape(dynamic shape, int packIdx,
                                                                 List<dynamic> contours)
        {
            int type;
            try { type = (int)shape.Type; }
            catch { return; }

            if (type == CorelConstants.CdrCurveShape)
            {
                string name = "";
                try { name = (string)shape.Name ?? ""; } catch { }
                bool namedForPack = packIdx > 0 && HasPackName(name, ContourPrefix, packIdx);
                if (namedForPack || IsMagentaCutContour(shape))
                    contours.Add(shape);
                return;
            }

            if (type == CorelConstants.CdrGroupShape)
            {
                try
                {
                    foreach (dynamic child in shape.Shapes)
                        CollectSelectedCutContoursFromShape(child, packIdx, contours);
                }
                catch { }
            }
        }

        private static bool IsMagentaCutContour(dynamic shape)
        {
            try
            {
                dynamic color = shape.Outline.Color;
                return Convert.ToInt32(color.RGBRed) == 255
                    && Convert.ToInt32(color.RGBGreen) == 0
                    && Convert.ToInt32(color.RGBBlue) == 255;
            }
            catch
            {
                return false;
            }
        }
        // ── Public name helpers (used by StickerCutService) ───────────────────

        public static string ContourName(int packIdx) => ContourPrefix + packIdx;
        public static string MarkerName(int packIdx)  => MarkerPrefix  + packIdx;

        // ── Delete pack contours ──────────────────────────────────────────────

        public int DeletePackContours()
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active document.");

            int oldUnit = (int)doc.Unit;
            doc.BeginCommandGroup("Vanya Tools - delete pack contours");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                int packIdx = ResolvePackIndex(app);
                List<dynamic> contours, markers;
                CollectPack(app, packIdx, out contours, out markers);
                if (contours.Count == 0)
                    throw new InvalidOperationException($"Контуры пака {packIdx} не найдены.");
                foreach (dynamic c in contours) try { c.Delete(); } catch { }
                return contours.Count;
            }
            finally
            {
                doc.Unit = oldUnit;
                app.ActiveWindow.Refresh();
                doc.EndCommandGroup();
            }
        }

        public int DeletePackMarkers()
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active document.");

            int oldUnit = (int)doc.Unit;
            doc.BeginCommandGroup("Vanya Tools - delete pack markers");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                int packIdx = ResolvePackIndex(app);
                List<dynamic> contours, markers;
                CollectPack(app, packIdx, out contours, out markers);
                if (markers.Count == 0)
                    throw new InvalidOperationException($"Маркеры пака {packIdx} не найдены.");
                foreach (dynamic m in markers) try { m.Delete(); } catch { }
                return markers.Count;
            }
            finally
            {
                doc.Unit = oldUnit;
                app.ActiveWindow.Refresh();
                doc.EndCommandGroup();
            }
        }

        // ── Add marker ────────────────────────────────────────────────────────

        public int AddMarker(double tabWidthMm, double tabHeightMm, double tabRadiusMm)
        {
            ValidateTabSize(tabWidthMm, tabHeightMm, tabRadiusMm);
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active document.");

            int oldUnit = (int)doc.Unit;
            int oldRef  = (int)doc.ReferencePoint;
            doc.BeginCommandGroup("Vanya Tools - add peel marker");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                doc.ReferencePoint = CorelConstants.CdrCenter;

                int packIdx = ResolvePackIndex(app, true);
                List<dynamic> contours, markers0;
                CollectPack(app, packIdx, out contours, out markers0);
                if (contours.Count == 0)
                {
                    contours = CollectSelectedCutContours(app, packIdx);
                    if (contours.Count > 0)
                    {
                        string contourName = ContourName(packIdx);
                        foreach (dynamic contour in contours)
                        {
                            try
                            {
                                string currentName = (string)contour.Name ?? "";
                                if (!HasPackName(currentName, ContourPrefix, packIdx))
                                    contour.Name = contourName;
                            }
                            catch (Exception ex)
                            {
                                Log.Error("Could not assign the pack id to a selected cut contour.", ex);
                            }
                        }
                        Log.Info("Recovered " + contours.Count + " selected cut contour(s) for pack " + packIdx + ".");
                    }
                }
                if (contours.Count == 0)
                    throw new InvalidOperationException(
                        $"Контур реза пака {packIdx} не найден. Выделите розовый контур реза и повторите.");

                string mName = MarkerName(packIdx);
                int created = 0;
                foreach (dynamic contour in contours)
                {
                    double sx = ((double)contour.LeftX + (double)contour.RightX) / 2;
                    double sy = (double)contour.TopY;
                    SnapInfo snap = SnapToContour(contour, sx, sy);
                    dynamic marker = BuildMarkerShape(doc, snap, tabWidthMm, tabHeightMm, tabRadiusMm);
                    marker.Name = mName;
                    FormatMarker(marker);
                    if (created == 0) marker.CreateSelection();
                    else marker.AddToSelection();
                    created++;
                }
                return created;
            }
            finally
            {
                doc.Unit = oldUnit;
                doc.ReferencePoint = oldRef;
                app.ActiveWindow.Refresh();
                doc.EndCommandGroup();
            }
        }

        // ── Reattach markers ──────────────────────────────────────────────────

        public int ReattachMarkers(double tabWidthMm, double tabHeightMm, double tabRadiusMm)
        {
            ValidateTabSize(tabWidthMm, tabHeightMm, tabRadiusMm);
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active document.");

            int oldUnit = (int)doc.Unit;
            int oldRef  = (int)doc.ReferencePoint;
            doc.BeginCommandGroup("Vanya Tools - reattach peel markers");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                doc.ReferencePoint = CorelConstants.CdrCenter;

                int packIdx = ResolvePackIndex(app);
                List<dynamic> contours, markers;
                CollectPack(app, packIdx, out contours, out markers);
                if (contours.Count == 0) throw new InvalidOperationException($"Контур реза пака {packIdx} не найден.");
                if (markers.Count == 0)  throw new InvalidOperationException($"Маркеры пака {packIdx} не найдены.");

                string mName = MarkerName(packIdx);
                int moved = 0;
                foreach (dynamic marker in markers)
                {
                    double mx = (double)marker.CenterX;
                    double my = (double)marker.CenterY;
                    dynamic nearest = FindNearestContourByCurve(contours, mx, my);
                    if (nearest == null) continue;
                    SnapInfo snap = SnapToContour(nearest, mx, my);
                    marker.Delete();
                    dynamic nm = BuildMarkerShape(doc, snap, tabWidthMm, tabHeightMm, tabRadiusMm);
                    nm.Name = mName;
                    FormatMarker(nm);
                    moved++;
                }
                return moved;
            }
            finally
            {
                doc.Unit = oldUnit;
                doc.ReferencePoint = oldRef;
                app.ActiveWindow.Refresh();
                doc.EndCommandGroup();
            }
        }

        // ── Apply markers ─────────────────────────────────────────────────────

        public int ApplyMarkers(double tabWidthMm, double tabHeightMm, double tabRadiusMm)
        {
            ValidateTabSize(tabWidthMm, tabHeightMm, tabRadiusMm);
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active document.");

            int oldUnit = (int)doc.Unit;
            int oldRef  = (int)doc.ReferencePoint;
            doc.BeginCommandGroup("Vanya Tools - apply peel tabs");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                doc.ReferencePoint = CorelConstants.CdrCenter;

                int packIdx = ResolvePackIndex(app);
                List<dynamic> contours, markers;
                CollectPack(app, packIdx, out contours, out markers);
                if (contours.Count == 0) throw new InvalidOperationException($"Контур реза пака {packIdx} не найден.");
                if (markers.Count == 0)  throw new InvalidOperationException($"Маркеры пака {packIdx} не найдены.");

                string cName = ContourName(packIdx);
                int applied = 0;
                foreach (dynamic marker in markers)
                {
                    double mx = (double)marker.CenterX;
                    double my = (double)marker.CenterY;
                    dynamic target = FindNearestContourByCurve(contours, mx, my);
                    if (target == null) continue;

                    SnapInfo snap = SnapToContour(target, mx, my);
                    dynamic tab = BuildTabCurve(doc, snap, tabWidthMm, tabHeightMm, tabRadiusMm);

                    dynamic welded = tab.Weld(target, false, false);
                    try { welded.Fillet(JoinRadius, true); } catch { }

                    welded.Name = cName;
                    StickerCutService.FormatCutContourPublic(welded);
                    ReplaceInList(contours, target, welded);
                    marker.Delete();
                    applied++;
                }
                return applied;
            }
            finally
            {
                doc.Unit = oldUnit;
                doc.ReferencePoint = oldRef;
                app.ActiveWindow.Refresh();
                doc.EndCommandGroup();
            }
        }

        // ── Snap ──────────────────────────────────────────────────────────────

        private static SnapInfo SnapToContour(dynamic contour, double hintX, double hintY)
        {
            double param = 0;
            dynamic segment = null;
            try { segment = contour.Curve.FindClosestSegment(hintX, hintY, ref param); } catch { }

            double anchorX = hintX, anchorY = hintY;
            double nx = 0, ny = 1, angleDeg = 0;

            if (segment != null)
            {
                try
                {
                    segment.GetPointPositionAt(ref anchorX, ref anchorY, param,
                                               CorelConstants.CdrParamSegmentOffset);
                    double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                    segment.GetPointPositionAt(ref x1, ref y1, Math.Max(0, param - 0.02),
                                               CorelConstants.CdrParamSegmentOffset);
                    segment.GetPointPositionAt(ref x2, ref y2, Math.Min(1, param + 0.02),
                                               CorelConstants.CdrParamSegmentOffset);
                    double tx = x2 - x1, ty = y2 - y1;
                    Normalize(ref tx, ref ty, 1, 0);
                    nx = -ty; ny = tx;
                    double cx = ((double)contour.LeftX + (double)contour.RightX) / 2;
                    double cy = ((double)contour.TopY  + (double)contour.BottomY) / 2;
                    if (nx * (anchorX - cx) + ny * (anchorY - cy) < 0) { nx = -nx; ny = -ny; }
                    angleDeg = Math.Atan2(ny, nx) * 180.0 / Math.PI - 90.0;
                }
                catch (Exception ex) { Log.Error("SnapToContour failed", ex); }
            }

            // 50/50: tab center sits exactly on the contour (inset = 0)
            return new SnapInfo(anchorX, anchorY, angleDeg, anchorX, anchorY, nx, ny);
        }

        // ── Shape builders ────────────────────────────────────────────────────

        private static dynamic BuildMarkerShape(dynamic doc, SnapInfo s,
                                                double tabWidthMm, double tabHeightMm, double tabRadiusMm)
        {
            double hw = tabWidthMm / 2.0, hh = tabHeightMm / 2.0;
            dynamic shape = doc.ActiveLayer.CreateRectangle(
                s.CenterX - hw, s.CenterY + hh,
                s.CenterX + hw, s.CenterY - hh,
                0, 0, 0, 0);
            if (tabRadiusMm > 0) try { shape.Fillet(tabRadiusMm, true); } catch { }
            shape.RotateEx(s.AngleDeg, s.CenterX, s.CenterY);
            return shape;
        }

        private static dynamic BuildTabCurve(dynamic doc, SnapInfo s,
                                             double tabWidthMm, double tabHeightMm, double tabRadiusMm)
        {
            dynamic shape = BuildMarkerShape(doc, s, tabWidthMm, tabHeightMm, tabRadiusMm);
            try { shape.ConvertToCurves(); } catch { }
            return shape;
        }

        private static void ValidateTabSize(double widthMm, double heightMm, double radiusMm)
        {
            if (widthMm <= 0 || heightMm <= 0)
                throw new InvalidOperationException("Ширина и длина язычка должны быть больше 0 мм.");
            if (radiusMm < 0 || radiusMm > Math.Min(widthMm, heightMm) / 2.0)
                throw new InvalidOperationException("Скругление должно быть от 0 до половины меньшей стороны язычка.");
        }

        // ── Find nearest contour ──────────────────────────────────────────────

        private static dynamic FindNearestContourByCurve(List<dynamic> contours, double x, double y)
        {
            dynamic nearest = null;
            double best = double.MaxValue;
            foreach (dynamic c in contours)
            {
                try
                {
                    double param = 0;
                    dynamic seg = c.Curve.FindClosestSegment(x, y, ref param);
                    if (seg == null) continue;
                    double px = 0, py = 0;
                    seg.GetPointPositionAt(ref px, ref py, param, CorelConstants.CdrParamSegmentOffset);
                    double d = Dist(x, y, px, py);
                    if (d < best) { best = d; nearest = c; }
                }
                catch { }
            }
            // fallback: bbox center distance
            if (nearest == null)
                foreach (dynamic c in contours)
                {
                    try
                    {
                        double cx = ((double)c.LeftX + (double)c.RightX) / 2;
                        double cy = ((double)c.TopY  + (double)c.BottomY) / 2;
                        double d  = Dist(x, y, cx, cy);
                        if (d < best) { best = d; nearest = c; }
                    }
                    catch { }
                }
            return nearest;
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static void FormatMarker(dynamic marker)
        {
            try { marker.Outline.Color.RGBAssign(0, 180, 255); marker.Outline.Width = 0.3; } catch { }
            try { marker.Fill.UniformColor.RGBAssign(173, 216, 230); } catch { }
        }

        private static void ReplaceInList(List<dynamic> list, dynamic oldItem, dynamic newItem)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], oldItem)) { list[i] = newItem; return; }
            list.Add(newItem);
        }

        private static void Normalize(ref double x, ref double y, double fx, double fy)
        {
            double l = Math.Sqrt(x * x + y * y);
            if (l < 1e-6) { x = fx; y = fy; } else { x /= l; y /= l; }
        }

        private static double Dist(double x1, double y1, double x2, double y2)
        { double dx = x2-x1, dy = y2-y1; return Math.Sqrt(dx*dx+dy*dy); }

        private readonly struct SnapInfo
        {
            public SnapInfo(double cx, double cy, double angleDeg,
                            double ax, double ay, double nx, double ny)
            { CenterX=cx; CenterY=cy; AngleDeg=angleDeg; AnchorX=ax; AnchorY=ay; NormalX=nx; NormalY=ny; }
            public double CenterX  { get; }
            public double CenterY  { get; }
            public double AngleDeg { get; }
            public double AnchorX  { get; }
            public double AnchorY  { get; }
            public double NormalX  { get; }
            public double NormalY  { get; }
        }
    }
}
