using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal sealed class CutMarkService
    {
        private const double MarkLegMm = 1.0;
        private const double GuideInsetMm = 4.0;
        private const double GuideDashLengthMm = 2.0;
        private const double GuideDashGapMm = 1.5;
        private const string CutMarkLayerName = "Vanya Tools — Метки реза";
        private const string GuideLayerName = "Vanya Tools — Внутренняя рамка (не печатать)";

        public int CreateMarks(string preset, double frameWidthMm, double frameHeightMm, bool rotateClockwise, out double scaleFactor, out bool rotatedToPortrait)
        {
            scaleFactor = 1.0;
            rotatedToPortrait = false;
            if (frameWidthMm <= 0 || frameHeightMm <= frameWidthMm)
                throw new InvalidOperationException("Размер рамки должен быть задан в книжной ориентации.");

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            dynamic selection = app.ActiveSelectionRange;
            dynamic oldActiveLayer = doc == null ? null : doc.ActiveLayer;

            if (doc == null)
                throw new InvalidOperationException("Нет активного документа.");
            if (selection == null || (int)selection.Count == 0)
                throw new InvalidOperationException("Выделите весь стикерпак, чтобы разместить метки вокруг него.");

            CutSpotColor.ValidateAvailable(app);
            var selectedShapes = new List<dynamic>();
            foreach (dynamic selectedShape in selection.Shapes)
                selectedShapes.Add(selectedShape);
            if (selectedShapes.Count == 0)
                throw new InvalidOperationException("CorelDRAW не вернул выбранные объекты стикерпака.");

            int oldUnit = (int)doc.Unit;
            int oldReferencePoint = (int)doc.ReferencePoint;
            var createdMarks = new List<dynamic>();
            var createdGuideDashes = new List<dynamic>();
            dynamic createdGuideGroup = null;
            double originalSelectionWidth = 0.0;
            double originalSelectionHeight = 0.0;
            double originalCenterX = 0.0;
            double originalCenterY = 0.0;
            bool selectionWasResized = false;
            bool selectionWasRotated = false;
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);

            doc.BeginCommandGroup("Vanya Tools - create " + preset + " cut marks and guide");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                doc.ReferencePoint = CorelConstants.CdrCenter;

                double selectedWidth = (double)selection.SizeWidth;
                double selectedHeight = (double)selection.SizeHeight;
                if (selectedWidth <= 0 || selectedHeight <= 0)
                    throw new InvalidOperationException("Не удалось определить размер выбранного стикерпака.");

                originalSelectionWidth = selectedWidth;
                originalSelectionHeight = selectedHeight;
                originalCenterX = ((double)selection.LeftX + (double)selection.RightX) / 2.0;
                originalCenterY = ((double)selection.TopY + (double)selection.BottomY) / 2.0;

                // Presets are portrait. Rotate a landscape pack as one range around its own center.
                if (selectedWidth > selectedHeight)
                {
                    double rotationAngle = rotateClockwise ? -90.0 : 90.0;
                    selection.RotateEx(rotationAngle, originalCenterX, originalCenterY);
                    selectionWasRotated = true;
                    rotatedToPortrait = true;
                    selectedWidth = (double)selection.SizeWidth;
                    selectedHeight = (double)selection.SizeHeight;
                    if (selectedWidth > selectedHeight)
                        throw new InvalidOperationException("Не удалось развернуть выделенный стикерпак в книжную ориентацию.");
                }

                double innerWidth = frameWidthMm - 2.0 * GuideInsetMm;
                double innerHeight = frameHeightMm - 2.0 * GuideInsetMm;
                scaleFactor = Math.Min(1.0, Math.Min(innerWidth / selectedWidth, innerHeight / selectedHeight));
                if (scaleFactor < 1.0)
                {
                    selectionWasResized = true;
                    selection.SetSize(selectedWidth * scaleFactor, selectedHeight * scaleFactor);
                    selectedWidth = (double)selection.SizeWidth;
                    selectedHeight = (double)selection.SizeHeight;
                    if (selectedWidth > innerWidth + 0.02 || selectedHeight > innerHeight + 0.02)
                        throw new InvalidOperationException(
                            $"CorelDRAW не уменьшил пак до внутренней рамки {innerWidth:0}×{innerHeight:0} мм. Пак остался {selectedWidth:0.##}×{selectedHeight:0.##} мм.");
                }

                double centerX = ((double)selection.LeftX + (double)selection.RightX) / 2.0;
                double centerY = ((double)selection.TopY + (double)selection.BottomY) / 2.0;
                double left = centerX - frameWidthMm / 2.0;
                double right = centerX + frameWidthMm / 2.0;
                double top = centerY + frameHeightMm / 2.0;
                double bottom = centerY - frameHeightMm / 2.0;

                dynamic page = doc.ActivePage;
                dynamic cutMarkLayer = GetOrCreateLayer(page, CutMarkLayerName, true);
                dynamic guideLayer = GetOrCreateLayer(page, GuideLayerName, false);

                // Each pair forms an inward-facing L: both 1 mm legs extend from the frame corner into the frame.
                AddCorner(cutMarkLayer, createdMarks, preset, id, "TL",
                    left, top, left + MarkLegMm, top,
                    left, top - MarkLegMm, left, top);
                AddCorner(cutMarkLayer, createdMarks, preset, id, "TR",
                    right - MarkLegMm, top, right, top,
                    right, top - MarkLegMm, right, top);
                AddCorner(cutMarkLayer, createdMarks, preset, id, "BL",
                    left, bottom, left + MarkLegMm, bottom,
                    left, bottom, left, bottom + MarkLegMm);
                AddCorner(cutMarkLayer, createdMarks, preset, id, "BR",
                    right - MarkLegMm, bottom, right, bottom,
                    right, bottom, right, bottom + MarkLegMm);

                double guideLeft = left + GuideInsetMm;
                double guideTop = top - GuideInsetMm;
                double guideRight = right - GuideInsetMm;
                double guideBottom = bottom + GuideInsetMm;
                AddDashedEdge(guideLayer, createdGuideDashes, preset, id, "Top",
                    guideLeft, guideTop, guideRight, guideTop);
                AddDashedEdge(guideLayer, createdGuideDashes, preset, id, "Right",
                    guideRight, guideTop, guideRight, guideBottom);
                AddDashedEdge(guideLayer, createdGuideDashes, preset, id, "Bottom",
                    guideRight, guideBottom, guideLeft, guideBottom);
                AddDashedEdge(guideLayer, createdGuideDashes, preset, id, "Left",
                    guideLeft, guideBottom, guideLeft, guideTop);

                createdGuideDashes[0].CreateSelection();
                for (int i = 1; i < createdGuideDashes.Count; i++)
                    createdGuideDashes[i].AddToSelection();
                createdGuideGroup = app.ActiveSelectionRange.Group();
                createdGuideGroup.Name = "VanyaTools_" + preset + "_InnerGuide_" + id;

                if (!TrySelectShapes(selectedShapes))
                    Log.Info("Could not restore the sticker-pack selection after creating the guide.");

                Log.Info($"Created {createdMarks.Count} inward-facing {preset} cut mark segments for {frameWidthMm:0}×{frameHeightMm:0} mm and a non-printing {innerWidth:0}×{innerHeight:0} mm guide; sticker pack scale factor {scaleFactor:0.####}; rotated to portrait: {rotatedToPortrait}.");
                return createdMarks.Count;
            }
            catch
            {
                if (selectionWasRotated || selectionWasResized)
                {
                    try
                    {
                        if (TrySelectShapes(selectedShapes))
                        {
                            dynamic rollbackSelection = app.ActiveSelectionRange;
                            if (selectionWasRotated)
                                rollbackSelection.RotateEx(rotateClockwise ? 90.0 : -90.0, originalCenterX, originalCenterY);
                            if (selectionWasResized)
                                rollbackSelection.SetSize(originalSelectionWidth, originalSelectionHeight);
                        }
                    }
                    catch (Exception restoreError)
                    {
                        Log.Error("Could not restore the sticker-pack transform after a failed mark operation.", restoreError);
                    }
                }
                foreach (dynamic shape in createdMarks)
                {
                    try { shape.Delete(); } catch { }
                }
                if (createdGuideGroup != null)
                {
                    try { createdGuideGroup.Delete(); } catch { }
                }
                else
                {
                    foreach (dynamic shape in createdGuideDashes)
                    {
                        try { shape.Delete(); } catch { }
                    }
                }
                throw;
            }
            finally
            {
                doc.Unit = oldUnit;
                doc.ReferencePoint = oldReferencePoint;
                try { oldActiveLayer?.Activate(); } catch { }
                try { app.ActiveWindow.Refresh(); } catch { }
                doc.EndCommandGroup();
            }
        }

        private static bool TrySelectShapes(List<dynamic> shapes)
        {
            bool selected = false;
            foreach (dynamic shape in shapes)
            {
                try
                {
                    if (!selected) shape.CreateSelection();
                    else shape.AddToSelection();
                    selected = true;
                }
                catch (Exception ex)
                {
                    Log.Error("Could not restore selection of a sticker-pack shape.", ex);
                }
            }
            return selected;
        }

        private static void AddDashedEdge(
            dynamic layer,
            List<dynamic> createdShapes,
            string preset,
            string id,
            string edgeName,
            double startX,
            double startY,
            double endX,
            double endY)
        {
            double dx = endX - startX;
            double dy = endY - startY;
            double edgeLength = Math.Sqrt(dx * dx + dy * dy);
            if (edgeLength <= 0.0)
                return;

            int dashIndex = 1;
            for (double offset = 0.0; offset < edgeLength; offset += GuideDashLengthMm + GuideDashGapMm)
            {
                double dashEnd = Math.Min(offset + GuideDashLengthMm, edgeLength);
                double startRatio = offset / edgeLength;
                double endRatio = dashEnd / edgeLength;
                dynamic dash = layer.CreateLineSegment(
                    startX + dx * startRatio,
                    startY + dy * startRatio,
                    startX + dx * endRatio,
                    startY + dy * endRatio);
                createdShapes.Add(dash);
                dash.Name = "VanyaTools_" + preset + "_InnerGuide_" + id + "_" + edgeName + "_" + dashIndex++;
                dash.Outline.Color.RGBAssign(0, 174, 239);
                dash.Outline.Width = 0.15;
            }
        }

        private static dynamic GetOrCreateLayer(dynamic page, string name, bool printable)
        {
            dynamic layer = null;
            foreach (dynamic candidate in page.Layers)
            {
                try
                {
                    if (string.Equals((string)candidate.Name, name, StringComparison.Ordinal))
                    {
                        layer = candidate;
                        break;
                    }
                }
                catch { }
            }

            if (layer == null)
                layer = page.CreateLayer(name);

            layer.Visible = true;
            layer.Printable = printable;
            layer.Editable = true;
            return layer;
        }

        private static void AddCorner(
            dynamic layer,
            List<dynamic> createdShapes,
            string preset,
            string id,
            string corner,
            double h1x, double h1y, double h2x, double h2y,
            double v1x, double v1y, double v2x, double v2y)
        {
            AddSegment(layer, createdShapes, preset, id, corner + "_H", h1x, h1y, h2x, h2y);
            AddSegment(layer, createdShapes, preset, id, corner + "_V", v1x, v1y, v2x, v2y);
        }

        private static void AddSegment(
            dynamic layer,
            List<dynamic> createdShapes,
            string preset,
            string id,
            string suffix,
            double startX, double startY, double endX, double endY)
        {
            dynamic shape = layer.CreateLineSegment(startX, startY, endX, endY);
            createdShapes.Add(shape);
            shape.Name = "VanyaTools_CutMark_" + preset + "_" + id + "_" + suffix;
            StickerCutService.FormatCutContourPublic(shape);
        }
    }
}