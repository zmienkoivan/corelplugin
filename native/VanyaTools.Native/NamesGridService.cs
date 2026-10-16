using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    internal enum NamesGridLetterCase { Original, Lower, Title, Upper }

    internal sealed class NamesGridEntry
    {
        public string Text;
        public double? MaxWidthMm;

        public string PrintText(bool twoLines, NamesGridLetterCase letterCase)
        {
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            string text = Text;
            switch (letterCase)
            {
                case NamesGridLetterCase.Lower: text = text.ToLower(culture); break;
                case NamesGridLetterCase.Title:
                    text = culture.TextInfo.ToTitleCase(text.ToLower(culture)); break;
                case NamesGridLetterCase.Upper: text = text.ToUpper(culture); break;
            }
            if (!twoLines) return text;
            int separator = text.IndexOf(' ');
            return separator < 0 ? text : text.Substring(0, separator) + "\r" + text.Substring(separator + 1);
        }
    }

    internal sealed class NamesGridOptions
    {
        public int Columns;
        public double HorizontalGapMm;
        public double VerticalGapMm;
        public bool CenterInCell;
        public NamesGridLetterCase LetterCase;
    }

    internal sealed class NamesGridReferenceInfo
    {
        public double WidthMm, HeightMm;
        public bool TwoLines;
    }

    internal sealed class NamesGridResult
    {
        public int Count;
        public int ReducedCount;
        public double WidthMm;
        public double HeightMm;
    }

    internal sealed class NamesGridService
    {
        private const string ReferenceName = "VT_NAMES_SAMPLE";

        private static List<object> References(dynamic document)
        {
            var result = new List<object>();
            var ids = new HashSet<long>();
            foreach (dynamic page in document.Pages)
                PageReferences((object)page, result, ids);
            // Corel can put objects outside a page on its desktop layer.
            PageReferences((object)document.MasterPage, result, ids);
            return result;
        }

        private static void PageReferences(object pageValue, List<object> result, HashSet<long> ids)
        {
            dynamic page = pageValue;
            // Explicit arguments avoid optional COM parameter conversion.
            dynamic found = page.FindShapes(ReferenceName, 0, true);
            foreach (dynamic shape in found)
                if (ids.Add((long)Convert.ToInt64(shape.StaticID))) result.Add((object)shape);
        }

        private static dynamic Reference(dynamic document)
        {
            List<object> references = References(document);
            if (references.Count == 0)
                throw new InvalidOperationException("Назначьте текстовый образец: создайте заготовку " +
                    "или выделите фигурный текст и нажмите «Взять выделенный текст».");
            if (references.Count > 1)
                throw new InvalidOperationException("В CDR несколько образцов имён. Выделите нужный " +
                    "и нажмите «Взять выделенный текст».");
            return references[0];
        }

        private static NamesGridReferenceInfo DescribeReference(dynamic sample)
        {
            if (Convert.ToInt32(sample.Type) != CorelConstants.CdrTextShape ||
                Convert.ToInt32(sample.Text.Type) != CorelConstants.CdrArtisticText)
                throw new InvalidOperationException("Образец должен быть фигурным текстом, " +
                    "сохранённым текстом, а не кривыми или группой.");
            string text = Convert.ToString(sample.Text.Story.WideText) ?? "";
            string[] lines = text.Replace("\r\n", "\r").Replace('\n', '\r')
                .Replace('\v', '\r').TrimEnd('\r').Split('\r');
            if (lines.Length < 1 || lines.Length > 2 || lines.Any(String.IsNullOrWhiteSpace))
                throw new InvalidOperationException("В образце нужны одна или две непустые строки текста, " +
                    "например XXXXXXXXX.");
            double width = Convert.ToDouble(sample.SizeWidth), height = Convert.ToDouble(sample.SizeHeight);
            if (Double.IsNaN(width) || Double.IsInfinity(width) || width <= 0 || width > 2000 ||
                Double.IsNaN(height) || Double.IsInfinity(height) || height <= 0)
                throw new InvalidOperationException("Проверьте габариты образца: ширина должна быть от 0 до 2000 мм.");
            return new NamesGridReferenceInfo { WidthMm = width, HeightMm = height,
                TwoLines = lines.Length == 2 };
        }

        private static void AssignReference(dynamic document, dynamic sample)
        {
            long selectedId = Convert.ToInt64(sample.StaticID);
            string originalName = Convert.ToString(sample.Name);
            var renamed = new List<object>();
            try
            {
                foreach (dynamic previous in References(document))
                    if (Convert.ToInt64(previous.StaticID) != selectedId)
                    {
                        previous.Name = "VT_NAMES_PREVIOUS_SAMPLE";
                        renamed.Add((object)previous);
                    }
                sample.Name = ReferenceName;
            }
            catch
            {
                try { sample.Name = originalName; } catch { }
                foreach (dynamic previous in renamed)
                    try { previous.Name = ReferenceName; } catch { }
                throw;
            }
        }

        public NamesGridReferenceInfo ReadReferenceInfo()
        {
            dynamic document = CorelApp.Get().ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int oldUnit = Convert.ToInt32(document.Unit);
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                return DescribeReference(Reference(document));
            }
            finally { document.Unit = oldUnit; }
        }

        public NamesGridReferenceInfo AssignSelectedReference()
        {
            dynamic app = CorelApp.Get(), document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selected = app.ActiveSelectionRange;
            if (selected == null || Convert.ToInt32(selected.Count) != 1)
                throw new InvalidOperationException("Выделите один фигурный текст для образца имён.");
            dynamic sample = selected[1];
            int oldUnit = Convert.ToInt32(document.Unit);
            bool commandStarted = false;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                NamesGridReferenceInfo info = DescribeReference(sample);
                document.BeginCommandGroup("Vanya Tools - образец имён");
                commandStarted = true;
                AssignReference(document, sample);
                return info;
            }
            finally
            {
                try { if (commandStarted) document.EndCommandGroup(); } finally { document.Unit = oldUnit; }
            }
        }

        public NamesGridReferenceInfo CreateReference(bool twoLines)
        {
            dynamic app = CorelApp.Get(), document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int oldUnit = Convert.ToInt32(document.Unit);
            bool commandStarted = false;
            dynamic sample = null;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                document.BeginCommandGroup("Vanya Tools - заготовка имён");
                commandStarted = true;
                sample = document.ActiveLayer.CreateArtisticTextWide(10.0,
                    Convert.ToDouble(document.ActivePage.SizeHeight) - 25.0,
                    twoLines ? "XXXXXXXXX\rXXXXXXXXX" : "XXXXXXXXX", 0, -1, "Arial", 36);
                sample.Fill.UniformColor.RGBAssign(0, 0, 0);
                sample.Outline.SetNoOutline();
                if (twoLines) sample.Text.Story.Alignment = 3;
                NamesGridReferenceInfo info = DescribeReference(sample);
                // Keep a new reference outside the print area and away from
                // generated names. Existing, assigned text is never moved.
                sample.Move(-10.0 - info.WidthMm - Convert.ToDouble(sample.LeftX),
                    Convert.ToDouble(document.ActivePage.SizeHeight) - 10.0 - Convert.ToDouble(sample.TopY));
                sample.CreateSelection();
                AssignReference(document, sample);
                try { app.ActiveWindow.ActiveView.ToFitShape(sample); } catch { }
                return info;
            }
            catch
            {
                try { if (sample != null) sample.Delete(); } catch { }
                throw;
            }
            finally
            {
                try { if (commandStarted) document.EndCommandGroup(); } finally { document.Unit = oldUnit; }
            }
        }

        public NamesGridResult Create(IList<NamesGridEntry> names, NamesGridOptions options)
        {
            if (names == null || names.Count == 0)
                throw new InvalidOperationException("Добавьте хотя бы одну строку с именем.");
            if (options == null || options.Columns < 1 || options.Columns > 20)
                throw new InvalidOperationException("Укажите от 1 до 20 колонок.");

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("Откройте документ CorelDRAW.");

            int oldUnit = (int)doc.Unit;
            dynamic range = app.CreateShapeRange();
            var shapes = new List<object>();
            int rows = (names.Count + options.Columns - 1) / options.Columns;
            var columnWidths = new double[options.Columns];
            var rowHeights = new double[rows];
            int reducedCount = 0;
            bool commandStarted = false;

            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                dynamic sample = Reference(doc);
                NamesGridReferenceInfo reference = DescribeReference(sample);
                var paragraphs = new List<object>();
                foreach (dynamic paragraph in sample.Text.Story.Paragraphs) paragraphs.Add((object)paragraph);
                if (paragraphs.Count == 0)
                    throw new InvalidOperationException("Текстовый образец пуст.");
                doc.BeginCommandGroup("Vanya Tools - names grid");
                commandStarted = true;
                dynamic layer = doc.ActiveLayer;
                if (Convert.ToBoolean(layer.IsDesktopLayer))
                    layer = doc.ActivePage.CreateLayer("Имена");

                for (int i = 0; i < names.Count; i++)
                {
                    // Copy the shape's fill, outline and text attributes. The
                    // reference stays untouched and is read afresh each run.
                    dynamic shape = sample.CopyToLayer(layer);
                    shapes.Add((object)shape);
                    range.Add(shape);
                    shape.Name = "VT_NAME_" + (i + 1).ToString("0000", CultureInfo.InvariantCulture);
                    shape.Text.Story.WideText = names[i].PrintText(reference.TwoLines, options.LetterCase);
                    int paragraphIndex = 0;
                    foreach (dynamic paragraph in shape.Text.Story.Paragraphs)
                    {
                        paragraph.CopyAttributes(paragraphs[Math.Min(paragraphIndex, paragraphs.Count - 1)]);
                        paragraphIndex++;
                    }

                    double width = (double)shape.SizeWidth;
                    double height = (double)shape.SizeHeight;
                    if (width <= 0 || height <= 0)
                        throw new InvalidOperationException("CorelDRAW не смог измерить текст: " + names[i].Text);

                    double maxWidth = names[i].MaxWidthMm.HasValue
                        ? Math.Min(reference.WidthMm, names[i].MaxWidthMm.Value)
                        : reference.WidthMm;
                    if (width > maxWidth)
                    {
                        double scale = maxWidth / width;
                        shape.SetSize(maxWidth, height * scale);
                        width = (double)shape.SizeWidth;
                        height = (double)shape.SizeHeight;
                        if (width > maxWidth + 0.1)
                            throw new InvalidOperationException("CorelDRAW не уменьшил текст до заданной ширины: " + names[i].Text);
                        reducedCount++;
                    }

                    int column = i % options.Columns;
                    int row = i / options.Columns;
                    columnWidths[column] = Math.Max(columnWidths[column], width);
                    rowHeights[row] = Math.Max(rowHeights[row], height);
                }

                dynamic page = doc.ActivePage;
                double left = 10.0;
                double top = (double)page.SizeHeight - 10.0;
                double[] columnLefts = new double[options.Columns];
                double[] rowTops = new double[rows];
                double x = left;
                for (int column = 0; column < options.Columns; column++)
                {
                    columnLefts[column] = x;
                    x += columnWidths[column] + (column < options.Columns - 1 ? options.HorizontalGapMm : 0);
                }

                double y = top;
                for (int row = 0; row < rows; row++)
                {
                    rowTops[row] = y;
                    y -= rowHeights[row] + (row < rows - 1 ? options.VerticalGapMm : 0);
                }

                for (int i = 0; i < shapes.Count; i++)
                {
                    dynamic shape = shapes[i];
                    int column = i % options.Columns;
                    int row = i / options.Columns;
                    double targetLeft = columnLefts[column];
                    if (options.CenterInCell)
                        targetLeft += (columnWidths[column] - (double)shape.SizeWidth) / 2.0;
                    shape.Move(targetLeft - (double)shape.LeftX, rowTops[row] - (double)shape.TopY);
                }

                try { range.CreateSelection(); } catch (Exception ex) { Log.Info("Names grid selection: " + ex.Message); }
                try { app.ActiveWindow.ActiveView.ToFitShapeRange(range); } catch { }
                try { app.ActiveWindow.Refresh(); } catch { }
                return new NamesGridResult
                {
                    Count = names.Count,
                    ReducedCount = reducedCount,
                    WidthMm = x - left,
                    HeightMm = top - y
                };
            }
            catch
            {
                try { range.Delete(); } catch (Exception ex) { Log.Error("Names grid rollback failed.", ex); }
                throw;
            }
            finally
            {
                try { doc.Unit = oldUnit; } catch { }
                if (commandStarted)
                    try { doc.EndCommandGroup(); } catch (Exception ex) { Log.Error("Names grid command group failed.", ex); }
            }
        }
    }
}
