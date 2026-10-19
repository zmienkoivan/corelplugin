using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace VanyaTools.Native
{
    internal sealed class FilmExportResult
    {
        internal readonly List<string> Paths = new List<string>();
        internal readonly List<string> Notes = new List<string>();
    }

    internal static class FilmPdfExporter
    {
        internal static string Folder(string yearFolder, string documentPath)
        {
            if (String.IsNullOrWhiteSpace(yearFolder) || !Path.IsPathRooted(yearFolder) || !Directory.Exists(yearFolder))
                throw new InvalidOperationException("Укажите доступную папку года в «Настройки → Папка года».");
            string year = Path.GetFullPath(yearFolder);
            DateTime date = DateTime.Now;
            string yearName = new DirectoryInfo(year).Name;
            int folderYear = Int32.TryParse(yearName, out int parsedYear) && parsedYear >= 2000 && parsedYear <= 2100 ? parsedYear : date.Year;
            string month = date.Month.ToString("00", CultureInfo.InvariantCulture) +
                new CultureInfo("ru-RU").DateTimeFormat.GetMonthName(date.Month) + folderYear.ToString(CultureInfo.InvariantCulture);
            // Saved order CDRs retain their order month, even when reopened later.
            if (!String.IsNullOrWhiteSpace(documentPath))
            {
                var parent = Directory.GetParent(documentPath);
                for (int depth = 0; parent != null && depth < 4; depth++, parent = parent.Parent)
                {
                    if (parent.Parent == null) break;
                    if (!String.Equals(parent.Parent.FullName.TrimEnd('\\'), year.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    string candidate = parent.Name;
                    bool knownMonth = Enumerable.Range(1, 12).Any(m => String.Equals(candidate,
                        m.ToString("00", CultureInfo.InvariantCulture) + new CultureInfo("ru-RU").DateTimeFormat.GetMonthName(m) +
                        parent.Parent.Name, StringComparison.OrdinalIgnoreCase));
                    if (knownMonth) month = candidate;
                    break;
                }
            }
            return Path.Combine(year, month, "фотовывод");
        }

        internal static FilmExportResult Export(List<FilmTarget> targets, FilmOutputOptions options, string folder)
        {
            options.Validate();
            if (String.IsNullOrWhiteSpace(options.PdfPreset)) throw new InvalidOperationException("Укажите имя заготовки PDF Corel, например filmgate.");
            dynamic app = CorelApp.Get(), source = targets[0].Document;
            int sourceUnit = Convert.ToInt32(source.Unit);
            bool sourceDirty = Convert.ToBoolean(source.Dirty), optimization = Convert.ToBoolean(app.Optimization);
            object sourcePage = source.ActivePage;
            source.Activate();
            var selected = new List<object>(); foreach (dynamic shape in app.ActiveSelectionRange) selected.Add((object)shape);
            dynamic output = null;
            var result = new FilmExportResult();
            var temporary = new List<string>(); var staging = new List<string>(); var destinations = new List<string>();
            try
            {
                source.Unit = CorelConstants.CdrMillimeter;
                var bounds = targets.Select(FilmOutputService.PrintBounds).ToList();
                var gaps = bounds.Select(b => options.ExportBands ? FilmOutputService.BandGap(b, options) : 0).ToList();
                for (int i = 0; i < bounds.Count; i++)
                {
                    double width = bounds[i].Width + (options.ExportBands ? 2 * (gaps[i] + options.BandWidth) : 0);
                    if (width > options.FilmWidth + .001)
                        throw new InvalidOperationException("Ширина фотовывода " + width.ToString("0.##") + " мм превышает плёнку " + options.FilmWidth.ToString("0.##") + " мм.");
                    if (Math.Max(width, bounds[i].Height) > 5080)
                        throw new InvalidOperationException("PDF 1:1 не должен превышать 5,08 м по стороне.");
                    if (options.ExportBands && gaps[i] + .001 < options.BandGap)
                        result.Notes.Add("Слой " + targets[i].Caption.Number + ": зазор до полос уменьшен до " + gaps[i].ToString("0.##") + " мм.");
                }
                string title = SafeName(Path.GetFileNameWithoutExtension(Convert.ToString(source.FileName)));
                source.Activate();
                output = app.CreateDocument();
                output.Unit = CorelConstants.CdrMillimeter;
                output.AssignColorContext(source.ColorContext);
                object settings = output.PDFSettings;
                if (!Convert.ToBoolean(((dynamic)settings).Load(options.PdfPreset.Trim())))
                    throw new InvalidOperationException("В CorelDRAW не найдена заготовка PDF «" + options.PdfPreset +
                        "». Сохраните параметры с ваших скриншотов под этим именем в диалоге PDF или укажите имя существующей заготовки.");
                Configure(settings);
                Directory.CreateDirectory(folder);
                app.Optimization = true;
                string jobId = Guid.NewGuid().ToString("N");
                for (int i = 0; i < targets.Count; i++)
                {
                    source.Activate(); ((dynamic)sourcePage).Activate();
                    object range = FilmOutputService.Range(FilmOutputService.PrintShapes(targets[i]));
                    CorelClipboardTransfer.Copy((dynamic)range, "фотовывод");
                    output.Activate();
                    dynamic page = output.ActivePage, layer = output.ActiveLayer;
                    if (Convert.ToInt32(layer.Shapes.Count) > 0) layer.Shapes.All().Delete();
                    CorelClipboardTransfer.WaitAvailable("фотовывод");
                    dynamic copied = layer.Paste();
                    if (copied == null || Convert.ToInt32(copied.Count) == 0) throw new InvalidOperationException("Не удалось перенести фотовывод во временный документ.");
                    double extra = options.ExportBands ? gaps[i] + options.BandWidth : 0;
                    double width = bounds[i].Width + 2 * extra, height = bounds[i].Height;
                    page.SetSize(width, height);
                    double pageLeft = Convert.ToDouble(page.LeftX), pageBottom = Convert.ToDouble(page.BottomY);
                    // Clipboard placement can use a different origin. Move by
                    // the actual copied bbox while retaining the reserved caption bottom.
                    FilmBounds originalRaw = FilmOutputService.Measure(range), copiedRaw = FilmOutputService.Measure((object)copied);
                    copied.Move(pageLeft + extra - bounds[i].Left + originalRaw.Left - copiedRaw.Left,
                        pageBottom - bounds[i].Bottom + originalRaw.Bottom - copiedRaw.Bottom);
                    dynamic all = app.CreateShapeRange(); foreach (dynamic s in copied) all.Add(s);
                    var printBounds = new FilmBounds { Left = pageLeft + extra, Bottom = pageBottom, Width = bounds[i].Width, Height = height };
                    if (options.ExportBands) all.Add(FilmOutputService.CreateBands((object)layer, printBounds, gaps[i], options.BandWidth));
                    dynamic frame = layer.CreateRectangle2(pageLeft, pageBottom, width, height);
                    frame.Fill.UniformColor.CMYKAssign(0, 0, 0, 0); frame.Outline.SetNoOutline(); frame.OrderToBack(); all.Add(frame);
                    all.CreateSelection();
                    string pdf = Path.Combine(Path.GetTempPath(), "Vanya-film-" + jobId + "-" + i + ".pdf"); temporary.Add(pdf);
                    output.PublishToPDF(pdf);
                    using (Stream stream = File.OpenRead(pdf))
                    {
                        var header = new byte[5];
                        if (stream.Read(header, 0, 5) != 5 || Encoding.ASCII.GetString(header) != "%PDF-")
                            throw new InvalidDataException("CorelDRAW не создал корректный PDF.");
                    }
                    string target = Available(folder, title + " - " + SafeName(targets[i].Caption.Text), destinations);
                    string stage = Path.Combine(folder, ".Vanya-film-" + jobId + "-" + i + ".partial"); staging.Add(stage);
                    File.Copy(pdf, stage); destinations.Add(target);
                }
                // A hot folder receives finished PDFs only after all layers publish.
                for (int i = 0; i < staging.Count; i++) { File.Move(staging[i], destinations[i]); result.Paths.Add(destinations[i]); }
                return result;
            }
            catch (Exception error)
            {
                if (result.Paths.Count > 0)
                    throw new InvalidOperationException("Часть серии уже сохранена. Перед повтором проверьте эти PDF:\n" +
                        String.Join("\n", result.Paths) + "\nОшибка: " + error.GetBaseException().Message, error);
                throw;
            }
            finally
            {
                if (output != null) try { output.Dirty = false; output.Close(); } catch (Exception error) { Log.Error("Could not close temporary film document.", error); }
                try { source.Activate(); ((dynamic)sourcePage).Activate(); ((dynamic)FilmOutputService.Range(selected)).CreateSelection(); }
                catch (Exception error) { result.Notes.Add("Не удалось восстановить исходное выделение."); Log.Error("Could not restore film selection.", error); }
                try { source.Unit = sourceUnit; source.Dirty = sourceDirty; }
                catch (Exception error) { result.Notes.Add("Не удалось восстановить состояние исходного документа."); Log.Error("Could not restore film document state.", error); }
                try { app.Optimization = optimization; }
                catch (Exception error) { result.Notes.Add("Не удалось восстановить режим обновления Corel."); Log.Error("Could not restore Corel optimization.", error); }
                foreach (string path in temporary.Concat(staging))
                    try { if (File.Exists(path)) File.Delete(path); } catch (Exception error) { Log.Error("Could not remove temporary film PDF.", error); }
            }
        }

        private static void Configure(object settings)
        {
            // Acrobat compatibility and ICC embedding come from the user's
            // filmgate preset: the legacy UseColorProfile setter is E_NOTIMPL
            // in Corel 2026 and cannot reproduce that checkbox reliably.
            var values = new Dictionary<string, object> {
                { "PublishRange", 2 }, { "UsePageBoundingBox", false }, { "BitmapCompression", 0 },
                { "DownsampleColor", false }, { "DownsampleGray", false }, { "DownsampleMono", false },
                { "TextAsCurves", true }, { "CompressText", false }, { "ComplexFillsAsBitmaps", false },
                { "ColorMode", 3 }, { "OutputSpotColorsAs", 0 }, { "Overprints", false }, { "Halftones", false },
                { "IncludeBleed", false }, { "CropMarks", false }, { "RegistrationMarks", false },
                { "DensitometerScales", false }, { "FileInformation", false }, { "MaintainOPILinks", false },
                { "EncryptType", 0 },
                { "Hyperlinks", false }, { "Bookmarks", false }, { "Thumbnails", false }
            };
            foreach (var value in values)
            {
                try
                {
                    settings.GetType().InvokeMember(value.Key, BindingFlags.SetProperty, null, settings, new[] { value.Value });
                    object actual = settings.GetType().InvokeMember(value.Key, BindingFlags.GetProperty, null, settings, null);
                    if (!Object.Equals(Convert.ChangeType(actual, value.Value.GetType(), CultureInfo.InvariantCulture), value.Value))
                        throw new InvalidOperationException("Corel не применил значение.");
                }
                catch (Exception error) { throw new InvalidOperationException("Не удалось задать параметр PDF «" + value.Key + "»: " + error.GetBaseException().Message, error); }
            }
        }

        private static string SafeName(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "Фотовывод";
            string result = new string(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return result.Length > 100 ? result.Substring(0, 100) : result;
        }
        private static string Available(string folder, string title, List<string> reserved)
        {
            string path = Path.Combine(folder, title + ".pdf");
            for (int n = 2; File.Exists(path) || reserved.Contains(path, StringComparer.OrdinalIgnoreCase); n++) path = Path.Combine(folder, title + " (" + n + ").pdf");
            return path;
        }
    }
}
