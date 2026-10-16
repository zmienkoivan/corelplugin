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
            if (dpi != 150 && dpi != 300)
                throw new InvalidOperationException("Выберите качество экспорта 150 или 300 DPI.");
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new InvalidOperationException("Выберите доступную папку для листов печати.");
            folder = System.IO.Path.GetFullPath(folder);
            dynamic document = CorelApp.Get().ActiveDocument;
            if (document == null)
                throw new InvalidOperationException("Откройте CDR со страницей раскладки печати.");
            dynamic page = document.ActivePage;
            string pageName = Convert.ToString(page.Name) ?? "";
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
            if (!printPage || pageName.StartsWith("Резка спортформы · ", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Откройте страницу раскладки печати или допечатки после этапа 2.");
            if (shapeCount == 0) throw new InvalidOperationException("Лист печати пуст.");

            int oldUnit = Convert.ToInt32(document.Unit);
            bool oldDirty = Convert.ToBoolean(document.Dirty);
            object settings = document.PDFSettings;
            var values = Settings(dpi);
            var previous = new List<KeyValuePair<string, object>>();
            string temporary = null, staging = null;
            var result = new Result();
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                result.WidthMm = Convert.ToDouble(page.SizeWidth);
                result.HeightMm = Convert.ToDouble(page.SizeHeight);
                if (result.WidthMm <= 0 || result.HeightMm <= 0 ||
                    Double.IsNaN(result.WidthMm + result.HeightMm) || Double.IsInfinity(result.WidthMm + result.HeightMm))
                    throw new InvalidOperationException("У страницы нет корректного размера.");
                // Stay within the conventional 200-inch PDF page size. Never
                // silently scale a roll-printing job to fit a smaller PDF page.
                if (Math.Max(result.WidthMm, result.HeightMm) > 5080.01)
                    throw new InvalidOperationException("Для PDF в масштабе 1:1 размер страницы должен быть не более 5,08 м. " +
                        "Укажите длину страницы, например 4 м, и повторите этап 2.");

                // Snapshot everything before changing anything. PDF settings
                // belong to the document and must not affect the next export.
                foreach (var value in values)
                    previous.Add(new KeyValuePair<string, object>(value.Key, Get(settings, value.Key)));
                foreach (var value in values) Set(settings, value.Key, value.Value);

                string documentName = System.IO.Path.GetFileNameWithoutExtension(Convert.ToString(document.FileName));
                string prefix = SafeName(documentName) + " - печать - лист " +
                    Convert.ToInt32(page.Index).ToString("000", CultureInfo.InvariantCulture) + " - " +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string target = AvailablePath(folder, prefix);
                string job = Guid.NewGuid().ToString("N");
                temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vanya-print-" + job + ".pdf");
                staging = System.IO.Path.Combine(folder, ".Vanya-print-" + job + ".partial");
                Log.Info("Sport print export: page=" + page.Index + ", " +
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
                for (int i = 0; i < previous.Count; i++)
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

        private static IList<KeyValuePair<string, object>> Settings(int dpi)
        {
            // Values verified against CdrPDFVBALib in CorelDRAW 2026.
            // PDF 1.7 preserves transparency; only bitmap resolution is capped.
            return new List<KeyValuePair<string, object>> {
                Pair("pdfVersion", 8), // pdfVersion17
                Pair("PublishRange", 1), // pdfCurrentPage, never the selection
                Pair("UsePageBoundingBox", true),
                Pair("BitmapCompression", 2), // pdfJPEG
                Pair("JPEGQualityFactor", 95),
                Pair("DownsampleColor", true), Pair("ColorResolution", dpi),
                Pair("DownsampleGray", true), Pair("GrayResolution", dpi),
                Pair("DownsampleMono", true), Pair("MonoResolution", 1200),
                Pair("TextAsCurves", true), Pair("CompressText", true),
                Pair("Encoding", 1), // pdfBinary
                Pair("ComplexFillsAsBitmaps", false),
                Pair("ColorMode", 3), // pdfNative: retain source RGB/CMYK colors
                Pair("UseColorProfile", false), // no composite-printer conversion
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

        private static object Get(object settings, string name) => settings.GetType().InvokeMember(
            name, BindingFlags.GetProperty, null, settings, null);

        private static void Set(object settings, string name, object value) => settings.GetType().InvokeMember(
            name, BindingFlags.SetProperty, null, settings, new[] { value });

        private static string SafeName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) name = "Спортформа";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var result = new StringBuilder();
            foreach (char character in name)
            {
                result.Append(Array.IndexOf(invalid, character) < 0 ? character : '_');
                if (result.Length >= 100) break;
            }
            string safe = result.ToString().Trim().TrimEnd('.');
            return safe.Length == 0 ? "Спортформа" : safe;
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
