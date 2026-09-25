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
                    LastUsedAlphaMask = ApplyAlphaTraceMask(rasterShape, alphaThreshold);
                    Log.Info(LastUsedAlphaMask
                        ? $"Tracing binary alpha mask with threshold {alphaThreshold}."
                        : "Alpha mask has no transparent background; using regular color trace.");
                }
                tracedRange = TraceStickerSilhouette(rasterShape, smoothing, detail,
                                                    mergeAdjacentObjects, LastUsedAlphaMask);
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

        // Work on the temporary raster only. Keep its RGB image type and geometry;
        // replace RGB pixels by a binary opacity mask, then remove the softmask.
        private static bool ApplyAlphaTraceMask(dynamic rasterShape, int threshold)
        {
            dynamic bitmap = rasterShape.Bitmap;
            if (!Convert.ToBoolean(bitmap.Transparent)) return false;
            dynamic alpha = bitmap.ImageAlpha;
            if (alpha == null) return false;
            dynamic mask = bitmap.Image.GetCopy();
            if (mask == null) throw new InvalidOperationException("Не удалось скопировать временный растр.");
            try { mask.ReadOnly = false; } catch { }

            int alphaWidth = Convert.ToInt32(alpha.Width);
            int alphaHeight = Convert.ToInt32(alpha.Height);
            int colorWidth = Convert.ToInt32(mask.Width);
            int colorHeight = Convert.ToInt32(mask.Height);
            if (alphaWidth <= 0 || alphaHeight <= 0 || colorWidth <= 0 || colorHeight <= 0)
                throw new InvalidOperationException("Некорректный размер временного растра.");
            byte[] opacity = new byte[checked(alphaWidth * alphaHeight)];
            byte[] covered = new byte[opacity.Length];
            dynamic sourceTiles = alpha.Tiles;
            int alphaTileCount = Convert.ToInt32(sourceTiles.Count);
            if (alphaTileCount == 0) throw new InvalidOperationException("Альфа-канал не содержит пикселей.");
            for (int i = 1; i <= alphaTileCount; i++)
            {
                dynamic tile = sourceTiles.Item[i];
                int left = Convert.ToInt32(tile.Left), bottom = Convert.ToInt32(tile.Bottom);
                int width = Convert.ToInt32(tile.Width), height = Convert.ToInt32(tile.Height);
                int stride = Math.Abs(Convert.ToInt32(tile.BytesPerLine));
                int bpp = Convert.ToInt32(tile.BytesPerPixel);
                byte[] data = (byte[])tile.PixelData;
                if (bpp != 1 || data == null || stride < width || data.Length < stride * height)
                    throw new InvalidOperationException("Неподдерживаемый формат альфа-канала.");
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int px = left + x, py = bottom + y;
                    if (px < 0 || px >= alphaWidth || py < 0 || py >= alphaHeight) continue;
                    int index = py * alphaWidth + px;
                    opacity[index] = data[y * stride + x];
                    covered[index] = 1;
                }
            }

            bool hasVisible = false, hasBackground = false;
            dynamic colorTiles = mask.Tiles;
            for (int i = 1; i <= Convert.ToInt32(colorTiles.Count); i++)
            {
                dynamic tile = colorTiles.Item[i];
                int left = Convert.ToInt32(tile.Left), bottom = Convert.ToInt32(tile.Bottom);
                int width = Convert.ToInt32(tile.Width), height = Convert.ToInt32(tile.Height);
                int stride = Math.Abs(Convert.ToInt32(tile.BytesPerLine));
                int bpp = Convert.ToInt32(tile.BytesPerPixel);
                byte[] pixels = (byte[])tile.PixelData;
                if (bpp < 3 || pixels == null || stride < width * bpp ||
                    pixels.Length < stride * height)
                    throw new InvalidOperationException("Неподдерживаемый формат цветного растра.");
                for (int y = 0; y < height; y++)
                {
                    int colorY = bottom + y;
                    if (colorY < 0 || colorY >= colorHeight) continue;
                    int alphaY = Math.Min(alphaHeight - 1, (int)((long)colorY * alphaHeight / colorHeight));
                    for (int x = 0; x < width; x++)
                    {
                        int colorX = left + x;
                        if (colorX < 0 || colorX >= colorWidth) continue;
                        int alphaX = Math.Min(alphaWidth - 1, (int)((long)colorX * alphaWidth / colorWidth));
                        int alphaIndex = alphaY * alphaWidth + alphaX;
                        if (covered[alphaIndex] == 0)
                            throw new InvalidOperationException("Не удалось прочитать пиксель альфа-канала.");
                        bool visible = opacity[alphaIndex] > threshold;
                        byte value = visible ? (byte)0 : (byte)255;
                        int index = y * stride + x * bpp;
                        pixels[index] = pixels[index + 1] = pixels[index + 2] = value;
                        if (bpp > 3) pixels[index + 3] = 255;
                        if (visible) hasVisible = true; else hasBackground = true;
                    }
                }
                tile.PixelData = pixels;
            }
            if (!hasVisible) throw new InvalidOperationException(
                "При выбранном пороге альфа-канал полностью прозрачный. Уменьшите порог.");
            if (!hasBackground) return false;
            bitmap.SetImageData(mask, null);
            return true;
        }

        private static dynamic TraceStickerSilhouette(dynamic rasterShape, int smoothing, int detail, bool mergeAdjacentObjects, bool binaryMask)
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
            if (binaryMask)
            {
                trace.BackgroundRemovalMode = 2; // cdrTraceBackgroundManual
                trace.BackgroundColor.RGBAssign(255, 255, 255);
            }
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
