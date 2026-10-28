using System;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    /// <summary>Renders exact vector patches. All Corel calls belong on the caller's UI thread.</summary>
    internal sealed class ColorProofGridService
    {
        public int Create(ColorProofReference reference, ColorProofGridOptions options)
        {
            if (options == null) throw new InvalidOperationException("Заполните настройки сетки цветопробы.");
            ColorProofGridOptions effective = options.Copy();
            if (effective.BaseChannels == null)
            {
                if (reference == null) throw new InvalidOperationException("Выберите эталон или введите значения каналов вручную.");
                effective.BaseChannels = new ColorProofService().Components(reference, effective.Cmyk);
            }
            // Validate the whole layout before any document mutation.
            ColorProofGridPlan plan = ColorProofGridPlan.Build(effective);
            return Render(plan);
        }

        private static int Render(ColorProofGridPlan plan)
        {
            ColorProofGridOptions options = plan.Options;
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int oldUnit = (int)doc.Unit;
            object originalPage = doc.ActivePage;
            bool started = false, oldOptimization = Convert.ToBoolean(app.Optimization);
            dynamic page = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                const double headerHeight = 27, columnLabelsHeight = 8, footerHeight = 25;
                double pageHeight = 2 * ColorProofGridPlan.MarginMm + headerHeight + columnLabelsHeight + plan.HeightMm + footerHeight;
                if (pageHeight > 45000) throw new InvalidOperationException("Лист цветопробы длиннее 45 м. Уменьшите сетку или размер плашек.");
                app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools — сетка цветопробы"); started = true;
                page = doc.AddPages(1); page.Activate(); page.SetSize(options.SheetWidthMm, pageHeight);
                page.Name = "Сетка " + (options.Cmyk ? "CMYK" : "RGB") + " · " + options.Columns + "×" + options.Rows;
                dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
                double pageLeft = Convert.ToDouble(page.LeftX), pageTop = Convert.ToDouble(page.TopY);
                double left = pageLeft + ColorProofGridPlan.MarginMm;
                double top = pageTop - ColorProofGridPlan.MarginMm;
                double usable = options.SheetWidthMm - 2 * ColorProofGridPlan.MarginMm;
                double gridLeft = left + ColorProofGridPlan.AxisWidthMm;
                double gridTop = top - headerHeight - columnLabelsHeight;
                string[] names = ChannelNames(options.Cmyk);
                string location = "R" + Id(plan.ReferenceRow) + " C" + Id(plan.ReferenceColumn) +
                    (options.Compound ? " Q" + (plan.ReferenceQuadrant + 1) : "");
                AddText(layer, range, left, top, "Сетка цветопробы " + (options.Cmyk ? "CMYK" : "RGB") +
                    " · " + options.Columns + " × " + options.Rows + " · плашек " + plan.Patches.Count, 10f, usable);
                AddText(layer, range, left, top - 5, "Базовый цвет · " + FormatValues(plan.BaseChannels, options.Cmyk) +
                    " · ЭТАЛОН " + location, 7.5f, usable);
                AddText(layer, range, left, top - 10, "X → " + names[options.HorizontalChannel] + " · шаг " + options.StepX +
                    " | Y ↓ " + names[options.VerticalChannel] + " · шаг " + options.StepY +
                    " | значения каналов " + (options.Cmyk ? "0–100%" : "0–255"), 7.5f, usable);
                string sizes = options.Compound
                    ? "Блок " + Mm(options.TileMm) + " × " + Mm(options.TileMm) + " мм; четыре плашки " +
                        Mm(options.TileMm / 2) + " × " + Mm(options.TileMm / 2) + " мм без внутреннего зазора"
                    : "Плашка " + Mm(options.CellMm) + " × " + Mm(options.CellMm) + " мм";
                AddText(layer, range, left, top - 15, sizes + "; между блоками " + Mm(options.GapMm) + " мм.", 7f, usable);
                AddText(layer, range, left, top - 19, "Обозначение R — строка, C — столбец, Q — квадрант. * — положение эталонных значений оси.", 7f, usable);

                double cell = options.Compound ? options.TileMm : options.CellMm;
                double pitch = cell + options.GapMm;
                for (int column = 0; column < options.Columns; column++)
                {
                    string label = "C" + Id(column) + " " + names[options.HorizontalChannel] + ":" + plan.AxisX[column] +
                        (column == plan.ReferenceColumn ? "*" : "");
                    AddText(layer, range, gridLeft + column * pitch, gridTop + columnLabelsHeight - 2,
                        label, 7f, cell, true);
                }
                for (int row = 0; row < options.Rows; row++)
                {
                    string label = "R" + Id(row) + " " + names[options.VerticalChannel] + ":" + plan.AxisY[row] +
                        (row == plan.ReferenceRow ? "*" : "");
                    AddText(layer, range, left, gridTop - row * pitch - cell / 2, label,
                        7f, ColorProofGridPlan.AxisWidthMm - 2, false, true);
                }
                foreach (ColorProofGridPatch patch in plan.Patches)
                {
                    int[] values = patch.Values;
                    dynamic color = options.Cmyk
                        ? app.CreateCMYKColor(values[0], values[1], values[2], values[3])
                        : app.CreateRGBColor(values[0], values[1], values[2]);
                    dynamic shape = layer.CreateRectangle2(gridLeft + patch.Xmm,
                        gridTop - patch.Ymm - patch.HeightMm, patch.WidthMm, patch.HeightMm);
                    range.Add(shape);
                    shape.Fill.ApplyUniformFill(color); shape.Outline.SetNoOutline();
                    shape.Name = "Проба R" + Id(patch.Row) + " C" + Id(patch.Column) +
                        (options.Compound ? " Q" + (patch.Quadrant + 1) : "") + " " + FormatValues(values, options.Cmyk);
                }
                double footerTop = gridTop - plan.HeightMm - 4;
                if (options.Compound)
                {
                    int[] remaining = Enumerable.Range(0, plan.BaseChannels.Length)
                        .Where(channel => channel != options.HorizontalChannel && channel != options.VerticalChannel).ToArray();
                    AddText(layer, range, left, footerTop, "Квадранты: Q1 — слева сверху; Q2 — справа сверху; Q3 — слева снизу; Q4 — справа снизу.", 7f, usable);
                    var legend = plan.Patches.Where(p => p.Row == plan.ReferenceRow && p.Column == plan.ReferenceColumn)
                        .OrderBy(p => p.Quadrant).Select(p => "Q" + (p.Quadrant + 1) + ": " +
                            String.Join(" ", remaining.Select(channel => names[channel] + ":" + p.Values[channel])));
                    AddText(layer, range, left, footerTop - 4, String.Join("   |   ", legend), 7f, usable);
                }
                else
                {
                    string fixedChannels = String.Join(" ", Enumerable.Range(0, plan.BaseChannels.Length)
                        .Where(channel => channel != options.HorizontalChannel && channel != options.VerticalChannel)
                        .Select(channel => names[channel] + ":" + plan.BaseChannels[channel]));
                    AddText(layer, range, left, footerTop, "Фиксированные каналы: " + fixedChannels + ".", 7f, usable);
                }
                AddText(layer, range, left, footerTop - 9,
                    "Заливки — " + (options.Cmyk ? "CMYK" : "RGB") + "; плашки без обводки. Печатайте без масштабирования, 1:1.", 7f, usable);
                range.CreateSelection();
                Log.Info("Color proof grid: model=" + (options.Cmyk ? "CMYK" : "RGB") +
                    ", compound=" + options.Compound + ", columns=" + options.Columns + ", rows=" + options.Rows +
                    ", patches=" + plan.Patches.Count + ", gridMm=" + Mm(plan.WidthMm) + "x" + Mm(plan.HeightMm));
                return plan.Patches.Count;
            }
            catch
            {
                if (page != null) try { page.Delete(); } catch (Exception error) { Log.Error("Color proof grid rollback failed.", error); }
                try { ((dynamic)originalPage).Activate(); } catch { }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); } catch (Exception error) { Log.Error("Color proof grid command group failed.", error); }
                try { doc.Unit = oldUnit; } catch { }
                try { app.Optimization = oldOptimization; } catch { }
            }
        }

        private static void AddText(dynamic layer, dynamic range, double left, double top,
            string value, float points, double maxWidth, bool center = false, bool middle = false)
        {
            dynamic text = layer.CreateArtisticTextWide(0, 0, value, 0, -1, "Arial", points);
            range.Add(text); text.Fill.UniformColor.RGBAssign(0, 0, 0); text.Outline.SetNoOutline();
            double width = (double)text.SizeWidth, height = (double)text.SizeHeight;
            if (width > maxWidth && width > 0)
            {
                text.SetSize(maxWidth, height * maxWidth / width);
                width = (double)text.SizeWidth; height = (double)text.SizeHeight;
            }
            double targetLeft = center ? left + (maxWidth - width) / 2 : left;
            double targetTop = middle ? top + height / 2 : top;
            text.Move(targetLeft - (double)text.LeftX, targetTop - (double)text.TopY);
        }
        private static string Id(int index) => (index + 1).ToString("00", CultureInfo.InvariantCulture);
        private static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        private static string[] ChannelNames(bool cmyk) => cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
        private static string FormatValues(int[] values, bool cmyk) => cmyk
            ? String.Format(CultureInfo.InvariantCulture, "C:{0} M:{1} Y:{2} K:{3}", values[0], values[1], values[2], values[3])
            : String.Format(CultureInfo.InvariantCulture, "R:{0} G:{1} B:{2}", values[0], values[1], values[2]);
    }
}
