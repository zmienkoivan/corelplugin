using System;

namespace VanyaTools.Native
{
    internal enum TrimMode
    {
        TransparentPixels = 0,
        TopLeftColor = 1,
        BottomRightColor = 2
    }

    [Flags]
    internal enum TrimSides
    {
        None   = 0,
        Top    = 1,
        Bottom = 2,
        Left   = 4,
        Right  = 8,
        All    = Top | Bottom | Left | Right
    }

    // Trims the border of selected bitmaps, either by transparency or by a corner background color.
    //
    // Verified facts this is built on (probed against CorelDRAW 27):
    //   * No built-in Trim/AutoCrop API exists, so content bounds are found by reading pixels.
    //   * Pixel[x, y] is BOTTOM-origin (y=0 is the image bottom), 0-based, size Width x Height.
    //     This matches the Y-up document, so the crop maps Y directly with no inversion.
    //   * Alpha image is grayscale (Type 9): Gray = 0 transparent .. 255 opaque.
    //   * Crop uses CropEnvelope + Crop() with ReferencePoint = cdrBottomLeft (from the VBA macro).
    //
    // Speed: a coarse grid pass brackets the content box, then each of the four edges is refined
    // to 1px within a narrow band. That is thousands of reads instead of millions.
    internal sealed class BitmapTrimService
    {
        private const int AlphaThreshold = 10;    // alpha Gray > this counts as content
        private const int ColorThreshold = 60;    // sum |dR|+|dG|+|dB| > this differs from background

        public TrimResult TrimSelected(TrimMode mode, TrimSides sides, int paddingPx, bool useTiles = true)
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            dynamic range = app.ActiveSelectionRange;

            if (doc == null)
                throw new InvalidOperationException("No active document.");
            if (range == null || range.Count == 0)
                throw new InvalidOperationException("Select one or more bitmaps.");

            int trimmed = 0, skipped = 0;
            doc.BeginCommandGroup("Vanya Tools - trim bitmap edges");
            try { app.Optimization = true; } catch { }

            try
            {
                foreach (dynamic shape in range.Shapes)
                {
                    if (TrimOne(shape, mode, sides, paddingPx, useTiles)) trimmed++;
                    else skipped++;
                }
            }
            finally
            {
                try { app.Optimization = false; } catch { }
                app.ActiveWindow.Refresh();
                doc.EndCommandGroup();
            }

            return new TrimResult(trimmed, skipped);
        }

        // Encapsulates "is pixel (x,y) content (i.e. NOT border) ?" for the chosen mode.
        private sealed class ContentTest
        {
            public dynamic Source;     // alpha image or color image
            public bool IsAlpha;
            public int BgR, BgG, BgB;  // background color for color modes

            public bool At(int x, int y)
            {
                if (IsAlpha)
                    return ReadAlpha(Source, x, y) > AlphaThreshold;
                ReadRgb(Source, x, y, out int r, out int g, out int b);
                return Math.Abs(r - BgR) + Math.Abs(g - BgG) + Math.Abs(b - BgB) > ColorThreshold;
            }
        }

        private static bool TrimOne(dynamic shape, TrimMode mode, TrimSides sides, int paddingPx, bool useTiles)
        {
            if (shape == null || (int)shape.Type != CorelConstants.CdrBitmapShape)
                return false;

            // The bitmap's pixel data and GetBoundingBox live in the shape's UNROTATED frame.
            // If the shape is rotated, temporarily zero the rotation, trim, then restore it — so
            // the crop math always runs in the 0-degree frame where it is exact.
            double savedAngle = 0;
            bool hadRotation = false;
            try
            {
                savedAngle = (double)shape.RotationAngle;
                if (Math.Abs(savedAngle) > 0.0001)
                {
                    hadRotation = true;
                    shape.RotationAngle = 0;
                    Log.Info($"Temporarily zeroed rotation {savedAngle:0.###} deg for trim.");
                }
            }
            catch { /* RotationAngle may be unavailable; proceed unrotated */ }

            try
            {
                return TrimOneCore(shape, mode, sides, paddingPx, useTiles);
            }
            finally
            {
                if (hadRotation)
                {
                    try { shape.RotationAngle = savedAngle; Log.Info($"Restored rotation {savedAngle:0.###} deg."); }
                    catch (Exception ex) { Log.Error("Failed to restore rotation", ex); }
                }
            }
        }

        private static bool TrimOneCore(dynamic shape, TrimMode mode, TrimSides sides, int paddingPx, bool useTiles)
        {
            dynamic bitmap = shape.Bitmap;
            var test = new ContentTest();
            int w = 0, h = 0;

            if (mode == TrimMode.TransparentPixels)
            {
                if (!SafeBool(() => bitmap.Transparent))
                    return false;
                dynamic alpha = bitmap.ImageAlpha;
                if (alpha == null) return false;
                w = SafeInt(() => alpha.Width);
                h = SafeInt(() => alpha.Height);
                if (w <= 0 || h <= 0) return false;
                test.Source = alpha;
                test.IsAlpha = true;
            }

            dynamic colorImage = null, alphaImage = null;
            int bgR = 0, bgG = 0, bgB = 0;
            bool fallbackToAlpha = false;

            if (mode != TrimMode.TransparentPixels)
            {
                colorImage = bitmap.Image;
                if (colorImage == null) return false;
                w = SafeInt(() => colorImage.Width);
                h = SafeInt(() => colorImage.Height);
                if (w <= 0 || h <= 0) return false;

                if (SafeBool(() => bitmap.Transparent))
                    alphaImage = bitmap.ImageAlpha;

                // Find background color = first opaque pixel near the chosen corner.
                // Bottom-origin: screen top-left=(0,h-1), bottom-right=(w-1,0).
                // If the entire corner scan area is transparent, the bitmap has no solid background
                // — fall back to alpha (transparency) mode so dark content isn't lost.
                bool topLeft = mode == TrimMode.TopLeftColor;
                bool found = false;
                int scanLimit = Math.Min(200, Math.Min(w, h));
                for (int d = 0; d < scanLimit && !found; d++)
                {
                    int cx = topLeft ? d : w - 1 - d;
                    int cy = topLeft ? h - 1 - d : d;
                    bool opaque = alphaImage == null || ReadAlpha(alphaImage, cx, cy) > AlphaThreshold;
                    if (opaque)
                    {
                        ReadRgb(colorImage, cx, cy, out bgR, out bgG, out bgB);
                        Log.Info($"Trim color bg @({cx},{cy}) d={d} = R{bgR} G{bgG} B{bgB}");
                        found = true;
                    }
                }
                if (!found)
                {
                    Log.Info("Trim color bg: corner transparent, falling back to alpha scan.");
                    fallbackToAlpha = true;
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            int left = 0, right = 0, bottom = 0, top = 0;

            bool ok;
            if (fallbackToAlpha)
            {
                dynamic fallbackAlpha = bitmap.ImageAlpha;
                if (fallbackAlpha == null) return false;
                ok = TileScan(fallbackAlpha, w, h, true, 0, 0, 0, out left, out top, out right, out bottom);
            }
            else
            {
                ok = mode == TrimMode.TransparentPixels
                    ? TileScan(test.Source, w, h, true, 0, 0, 0, out left, out top, out right, out bottom)
                    : TileScanColor(colorImage, alphaImage, w, h, bgR, bgG, bgB, out left, out top, out right, out bottom);
            }
            if (!ok)
            {
                Log.Info("Trim: no content found, skipped.");
                return false;
            }

            // Apply padding, clamped.
            if (paddingPx < 0) paddingPx = 0;
            left   = Math.Max(0, left - paddingPx);
            right  = Math.Min(w - 1, right + paddingPx);
            bottom = Math.Max(0, bottom - paddingPx);
            top    = Math.Min(h - 1, top + paddingPx);

            // Honor side selection: a deselected side keeps the full-image extent on that side.
            // Pixel space is bottom-origin: max-y (top) is the screen TOP, min-y (bottom) the BOTTOM.
            if (!sides.HasFlag(TrimSides.Left))   left   = 0;
            if (!sides.HasFlag(TrimSides.Right))  right  = w - 1;
            if (!sides.HasFlag(TrimSides.Top))    top    = h - 1;
            if (!sides.HasFlag(TrimSides.Bottom)) bottom = 0;

            Log.Info($"Trim bounds (bottom-origin px): L={left} T={top} R={right} B={bottom} on {w}x{h} ({sw.ElapsedMilliseconds} ms)");

            if (left == 0 && bottom == 0 && right == w - 1 && top == h - 1)
            {
                Log.Info("Trim: nothing to crop, skipped.");
                return false;
            }

            return ApplyCrop(shape, left, top, right, bottom, w, h);
        }

        // ---- fast tile scan -----------------------------------------------------------------
        // Scans every tile buffer once (one COM call each) to find the content bounding box.
        // Tile geometry (measured & verified):
        //   pixelX = tile.Left + lx
        //   pixelY = tile.Bottom + ly  (buffer row 0 = tile's BOTTOM edge; ly grows upward)
        //            tile.Bottom = tile.Top - (tile.Height - 1)
        // pixelY is bottom-origin — exactly what ApplyCrop expects.
        //
        // Works for both alpha (bpp=1: opaque if Gray>AlphaThreshold) and color (bpp>=3: content if
        // BGR differs from the background by > ColorThreshold). Returns bounds, or false on failure.
        private static bool TileScan(dynamic image, int w, int h, bool isAlpha,
                                     int bgR, int bgG, int bgB,
                                     out int left, out int top, out int right, out int bottom)
        {
            left = top = right = bottom = 0;
            try
            {
                dynamic tiles = image.Tiles;
                int n = Convert.ToInt32(tiles.Count);
                if (n <= 0) return false;

                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (int i = 1; i <= n; i++)
                {
                    dynamic t = tiles.Item[i];
                    int tL = Convert.ToInt32(t.Left);
                    int tT = Convert.ToInt32(t.Top);
                    int tW = Convert.ToInt32(t.Width);
                    int tH = Convert.ToInt32(t.Height);
                    int stride = Math.Abs(Convert.ToInt32(t.BytesPerLine));
                    int bpp = Convert.ToInt32(t.BytesPerPixel);
                    byte[] data = (byte[])t.PixelData;
                    if (data == null || bpp < 1 || stride <= 0) continue;
                    if (!isAlpha && bpp < 3) continue;

                    int tileBottom = tT - (tH - 1);
                    for (int ly = 0; ly < tH; ly++)
                    {
                        int py = tileBottom + ly;
                        if (py < 0 || py >= h) continue;
                        int rowOff = ly * stride;
                        for (int lx = 0; lx < tW; lx++)
                        {
                            int off = rowOff + lx * bpp;
                            if (off < 0 || off + (isAlpha ? 0 : 2) >= data.Length) continue;

                            bool content;
                            if (isAlpha)
                                content = data[off] > AlphaThreshold;
                            else
                            {
                                int b = data[off], g = data[off + 1], r = data[off + 2];
                                content = Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB) > ColorThreshold;
                            }
                            if (!content) continue;

                            int px = tL + lx;
                            if (px < 0 || px >= w) continue;
                            if (px < minX) minX = px;
                            if (px > maxX) maxX = px;
                            if (py < minY) minY = py;
                            if (py > maxY) maxY = py;
                        }
                    }
                }
                if (maxX < 0) return false;

                left = minX; right = maxX; bottom = minY; top = maxY;
                Log.Info($"TileScan box X[{left}..{right}] Y[{bottom}..{top}] ({(isAlpha ? "alpha" : "color")})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("TileScan failed, using per-pixel path", ex);
                return false;
            }
        }

        // Color tile scan that is ALPHA-AWARE: a pixel is content only if it is (a) not transparent
        // and (b) differs from the background color. Transparent pixels (meaningless RGB) are
        // treated as background. alphaImage may be null (fully opaque bitmap).
        private static bool TileScanColor(dynamic colorImage, dynamic alphaImage, int w, int h,
                                          int bgR, int bgG, int bgB,
                                          out int left, out int top, out int right, out int bottom)
        {
            left = top = right = bottom = 0;
            try
            {
                // Snapshot alpha tiles keyed by (Left,Top) so we can look up transparency per pixel.
                var alphaByKey = new System.Collections.Generic.Dictionary<long, (int Stride, int Bpp, byte[] D)>();
                if (alphaImage != null)
                {
                    dynamic at = alphaImage.Tiles;
                    int an = Convert.ToInt32(at.Count);
                    for (int i = 1; i <= an; i++)
                    {
                        dynamic t = at.Item[i];
                        int tl = Convert.ToInt32(t.Left), tt = Convert.ToInt32(t.Top);
                        long key = ((long)tl << 32) ^ (uint)tt;
                        alphaByKey[key] = (Math.Abs(Convert.ToInt32(t.BytesPerLine)),
                                           Convert.ToInt32(t.BytesPerPixel), (byte[])t.PixelData);
                    }
                }

                dynamic tiles = colorImage.Tiles;
                int n = Convert.ToInt32(tiles.Count);
                if (n <= 0) return false;

                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (int i = 1; i <= n; i++)
                {
                    dynamic t = tiles.Item[i];
                    int tL = Convert.ToInt32(t.Left);
                    int tT = Convert.ToInt32(t.Top);
                    int tW = Convert.ToInt32(t.Width);
                    int tH = Convert.ToInt32(t.Height);
                    int stride = Math.Abs(Convert.ToInt32(t.BytesPerLine));
                    int bpp = Convert.ToInt32(t.BytesPerPixel);
                    byte[] data = (byte[])t.PixelData;
                    if (data == null || bpp < 3 || stride <= 0) continue;

                    // matching alpha tile (same Left/Top) if available
                    (int Stride, int Bpp, byte[] D) aTile = default;
                    bool haveAlpha = alphaByKey.TryGetValue(((long)tL << 32) ^ (uint)tT, out aTile)
                                     && aTile.D != null && aTile.Bpp >= 1;

                    int tileBottom = tT - (tH - 1);
                    for (int ly = 0; ly < tH; ly++)
                    {
                        int py = tileBottom + ly;
                        if (py < 0 || py >= h) continue;
                        int rowOff = ly * stride;
                        int aRowOff = haveAlpha ? ly * aTile.Stride : 0;
                        for (int lx = 0; lx < tW; lx++)
                        {
                            // skip transparent pixels
                            if (haveAlpha)
                            {
                                int aOff = aRowOff + lx * aTile.Bpp;
                                if (aOff >= 0 && aOff < aTile.D.Length && aTile.D[aOff] <= AlphaThreshold)
                                    continue; // transparent -> background
                            }
                            int off = rowOff + lx * bpp;
                            if (off < 0 || off + 2 >= data.Length) continue;
                            int b = data[off], g = data[off + 1], r = data[off + 2];
                            if (Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB) <= ColorThreshold)
                                continue; // matches background

                            int px = tL + lx;
                            if (px < 0 || px >= w) continue;
                            if (px < minX) minX = px;
                            if (px > maxX) maxX = px;
                            if (py < minY) minY = py;
                            if (py > maxY) maxY = py;
                        }
                    }
                }
                if (maxX < 0) return false;

                left = minX; right = maxX; bottom = minY; top = maxY;
                Log.Info($"TileScanColor box X[{left}..{right}] Y[{bottom}..{top}] (alphaAware={alphaImage != null})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("TileScanColor failed", ex);
                return false;
            }
        }

        // ---- edge refinement ----------------------------------------------------------------
        private static int RefineEdgeX(ContentTest t, int xStart, int xEnd, int yLo, int yHi, int step, bool forward)
        {
            if (forward)
            {
                for (int x = xStart; x <= xEnd; x++)
                    if (ColumnHasContent(t, x, yLo, yHi, step)) return x;
            }
            else
            {
                for (int x = xEnd; x >= xStart; x--)
                    if (ColumnHasContent(t, x, yLo, yHi, step)) return x;
            }
            return -1;
        }

        private static int RefineEdgeY(ContentTest t, int yStart, int yEnd, int xLo, int xHi, int step, bool forward)
        {
            if (forward)
            {
                for (int y = yStart; y <= yEnd; y++)
                    if (RowHasContent(t, y, xLo, xHi, step)) return y;
            }
            else
            {
                for (int y = yEnd; y >= yStart; y--)
                    if (RowHasContent(t, y, xLo, xHi, step)) return y;
            }
            return -1;
        }

        private static bool RowHasContent(ContentTest t, int y, int xLo, int xHi, int step)
        {
            for (int x = xLo; x <= xHi; x += step)
                if (t.At(x, y)) return true;
            return t.At(xHi, y);
        }

        private static bool ColumnHasContent(ContentTest t, int x, int yLo, int yHi, int step)
        {
            for (int y = yLo; y <= yHi; y += step)
                if (t.At(x, y)) return true;
            return t.At(x, yHi);
        }

        private static int ReadAlpha(dynamic alpha, int x, int y)
        {
            try { return (int)alpha.Pixel[x, y].Gray; }
            catch
            {
                try { return (int)alpha.Pixel[x, y].RGBRed; }
                catch { return 255; }
            }
        }

        private static void ReadRgb(dynamic image, int x, int y, out int r, out int g, out int b)
        {
            try
            {
                dynamic c = image.Pixel[x, y];
                r = (int)c.RGBRed; g = (int)c.RGBGreen; b = (int)c.RGBBlue;
            }
            catch
            {
                try
                {
                    dynamic rgb = image.Pixel[x, y].RGB;
                    r = (int)rgb.Red; g = (int)rgb.Green; b = (int)rgb.Blue;
                }
                catch { r = g = b = 0; }
            }
        }

        // ---- crop ---------------------------------------------------------------------------
        private static bool ApplyCrop(dynamic shape, int leftPx, int topPx, int rightPx, int bottomPx, int w, int h)
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            int oldRef = (int)doc.ReferencePoint;

            try
            {
                doc.ReferencePoint = CorelConstants.CdrBottomLeft;
                shape.Bitmap.ResetCropEnvelope();

                double x = 0, y = 0, width = 0, height = 0;
                shape.GetBoundingBox(ref x, ref y, ref width, ref height, false);

                // Pixel space is bottom-origin (y=0 = image bottom), same as the Y-up document,
                // so Y maps directly. topPx/bottomPx are max/min pixel-y of content.
                double newLeft   = x + width  * ((double)leftPx / w);
                double newRight  = x + width  * ((double)(rightPx + 1) / w);
                double newBottom = y + height * ((double)bottomPx / h);
                double newTop    = y + height * ((double)(topPx + 1) / h);
                double midX = x + width  / 2;
                double midY = y + height / 2;

                Log.Info($"Crop: shape x={x:0.###} y={y:0.###} w={width:0.###} h={height:0.###} " +
                         $"-> L={newLeft:0.###} T={newTop:0.###} R={newRight:0.###} B={newBottom:0.###}");

                dynamic crop = shape.Bitmap.CropEnvelope;
                foreach (dynamic node in crop.Nodes)
                {
                    node.PositionX = node.PositionX < midX ? newLeft : newRight;
                    node.PositionY = node.PositionY < midY ? newBottom : newTop;
                }

                if (!SafeBool(() => shape.Bitmap.Cropped))
                    return false;

                shape.Bitmap.Crop();
                return true;
            }
            finally
            {
                doc.ReferencePoint = oldRef;
            }
        }

        // Calibration: on a known test image (white circle in one corner of a black rectangle,
        // transparent background), compare the content bounding box found via TILES against the
        // box found via the proven Pixel[x,y] reader. Whichever tile coordinate mapping makes the
        // two boxes match is the correct one. Pure diagnostics, no crop.
        public string CalibrateTiles()
        {
            dynamic app = CorelApp.Get();
            dynamic range = app.ActiveSelectionRange;
            if (range == null || range.Count == 0) throw new InvalidOperationException("Выделите растр.");
            dynamic shape = range.Shapes[1];
            if ((int)shape.Type != CorelConstants.CdrBitmapShape) throw new InvalidOperationException("Это не растр.");

            dynamic bitmap = shape.Bitmap;
            if (!SafeBool(() => bitmap.Transparent)) throw new InvalidOperationException("Растр без прозрачности.");
            dynamic alpha = bitmap.ImageAlpha;
            int w = Convert.ToInt32(alpha.Width), h = Convert.ToInt32(alpha.Height);
            Log.Info($"== CalibrateTiles == alpha {w}x{h}");

            // (A) Truth via Pixel[x,y]: bounding box of opaque pixels, coarse grid (fast enough here).
            int pMinX = int.MaxValue, pMinY = int.MaxValue, pMaxX = -1, pMaxY = -1;
            int gs = Math.Max(1, Math.Max(w, h) / 300);
            for (int y = 0; y < h; y += gs)
                for (int x = 0; x < w; x += gs)
                {
                    int v;
                    try { v = (int)alpha.Pixel[x, y].Gray; } catch { v = 255; }
                    if (v > AlphaThreshold)
                    {
                        if (x < pMinX) pMinX = x; if (x > pMaxX) pMaxX = x;
                        if (y < pMinY) pMinY = y; if (y > pMaxY) pMaxY = y;
                    }
                }
            Log.Info($"  PIXEL box (bottom-origin): X[{pMinX}..{pMaxX}] Y[{pMinY}..{pMaxY}]");

            // (B) Via tiles: dump layout + compute box under THREE Y hypotheses to see which matches.
            dynamic tiles = alpha.Tiles;
            int n = Convert.ToInt32(tiles.Count);
            Log.Info($"  tiles count = {n}");

            // direct: y = tt+ly ;  flip: y = h-1-(tt+ly)
            int dMinX = int.MaxValue, dMinY = int.MaxValue, dMaxX = -1, dMaxY = -1;
            int fMinY = int.MaxValue, fMaxY = -1;
            int shown = 0;
            for (int i = 1; i <= n; i++)
            {
                dynamic t = tiles.Item[i];
                int tl = Convert.ToInt32(t.Left), tt = Convert.ToInt32(t.Top);
                int tw = Convert.ToInt32(t.Width), th = Convert.ToInt32(t.Height);
                int bpp = Convert.ToInt32(t.BytesPerPixel);
                int stride = Math.Abs(Convert.ToInt32(t.BytesPerLine));
                byte[] data = (byte[])t.PixelData;
                if (data == null || bpp < 1) continue;

                bool hasContent = false;
                for (int ly = 0; ly < th; ly++)
                {
                    int rowOff = ly * stride;
                    for (int lx = 0; lx < tw; lx++)
                    {
                        int off = rowOff + lx * bpp;
                        if (off < 0 || off >= data.Length) continue;
                        if (data[off] > AlphaThreshold)
                        {
                            hasContent = true;
                            int x = tl + lx;
                            int yDirect = tt + ly;
                            int yFlip = h - 1 - (tt + ly);
                            if (x < dMinX) dMinX = x; if (x > dMaxX) dMaxX = x;
                            if (yDirect < dMinY) dMinY = yDirect; if (yDirect > dMaxY) dMaxY = yDirect;
                            if (yFlip < fMinY) fMinY = yFlip; if (yFlip > fMaxY) fMaxY = yFlip;
                        }
                    }
                }
                if (hasContent && shown < 6)
                {
                    Log.Info($"  content tile[{i}]: Left={tl} Top={tt} W={tw} H={th} bpp={bpp} stride={stride}");
                    shown++;
                }
            }
            Log.Info($"  TILE box X[{dMinX}..{dMaxX}]  Y-direct[{dMinY}..{dMaxY}]  Y-flip[{fMinY}..{fMaxY}]");
            Log.Info($"  COMPARE: pixel Y[{pMinY}..{pMaxY}] vs direct[{dMinY}..{dMaxY}] vs flip[{fMinY}..{fMaxY}]");
            Log.Info($"  COMPARE: pixel X[{pMinX}..{pMaxX}] vs tile X[{dMinX}..{dMaxX}]");

            // Solve pixelY = K - directY. If the two boxes are the same band, then
            // pMaxY = K - dMinY and pMinY = K - dMaxY, so K = pMaxY + dMinY (should equal pMinY + dMaxY).
            if (pMaxX >= 0 && dMaxX >= 0)
            {
                int k1 = pMaxY + dMinY;
                int k2 = pMinY + dMaxY;
                int dxOffset = dMinX - pMinX; // expected 0 (X matched)
                Log.Info($"  SOLVE: K(top)={k1} K(bottom)={k2} {(k1 == k2 ? "MATCH -> pixelY = K - directY" : "MISMATCH (not a simple flip)")}, X offset={dxOffset}");
                Log.Info($"  HINT: minTileTop and h: tile minTop seen in direct band start; h={h}");
            }

            return "CalibrateTiles готово. Пришлите лог.";
        }

        private static bool SafeBool(Func<dynamic> getter)
        {
            try { return (bool)getter(); }
            catch { return false; }
        }

        private static int SafeInt(Func<dynamic> getter)
        {
            try { return Convert.ToInt32(getter()); }
            catch { return 0; }
        }
    }

    internal readonly struct TrimResult
    {
        public TrimResult(int trimmed, int skipped)
        {
            Trimmed = trimmed;
            Skipped = skipped;
        }

        public int Trimmed { get; }
        public int Skipped { get; }
    }
}
