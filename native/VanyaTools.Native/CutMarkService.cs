using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal sealed class CutMarkService
    {
        private const double MarkLegMm = 1.0;
        private const double GuideInsetMm = 4.0;
        private const string CutMarkLayerName = "Vanya Tools — Метки реза";
        private const string GuideLayerName = "Vanya Tools — Внутренняя рамка (не печатать)";

        public int CreateMarks(string preset, double frameWidthMm, double frameHeightMm, out double scaleFactor)
        {
            scaleFactor = 1.0;
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

            int oldUnit = (int)doc.Unit;
            int oldReferencePoint = (int)doc.ReferencePoint;
            var createdMarks = new List<dynamic>();
            dynamic guideShape = null;
            double originalSelectionWidth = 0.0;
            double originalSelectionHeight = 0.0;
            bool selectionWasResized = false;
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
                double innerWidth = frameWidthMm - 2.0 * GuideInsetMm;
                double innerHeight = frameHeightMm - 2.0 * GuideInsetMm;
                scaleFactor = Math.Min(1.0, Math.Min(innerWidth / selectedWidth, innerHeight / selectedHeight));
                if (scaleFactor < 1.0)
                {
                    selectionWasResized = true;
                    selection.SetSize(selectedWidth * scaleFactor, selectedHeight * scaleFactor);
                    selectedWidth = (double)selection.SizeWidth;
                    selectedHeight = (double)selection.SizeHeight;
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

                // Inward-facing L marks: each pair of 1 mm legs meets at a frame corner.
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

                guideShape = guideLayer.CreateRectangle(
                    left + GuideInsetMm,
                    top - GuideInsetMm,
                    right - GuideInsetMm,
                    bottom + GuideInsetMm,
                    0, 0, 0, 0);
                guideShape.Name = "VanyaTools_" + preset + "_InnerGuide_" + id;
                guideShape.Fill.ApplyNoFill();
                guideShape.Outline.Color.RGBAssign(0, 174, 239);
                guideShape.Outline.Width = 0.15;

                dynamic dashStyle = guideShape.Outline.Style;
                dashStyle.DashCount = 1;
                dashStyle.set_DashLength(1, 8.0);
                dashStyle.set_GapLength(1, 8.0);
                guideShape.Outline.Style = dashStyle;

                selection.CreateSelection();

                Log.Info($"Created {createdMarks.Count} inward-facing {preset} cut mark segments for {frameWidthMm:0}×{frameHeightMm:0} mm and a non-printing {innerWidth:0}×{innerHeight:0} mm guide; sticker pack scale factor {scaleFactor:0.####}.");
                return createdMarks.Count;
            }
            catch
            {
                if (selectionWasResized)
                {
                    try { selection.SetSize(originalSelectionWidth, originalSelectionHeight); } catch { }
                }
                foreach (dynamic shape in createdMarks)
                {
                    try { shape.Delete(); } catch { }
                }
                try { guideShape?.Delete(); } catch { }
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