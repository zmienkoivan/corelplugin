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
        public int Cells;
        public double SquareMm;
    }

    internal sealed class ColorProofService
    {
        private sealed class Variant
        {
            public int[] Values;
            public string Caption;
        }

        private sealed class SampleRow
        {
            public string Title;
            public List<Variant> Samples = new List<Variant>();
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
            if (reference == null) throw new InvalidOperationException("Сначала выберите эталонный цвет.");
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

        internal static int[] NearbyValues(int center, int step, int cells, int limit) =>
            Enumerable.Range(-cells / 2, cells).Select(offset => center + offset * step)
                .Where(value => value >= 0 && value <= limit).Distinct().ToArray();

        public int Create(ColorProofReference reference, ColorProofOptions options)
        {
            Check(reference, options.Cells, options.SquareMm);
            int[] center = Components(reference, options.Cmyk);
            int limit = options.Cmyk ? 100 : 255;
            if (options.Step < 1 || options.Step > limit)
                throw new InvalidOperationException("Шаг канала: от 1 до " + limit + ".");
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
                    var row = new SampleRow { Title = names[channel] + " · остальные каналы эталона сохранены" };
                    foreach (int value in NearbyValues(center[channel], options.Step, options.Cells, limit))
                    {
                        var values = (int[])center.Clone(); values[channel] = value;
                        row.Samples.Add(new Variant { Values = values,
                            Caption = names[channel] + " " + Delta(value - center[channel]) });
                    }
                    rows.Add(row);
                }
            }
            else
            {
                int x = options.HorizontalChannel, y = options.VerticalChannel;
                int[] horizontal = NearbyValues(center[x], options.Step, options.Cells, limit);
                foreach (int valueY in NearbyValues(center[y], options.Step, options.Cells, limit).Reverse())
                {
                    var row = new SampleRow { Title = names[y] + " " + Delta(valueY - center[y]) +
                        " · по горизонтали " + names[x] + " от − к +" };
                    foreach (int valueX in horizontal)
                    {
                        var values = (int[])center.Clone(); values[x] = valueX; values[y] = valueY;
                        row.Samples.Add(new Variant { Values = values });
                    }
                    rows.Add(row);
                }
            }
            return Render(reference, options.Cmyk, rows, options.SquareMm,
                "Каналы относительно эталона · шаг " + options.Step + (options.Cmyk ? "%" : ""));
        }

        public int CreateCombinations(ColorProofReference reference, bool cmyk, int[][] levels, double squareMm)
        {
            Check(reference, 3, squareMm);
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
            int total = levels.Aggregate(1, (count, values) => count * values.Distinct().Count());
            if (total > 500) throw new InvalidOperationException("Максимум 500 плашек. Сократите отклонения каналов.");
            const double gap = 3;
            int columns = Math.Min(total, (int)Math.Ceiling(Math.Sqrt(total * (squareMm + 18) / (squareMm + gap))));
            var rows = new List<SampleRow>();
            for (int index = 0; index < total; index++)
            {
                if (index % columns == 0) rows.Add(new SampleRow());
                int remainder = index;
                var values = new int[center.Length];
                for (int channel = 0; channel < values.Length; channel++)
                {
                    int[] distinct = levels[channel].Distinct().ToArray();
                    values[channel] = distinct[remainder % distinct.Length]; remainder /= distinct.Length;
                }
                rows[rows.Count - 1].Samples.Add(new Variant { Values = values });
            }
            return Render(reference, cmyk, rows, squareMm, "Все сочетания отклонений от эталона");
        }

        public int CreateVisual(ColorProofReference reference, bool cmyk, int cells, double squareMm,
            double hueStep, double lightStep, double saturationStep)
        {
            Check(reference, cells, squareMm);
            if (!ValidStep(hueStep, 30) || !ValidStep(lightStep, 20) || !ValidStep(saturationStep, 30))
                throw new InvalidOperationException("Шаг оттенка: 1–30°, светлоты: 1–20%, насыщенности: 1–30%.");
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
                var variants = new SortedDictionary<int, Variant> {
                    [0] = new Variant { Values = (int[])center.Clone(), Caption = "ЭТАЛОН · 0" } };
                for (int offset = -cells / 2; offset <= cells / 2; offset++)
                {
                    if (offset == 0) continue;
                    // Achromatic references have no hue. Do not invent an
                    // arbitrary red starting hue when increasing saturation.
                    if (axis != 0 && saturation < 1e-9) continue;
                    double h = hue, s = saturation, l = lightness;
                    if (axis == 0) l += offset * lightStep / 100;
                    else if (axis == 1) h = (h + offset * hueStep + 360) % 360;
                    else s += offset * saturationStep / 100;
                    if (l < 0 || l > 1 || s < 0 || s > 1) continue;
                    int[] candidate = FromHsl(h, s, l);
                    dynamic color = app.CreateRGBColor(candidate[0], candidate[1], candidate[2]);
                    if (cmyk) color.ConvertToCMYK();
                    int[] values = ReadComponents((object)color, cmyk);
                    if (!unique.Add(String.Join(",", values))) continue;
                    variants.Add(offset, new Variant { Values = values,
                        Caption = Delta(offset * steps[axis]) + (axis == 1 ? "°" : "%") });
                }
                rows.Add(new SampleRow { Title = titles[axis], Samples = variants.Values.ToList() });
            }
            return Render(reference, cmyk, rows, squareMm,
                "Наглядный поиск HSL · каждый ряд меняет одно свойство");
        }

        private static bool ValidStep(double value, double max) =>
            !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 1 && value <= max;

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

        private int Render(ColorProofReference reference, bool cmyk, IList<SampleRow> rows,
            double squareMm, string title)
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int count = rows.Sum(row => row.Samples.Count), oldUnit = (int)doc.Unit;
            object originalPage = doc.ActivePage;
            bool started = false, oldOptimization = Convert.ToBoolean(app.Optimization);
            dynamic page = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools — цветопроба относительно эталона"); started = true;
                double width = Math.Max(180, 16 + rows.Max(row => row.Samples.Count) * (squareMm + 3) - 3);
                double height = 53 + rows.Count * (squareMm + 18);
                page = doc.AddPages(1); page.Activate(); page.SetSize(width, height);
                page.Name = "Цветопроба " + (cmyk ? "CMYK" : "RGB") + " · " + count;
                dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
                double left = Convert.ToDouble(page.LeftX) + 8, top = Convert.ToDouble(page.TopY) - 8;
                int[] center = Components(reference, cmyk);
                AddSwatch(layer, range, left, top, 18, reference.Color);
                AddText(layer, range, left + 23, top - 4, "ЭТАЛОН · " + reference.Name +
                    (reference.IsSpot ? " · плашечный" : ""), 9f, false);
                AddText(layer, range, left + 23, top - 11,
                    FormatValues(center, cmyk).Replace("\r", "  "), 7f, false);
                AddText(layer, range, left, top - 26, title, 7f, false);
                AddText(layer, range, left, top - 32,
                    (cmyk ? "CMYK" : "RGB") + " · " + count + " плашек · эталон выделен тёмной рамкой", 7f, false);
                for (int row = 0; row < rows.Count; row++)
                {
                    double rowTop = top - 40 - row * (squareMm + 18);
                    if (!String.IsNullOrEmpty(rows[row].Title))
                        AddText(layer, range, left, rowTop, rows[row].Title, 7f, false);
                    for (int column = 0; column < rows[row].Samples.Count; column++)
                    {
                        Variant variant = rows[row].Samples[column]; int[] v = variant.Values;
                        dynamic color = cmyk ? app.CreateCMYKColor(v[0], v[1], v[2], v[3]) : app.CreateRGBColor(v[0], v[1], v[2]);
                        double x = left + column * (squareMm + 3), y = rowTop - 4;
                        AddSwatch(layer, range, x, y, squareMm, color, v.SequenceEqual(center));
                        string caption = FormatValues(v, cmyk) + (String.IsNullOrEmpty(variant.Caption) ? "" : "\r" + variant.Caption);
                        dynamic text = AddText(layer, range, 0, 0, caption, 6.5f, true);
                        if ((double)text.SizeWidth > squareMm)
                            text.SetSize(squareMm, (double)text.SizeHeight * squareMm / (double)text.SizeWidth);
                        text.Move(x + (squareMm - (double)text.SizeWidth) / 2 - (double)text.LeftX,
                            y - squareMm - 10 - (double)text.BottomY);
                    }
                }
                range.CreateSelection();
                Log.Info("Color proof: " + title + ", model=" + (cmyk ? "CMYK" : "RGB") + ", samples=" + count);
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

        private static void Check(ColorProofReference reference, int cells, double squareMm)
        {
            if (reference == null) throw new InvalidOperationException("Сначала выберите эталонный цвет.");
            if (cells != 3 && cells != 5 && cells != 7) throw new InvalidOperationException("Число вариантов: 3, 5 или 7.");
            if (Double.IsNaN(squareMm) || Double.IsInfinity(squareMm) || squareMm < 12 || squareMm > 40)
                throw new InvalidOperationException("Размер плашки: от 12 до 40 мм.");
        }

        private static ColorProofReference CopyReference(dynamic app, dynamic source, string fallbackName)
        {
            dynamic color = app.CreateColor(); color.CopyAssign(source);
            bool spot = Convert.ToBoolean(color.IsSpot);
            string name = spot ? Convert.ToString(color.SpotColorName) : fallbackName;
            return new ColorProofReference { Color = color, Name = String.IsNullOrWhiteSpace(name) ? fallbackName : name, IsSpot = spot };
        }

        private static dynamic AddSwatch(dynamic layer, dynamic range, double left, double top, double size, dynamic color, bool highlight = false)
        {
            dynamic border = layer.CreateRectangle2(left, top - size, size, size); range.Add(border);
            border.Fill.UniformColor.RGBAssign(highlight ? 30 : 150, highlight ? 30 : 150, highlight ? 30 : 150);
            border.Outline.SetNoOutline();
            dynamic swatch = layer.CreateRectangle2(left + .3, top - size + .3, size - .6, size - .6); range.Add(swatch);
            swatch.Fill.ApplyUniformFill(color); swatch.Outline.SetNoOutline(); return swatch;
        }

        private static dynamic AddText(dynamic layer, dynamic range, double left, double bottom, string value, float points, bool center)
        {
            dynamic text = layer.CreateArtisticTextWide(left, bottom, value, 0, -1, "Arial", points); range.Add(text);
            text.Fill.UniformColor.RGBAssign(0, 0, 0); text.Outline.SetNoOutline();
            if (center) text.Text.Story.Alignment = 3; return text;
        }

        private static string Delta(double value) => value == 0 ? "0" : value.ToString("+0.##;-0.##", CultureInfo.InvariantCulture);
        private static string[] ChannelNames(bool cmyk) => cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
        private static string FormatValues(int[] values, bool cmyk) => cmyk
            ? String.Format(CultureInfo.InvariantCulture, "C:{0} M:{1}\rY:{2} K:{3}", values[0], values[1], values[2], values[3])
            : String.Format(CultureInfo.InvariantCulture, "R:{0} G:{1}\rB:{2}", values[0], values[1], values[2]);
    }
}
