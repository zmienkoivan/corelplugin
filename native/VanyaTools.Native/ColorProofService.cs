using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    internal sealed class ColorProofReference
    {
        public dynamic Color;
        public string Name;
        public bool IsSpot;
    }

    internal sealed class ColorProofOptions
    {
        public bool Cmyk;
        public bool AllChannels;
        public int HorizontalChannel;
        public int VerticalChannel;
        public int Step;
    }

    internal sealed class ColorProofService
    {
        internal const int Columns = 21;
        private const int Radius = Columns / 2;

        private sealed class Variant
        {
            public int[] Values;
        }

        private sealed class SampleRow
        {
            public string Title;
            // Null slots retain the reference column when a channel reaches
            // its boundary or a rounded HSL candidate repeats another color.
            public List<Variant> Samples = Enumerable.Repeat<Variant>(null, Columns).ToList();
        }

        public ColorProofReference FromSelection()
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selection = app.ActiveSelectionRange;
            if ((int)selection.Count != 1)
                throw new InvalidOperationException("Выделите один объект с однородной заливкой.");
            dynamic shape = selection[1];
            if ((int)shape.Fill.Type != 1)
                throw new InvalidOperationException("У выбранного объекта нужна однородная заливка.");
            return CopyReference(app, shape.Fill.UniformColor, "Цвет объекта");
        }

        public ColorProofReference FromPalette()
        {
            dynamic app = CorelApp.Get();
            if (app.ActiveDocument == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic color = app.CreateColor();
            if (!Convert.ToBoolean(color.UserAssignEx())) return null;
            return CopyReference(app, color, "Цвет палитры");
        }

        public int[] Components(ColorProofReference reference, bool cmyk)
        {
            Check(reference);
            dynamic app = CorelApp.Get(), converted = app.CreateColor();
            converted.CopyAssign(reference.Color);
            if (cmyk) converted.ConvertToCMYK(); else converted.ConvertToRGB();
            return ReadComponents((object)converted, cmyk);
        }

        private static int[] ReadComponents(object colorObject, bool cmyk)
        {
            dynamic color = colorObject;
            return cmyk ? new[] { (int)color.CMYKCyan, (int)color.CMYKMagenta,
                (int)color.CMYKYellow, (int)color.CMYKBlack } :
                new[] { (int)color.RGBRed, (int)color.RGBGreen, (int)color.RGBBlue };
        }

        public int Create(ColorProofReference reference, ColorProofOptions options, ColorProofSheetOptions sheet = null)
        {
            Check(reference);
            int[] center = Components(reference, options.Cmyk);
            int limit = options.Cmyk ? 100 : 255;
            if (options.Step < 1 || options.Step > limit)
                throw new InvalidOperationException("Шаг канала: целое число от 1 до " + limit + ".");
            if (options.HorizontalChannel < 0 || options.HorizontalChannel >= center.Length ||
                options.VerticalChannel < 0 || options.VerticalChannel >= center.Length ||
                !options.AllChannels && options.HorizontalChannel == options.VerticalChannel)
                throw new InvalidOperationException("Выберите разные каналы для горизонтали и вертикали.");
            string[] names = ChannelNames(options.Cmyk);
            var rows = new List<SampleRow>();
            if (options.AllChannels)
            {
                for (int channel = 0; channel < center.Length; channel++)
                {
                    var row = new SampleRow { Title = names[channel] + " · шаг " + options.Step +
                        (options.Cmyk ? "%" : "") + " · уменьшение ← ЭТАЛОН → увеличение" };
                    for (int offset = -Radius; offset <= Radius; offset++)
                    {
                        int value = center[channel] + offset * options.Step;
                        if (value < 0 || value > limit) continue;
                        var values = (int[])center.Clone(); values[channel] = value;
                        row.Samples[offset + Radius] = new Variant { Values = values };
                    }
                    rows.Add(row);
                }
            }
            else
            {
                int x = options.HorizontalChannel, y = options.VerticalChannel;
                for (int offsetY = Radius; offsetY >= -Radius; offsetY--)
                {
                    int valueY = center[y] + offsetY * options.Step;
                    if (valueY < 0 || valueY > limit) continue;
                    var row = new SampleRow { Title = names[y] + " " + Delta(offsetY * options.Step) +
                        " · по горизонтали " + names[x] + " · шаг " + options.Step };
                    for (int offsetX = -Radius; offsetX <= Radius; offsetX++)
                    {
                        int valueX = center[x] + offsetX * options.Step;
                        if (valueX < 0 || valueX > limit) continue;
                        var values = (int[])center.Clone(); values[x] = valueX; values[y] = valueY;
                        row.Samples[offsetX + Radius] = new Variant { Values = values };
                    }
                    rows.Add(row);
                }
            }
            return Render(reference, options.Cmyk, rows,
                options.AllChannels ? "Каналы относительно эталона" : "Два канала относительно эталона", sheet);
        }

        public int CreateCombinations(ColorProofReference reference, bool cmyk, int[][] levels,
            ColorProofSheetOptions sheet = null)
        {
            Check(reference);
            int[] center = Components(reference, cmyk);
            int limit = cmyk ? 100 : 255;
            if (levels == null || levels.Length != center.Length)
                throw new InvalidOperationException("Заполните отклонения всех каналов.");
            for (int channel = 0; channel < levels.Length; channel++)
            {
                int[] values = levels[channel];
                if (values == null || values.Length == 0 || values.Length > 9 ||
                    values.Any(v => v < 0 || v > limit) || !values.Contains(center[channel]))
                    throw new InvalidOperationException("Списки каналов должны включать эталон и допустимые значения.");
            }
            int[][] uniqueLevels = levels.Select(values => values.Distinct().ToArray()).ToArray();
            int total = uniqueLevels.Aggregate(1, (count, values) => count * values.Length);
            if (total > 500) throw new InvalidOperationException("Максимум 500 сочетаний. Сократите отклонения каналов.");
            var variants = new List<Variant>();
            for (int index = 0; index < total; index++)
            {
                int remainder = index;
                var values = new int[center.Length];
                for (int channel = 0; channel < values.Length; channel++)
                {
                    values[channel] = uniqueLevels[channel][remainder % uniqueLevels[channel].Length];
                    remainder /= uniqueLevels[channel].Length;
                }
                if (!values.SequenceEqual(center)) variants.Add(new Variant { Values = values });
            }
            int Side(Variant variant)
            {
                for (int i = 0; i < center.Length; i++)
                    if (variant.Values[i] != center[i]) return Math.Sign(variant.Values[i] - center[i]);
                return 0;
            }
            double Distance(Variant variant) => variant.Values.Select((value, i) =>
                (double)(value - center[i]) * (value - center[i])).Sum();
            var less = variants.Where(v => Side(v) < 0).OrderBy(Distance).ToList();
            var more = variants.Where(v => Side(v) > 0).OrderBy(Distance).ToList();
            var rows = new List<SampleRow>();
            int rowCount = Math.Max(1, (Math.Max(less.Count, more.Count) + Radius - 1) / Radius);
            for (int r = 0; r < rowCount; r++)
            {
                var row = new SampleRow { Title = "Сочетания · ряд " + (r + 1) + " · эталон в центре" };
                row.Samples[Radius] = new Variant { Values = (int[])center.Clone() };
                for (int i = 0; i < Radius; i++)
                {
                    int index = r * Radius + i;
                    if (index < less.Count) row.Samples[Radius - 1 - i] = less[index];
                    if (index < more.Count) row.Samples[Radius + 1 + i] = more[index];
                }
                rows.Add(row);
            }
            return Render(reference, cmyk, rows, "Сочетания: слева меньше, справа больше по первому отличающемуся каналу", sheet);
        }

        public int CreateVisual(ColorProofReference reference, bool cmyk,
            double hueStep, double lightStep, double saturationStep, ColorProofSheetOptions sheet = null)
        {
            Check(reference);
            if (!ValidStep(hueStep, 30) || !ValidStep(lightStep, 20) || !ValidStep(saturationStep, 30))
                throw new InvalidOperationException("Шаг оттенка: 0,1–30°, светлоты: 0,1–20%, насыщенности: 0,1–30%.");
            int[] rgb = Components(reference, false), center = Components(reference, cmyk);
            ToHsl(rgb, out double hue, out double saturation, out double lightness);
            double[] steps = { lightStep, hueStep, saturationStep };
            string[] titles = { "Темнее ← ЭТАЛОН → светлее", "Оттенок − ← ЭТАЛОН → оттенок +",
                "Менее насыщенный ← ЭТАЛОН → более насыщенный" };
            var rows = new List<SampleRow>();
            dynamic app = CorelApp.Get();
            for (int axis = 0; axis < 3; axis++)
            {
                var unique = new HashSet<string> { String.Join(",", center) };
                var row = new SampleRow { Title = titles[axis] + " · шаг " + steps[axis].ToString("0.##") +
                    (axis == 1 ? "°" : "%") };
                row.Samples[Radius] = new Variant { Values = (int[])center.Clone() };
                // Compare nearer variants first so rounding retains the closest
                // useful color; its slot stays at the actual requested offset.
                foreach (int offset in Enumerable.Range(1, Radius).SelectMany(n => new[] { -n, n }))
                {
                    if (axis != 0 && saturation < 1e-9) continue;
                    double h = hue, s = saturation, l = lightness;
                    if (axis == 0) l += offset * lightStep / 100;
                    else if (axis == 1) h = ((h + offset * hueStep) % 360 + 360) % 360;
                    else s += offset * saturationStep / 100;
                    if (l < -1e-9 || l > 1 + 1e-9 || s < -1e-9 || s > 1 + 1e-9) continue;
                    l = Math.Max(0, Math.Min(1, l)); s = Math.Max(0, Math.Min(1, s));
                    int[] candidate = FromHsl(h, s, l);
                    dynamic color = app.CreateRGBColor(candidate[0], candidate[1], candidate[2]);
                    if (cmyk) color.ConvertToCMYK();
                    int[] values = ReadComponents((object)color, cmyk);
                    if (!unique.Add(String.Join(",", values))) continue;
                    row.Samples[offset + Radius] = new Variant { Values = values };
                }
                rows.Add(row);
            }
            return Render(reference, cmyk, rows, "Наглядный поиск · 10 шагов влево и вправо от эталона", sheet);
        }

        private static bool ValidStep(double value, double max) =>
            !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= .1 && value <= max;

        private static void ToHsl(int[] rgb, out double hue, out double saturation, out double lightness)
        {
            double r = rgb[0] / 255.0, g = rgb[1] / 255.0, b = rgb[2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double difference = max - min;
            lightness = (max + min) / 2; hue = 0; saturation = 0;
            if (difference < 1e-12) return;
            saturation = difference / (1 - Math.Abs(2 * lightness - 1));
            hue = max == r ? (g - b) / difference : max == g ? (b - r) / difference + 2 : (r - g) / difference + 4;
            hue = (hue * 60 + 360) % 360;
        }

        private static int[] FromHsl(double hue, double saturation, double lightness)
        {
            double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            double x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
            double m = lightness - c / 2;
            double r = 0, g = 0, b = 0;
            if (hue < 60) { r = c; g = x; }
            else if (hue < 120) { r = x; g = c; }
            else if (hue < 180) { g = c; b = x; }
            else if (hue < 240) { g = x; b = c; }
            else if (hue < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return new[] { r, g, b }.Select(v => (int)Math.Round(Math.Max(0, Math.Min(1, v + m)) * 255)).ToArray();
        }

        private static List<SampleRow> FitArtworkRows(IList<SampleRow> rows, int columns)
        {
            if (columns == Columns) return rows.ToList();
            var result = new List<SampleRow>();
            foreach (SampleRow source in rows)
            {
                if (columns == 1)
                {
                    foreach (Variant sample in source.Samples.Where(v => v != null))
                        result.Add(new SampleRow { Title = source.Title, Samples = new List<Variant> { sample } });
                    continue;
                }
                int half = columns / 2;
                for (int start = 0; start < Radius; start += half)
                {
                    var target = Enumerable.Repeat<Variant>(null, columns).ToList();
                    target[half] = source.Samples[Radius];
                    bool hasVariants = false;
                    for (int i = 0; i < half && start + i < Radius; i++)
                    {
                        Variant less = source.Samples[Radius - 1 - start - i], more = source.Samples[Radius + 1 + start + i];
                        target[half - 1 - i] = less; target[half + 1 + i] = more;
                        hasVariants |= less != null || more != null;
                    }
                    if (start == 0 || hasVariants)
                        result.Add(new SampleRow { Title = source.Title + (start == 0 ? "" : " · продолжение"), Samples = target });
                }
            }
            return result;
        }

        private int Render(ColorProofReference reference, bool cmyk, IList<SampleRow> rows,
            string title, ColorProofSheetOptions sheet)
        {
            sheet = sheet ?? new ColorProofSheetOptions();
            if (Double.IsNaN(sheet.WidthMm) || Double.IsInfinity(sheet.WidthMm) || sheet.WidthMm < 100 || sheet.WidthMm > 4000)
                throw new InvalidOperationException("Ширина листа цветопробы: от 100 до 4000 мм.");
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int oldUnit = (int)doc.Unit;
            object originalPage = doc.ActivePage;
            bool started = false, oldOptimization = Convert.ToBoolean(app.Optimization);
            dynamic page = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                ColorProofArtwork.Bounds artwork = sheet.Artwork?.Measure((object)doc);
                const double margin = 8, gap = 1.5;
                double usableWidth = sheet.WidthMm - 2 * margin;
                int columns = Columns;
                if (artwork != null)
                {
                    int fit = (int)Math.Floor((usableWidth + gap) / (Math.Max(25, artwork.Width) + gap));
                    if (fit < 1) throw new InvalidOperationException("Логотип не помещается на лист с полями по 8 мм. Увеличьте ширину листа.");
                    columns = Math.Min(Columns, fit % 2 == 0 ? fit - 1 : fit);
                    rows = FitArtworkRows(rows, columns);
                }
                double cellWidth = (usableWidth - (columns - 1) * gap) / columns;
                double sampleWidth = artwork == null ? cellWidth : artwork.Width;
                double sampleHeight = artwork == null ? cellWidth : artwork.Height;
                const double headerHeight = 16;
                double rowPitch = sampleHeight + 9;
                double height = margin * 2 + headerHeight + rows.Count * rowPitch;
                if (height > 45000) throw new InvalidOperationException("Лист цветопробы длиннее 45 м. Уменьшите число вариантов или размер логотипа.");
                int count = rows.Sum(row => row.Samples.Count(v => v != null));
                app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools — компактная цветопроба"); started = true;
                page = doc.AddPages(1); page.Activate(); page.SetSize(sheet.WidthMm, height);
                page.Name = "Цветопроба " + (cmyk ? "CMYK" : "RGB") + " · " + count;
                dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
                double left = Convert.ToDouble(page.LeftX) + margin, top = Convert.ToDouble(page.TopY) - margin;
                int[] center = Components(reference, cmyk);
                AddText(layer, range, left, top - 3, title, 9f, false, usableWidth);
                AddText(layer, range, left, top - 7, "ЭТАЛОН · " + reference.Name + " · " + FormatValues(center, cmyk), 7f, false, usableWidth);
                AddText(layer, range, left, top - 11, "Ширина " + sheet.WidthMm + " мм · ячеек в ряду: " + columns +
                    " · центральная колонка: " + (columns / 2 + 1) + (artwork == null ? "" : " · логотип 1:1"), 7f, false, usableWidth);
                for (int row = 0; row < rows.Count; row++)
                {
                    double rowTop = top - headerHeight - row * rowPitch;
                    AddText(layer, range, left, rowTop, rows[row].Title ?? "", 7f, false, usableWidth);
                    for (int column = 0; column < rows[row].Samples.Count; column++)
                    {
                        double x = left + column * (cellWidth + gap), y = rowTop - 2;
                        Variant variant = rows[row].Samples[column];
                        if (variant == null)
                        {
                            if (artwork == null)
                            {
                                dynamic empty = layer.CreateRectangle2(x, y - sampleHeight, sampleWidth, sampleHeight);
                                range.Add(empty); empty.Fill.ApplyNoFill();
                                empty.Outline.Width = .1; empty.Outline.Color.RGBAssign(215, 215, 215);
                            }
                            continue;
                        }
                        int[] v = variant.Values;
                        dynamic color = cmyk ? app.CreateCMYKColor(v[0], v[1], v[2], v[3]) : app.CreateRGBColor(v[0], v[1], v[2]);
                        if (artwork == null) AddSwatch(layer, range, x, y, sampleWidth, color, v.SequenceEqual(center));
                        else
                        {
                            dynamic logo = sheet.Artwork.Copy((object)doc, (object)layer, (object)color,
                                x + (cellWidth - sampleWidth) / 2, y);
                            logo.Name = "VT_COLOR_PROOF_" + row + "_" + column; range.Add(logo);
                        }
                        dynamic label = AddText(layer, range, 0, 0, FormatValues(v, cmyk), 6.5f, true, cellWidth);
                        label.Move(x + (cellWidth - (double)label.SizeWidth) / 2 - (double)label.LeftX,
                            y - sampleHeight - 1 - (double)label.TopY);
                    }
                }
                range.CreateSelection();
                Log.Info("Color proof: " + title + ", model=" + (cmyk ? "CMYK" : "RGB") + ", samples=" + count +
                    ", artwork=" + (artwork != null) + ", width=" + sheet.WidthMm + ", columns=" + columns + ", rows=" + rows.Count);
                return count;
            }
            catch
            {
                if (page != null) try { page.Delete(); } catch (Exception ex) { Log.Error("Color proof rollback failed.", ex); }
                try { ((dynamic)originalPage).Activate(); } catch { }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); } catch (Exception ex) { Log.Error("Color proof command group failed.", ex); }
                try { doc.Unit = oldUnit; } catch { }
                try { app.Optimization = oldOptimization; } catch { }
            }
        }

        private static void Check(ColorProofReference reference)
        {
            if (reference == null) throw new InvalidOperationException("Сначала выберите эталонный цвет.");
        }

        private static ColorProofReference CopyReference(dynamic app, dynamic source, string fallbackName)
        {
            dynamic color = app.CreateColor(); color.CopyAssign(source);
            bool spot = Convert.ToBoolean(color.IsSpot);
            string name = spot ? Convert.ToString(color.SpotColorName) : fallbackName;
            return new ColorProofReference { Color = color, Name = String.IsNullOrWhiteSpace(name) ? fallbackName : name, IsSpot = spot };
        }

        private static dynamic AddSwatch(dynamic layer, dynamic range, double left, double top, double size, dynamic color, bool highlight)
        {
            dynamic border = layer.CreateRectangle2(left, top - size, size, size); range.Add(border);
            border.Fill.UniformColor.RGBAssign(highlight ? 30 : 150, highlight ? 30 : 150, highlight ? 30 : 150);
            border.Outline.SetNoOutline();
            double inset = highlight ? .45 : .25;
            dynamic swatch = layer.CreateRectangle2(left + inset, top - size + inset, size - 2 * inset, size - 2 * inset); range.Add(swatch);
            swatch.Fill.ApplyUniformFill(color); swatch.Outline.SetNoOutline(); return swatch;
        }

        private static dynamic AddText(dynamic layer, dynamic range, double left, double bottom, string value, float points, bool center,
            double maxWidth = Double.PositiveInfinity)
        {
            dynamic text = layer.CreateArtisticTextWide(left, bottom, value, 0, -1, "Arial", points); range.Add(text);
            text.Fill.UniformColor.RGBAssign(0, 0, 0); text.Outline.SetNoOutline();
            if (center) text.Text.Story.Alignment = 3;
            if ((double)text.SizeWidth > maxWidth)
                text.SetSize(maxWidth, (double)text.SizeHeight * maxWidth / (double)text.SizeWidth);
            return text;
        }

        private static string Delta(double value) => value == 0 ? "0" : value.ToString("+0.##;-0.##", CultureInfo.InvariantCulture);
        private static string[] ChannelNames(bool cmyk) => cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
        private static string FormatValues(int[] values, bool cmyk) => cmyk
            ? String.Format(CultureInfo.InvariantCulture, "C:{0} M:{1} Y:{2} K:{3}", values[0], values[1], values[2], values[3])
            : String.Format(CultureInfo.InvariantCulture, "R:{0} G:{1} B:{2}", values[0], values[1], values[2]);
    }
}
