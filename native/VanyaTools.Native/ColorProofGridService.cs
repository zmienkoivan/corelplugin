using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    /// <summary>Renders exact vector patches. All Corel calls belong on the caller's UI thread.</summary>
    internal sealed class ColorProofGridService
    {
        private const double HeaderHeightMm = 34, ColumnLabelsHeightMm = 8, FooterHeightMm = 25;

        public int Create(ColorProofReference reference, ColorProofGridOptions options)
        {
            ColorProofGridOptions effective = ResolveGrid(reference, options);
            ColorProofGridPlan plan = ColorProofGridPlan.Build(effective);
            return Render(reference, new[] { plan }, null);
        }

        public int CreateSeries(ColorProofReference reference, ColorProofGridSeriesOptions options)
        {
            if (options == null) throw new InvalidOperationException("Заполните настройки серии цветопроб.");
            ColorProofGridSeriesOptions effective = options.Copy();
            effective.Grid = ResolveGrid(reference, effective.Grid);
            // Every page, explicit offset, dimension and total count is validated before mutation.
            ColorProofGridSeriesPlan series = ColorProofGridSeriesPlan.Build(effective);
            return Render(reference, series.Pages, series);
        }

        private static ColorProofGridOptions ResolveGrid(ColorProofReference reference, ColorProofGridOptions options)
        {
            if (options == null) throw new InvalidOperationException("Заполните настройки сетки цветопробы.");
            ColorProofGridOptions effective = options.Copy();
            if (effective.BaseChannels == null)
            {
                if (reference == null) throw new InvalidOperationException("Выберите эталон или введите значения каналов вручную.");
                effective.BaseChannels = new ColorProofService().Components(reference, effective.Cmyk);
            }
            return effective;
        }

        private static int Render(ColorProofReference reference, IReadOnlyList<ColorProofGridPlan> plans, ColorProofGridSeriesPlan series)
        {
            foreach (ColorProofGridPlan plan in plans)
                if (PageHeight(plan) > 45000)
                    throw new InvalidOperationException("Лист цветопробы длиннее 45 м. Уменьшите сетку или размер плашек.");
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int oldUnit = (int)doc.Unit;
            object originalPage = doc.ActivePage;
            bool started = false, oldOptimization = Convert.ToBoolean(app.Optimization);
            var createdPages = new List<object>();
            int total = 0;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                app.Optimization = true;
                doc.BeginCommandGroup(series == null ? "Vanya Tools — сетка цветопробы" : "Vanya Tools — серия таблиц цветопробы");
                started = true;
                object lastRange = null;
                for (int index = 0; index < plans.Count; index++)
                {
                    ColorProofGridPlan plan = plans[index];
                    dynamic page = doc.AddPages(1);
                    createdPages.Add((object)page);
                    page.Activate(); page.SetSize(plan.Options.SheetWidthMm, PageHeight(plan));
                    page.Name = (series == null ? "Сетка " : "Цветопроба " + (index + 1) + "/" + plans.Count + " · ") +
                        (plan.Options.Cmyk ? "CMYK" : "RGB") + " · " + plan.Options.Columns + "×" + plan.Options.Rows;
                    lastRange = DrawPage((object)app, (object)doc, (object)page, reference, plan, series, index);
                    total += plan.Patches.Count;
                }
                if (lastRange != null) ((dynamic)lastRange).CreateSelection();
                Log.Info("Color proof grids created: pages=" + plans.Count + ", grid patches=" + total + ", series=" + (series != null));
                return total;
            }
            catch
            {
                for (int index = createdPages.Count - 1; index >= 0; index--)
                    try { ((dynamic)createdPages[index]).Delete(); }
                    catch (Exception error) { Log.Error("Color proof grid page rollback failed.", error); }
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

        private static object DrawPage(object appObject, object documentObject, object pageObject,
            ColorProofReference reference, ColorProofGridPlan plan, ColorProofGridSeriesPlan series, int pageIndex)
        {
            dynamic app = appObject, doc = documentObject, page = pageObject;
            ColorProofGridOptions options = plan.Options;
            dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
            double pageLeft = Convert.ToDouble(page.LeftX), pageTop = Convert.ToDouble(page.TopY);
            double left = pageLeft + ColorProofGridPlan.MarginMm, top = pageTop - ColorProofGridPlan.MarginMm;
            double usable = options.SheetWidthMm - 2 * ColorProofGridPlan.MarginMm;
            double headerWidth = usable - 18;
            double gridLeft = left + ColorProofGridPlan.AxisWidthMm;
            double gridTop = top - HeaderHeightMm - ColumnLabelsHeightMm;
            string[] names = ChannelNames(options.Cmyk);
            int[] original = series == null ? plan.BaseChannels : series.OriginalBaseChannels;
            string location = "R" + Id(plan.ReferenceRow) + " C" + Id(plan.ReferenceColumn) +
                (options.Compound ? " Q" + (plan.ReferenceQuadrant + 1) : "");
            bool containsOriginal = series == null || pageIndex == series.ReferencePageIndex;
            AddText(layer, range, left, top, "Сетка цветопробы " + (options.Cmyk ? "CMYK" : "RGB") +
                " · " + options.Columns + " × " + options.Rows + " · плашек " + plan.Patches.Count, 10f, headerWidth);
            AddText(layer, range, left, top - 5, "Исходный эталон · " + FormatValues(original, options.Cmyk), 7.5f, headerWidth);
            string pageDescription;
            if (series == null) pageDescription = "ЭТАЛОН в сетке: " + location + ".";
            else
            {
                string channel = names[series.Options.Channel];
                pageDescription = "Таблица " + (pageIndex + 1) + "/" + series.Pages.Count + " · " + channel + ":" +
                    series.ChannelValues[pageIndex] + " · Δ" + channel + ":" + Delta(series.Offsets[pageIndex]) + ". " +
                    (containsOriginal ? "ЭТАЛОН в сетке: " + location + "." : "Исходный эталон — плашка справа; в этой сетке его нет.");
            }
            AddText(layer, range, left, top - 10, pageDescription, 7.5f, headerWidth);
            AddText(layer, range, left, top - 15, "X → " + names[options.HorizontalChannel] + " · шаг " + options.StepX +
                " | Y ↓ " + names[options.VerticalChannel] + " · шаг " + options.StepY +
                " | значения каналов " + (options.Cmyk ? "0–100%" : "0–255"), 7.5f, headerWidth);
            string sizes = options.Compound
                ? "Блок " + Mm(options.TileMm) + " × " + Mm(options.TileMm) + " мм; четыре плашки " +
                    Mm(options.TileMm / 2) + " × " + Mm(options.TileMm / 2) + " мм без внутреннего зазора"
                : "Плашка " + Mm(options.CellMm) + " × " + Mm(options.CellMm) + " мм";
            AddText(layer, range, left, top - 20, sizes + "; между блоками " + Mm(options.GapMm) + " мм.", 7f, usable);
            AddText(layer, range, left, top - 25,
                "R — строка; C — столбец; Q — квадрант. * — исходное значение только канала оси; полный эталон указан отдельно.", 7f, usable);
            dynamic referenceColor = CreateColor((object)app, original, options.Cmyk);
            dynamic referencePatch = layer.CreateRectangle2(left + usable - 12, top - 12, 12, 12);
            range.Add(referencePatch); referencePatch.Fill.ApplyUniformFill(referenceColor); referencePatch.Outline.SetNoOutline();
            referencePatch.Name = "Исходный эталон " + FormatValues(original, options.Cmyk);
            AddText(layer, range, left + usable - 15, top - 13, "Эталон", 6f, 18, true);

            double cell = options.Compound ? options.TileMm : options.CellMm, pitch = cell + options.GapMm;
            for (int column = 0; column < options.Columns; column++)
            {
                string label = "C" + Id(column) + " " + names[options.HorizontalChannel] + ":" + plan.AxisX[column] +
                    (column == plan.ReferenceColumn ? "*" : "");
                AddText(layer, range, gridLeft + column * pitch, gridTop + ColumnLabelsHeightMm - 2, label, 7f, cell, true);
            }
            for (int row = 0; row < options.Rows; row++)
            {
                string label = "R" + Id(row) + " " + names[options.VerticalChannel] + ":" + plan.AxisY[row] +
                    (row == plan.ReferenceRow ? "*" : "");
                AddText(layer, range, left, gridTop - row * pitch - cell / 2, label, 7f, ColorProofGridPlan.AxisWidthMm - 2, false, true);
            }
            foreach (ColorProofGridPatch patch in plan.Patches)
            {
                dynamic color = CreateColor((object)app, patch.Values, options.Cmyk);
                dynamic shape = layer.CreateRectangle2(gridLeft + patch.Xmm,
                    gridTop - patch.Ymm - patch.HeightMm, patch.WidthMm, patch.HeightMm);
                range.Add(shape); shape.Fill.ApplyUniformFill(color); shape.Outline.SetNoOutline();
                shape.Name = "Проба R" + Id(patch.Row) + " C" + Id(patch.Column) +
                    (options.Compound ? " Q" + (patch.Quadrant + 1) : "") + " " + FormatValues(patch.Values, options.Cmyk);
                if (containsOriginal && patch.Row == plan.ReferenceRow && patch.Column == plan.ReferenceColumn &&
                    patch.Quadrant == plan.ReferenceQuadrant && patch.Values.SequenceEqual(original))
                    AddReferenceFrame((object)app, (object)layer, (object)range, gridLeft, gridTop, patch);
            }
            double footerTop = gridTop - plan.HeightMm - 4;
            if (options.Compound)
            {
                int[] remaining = Enumerable.Range(0, plan.BaseChannels.Length)
                    .Where(channel => channel != options.HorizontalChannel && channel != options.VerticalChannel).ToArray();
                AddText(layer, range, left, footerTop,
                    "Квадранты: Q1 — слева сверху; Q2 — справа сверху; Q3 — слева снизу; Q4 — справа снизу.", 7f, usable);
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
                AddText(layer, range, left, footerTop, "Фиксированные каналы этой таблицы: " + fixedChannels + ".", 7f, usable);
            }
            AddText(layer, range, left, footerTop - 9,
                "Заливки — " + (options.Cmyk ? "CMYK" : "RGB") + "; плашки без обводки. Печатайте без масштабирования, 1:1.", 7f, usable);
            return (object)range;
        }

        private static void AddReferenceFrame(object appObject, object layerObject, object rangeObject,
            double gridLeft, double gridTop, ColorProofGridPatch patch)
        {
            dynamic app = appObject, layer = layerObject, range = rangeObject;
            dynamic white = app.CreateCMYKColor(0, 0, 0, 0), black = app.CreateCMYKColor(0, 0, 0, 100);
            int index = 0;
            foreach (ColorProofReferenceFrameRect band in ColorProofReferenceFrame.Build(
                patch.Xmm, patch.Ymm, patch.WidthMm, patch.HeightMm))
            {
                dynamic strip = layer.CreateRectangle2(gridLeft + band.Xmm,
                    gridTop - band.Ymm - band.HeightMm, band.WidthMm, band.HeightMm);
                range.Add(strip); strip.Fill.ApplyUniformFill(band.White ? white : black); strip.Outline.SetNoOutline();
                strip.Name = "Рамка исходного эталона R" + Id(patch.Row) + " C" + Id(patch.Column) +
                    " Q" + (patch.Quadrant + 1) + " · " + (++index);
            }
        }

        private static dynamic CreateColor(object appObject, int[] values, bool cmyk)
        {
            dynamic app = appObject;
            return cmyk ? app.CreateCMYKColor(values[0], values[1], values[2], values[3])
                : app.CreateRGBColor(values[0], values[1], values[2]);
        }
        private static double PageHeight(ColorProofGridPlan plan) => 2 * ColorProofGridPlan.MarginMm +
            HeaderHeightMm + ColumnLabelsHeightMm + plan.HeightMm + FooterHeightMm;
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
        private static string Delta(int value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);
        private static string[] ChannelNames(bool cmyk) => cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
        private static string FormatValues(int[] values, bool cmyk) => cmyk
            ? String.Format(CultureInfo.InvariantCulture, "C:{0} M:{1} Y:{2} K:{3}", values[0], values[1], values[2], values[3])
            : String.Format(CultureInfo.InvariantCulture, "R:{0} G:{1} B:{2}", values[0], values[1], values[2]);
    }
}
