using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal sealed class CutMarkService
    {
        private const double FrameWidthMm = 142.0;
        private const double FrameHeightMm = 105.0;
        private const double MarkLegMm = 1.0;
        private const double GuideInsetMm = 4.0;
        private const string CutMarkLayerName = "Vanya Tools — Метки реза";
        private const string GuideLayerName = "Vanya Tools — Внутренняя рамка (не печатать)";

        public int CreateMediumMarks()
        {
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
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);

            doc.BeginCommandGroup("Vanya Tools - create M cut marks and guide");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                doc.ReferencePoint = CorelConstants.CdrCenter;

                double selectedWidth = (double)selection.SizeWidth;
                double selectedHeight = (double)selection.SizeHeight;
                if (selectedWidth > FrameWidthMm || selectedHeight > FrameHeightMm)
                    throw new InvalidOperationException(
                        $"Выделение больше рамки M ({FrameWidthMm:0}×{FrameHeightMm:0} мм). Уменьшите выделение или задайте другой размер.");

                double centerX = ((double)selection.LeftX + (double)selection.RightX) / 2.0;
                double centerY = ((double)selection.TopY + (double)selection.BottomY) / 2.0;
                double left = centerX - FrameWidthMm / 2.0;
                double right = centerX + FrameWidthMm / 2.0;
                double top = centerY + FrameHeightMm / 2.0;
                double bottom = centerY - FrameHeightMm / 2.0;

                dynamic page = doc.ActivePage;
                dynamic cutMarkLayer = GetOrCreateLayer(page, CutMarkLayerName, true);
                dynamic guideLayer = GetOrCreateLayer(page, GuideLayerName, false);

                // Inward-facing L marks: the two 1 mm legs meet exactly at each
                // corner of the M frame and extend toward the pack.
                AddCorner(cutMarkLayer, createdMarks, id, "TL",
                    left, top, left + MarkLegMm, top,
                    left, top - MarkLegMm, left, top);
                AddCorner(cutMarkLayer, createdMarks, id, "TR",
                    right - MarkLegMm, top, right, top,
                    right, top - MarkLegMm, right, top);
                AddCorner(cutMarkLayer, createdMarks, id, "BL",
                    left, bottom, left + MarkLegMm, bottom,
                    left, bottom, left, bottom + MarkLegMm);
                AddCorner(cutMarkLayer, createdMarks, id, "BR",
                    right - MarkLegMm, bottom, right, bottom,
                    right, bottom, right, bottom + MarkLegMm);

                guideShape = guideLayer.CreateRectangle(
                    left + GuideInsetMm,
                    top - GuideInsetMm,
                    right - GuideInsetMm,
                    bottom + GuideInsetMm,
                    0, 0, 0, 0);
                guideShape.Name = "VanyaTools_M_InnerGuide_" + id;
                guideShape.Fill.ApplyNoFill();
                guideShape.Outline.Color.RGBAssign(0, 174, 239);
                guideShape.Outline.Width = 0.15;

                dynamic dashStyle = guideShape.Outline.Style;
                dashStyle.DashCount = 1;
                dashStyle.set_DashLength(1, 8.0);
                dashStyle.set_GapLength(1, 8.0);
                guideShape.Outline.Style = dashStyle;

                for (int i = 0; i < createdMarks.Count; i++)
                {
                    if (i == 0) createdMarks[i].CreateSelection();
                    else createdMarks[i].AddToSelection();
                }

                Log.Info($"Created {createdMarks.Count} inward-facing M cut mark segments and a non-printing {FrameWidthMm - 2 * GuideInsetMm:0}×{FrameHeightMm - 2 * GuideInsetMm:0} mm guide.");
                return createdMarks.Count;
            }
            catch
            {
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
            string id,
            string corner,
            double h1x, double h1y, double h2x, double h2y,
            double v1x, double v1y, double v2x, double v2y)
        {
            AddSegment(layer, createdShapes, id, corner + "_H", h1x, h1y, h2x, h2y);
            AddSegment(layer, createdShapes, id, corner + "_V", v1x, v1y, v2x, v2y);
        }

        private static void AddSegment(
            dynamic layer,
            List<dynamic> createdShapes,
            string id,
            string suffix,
            double startX, double startY, double endX, double endY)
        {
            dynamic shape = layer.CreateLineSegment(startX, startY, endX, endY);
            createdShapes.Add(shape);
            shape.Name = "VanyaTools_CutMark_M_" + id + "_" + suffix;
            StickerCutService.FormatCutContourPublic(shape);
        }
    }
}