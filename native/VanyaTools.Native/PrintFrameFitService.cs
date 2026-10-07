using System;

namespace VanyaTools.Native
{
    internal enum PrintFrameAnchor
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, Center, MiddleRight,
        BottomLeft, BottomCenter, BottomRight
    }

    internal static class PrintFrameFitService
    {
        internal static int FitSelected(PrintFrameAnchor anchor)
        {
            int anchorIndex = (int)anchor;
            if (anchorIndex < 0 || anchorIndex > 8)
                throw new ArgumentOutOfRangeException(nameof(anchor));
            int anchorColumn = anchorIndex % 3;
            int anchorRow = anchorIndex / 3;

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selection = app.ActiveSelectionRange;
            int selectedCount = selection == null ? 0 : Convert.ToInt32(selection.Count);
            if (selectedCount < 2)
                throw new InvalidOperationException("Выделите принт, затем рамку последней (Shift+щелчок).");

            // CorelDRAW returns selected shapes in reverse selection order.
            dynamic frame = selection.FirstShape;
            int pageIndex = Convert.ToInt32(frame.Page.Index);
            dynamic printRange = app.CreateShapeRange();
            for (int i = 2; i <= selectedCount; i++)
            {
                dynamic part = selection.Item[i];
                if (Convert.ToInt32(part.Page.Index) != pageIndex)
                    throw new InvalidOperationException("Все части принта и рамка должны находиться на одной странице.");
                printRange.Add(part);
            }

            double frameWidth = Convert.ToDouble(frame.SizeWidth);
            double frameHeight = Convert.ToDouble(frame.SizeHeight);
            if (!Valid(frameWidth) || !Valid(frameHeight))
                throw new InvalidOperationException("Не удалось определить размер рамки.");
            double frameAnchorX = AnchorX(Convert.ToDouble(frame.CenterX), frameWidth, anchorColumn);
            double frameAnchorY = AnchorY(Convert.ToDouble(frame.TopY), frameHeight, anchorRow);

            bool groupOpen = false;
            bool changed = false;
            try
            {
                doc.BeginCommandGroup("Vanya Tools - fit print frame");
                groupOpen = true;
                dynamic print = selectedCount == 2 ? selection.LastShape : printRange.Group();
                if (print == null) throw new InvalidOperationException("CorelDRAW не смог сгруппировать принт.");
                if (selectedCount > 2) changed = true;
                double printWidth = Convert.ToDouble(print.SizeWidth);
                double printHeight = Convert.ToDouble(print.SizeHeight);
                if (!Valid(printWidth) || !Valid(printHeight))
                    throw new InvalidOperationException("Не удалось определить размер принта.");

                double printAnchorX = AnchorX(Convert.ToDouble(print.CenterX), printWidth, anchorColumn);
                double printAnchorY = AnchorY(Convert.ToDouble(print.TopY), printHeight, anchorRow);
                double movePrintX = frameAnchorX - printAnchorX;
                double movePrintY = frameAnchorY - printAnchorY;
                if (NeedsMove(movePrintX, movePrintY))
                {
                    print.Move(movePrintX, movePrintY);
                    changed = true;
                }

                if (Math.Abs(Convert.ToDouble(frame.SizeWidth) - printWidth) > 0.000001 ||
                    Math.Abs(Convert.ToDouble(frame.SizeHeight) - printHeight) > 0.000001)
                {
                    frame.SetSize(printWidth, printHeight);
                    changed = true;
                }

                // SetSize uses the document reference point; align the resized
                // frame with the print afterwards.
                double moveFrameX = Convert.ToDouble(print.CenterX) - Convert.ToDouble(frame.CenterX);
                double moveFrameY = Convert.ToDouble(print.TopY) - Convert.ToDouble(frame.TopY);
                if (NeedsMove(moveFrameX, moveFrameY))
                {
                    frame.Move(moveFrameX, moveFrameY);
                    changed = true;
                }

                doc.EndCommandGroup();
                groupOpen = false;
                try { app.ActiveWindow.Refresh(); } catch { }
                return selectedCount - 1;
            }
            catch
            {
                if (groupOpen)
                {
                    try { doc.EndCommandGroup(); } catch { }
                }
                if (changed)
                {
                    try { doc.Undo(); }
                    catch (Exception error) { Log.Error("Could not undo failed print-frame fit.", error); }
                }
                throw;
            }
        }

        private static bool Valid(double value)
        {
            return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static double AnchorX(double centerX, double width, int column)
        {
            return centerX + (column - 1) * width / 2.0;
        }

        private static double AnchorY(double topY, double height, int row)
        {
            return topY - row * height / 2.0;
        }

        private static bool NeedsMove(double x, double y)
        {
            return Math.Abs(x) > 0.000001 || Math.Abs(y) > 0.000001;
        }
    }
}
