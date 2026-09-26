using System;

namespace VanyaTools.Native
{
    internal sealed class StickerCutService
    {
        public bool LastUsedAlphaMask { get; private set; }
        public int SmoothSelectedContours(double roundSpikesMm, double simplifyToleranceMm)
        {
            if (roundSpikesMm < 0)
                throw new InvalidOperationException("Радиус скругления не может быть отрицательным.");
            ValidateSimplification(simplifyToleranceMm);

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            dynamic sourceRange = app.ActiveSelectionRange;

            if (doc == null)
                throw new InvalidOperationException("No active document.");
            if (sourceRange == null || sourceRange.Count == 0)
                throw new InvalidOperationException("Select one or more cut contour curves.");

            CutSpotColor.ValidateAvailable(app);
            int oldUnit = (int)doc.Unit;
            int processed = 0;

            doc.BeginCommandGroup("Vanya Tools - smooth selected cut contours");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                foreach (dynamic shape in sourceRange.Shapes)
                {
                    if ((int)shape.Type != CorelConstants.CdrCurveShape) continue;
                    dynamic smoothed = SmoothCutContour(shape, roundSpikesMm, simplifyToleranceMm);
                    FormatCutContourPublic(smoothed);
                    smoothed.CreateSelection();
                    processed++;
                }
                return processed;
            }
            finally
            {
                doc.Unit = oldUnit;
                app.ActiveWindow.Refresh();
                doc.EndCommandGroup();
            }
        }

        public void CreateCutContour(double offsetMm, int rasterDpi, int smoothing, int detail,
                                     double roundSpikesMm, double simplifyToleranceMm,
                                     bool mergeAdjacentObjects = false, bool useAlphaMask = true, int alphaThreshold = 10)
        {
            if (offsetMm <= 0)
                throw new InvalidOperationException("Offset must be greater than 0 mm.");
            if (roundSpikesMm < 0)
                throw new InvalidOperationException("Радиус скругления не может быть отрицательным.");
            ValidateSimplification(simplifyToleranceMm);
            if (alphaThreshold < 0 || alphaThreshold > 254)
                throw new InvalidOperationException("Порог альфа-канала должен быть от 0 до 254.");

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            dynamic sourceRange = app.ActiveSelectionRange;

            if (doc == null)
                throw new InvalidOperationException("No active document.");
            if (sourceRange == null || sourceRange.Count == 0)
                throw new InvalidOperationException("Выделите стикеры для создания контура реза.");

            CutSpotColor.ValidateAvailable(app);
            LastUsedAlphaMask = false;
            int oldUnit = (int)doc.Unit;
            int oldRef  = (int)doc.ReferencePoint;
            dynamic workingShape  = null;
            dynamic rasterShape   = null;
            dynamic tracedRange   = null;
            dynamic boundaryShape = null;

            doc.BeginCommandGroup("Vanya Tools - create sticker cut contour");
            app.Optimization = true;

            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                doc.ReferencePoint = CorelConstants.CdrBottomLeft;

                dynamic workingRange = sourceRange.Duplicate(0, 0);
                workingShape = workingRange.Group();
                // Rasterise with transparency — alpha channel is what we trace
                rasterShape = workingShape.ConvertToBitmapEx(
                    CorelConstants.CdrRgbColorImage,
                    true,  // transparent background
                    true,  // anti-aliasing
                    rasterDpi,
                    CorelConstants.CdrNormalAntiAliasing,
                    true);

                if (useAlphaMask)
                {
                    tracedRange = TraceAlphaSilhouette(doc, app, rasterShape,
                        alphaThreshold, smoothing, detail, simplifyToleranceMm,
                        mergeAdjacentObjects);
                    if (tracedRange != null)
                    {
                        LastUsedAlphaMask = true;
                        TryDelete(rasterShape);
                        rasterShape = null;
                    }
                    else
                        Log.Info("Alpha mask has no transparent background; using regular color trace.");
                }
                if (tracedRange == null)
                    tracedRange = TraceStickerSilhouette(rasterShape, smoothing, detail,
                                                        mergeAdjacentObjects);
                if (tracedRange == null || tracedRange.Count == 0)
                    throw new InvalidOperationException("Trace produced no curves.");

                boundaryShape = CreateOutsideBoundary(tracedRange, offsetMm);
                if (boundaryShape == null)
                    throw new InvalidOperationException("Could not create outside boundary.");

                dynamic contourCurve = boundaryShape.Curve.Contour(
                    offsetMm,
                    CorelConstants.CdrContourOutside,
                    CorelConstants.CdrContourRoundCap,
                    CorelConstants.CdrContourCornerRound,
                    0);

                dynamic cutShape = doc.ActiveLayer.CreateCurve(contourCurve);
                boundaryShape.Delete();
                boundaryShape = null;

                cutShape = SmoothCutContour(cutShape, roundSpikesMm, simplifyToleranceMm);

                // Assign pack index and name all resulting curves
                int packIdx = PeelTabService.NextPackIndex(doc);
                string cName = PeelTabService.ContourName(packIdx);
                BreakApartRemoveInnerAndFormat(cutShape, cName);
                Log.Info($"Created cut contour for pack {packIdx}: {cName}");
            }
            catch
            {
                TryDelete(boundaryShape);
                TryDeleteRange(tracedRange);
                TryDelete(rasterShape);
                TryDelete(workingShape);
                throw;
            }
            finally
            {
                doc.Unit = oldUnit;
                doc.ReferencePoint = oldRef;
                app.Optimization = false;
                app.ActiveWindow.Refresh();
                doc.EndCommandGroup();
            }
        }

        private static dynamic TraceAlphaSilhouette(dynamic doc, dynamic app,
            dynamic rasterShape, int threshold, int smoothing, int detail,
            double simplifyToleranceMm, bool mergeAdjacent)
        {
            dynamic bitmap = rasterShape.Bitmap;
            if (!Convert.ToBoolean(bitmap.Transparent)) return null;
            dynamic alpha = bitmap.ImageAlpha;
            if (alpha == null) return null;
            int width = Convert.ToInt32(alpha.Width), height = Convert.ToInt32(alpha.Height);
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Некорректный размер альфа-канала.");

            bool[] visible = new bool[checked(width * height)];
            bool[] covered = new bool[visible.Length];
            dynamic tiles = alpha.Tiles;
            if (Convert.ToInt32(tiles.Count) == 0)
                throw new InvalidOperationException("Альфа-канал не содержит пикселей.");
            for (int i = 1; i <= Convert.ToInt32(tiles.Count); i++)
            {
                dynamic tile = tiles.Item[i];
                int left = Convert.ToInt32(tile.Left), bottom = Convert.ToInt32(tile.Bottom);
                int tileWidth = Convert.ToInt32(tile.Width), tileHeight = Convert.ToInt32(tile.Height);
                int stride = Math.Abs(Convert.ToInt32(tile.BytesPerLine));
                int bpp = Convert.ToInt32(tile.BytesPerPixel);
                byte[] data = (byte[])tile.PixelData;
                if (bpp != 1 || data == null || stride < tileWidth ||
                    data.Length < stride * tileHeight)
                    throw new InvalidOperationException("Неподдерживаемый формат альфа-канала.");
                for (int y = 0; y < tileHeight; y++)
                for (int x = 0; x < tileWidth; x++)
                {
                    int px = left + x, py = bottom + y;
                    if (px < 0 || px >= width || py < 0 || py >= height) continue;
                    int index = py * width + px;
                    visible[index] = data[y * stride + x] > threshold;
                    covered[index] = true;
                }
            }

            int visibleCount = 0;
            for (int i = 0; i < visible.Length; i++)
            {
                if (!covered[i])
                    throw new InvalidOperationException("Не удалось прочитать пиксель альфа-канала.");
                if (visible[i]) visibleCount++;
            }
            if (visibleCount == 0)
                throw new InvalidOperationException(
                    "При выбранном пороге альфа-канал полностью прозрачный. Уменьшите порог.");
            if (visibleCount == visible.Length) return null;

            Log.Info($"Alpha mask: {width}x{height} px, {visibleCount} visible pixels, " +
                     $"threshold {threshold}.");
            return AlphaMaskVectorizer.CreateShapes(doc, app, visible, width, height,
                (double)rasterShape.LeftX, (double)rasterShape.BottomY,
                (double)rasterShape.SizeWidth, (double)rasterShape.SizeHeight,
                smoothing, detail, simplifyToleranceMm, mergeAdjacent);
        }

        private static dynamic TraceStickerSilhouette(dynamic rasterShape, int smoothing, int detail, bool mergeAdjacentObjects)
        {
            dynamic trace = rasterShape.Bitmap.Trace(
                CorelConstants.CdrTraceClipart,
                smoothing, detail,
                CorelConstants.CdrColorRgb,
                CorelConstants.CdrCustom,
                0, true, true, true);

            trace.Smoothing = smoothing;
            trace.DetailLevelPercent = detail;
            trace.CornerSmoothness = smoothing;
            trace.DeleteOriginalObject = true;
            trace.RemoveBackground = true;
            trace.RemoveEntireBackColor = true;
            trace.MergeAdjacentObjects = mergeAdjacentObjects;
            trace.RemoveOverlap = true;

            return trace.Finish();
        }

        private static dynamic CreateOutsideBoundary(dynamic tracedRange, double offsetMm)
        {
            double biasX = (double)tracedRange.LeftX - offsetMm * 10;
            double biasY = (double)tracedRange.BottomY - offsetMm * 10;
            return tracedRange.CreateBoundary(biasX, biasY, true, true);
        }

        public static void FormatCutContourPublic(dynamic cutShape)
        {
            cutShape.Fill.ApplyNoFill();
            CutSpotColor.AssignToOutline(cutShape.Outline, CorelApp.Get());
            cutShape.Outline.Width = 0.1;
        }

        // Keep the external contours and remove true inner paths. A bbox-only
        // containment test also deletes separate sticker contours whose bounds
        // happen to nest, so require geometric containment and opposite winding.
        private static void BreakApartRemoveInnerAndFormat(dynamic cutShape, string name)
        {
            dynamic range = null;
            try { range = cutShape.BreakApartEx(); } catch { }

            if (range == null || (int)range.Count == 0)
            {
                cutShape.Name = name;
                FormatCutContourPublic(cutShape);
                cutShape.CreateSelection();
                return;
            }

            var curves = new System.Collections.Generic.List<dynamic>();
            var bounds = new System.Collections.Generic.List<double[]>();
            foreach (dynamic shape in range.Shapes)
            {
                if ((int)shape.Type != CorelConstants.CdrCurveShape) continue;
                try
                {
                    double l = (double)shape.LeftX,  b2 = (double)shape.BottomY;
                    double r = (double)shape.RightX,  t  = (double)shape.TopY;
                    double w = r - l, h = t - b2;
                    curves.Add(shape);
                    bounds.Add(new[] { l, b2, r, t });
                    Log.Info($"  BreakApart curve[{curves.Count}]: {w:0.##}x{h:0.##} mm @ L={l:0.##} B={b2:0.##}");
                }
                catch { }
            }
            Log.Info($"BreakApart total curves: {curves.Count}");

            if (curves.Count == 0)
            {
                Log.Info("BreakApart returned no curve shapes; retaining original cut shape.");
                cutShape.Name = name;
                FormatCutContourPublic(cutShape);
                cutShape.CreateSelection();
                return;
            }

            var isInner = new bool[curves.Count];
            for (int i = 0; i < curves.Count; i++)
            {
                for (int j = 0; j < curves.Count; j++)
                {
                    if (i == j) continue;
                    if (IsInnerPath(curves[i], bounds[i], curves[j], bounds[j]))
                    {
                        isInner[i] = true;
                        Log.Info($"  INNER path[{i + 1}] removed; contained by path[{j + 1}].");
                        break;
                    }
                }
            }

            // Assign pack names before formatting: if Corel rejects a color,
            // the generated curves remain findable and removable by pack tools.
            for (int i = 0; i < curves.Count; i++)
                if (!isInner[i])
                    try { curves[i].Name = name; } catch { }

            int outerCount = 0;
            for (int i = 0; i < curves.Count; i++)
            {
                if (isInner[i])
                {
                    try { curves[i].Delete(); } catch (Exception ex) { Log.Error("Could not remove an inner cut path.", ex); }
                    continue;
                }

                FormatCutContourPublic(curves[i]);
                outerCount++;
            }

            if (outerCount == 0)
            {
                // Fail open: a detection/API anomaly must not erase the whole cut.
                foreach (dynamic curve in curves)
                {
                    try { FormatCutContourPublic(curve); curve.Name = name; } catch { }
                }
            }
            // Select all retained external paths so peel-tab tools can resolve
            // this pack immediately after contour creation.
            bool selected = false;
            for (int i = 0; i < curves.Count; i++)
            {
                if (isInner[i] && outerCount > 0) continue;
                try
                {
                    if (!selected) curves[i].CreateSelection();
                    else curves[i].AddToSelection();
                    selected = true;
                }
                catch { }
            }
        }

        private static bool IsInnerPath(dynamic candidate, double[] inner, dynamic container, double[] outer)
        {
            const double marginMm = 0.02;
            double innerWidth = inner[2] - inner[0], innerHeight = inner[3] - inner[1];
            double outerWidth = outer[2] - outer[0], outerHeight = outer[3] - outer[1];
            if (innerWidth <= 0 || innerHeight <= 0 || outerWidth <= innerWidth || outerHeight <= innerHeight)
                return false;

            if (inner[0] < outer[0] - marginMm || inner[1] < outer[1] - marginMm ||
                inner[2] > outer[2] + marginMm || inner[3] > outer[3] + marginMm)
                return false;

            try
            {
                // Inner loops normally wind opposite to their enclosing outline.
                if ((bool)candidate.Curve.IsClockwise == (bool)container.Curve.IsClockwise)
                    return false;

                // Check several points, not just the bbox center: concave outer
                // paths must not cause a separate sticker to be classified as a hole.
                double[] ratios = { 0.2, 0.5, 0.8 };
                foreach (double rx in ratios)
                    foreach (double ry in ratios)
                    {
                        double x = inner[0] + innerWidth * rx;
                        double y = inner[1] + innerHeight * ry;
                        if ((bool)container.Curve.IsPointInside(x, y)) continue;
                        if ((bool)candidate.Curve.IsPointInside(x, y)) return false;
                    }

                // Require at least one sample from the candidate's filled area
                // to avoid treating an empty bbox as a contained path.
                foreach (double rx in ratios)
                    foreach (double ry in ratios)
                    {
                        double x = inner[0] + innerWidth * rx;
                        double y = inner[1] + innerHeight * ry;
                        if ((bool)candidate.Curve.IsPointInside(x, y)) return true;
                    }
            }
            catch (Exception ex)
            {
                Log.Error("Could not verify whether a cut path is an inner loop; keeping it.", ex);
            }
            return false;
        }

        private static dynamic SmoothCutContour(dynamic cutShape, double roundSpikesMm, double simplifyToleranceMm)
        {
            if (cutShape == null) return cutShape;

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            dynamic expandedShape = null;
            dynamic smoothedShape = null;

            try
            {
                Log.Info($"Smoothing cut contour: round={roundSpikesMm:0.###} mm, simplify tolerance={simplifyToleranceMm:0.###} mm");

                if (roundSpikesMm > 0)
                {
                    dynamic expandedCurve = cutShape.Curve.Contour(
                        roundSpikesMm,
                        CorelConstants.CdrContourOutside,
                        CorelConstants.CdrContourRoundCap,
                        CorelConstants.CdrContourCornerRound, 0);
                    expandedShape = doc.ActiveLayer.CreateCurve(expandedCurve);

                    dynamic closedCurve = expandedShape.Curve.Contour(
                        roundSpikesMm,
                        CorelConstants.CdrContourInside,
                        CorelConstants.CdrContourRoundCap,
                        CorelConstants.CdrContourCornerRound, 0);
                    smoothedShape = doc.ActiveLayer.CreateCurve(closedCurve);
                    smoothedShape.Fillet(roundSpikesMm * 0.35, true);
                }
                else
                {
                    smoothedShape = cutShape.Duplicate(0, 0);
                }

                if (simplifyToleranceMm > 0)
                {
                    try { smoothedShape.Curve.Nodes.All.AutoReduce(simplifyToleranceMm); }
                    catch (Exception ex) { Log.Error("Node simplification failed; keeping the generated contour.", ex); }
                }

                cutShape.Delete();
                TryDelete(expandedShape);
                return smoothedShape;
            }
            catch (Exception ex)
            {
                Log.Error("Cut contour smoothing failed; keeping unsmoothed contour.", ex);
                TryDelete(expandedShape);
                TryDelete(smoothedShape);
                return cutShape;
            }
        }

        private static void TryDelete(dynamic shape)
        { try { shape?.Delete(); } catch { } }

        private static void TryDeleteRange(dynamic range)
        { try { range?.Delete(); } catch { } }

        private static void ValidateSimplification(double simplifyToleranceMm)
        {
            if (simplifyToleranceMm < 0 || simplifyToleranceMm > 5)
                throw new InvalidOperationException("Допуск упрощения должен быть от 0 до 5 мм.");
        }
    }
}
