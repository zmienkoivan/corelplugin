using System;
using System.Globalization;

namespace VanyaTools.Native
{
    // A focused child can have valid selection bounds while Corel's PDF
    // selection publisher sees no printable objects. Native clipboard transfer
    // materializes that exact selection on an ordinary layer of a fresh CDR.
    // CopyToLayer cannot transfer objects between documents.
    internal sealed class PrintSelectionWorkspace : IDisposable
    {
        private readonly object _source, _sourcePage, _selection;
        private object _document;
        private bool _disposed;
        internal object Document => _document;
        internal object Page { get; private set; }
        internal PrintExportBounds Bounds { get; private set; }
        internal string Warning { get; private set; }

        private PrintSelectionWorkspace(object source, object page, object selection)
        { _source = source; _sourcePage = page; _selection = selection; }

        internal static PrintSelectionWorkspace Create(object application, object source, object page, object selection)
        {
            dynamic app = application;
            dynamic snapshot = app.CreateShapeRange();
            foreach (dynamic shape in (dynamic)selection) snapshot.Add(shape);
            if (Convert.ToInt32(snapshot.Count) == 0)
                throw new InvalidOperationException("Выделение для печати больше не содержит объектов. Выделите рисунок заново.");
            var workspace = new PrintSelectionWorkspace(source, page, (object)snapshot);
            try
            {
                workspace.Bounds = ReadBounds((object)snapshot);
                PrintExportBounds.RequirePage(workspace.Bounds.Width, workspace.Bounds.Height, "Выделение");
                // Copy before switching documents: this is the real native range,
                // including children of the current Focus group, not its parent.
                CorelClipboardTransfer.Copy(snapshot, "выделение для печати");
                dynamic output = app.CreateDocument(); workspace._document = (object)output;
                output.Unit = CorelConstants.CdrMillimeter;
                output.AssignColorContext(((dynamic)source).ColorContext);
                dynamic targetPage = output.ActivePage, layer = output.ActiveLayer;
                workspace.Page = (object)targetPage;
                if (Convert.ToInt32(layer.Shapes.Count) > 0) layer.Shapes.All().Delete();
                CorelClipboardTransfer.WaitAvailable("выделение для печати");
                dynamic copied = layer.PasteEx(app.CreateStructPasteOptions());
                if (copied == null || Convert.ToInt32(copied.Count) == 0)
                    throw new InvalidOperationException("Corel не перенёс объекты выделения во временный документ. PDF не отправлен.");
                PrintExportBounds pasted = ReadBounds((object)copied);
                PrintExportBounds.RequireSameSize(workspace.Bounds.Width, workspace.Bounds.Height,
                    pasted.Width, pasted.Height, "После переноса выделения");
                targetPage.SetSize(workspace.Bounds.Width, workspace.Bounds.Height);
                PrintExportBounds.RequireSameSize(workspace.Bounds.Width, workspace.Bounds.Height,
                    Convert.ToDouble(targetPage.SizeWidth), Convert.ToDouble(targetPage.SizeHeight), "Страница Corel");
                double left = Convert.ToDouble(targetPage.LeftX), bottom = Convert.ToDouble(targetPage.BottomY);
                if (!PrintExportBounds.Finite(left) || !PrintExportBounds.Finite(bottom))
                    throw new InvalidOperationException("Corel вернул некорректные координаты временной страницы. PDF не отправлен.");
                copied.Move(left - pasted.Left, bottom - pasted.Bottom);
                PrintExportBounds placed = ReadBounds((object)copied);
                PrintExportBounds.RequireSameSize(workspace.Bounds.Width, workspace.Bounds.Height,
                    placed.Width, placed.Height, "После размещения выделения");
                if (Math.Abs(placed.Left - left) > PrintExportBounds.TransferToleranceMm ||
                    Math.Abs(placed.Bottom - bottom) > PrintExportBounds.TransferToleranceMm)
                    throw new InvalidOperationException("Не удалось совместить выделение с границами временной страницы. PDF не отправлен.");
                copied.CreateSelection();
                Log.Info("Print selection transferred: objects=" + Convert.ToInt32(copied.Count) + ", " +
                    PrintExportBounds.Size(workspace.Bounds.Width, workspace.Bounds.Height) + " mm, explicit page bounds.");
                return workspace;
            }
            catch { workspace.Dispose(); throw; }
        }

        private static PrintExportBounds ReadBounds(object range)
        {
            double left, bottom, width, height;
            ((dynamic)range).GetBoundingBox(out left, out bottom, out width, out height, true);
            if (!PrintExportBounds.Finite(left) || !PrintExportBounds.Finite(bottom))
                throw new InvalidOperationException("У выделения нет корректных координат. PDF не отправлен.");
            PrintExportBounds.RequirePage(width, height, "Выделение");
            return new PrintExportBounds { Left = left, Bottom = bottom, Width = width, Height = height };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_document != null)
                try { dynamic output = _document; output.Dirty = false; output.Close(); }
                catch (Exception error) { Log.Error("Could not close temporary print document.", error); }
            try
            {
                ((dynamic)_source).Activate(); ((dynamic)_sourcePage).Activate();
                ((dynamic)_selection).CreateSelection();
            }
            catch (Exception error)
            {
                Warning = "Не удалось восстановить прежнее выделение после экспорта.";
                Log.Error("Could not restore original print selection.", error);
            }
        }
    }

    // Pure geometry checks shared by real native transfer and isolated tests.
    internal sealed class PrintExportBounds
    {
        internal const double TransferToleranceMm = .02;
        internal double Left, Bottom, Width, Height;
        internal static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        internal static string Size(double width, double height) =>
            width.ToString("0.###", CultureInfo.InvariantCulture) + " × " + height.ToString("0.###", CultureInfo.InvariantCulture);
        internal static void RequirePage(double width, double height, string context)
        {
            if (!Finite(width) || !Finite(height) || width <= 0 || height <= 0)
                throw new InvalidOperationException(context + ": некорректный размер " + Size(width, height) + " мм. PDF не отправлен.");
            if (Math.Max(width, height) > 5080.01)
                throw new InvalidOperationException(context + ": " + Size(width, height) + " мм. Для PDF 1:1 сторона не должна превышать 5080 мм (200 дюймов). " +
                    "Разделите задание на меньшие листы или выделения; уменьшение масштаба не выполняется.");
        }
        internal static void RequireSameSize(double expectedWidth, double expectedHeight,
            double actualWidth, double actualHeight, string context)
        {
            RequirePage(actualWidth, actualHeight, context);
            if (Math.Abs(actualWidth - expectedWidth) > TransferToleranceMm ||
                Math.Abs(actualHeight - expectedHeight) > TransferToleranceMm)
                throw new InvalidOperationException(context + ": ожидалось " + Size(expectedWidth, expectedHeight) +
                    " мм, получено " + Size(actualWidth, actualHeight) + " мм. PDF не отправлен; масштаб 1:1 не сохранён.");
        }
    }
}
