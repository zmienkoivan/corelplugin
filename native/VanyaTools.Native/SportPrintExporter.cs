using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace VanyaTools.Native
{
    internal static class SportPrintExporter
    {
        internal sealed class Result
        {
            public string Path, Warning;
            public double WidthMm, HeightMm;
            public long Bytes;
        }

        public static Result ExportCurrentPage(string folder, int dpi)
        {
            return Export(folder, dpi, false, false, true);
        }

        // Shared PDF publisher: ordinary CDRs need no sport markers or page names.
        // DTF/UV deliberately have no fallback from an empty selection to a whole page.
        internal static Result Export(string folder, int dpi, bool selectionOrPage,
            bool selectionOnly, bool sportPage = false, string processName = "DTF", Func<string, bool> confirmPage = null)
        {
            if (dpi != 150 && dpi != 300)
                throw new InvalidOperationException("Выберите качество экспорта 150 или 300 DPI.");
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new InvalidOperationException("Выберите доступную папку для листов печати.");
            folder = System.IO.Path.GetFullPath(folder);
            dynamic document = CorelApp.Get().ActiveDocument;
            if (document == null)
                throw new InvalidOperationException("Откройте документ CorelDRAW для печати.");
            dynamic page = document.ActivePage;
            dynamic selection = CorelApp.Get().ActiveSelectionRange;
            bool selected = (selectionOrPage || selectionOnly) && selection != null && Convert.ToInt32(selection.Count) > 0;
            if (selectionOnly && !selected)
                throw new InvalidOperationException("Для " + processName + " выделите объекты, которые нужно напечатать. Целая страница без выделения не отправляется.");
            string pageName = Convert.ToString(page.Name) ?? "";
            if (selectionOrPage && !selected)
            {
                if (confirmPage == null)
                    throw new InvalidOperationException("Для печати целого листа без выделения нужно подтверждение.");
                string title = Convert.ToString(document.FileName) ?? "Документ";
                string question = "Нет выделения. Печатаем активный лист?\n\n" + title + "\nЛист " +
                    Convert.ToInt32(page.Index) + (String.IsNullOrWhiteSpace(pageName) ? "" : " · " + pageName);
                if (!confirmPage(question)) throw new OperationCanceledException("Печать активного листа отменена.");
                // Publish the page that was shown in the confirmation, even if
                // Corel activates another document while closing the dialog.
                document.Activate(); page.Activate();
            }
            bool printPage = pageName.StartsWith("Спортформа · ", StringComparison.OrdinalIgnoreCase) ||
                pageName.StartsWith("Допечатка · ", StringComparison.OrdinalIgnoreCase);
            int shapeCount = 0;
            foreach (dynamic layer in page.Layers)
            {
                int count = Convert.ToInt32(layer.Shapes.Count);
                shapeCount += count;
                if (count > 0 && String.Equals(Convert.ToString(layer.Name), "Детали печати",
                    StringComparison.OrdinalIgnoreCase)) printPage = true;
            }
            if (sportPage && (!printPage || pageName.StartsWith("Резка спортформы · ", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Откройте страницу раскладки печати или допечатки после этапа 2.");
            if (!selected && shapeCount == 0) throw new InvalidOperationException("Лист печати пуст.");

            int oldUnit = Convert.ToInt32(document.Unit);
            bool oldDirty = Convert.ToBoolean(document.Dirty);
            object settings = document.PDFSettings;
            var values = Settings(dpi, selected, selectionOnly);
            var previous = new List<KeyValuePair<string, object>>();
            int applied = 0;
            string temporary = null, staging = null;
            var result = new Result();
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                if (selected)
                {
                    double left, bottom, width, height;
                    selection.GetBoundingBox(out left, out bottom, out width, out height, true);
                    result.WidthMm = width; result.HeightMm = height;
                }
                else
                {
                    result.WidthMm = Convert.ToDouble(page.SizeWidth);
                    result.HeightMm = Convert.ToDouble(page.SizeHeight);
                }
                if (result.WidthMm <= 0 || result.HeightMm <= 0 ||
                    Double.IsNaN(result.WidthMm + result.HeightMm) || Double.IsInfinity(result.WidthMm + result.HeightMm))
                    throw new InvalidOperationException("У страницы нет корректного размера.");
                // Stay within the conventional 200-inch PDF page size. Never
                // silently scale a roll-printing job to fit a smaller PDF page.
                if (Math.Max(result.WidthMm, result.HeightMm) > 5080.01)
                    throw new InvalidOperationException("Для PDF в масштабе 1:1 размер страницы должен быть не более 5,08 м. " +
                        "Разделите задание на меньшие листы или выделения.");

                // Snapshot everything before changing anything. PDF settings
                // belong to the document and must not affect the next export.
                foreach (var value in values)
                    previous.Add(new KeyValuePair<string, object>(value.Key, Get(settings, value.Key)));
                foreach (var value in values)
                {
                    Set(settings, value.Key, value.Value);
                    applied++;
                }

                string documentName = System.IO.Path.GetFileNameWithoutExtension(Convert.ToString(document.FileName));
                string job = Guid.NewGuid().ToString("N");
                string prefix = SafeName(documentName) + (selectionOnly ? " - " + processName + " - " : " - печать - ") +
                    (selected ? "выделение - лист " : "лист ") +
                    Convert.ToInt32(page.Index).ToString("000", CultureInfo.InvariantCulture) + " - " +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + job.Substring(0, 8);
                string target = AvailablePath(folder, prefix);
                temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vanya-print-" + job + ".pdf");
                staging = System.IO.Path.Combine(folder, ".Vanya-print-" + job + ".partial");
                Log.Info("Print PDF export: selection=" + selected + ", lossless=" + selectionOnly + ", page=" + page.Index + ", " +
                    result.WidthMm.ToString("0.###", CultureInfo.InvariantCulture) + "x" +
                    result.HeightMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm, " + dpi + " DPI, PDF.");
                document.PublishToPDF(temporary);
                using (var stream = File.OpenRead(temporary))
                {
                    byte[] header = new byte[5];
                    if (stream.Read(header, 0, header.Length) != header.Length ||
                        Encoding.ASCII.GetString(header) != "%PDF-")
                        throw new InvalidDataException("CorelDRAW не создал корректный PDF.");
                }
                // Native publishing happens outside the watched print folder.
                // Copy with a non-PDF extension, then rename on the destination
                // volume only when complete (also works for network folders).
                File.Copy(temporary, staging);
                File.Move(staging, target);
                result.Path = target;
                result.Bytes = new FileInfo(target).Length;
                Log.Info("Sport print export complete: " + System.IO.Path.GetFileName(target) +
                    ", bytes=" + result.Bytes);
                return result;
            }
            finally
            {
                bool restoreFailed = false;
                // Restore compatibility first, since changing it can reset or
                // constrain the remaining PDF options in Corel.
                for (int i = 0; i < applied; i++)
                    try { Set(settings, previous[i].Key, previous[i].Value); }
                    catch (Exception error)
                    { restoreFailed = true; Log.Error("Could not restore PDF setting " + previous[i].Key + ".", error); }
                try { document.Unit = oldUnit; document.Dirty = oldDirty; }
                catch (Exception error)
                { restoreFailed = true; Log.Error("Could not restore document state after print export.", error); }
                if (restoreFailed) result.Warning = "PDF сохранён, но не все прежние настройки экспорта Corel удалось восстановить.";
                if (temporary != null)
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception error) { Log.Error("Could not remove incomplete print PDF.", error); }
                if (staging != null)
                    try { if (File.Exists(staging)) File.Delete(staging); }
                    catch (Exception error) { Log.Error("Could not remove staged print PDF.", error); }
            }
        }

        private static IList<KeyValuePair<string, object>> Settings(int dpi, bool selected, bool lossless)
        {
            // Values verified against CdrPDFVBALib in CorelDRAW 2026.
            // PDF 1.7 preserves transparency; only bitmap resolution is capped.
            return new List<KeyValuePair<string, object>> {
                Pair("pdfVersion", 8), // pdfVersion17
                Pair("PublishRange", selected ? 2 : 1), // pdfSelection / pdfCurrentPage
                Pair("UsePageBoundingBox", !selected), // selection gets its own bounds, including outside the page
                Pair("BitmapCompression", lossless ? 3 : 2), // pdfZIP (lossless) / pdfJPEG
                Pair("JPEGQualityFactor", 95),
                Pair("DownsampleColor", !lossless), Pair("ColorResolution", dpi),
                Pair("DownsampleGray", !lossless), Pair("GrayResolution", dpi),
                Pair("DownsampleMono", !lossless), Pair("MonoResolution", 1200),
                Pair("TextAsCurves", true), Pair("CompressText", true),
                Pair("Encoding", 1), // pdfBinary
                Pair("ComplexFillsAsBitmaps", false),
                Pair("ColorMode", 3), // pdfNative: retain source RGB/CMYK colors
                // The legacy UseColorProfile setter is an E_NOTIMPL stub in
                // CorelDRAW 2026 even though its getter/type-library exist.
                // ColorMode=pdfNative is the supported color-mode control.
                Pair("OutputSpotColorsAs", 0), // pdfSpotAsSpot
                Pair("IncludeBleed", false), Pair("Bleed", 0),
                Pair("CropMarks", false), Pair("RegistrationMarks", false),
                Pair("DensitometerScales", false), Pair("FileInformation", false),
                Pair("Hyperlinks", false), Pair("Bookmarks", false), Pair("Thumbnails", false),
                Pair("EmbedFile", false), Pair("MaintainOPILinks", false),
                Pair("Linearize", false), Pair("EncryptType", 0),
                Pair("PrintPermissions", 2) // pdfPrintPermissionHighResolution
            };
        }

        private static KeyValuePair<string, object> Pair(string name, object value) =>
            new KeyValuePair<string, object>(name, value);

        private static object Get(object settings, string name)
        {
            try { return settings.GetType().InvokeMember(name, BindingFlags.GetProperty, null, settings, null); }
            catch (Exception error)
            {
                Log.Error("Could not read PDF setting " + name + ".", error);
                throw new InvalidOperationException("Не удалось прочитать параметр PDF «" + name + "»: " +
                    error.GetBaseException().Message, error);
            }
        }

        private static void Set(object settings, string name, object value)
        {
            try { settings.GetType().InvokeMember(name, BindingFlags.SetProperty, null, settings, new[] { value }); }
            catch (Exception error)
            {
                Log.Error("Could not set PDF setting " + name + ".", error);
                throw new InvalidOperationException("Не удалось установить параметр PDF «" + name + "»: " +
                    error.GetBaseException().Message, error);
            }
        }

        private static string SafeName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) name = "Документ";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var result = new StringBuilder();
            foreach (char character in name)
            {
                result.Append(Array.IndexOf(invalid, character) < 0 ? character : '_');
                if (result.Length >= 100) break;
            }
            string safe = result.ToString().Trim().TrimEnd('.');
            return safe.Length == 0 ? "Документ" : safe;
        }

        private static string AvailablePath(string folder, string prefix)
        {
            string path = System.IO.Path.Combine(folder, prefix + ".pdf");
            for (int copy = 2; File.Exists(path); copy++)
                path = System.IO.Path.Combine(folder, prefix + " - " + copy + ".pdf");
            return path;
        }
    }
}
