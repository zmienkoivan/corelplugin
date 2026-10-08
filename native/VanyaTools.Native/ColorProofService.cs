using System;
using System.Globalization;

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
        public ColorProofReference FromSelection()
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
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
            if (app.ActiveDocument == null)
                throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic color = app.CreateColor();
            if (!Convert.ToBoolean(color.UserAssignEx())) return null;
            return CopyReference(app, color, "Цвет палитры");
        }

        public ColorProofReference BlueStartingColor()
        {
            dynamic app = CorelApp.Get();
            if (app.ActiveDocument == null)
                throw new InvalidOperationException("Откройте документ CorelDRAW.");
            return CopyReference(app, app.CreateCMYKColor(100, 70, 0, 0),
                "Синий · стартовый C100 M70 Y0 K0");
        }

        public ColorProofReference BlueRgbStartingColor()
        {
            dynamic app = CorelApp.Get();
            if (app.ActiveDocument == null)
                throw new InvalidOperationException("Откройте документ CorelDRAW.");
            return CopyReference(app, app.CreateRGBColor(20, 60, 255),
                "Синий · стартовый R20 G60 B255");
        }

        public int[] Components(ColorProofReference reference, bool cmyk)
        {
            dynamic app = CorelApp.Get();
            dynamic converted = app.CreateColor();
            converted.CopyAssign(reference.Color);
            if (cmyk)
            {
                converted.ConvertToCMYK();
                return new[] { (int)converted.CMYKCyan, (int)converted.CMYKMagenta,
                    (int)converted.CMYKYellow, (int)converted.CMYKBlack };
            }
            converted.ConvertToRGB();
            return new[] { (int)converted.RGBRed, (int)converted.RGBGreen,
                (int)converted.RGBBlue };
        }

        public void Create(ColorProofReference reference, ColorProofOptions options)
        {
            if (reference == null) throw new InvalidOperationException("Сначала выберите эталонный цвет.");
            if (!options.AllChannels && options.HorizontalChannel == options.VerticalChannel)
                throw new InvalidOperationException("Выберите разные каналы для горизонтали и вертикали.");
            if (options.Cells != 3 && options.Cells != 5 && options.Cells != 7)
                throw new InvalidOperationException("Размер сетки: 3, 5 или 7 клеток.");
            if (options.SquareMm < 12 || options.SquareMm > 40)
                throw new InvalidOperationException("Размер плашки: от 12 до 40 мм.");
            if (options.Step < 1 || options.Step > (options.Cmyk ? 50 : 127))
                throw new InvalidOperationException("Проверьте шаг изменения цвета.");

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int[] center = Components(reference, options.Cmyk);
            int limit = options.Cmyk ? 100 : 255;
            double gapX = 5.0;
            double rowHeight = options.SquareMm + (options.AllChannels ? 19.0 : 11.0);
            int rows = options.AllChannels ? center.Length : options.Cells;
            double totalWidth = options.Cells * options.SquareMm + (options.Cells - 1) * gapX;
            double totalHeight = 35.0 + rows * rowHeight;
            int oldUnit = (int)doc.Unit;
            dynamic range = app.CreateShapeRange();
            bool commandStarted = false;
            try
            {
                doc.BeginCommandGroup("Vanya Tools - color proof");
                commandStarted = true;
                doc.Unit = CorelConstants.CdrMillimeter;
                dynamic page = doc.ActivePage;
                if ((double)page.SizeWidth < totalWidth + 20 || (double)page.SizeHeight < totalHeight + 10)
                    throw new InvalidOperationException("Сетка не помещается на странице. Уменьшите плашку или число клеток.");
                dynamic layer = doc.ActiveLayer;
                double left = 10.0;
                double top = (double)page.SizeHeight - 10.0;

                AddSwatch(layer, range, left, top, 20, reference.Color);
                AddText(layer, range, left + 25, top - 5,
                    "ЭТАЛОН" + (reference.IsSpot ? " · плашечный" : ""), 10f, false);
                AddText(layer, range, left + 25, top - 12,
                    reference.Name, 8f, false);
                AddText(layer, range, left + 25, top - 19,
                    FormatValues(center, options.Cmyk).Replace("\r", "   "), 7f, false);

                string[] channels = options.Cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
                AddText(layer, range, left, top - 29,
                    (options.AllChannels ? "Все каналы" :
                    "Горизонталь: " + channels[options.HorizontalChannel] + " · вертикаль: " +
                    channels[options.VerticalChannel]) + " · шаг: " + options.Step +
                    (options.Cmyk ? "%" : ""), 7f, false);

                int middle = options.Cells / 2;
                for (int row = 0; row < rows; row++)
                {
                    double rowTop = top - 35.0 - row * rowHeight;
                    if (options.AllChannels)
                        AddText(layer, range, left, rowTop - 3.0,
                            channels[row] + " · " + ChannelName(options.Cmyk, row), 8f, false);
                    for (int column = 0; column < options.Cells; column++)
                    {
                        int[] values = (int[])center.Clone();
                        if (options.AllChannels)
                            values[row] = Clamp(center[row] + (column - middle) * options.Step, limit);
                        else
                        {
                            values[options.HorizontalChannel] = Clamp(center[options.HorizontalChannel] +
                                (column - middle) * options.Step, limit);
                            values[options.VerticalChannel] = Clamp(center[options.VerticalChannel] +
                                (middle - row) * options.Step, limit);
                        }
                        dynamic color = options.Cmyk
                            ? app.CreateCMYKColor(values[0], values[1], values[2], values[3])
                            : app.CreateRGBColor(values[0], values[1], values[2]);
                        double x = left + column * (options.SquareMm + gapX);
                        double y = rowTop - (options.AllChannels ? 8.0 : 0.0);
                        bool centerVariant = column == middle && (options.AllChannels || row == middle);
                        AddSwatch(layer, range, x, y, options.SquareMm, color, centerVariant);
                        string label = FormatValues(values, options.Cmyk);
                        dynamic text = AddText(layer, range, 0, 0, label, 7f, true);
                        if ((double)text.SizeWidth > options.SquareMm)
                        {
                            double scale = options.SquareMm / (double)text.SizeWidth;
                            text.SetSize(options.SquareMm, (double)text.SizeHeight * scale);
                        }
                        text.Move(x + (options.SquareMm - (double)text.SizeWidth) / 2.0 -
                            (double)text.LeftX, y - options.SquareMm - 8.0 - (double)text.BottomY);
                    }
                }
                range.CreateSelection();
                try { app.ActiveWindow.Refresh(); } catch { }
            }
            catch
            {
                try { range.Delete(); } catch (Exception ex) { Log.Error("Color proof rollback failed.", ex); }
                throw;
            }
            finally
            {
                try { doc.Unit = oldUnit; } catch { }
                if (commandStarted)
                    try { doc.EndCommandGroup(); } catch (Exception ex) { Log.Error("Color proof command group failed.", ex); }
            }
        }

        public int CreateCombinations(ColorProofReference reference, bool cmyk,
            int[][] levels, double squareMm)
        {
            if (reference == null) throw new InvalidOperationException("Сначала выберите эталонный цвет.");
            int channelCount = cmyk ? 4 : 3;
            if (levels == null || levels.Length != channelCount)
                throw new InvalidOperationException("Заполните значения всех каналов.");
            int maxValue = cmyk ? 100 : 255;
            foreach (int[] values in levels)
                if (values == null || values.Length == 0 || values.Length > 9 ||
                    Array.Exists(values, value => value < 0 || value > maxValue))
                    throw new InvalidOperationException("Проверьте значения каналов цветопробы.");
            if (squareMm < 12 || squareMm > 40)
                throw new InvalidOperationException("Размер плашки: от 12 до 40 мм.");
            int totalSwatches = 1;
            foreach (int[] values in levels) totalSwatches *= values.Length;
            if (totalSwatches > 500)
                throw new InvalidOperationException("Слишком много комбинаций. Уменьшите списки до 500 плашек.");

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic originalPage = doc.ActivePage;
            int oldUnit = (int)doc.Unit;
            bool commandStarted = false;
            dynamic createdPage = null;
            try
            {
                doc.BeginCommandGroup("Vanya Tools - color combinations");
                commandStarted = true;
                doc.Unit = CorelConstants.CdrMillimeter;
                const double gapX = 2.0;
                const double labelSpace = 7.0;
                double columnPitch = squareMm + gapX;
                double rowPitch = squareMm + labelSpace;
                int columns = Math.Min(totalSwatches, Math.Max(1,
                    (int)Math.Ceiling(Math.Sqrt(totalSwatches * rowPitch / columnPitch))));
                int rows = (totalSwatches + columns - 1) / columns;
                double tableWidth = columns * squareMm + (columns - 1) * gapX;
                double pageWidth = Math.Max(140.0, tableWidth + 16.0);
                double pageHeight = Math.Max(100.0, 47.0 + rows * rowPitch);
                int[] referenceValues = Components(reference, cmyk);
                createdPage = doc.AddPages(1);
                createdPage.Activate();
                createdPage.SetSize(pageWidth, pageHeight);
                createdPage.Name = "Цветопроба " + (cmyk ? "CMYK" : "RGB") +
                    " · " + totalSwatches;
                dynamic layer = doc.ActiveLayer;
                dynamic range = app.CreateShapeRange();
                double left = 8.0;
                double top = pageHeight - 8.0;
                AddSwatch(layer, range, left, top, 16.0, reference.Color);
                AddText(layer, range, left + 20.0, top - 4.0,
                    "ЭТАЛОН" + (reference.IsSpot ? " · плашечный" : ""), 9f, false);
                AddText(layer, range, left + 20.0, top - 10.0, reference.Name, 7f, false);
                AddText(layer, range, left + 20.0, top - 16.0,
                    FormatValues(referenceValues, cmyk).Replace("\r", "  "), 6.5f, false);
                AddText(layer, range, left, top - 25.0,
                    (cmyk ? "CMYK" : "RGB") + " · все сочетания · " + totalSwatches + " плашек", 7f, false);

                for (int index = 0; index < totalSwatches; index++)
                {
                    int remainder = index;
                    var values = new int[channelCount];
                    for (int channel = 0; channel < channelCount; channel++)
                    {
                        values[channel] = levels[channel][remainder % levels[channel].Length];
                        remainder /= levels[channel].Length;
                    }
                    dynamic color = cmyk
                        ? app.CreateCMYKColor(values[0], values[1], values[2], values[3])
                        : app.CreateRGBColor(values[0], values[1], values[2]);
                    int row = index / columns;
                    int column = index % columns;
                    double x = left + column * columnPitch;
                    double y = top - 31.0 - row * rowPitch;
                    AddSwatch(layer, range, x, y, squareMm, color,
                        SameValues(values, referenceValues));
                    dynamic label = AddText(layer, range, 0, 0,
                        FormatValues(values, cmyk), 6.5f, true);
                    if ((double)label.SizeWidth > squareMm)
                    {
                        double scale = squareMm / (double)label.SizeWidth;
                        label.SetSize(squareMm, (double)label.SizeHeight * scale);
                    }
                    label.Move(x + (squareMm - (double)label.SizeWidth) / 2.0 -
                        (double)label.LeftX, y - squareMm - 5.5 - (double)label.BottomY);
                }
                range.CreateSelection();
                try { app.ActiveWindow.Refresh(); } catch { }
                return 1;
            }
            catch
            {
                if (createdPage != null)
                    try { createdPage.Delete(); }
                    catch (Exception ex) { Log.Error("Color combination page rollback failed.", ex); }
                try { originalPage.Activate(); } catch { }
                throw;
            }
            finally
            {
                try { doc.Unit = oldUnit; } catch { }
                if (commandStarted)
                    try { doc.EndCommandGroup(); } catch (Exception ex) { Log.Error("Color combination command group failed.", ex); }
            }
        }

        private static ColorProofReference CopyReference(dynamic app, dynamic source, string fallbackName)
        {
            dynamic color = app.CreateColor();
            color.CopyAssign(source);
            bool spot = Convert.ToBoolean(color.IsSpot);
            string name = spot ? Convert.ToString(color.SpotColorName) : fallbackName;
            if (String.IsNullOrWhiteSpace(name)) name = spot ? "Плашечный цвет" : fallbackName;
            return new ColorProofReference { Color = color, Name = name, IsSpot = spot };
        }

        private static dynamic AddSwatch(dynamic layer, dynamic range, double left,
            double top, double size, dynamic color, bool highlight = false)
        {
            dynamic border = layer.CreateRectangle2(left, top - size, size, size);
            range.Add(border);
            border.Fill.UniformColor.RGBAssign(highlight ? 30 : 150,
                highlight ? 30 : 150, highlight ? 30 : 150);
            border.Outline.SetNoOutline();
            dynamic swatch = layer.CreateRectangle2(left + 0.3, top - size + 0.3,
                size - 0.6, size - 0.6);
            range.Add(swatch);
            swatch.Fill.ApplyUniformFill(color);
            swatch.Outline.SetNoOutline();
            return swatch;
        }

        private static dynamic AddText(dynamic layer, dynamic range, double left,
            double bottom, string value, float points, bool center)
        {
            dynamic text = layer.CreateArtisticTextWide(left, bottom, value, 0, -1, "Arial", points);
            range.Add(text);
            text.Fill.UniformColor.RGBAssign(0, 0, 0);
            text.Outline.SetNoOutline();
            if (center) text.Text.Story.Alignment = 3;
            return text;
        }

        private static int Clamp(int value, int max) { return Math.Max(0, Math.Min(max, value)); }

        private static bool SameValues(int[] left, int[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static string ChannelName(bool cmyk, int channel)
        {
            return (cmyk
                ? new[] { "голубой", "пурпурный", "жёлтый", "чёрный" }
                : new[] { "красный", "зелёный", "синий" })[channel];
        }

        private static string FormatValues(int[] values, bool cmyk)
        {
            if (cmyk)
                return String.Format(CultureInfo.InvariantCulture,
                    "C:{0} M:{1}\rY:{2} K:{3}", values[0], values[1], values[2], values[3]);
            return String.Format(CultureInfo.InvariantCulture,
                "R:{0} G:{1}\rB:{2}", values[0], values[1], values[2]);
        }
    }
}
