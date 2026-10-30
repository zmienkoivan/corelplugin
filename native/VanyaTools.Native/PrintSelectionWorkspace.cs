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
        internal double PageWidthMm { get; private set; }
        internal double PageHeightMm { get; private set; }
        internal string Warning { get; private set; }

        private PrintSelectionWorkspace(object source, object page, object selection)
        { _source = source; _sourcePage = page; _selection = selection; }

        internal static PrintSelectionWorkspace Create(object application, object source, object page, object selection,
            double safetyMarginMm = 0)
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
                PrintExportBounds sheet = PrintExportBounds.PaddedPage(workspace.Bounds, safetyMarginMm);
                workspace.PageWidthMm = sheet.Width; workspace.PageHeightMm = sheet.Height;
                Log.Info("Print source ink bounds (including outlines): " + PrintExportBounds.Position(
                    workspace.Bounds.Left, workspace.Bounds.Bottom, workspace.Bounds.Width, workspace.Bounds.Height) +
                    "; transparent safety margin=" + safetyMarginMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm.");
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
                targetPage.SetSize(workspace.PageWidthMm, workspace.PageHeightMm);
                // Resizing a Corel page can move its origin and its objects.
                // The pre-SetSize pasted bounds are no longer safe to use for placement.
                pasted = ReadBounds((object)copied);
                PrintExportBounds.RequireSameSize(workspace.PageWidthMm, workspace.PageHeightMm,
                    Convert.ToDouble(targetPage.SizeWidth), Convert.ToDouble(targetPage.SizeHeight), "Страница Corel");
                double pageLeft = Convert.ToDouble(targetPage.LeftX), pageBottom = Convert.ToDouble(targetPage.BottomY);
                if (!PrintExportBounds.Finite(pageLeft) || !PrintExportBounds.Finite(pageBottom))
                    throw new InvalidOperationException("Corel вернул некорректные координаты временной страницы: " +
                        PrintExportBounds.Position(pageLeft, pageBottom, workspace.PageWidthMm, workspace.PageHeightMm) + ". PDF не отправлен.");
                double left = pageLeft + safetyMarginMm, bottom = pageBottom + safetyMarginMm;
                var expected = new PrintExportBounds { Left = left, Bottom = bottom,
                    Width = workspace.Bounds.Width, Height = workspace.Bounds.Height };
                RequirePlacementSize(expected, pasted, "После изменения размера временной страницы");
                copied.Move(left - pasted.Left, bottom - pasted.Bottom);
                PrintExportBounds placed = ReadBounds((object)copied);
                RequirePlacementSize(expected, placed, "После размещения выделения");
                if (Math.Abs(placed.Left - left) > PrintExportBounds.TransferToleranceMm ||
                    Math.Abs(placed.Bottom - bottom) > PrintExportBounds.TransferToleranceMm)
                    throw PlacementError(expected, placed, "Не удалось совместить выделение с границами временной страницы");
                PrintExportBounds.RequireInsidePage(placed, pageLeft, pageBottom,
                    workspace.PageWidthMm, workspace.PageHeightMm, safetyMarginMm);
                copied.CreateSelection();
                Log.Info("Print selection transferred: objects=" + Convert.ToInt32(copied.Count) + ", " +
                    PrintExportBounds.Position(placed.Left, placed.Bottom, placed.Width, placed.Height) + "; page " +
                    PrintExportBounds.Position(pageLeft, pageBottom, workspace.PageWidthMm, workspace.PageHeightMm) +
                    "; verified transparent margins, artwork scale 1:1.");
                return workspace;
            }
            catch { workspace.Dispose(); throw; }
        }

        private static PrintExportBounds ReadBounds(object range)
        {
            double left, bottom, width, height;
            ((dynamic)range).GetBoundingBox(out left, out bottom, out width, out height, true);
            if (!PrintExportBounds.Finite(left) || !PrintExportBounds.Finite(bottom))
                throw new InvalidOperationException("У выделения нет корректных координат: " +
                    PrintExportBounds.Position(left, bottom, width, height) + ". PDF не отправлен.");
            PrintExportBounds.RequirePage(width, height, "Выделение");
            return new PrintExportBounds { Left = left, Bottom = bottom, Width = width, Height = height };
        }

        private static void RequirePlacementSize(PrintExportBounds expected, PrintExportBounds actual, string context)
        {
            if (Math.Abs(actual.Width - expected.Width) > PrintExportBounds.TransferToleranceMm ||
                Math.Abs(actual.Height - expected.Height) > PrintExportBounds.TransferToleranceMm)
                throw PlacementError(expected, actual, context + ": масштаб 1:1 не сохранён");
        }

        private static InvalidOperationException PlacementError(PrintExportBounds expected, PrintExportBounds actual, string context)
            => new InvalidOperationException(context + ". Ожидалось: " +
                PrintExportBounds.Position(expected.Left, expected.Bottom, expected.Width, expected.Height) +
                "; получено: " + PrintExportBounds.Position(actual.Left, actual.Bottom, actual.Width, actual.Height) +
                ". Допуск " + PrintExportBounds.TransferToleranceMm.ToString("0.###", CultureInfo.InvariantCulture) +
                " мм. PDF не отправлен.");

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
        internal const double QueueSafetyMarginMm = 2;
        internal double Left, Bottom, Width, Height;
        internal static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        internal static string Size(double width, double height) =>
            width.ToString("0.###", CultureInfo.InvariantCulture) + " × " + height.ToString("0.###", CultureInfo.InvariantCulture);
        internal static string Position(double left, double bottom, double width, double height) =>
            "X=" + left.ToString("0.###", CultureInfo.InvariantCulture) + " мм, Y=" + bottom.ToString("0.###", CultureInfo.InvariantCulture) +
            " мм, размер " + Size(width, height) + " мм";
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

        internal static PrintExportBounds PaddedPage(PrintExportBounds artwork, double margin)
        {
            if (artwork == null || !Finite(artwork.Left) || !Finite(artwork.Bottom) || !Finite(margin) || margin < 0)
                throw new InvalidOperationException("Некорректные границы или поля печати. PDF не отправлен.");
            RequirePage(artwork.Width, artwork.Height, "Выделение");
            double width = artwork.Width + 2 * margin, height = artwork.Height + 2 * margin;
            RequirePage(width, height, "Страница с защитным полем");
            return new PrintExportBounds { Left = artwork.Left - margin, Bottom = artwork.Bottom - margin,
                Width = width, Height = height };
        }

        internal static void RequireInsidePage(PrintExportBounds artwork, double left, double bottom,
            double width, double height, double margin)
        {
            if (artwork == null || !Finite(left) || !Finite(bottom) || !Finite(margin) || margin < 0 ||
                !Finite(artwork.Left) || !Finite(artwork.Bottom))
                throw new InvalidOperationException("Некорректное размещение выделения. PDF не отправлен.");
            RequirePage(width, height, "Страница печати");
            RequirePage(artwork.Width, artwork.Height, "Выделение печати");
            double tolerance = TransferToleranceMm;
            if (artwork.Left < left + margin - tolerance || artwork.Bottom < bottom + margin - tolerance ||
                artwork.Left + artwork.Width > left + width - margin + tolerance ||
                artwork.Bottom + artwork.Height > bottom + height - margin + tolerance)
                throw new InvalidOperationException("Выделение выходит за защитные поля страницы. PDF не отправлен, чтобы не подрезать рисунок.");
        }
    }
}
