using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal sealed class CutMarkService
    {
        private const double FrameWidthMm = 142.0;
        private const double FrameHeightMm = 105.0;
        private const double MarkLegMm = 1.0;

        public int CreateMediumMarks()
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            dynamic selection = app.ActiveSelectionRange;

            if (doc == null)
                throw new InvalidOperationException("Нет активного документа.");
            if (selection == null || (int)selection.Count == 0)
                throw new InvalidOperationException("Выделите весь стикерпак, чтобы разместить метки вокруг него.");

            int oldUnit = (int)doc.Unit;
            int oldReferencePoint = (int)doc.ReferencePoint;
            var createdShapes = new List<dynamic>();
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);

            doc.BeginCommandGroup("Vanya Tools - create M cut marks");
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

                dynamic layer = doc.ActiveLayer;
                AddCorner(layer, createdShapes, id, "TL",
                    left - MarkLegMm, top, left, top,
                    left, top, left, top + MarkLegMm);
                AddCorner(layer, createdShapes, id, "TR",
                    right, top, right + MarkLegMm, top,
                    right, top, right, top + MarkLegMm);
                AddCorner(layer, createdShapes, id, "BL",
                    left - MarkLegMm, bottom, left, bottom,
                    left, bottom - MarkLegMm, left, bottom);
                AddCorner(layer, createdShapes, id, "BR",
                    right, bottom, right + MarkLegMm, bottom,
                    right, bottom - MarkLegMm, right, bottom);

                for (int i = 0; i < createdShapes.Count; i++)
                {
                    if (i == 0) createdShapes[i].CreateSelection();
                    else createdShapes[i].AddToSelection();
                }

                Log.Info($"Created {createdShapes.Count} M cut mark segments around selection center ({centerX:0.###}, {centerY:0.###}) mm.");
                return createdShapes.Count;
            }
            catch
            {
                foreach (dynamic shape in createdShapes)
                {
                    try { shape.Delete(); } catch { }
                }
                throw;
            }
            finally
            {
                doc.Unit = oldUnit;
                doc.ReferencePoint = oldReferencePoint;
                try { app.ActiveWindow.Refresh(); } catch { }
                doc.EndCommandGroup();
            }
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