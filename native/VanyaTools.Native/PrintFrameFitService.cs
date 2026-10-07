using System;

namespace VanyaTools.Native
{
    internal static class PrintFrameFitService
    {
        internal static void FitSelected()
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selection = app.ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) != 2)
                throw new InvalidOperationException("Выделите ровно два объекта: сначала принт, затем рамку (Shift+щелчок).");

            // CorelDRAW returns selected shapes in reverse selection order.
            dynamic print = selection.LastShape;
            dynamic frame = selection.FirstShape;
            if (Convert.ToInt32(print.Page.Index) != Convert.ToInt32(frame.Page.Index))
                throw new InvalidOperationException("Принт и рамка должны находиться на одной странице.");

            double printWidth = Convert.ToDouble(print.SizeWidth);
            double printHeight = Convert.ToDouble(print.SizeHeight);
            double frameCenterX = Convert.ToDouble(frame.CenterX);
            double frameTopY = Convert.ToDouble(frame.TopY);
            if (!Valid(printWidth) || !Valid(printHeight) ||
                !Valid(Convert.ToDouble(frame.SizeWidth)) || !Valid(Convert.ToDouble(frame.SizeHeight)))
                throw new InvalidOperationException("Не удалось определить размеры принта или рамки.");

            bool groupOpen = false;
            bool changed = false;
            try
            {
                doc.BeginCommandGroup("Vanya Tools - fit print frame");
                groupOpen = true;

                double movePrintX = frameCenterX - Convert.ToDouble(print.CenterX);
                double movePrintY = frameTopY - Convert.ToDouble(print.TopY);
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

                // SetSize uses the document reference point; position the frame
                // afterwards so its top and center match the aligned print.
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

        private static bool NeedsMove(double x, double y)
        {
            return Math.Abs(x) > 0.000001 || Math.Abs(y) > 0.000001;
        }
    }
}
