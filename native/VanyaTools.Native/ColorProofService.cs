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
            if (options.HorizontalChannel == options.VerticalChannel)
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
            double cellHeight = options.SquareMm + 11.0;
            double totalWidth = options.Cells * options.SquareMm + (options.Cells - 1) * gapX;
            double totalHeight = 35.0 + options.Cells * cellHeight;
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
                    "Горизонталь: " + channels[options.HorizontalChannel] + " · вертикаль: " +
                    channels[options.VerticalChannel] + " · шаг: " + options.Step +
                    (options.Cmyk ? "%" : ""), 7f, false);

                int middle = options.Cells / 2;
                for (int row = 0; row < options.Cells; row++)
                {
                    for (int column = 0; column < options.Cells; column++)
                    {
                        int[] values = (int[])center.Clone();
                        values[options.HorizontalChannel] = Clamp(center[options.HorizontalChannel] +
                            (column - middle) * options.Step, limit);
                        values[options.VerticalChannel] = Clamp(center[options.VerticalChannel] +
                            (middle - row) * options.Step, limit);
                        dynamic color = options.Cmyk
                            ? app.CreateCMYKColor(values[0], values[1], values[2], values[3])
                            : app.CreateRGBColor(values[0], values[1], values[2]);
                        double x = left + column * (options.SquareMm + gapX);
                        double y = top - 35.0 - row * cellHeight;
                        AddSwatch(layer, range, x, y, options.SquareMm, color);
                        string label = FormatValues(values, options.Cmyk);
                        dynamic text = AddText(layer, range, 0, 0, label, 7f, true);
                        if ((double)text.SizeWidth > options.SquareMm)
                        {
                            double scale = options.SquareMm / (double)text.SizeWidth;
                            text.SetSize(options.SquareMm, (double)text.SizeHeight * scale);
                        }
                        text.Move(x + (options.SquareMm - (double)text.SizeWidth) / 2.0 -
                            (double)text.LeftX, y - options.SquareMm - 8.0 - (double)text.BottomY);
                        if (row == middle && column == middle)
                        {
                            dynamic marker = AddText(layer, range, x, y - 3.0, "●", 8f, false);
                            marker.Fill.UniformColor.RGBAssign(0, 0, 0);
                        }
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
            double top, double size, dynamic color)
        {
            dynamic border = layer.CreateRectangle2(left, top - size, size, size);
            range.Add(border);
            border.Fill.UniformColor.RGBAssign(150, 150, 150);
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
