using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    /// <summary>Renders exact vector patches. All Corel calls belong on the caller's UI thread.</summary>
    internal sealed class ColorProofGridService
    {
        private const double HeaderHeightMm = 24, ColumnLabelsHeightMm = 0, FooterHeightMm = 14;
        internal string LastCatalogId { get; private set; }

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

        private int Render(ColorProofReference reference, IReadOnlyList<ColorProofGridPlan> plans, ColorProofGridSeriesPlan series)
        {
            foreach (ColorProofGridPlan plan in plans)
                if (PageHeight(plan) > 45000)
                    throw new InvalidOperationException("Лист цветопробы длиннее 45 м. Уменьшите сетку или размер плашек.");
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int oldUnit = (int)doc.Unit;
            object originalPage = doc.ActivePage;
            bool oldDirty = Convert.ToBoolean(doc.Dirty);
            string previousCatalog = ColorProofCatalogStore.ReadEncoded((object)doc);
            ColorProofCatalogStore.Load((object)doc);
            string context = "";
            try { context = new ColorProofPreviewService().DescribeContext(); }
            catch (Exception error) { Log.Error("Could not describe proof document profiles.", error); }
            var catalog = ColorProofCatalog.Build(plans, series?.OriginalBaseChannels ?? plans[0].BaseChannels, context);
            bool catalogTouched = false, failed = false, rollbackComplete = true;
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
                    page.Name = catalog.Id + " · " + (index + 1) + "/" + plans.Count;
                    lastRange = DrawPage((object)app, (object)doc, (object)page, plan, series, index, catalog);
                    ColorProofCatalogStore.BindPage((object)page, catalog.Id, index + 1);
                    total += plan.Patches.Count;
                }
                catalogTouched = true;
                ColorProofCatalogStore.Append((object)doc, catalog);
                if (lastRange != null) ((dynamic)lastRange).CreateSelection();
                LastCatalogId = catalog.Id;
                Log.Info("Color proof grids created: pages=" + plans.Count + ", grid patches=" + total + ", series=" + (series != null));
                return total;
            }
            catch
            {
                failed = true;
                for (int index = createdPages.Count - 1; index >= 0; index--)
                    try { ((dynamic)createdPages[index]).Delete(); }
                    catch (Exception error) { rollbackComplete = false; Log.Error("Color proof grid page rollback failed.", error); }
                if (catalogTouched)
                    try { ColorProofCatalogStore.RestoreEncoded((object)doc, previousCatalog); }
                    catch (Exception error) { rollbackComplete = false; Log.Error("Color proof catalog rollback failed.", error); }
                try { ((dynamic)originalPage).Activate(); } catch { }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); } catch (Exception error) { Log.Error("Color proof grid command group failed.", error); }
                try { doc.Unit = oldUnit; } catch { rollbackComplete = false; }
                try { app.Optimization = oldOptimization; } catch { }
                try { doc.Dirty = failed && rollbackComplete ? oldDirty : true; } catch { }
            }
        }

        private static object DrawPage(object appObject, object documentObject, object pageObject,
            ColorProofGridPlan plan, ColorProofGridSeriesPlan series, int pageIndex, ColorProofCatalog catalog)
        {
            dynamic app = appObject, doc = documentObject, page = pageObject;
            ColorProofGridOptions options = plan.Options;
            dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
            double left = Convert.ToDouble(page.LeftX) + ColorProofGridPlan.MarginMm;
            double top = Convert.ToDouble(page.TopY) - ColorProofGridPlan.MarginMm;
            double usable = Math.Min(options.SheetWidthMm - 2 * ColorProofGridPlan.MarginMm, Math.Max(plan.WidthMm, 84));
            double gridLeft = left, gridTop = top - HeaderHeightMm;
            int[] original = catalog.ReferenceChannels;
            bool containsOriginal = series == null || pageIndex == series.ReferencePageIndex;
            var recipes = catalog.Patches.Where(item => item.PageNumber == pageIndex + 1)
                .ToDictionary(item => item.Row + ":" + item.Column + ":" + item.Quadrant);
            AddText(layer, range, left, top, "Цветопроба " + catalog.Id + " · " + (options.Cmyk ? "CMYK" : "RGB"), 12f, usable - 18);
            AddText(layer, range, left, top - 6, "Выберите цвет и сообщите код таблицы и номер под образцом.", 8f, usable - 18);
            AddText(layer, range, left, top - 11, "Лист " + (pageIndex + 1) + " из " + catalog.Patches.Max(item => item.PageNumber) +
                " · образцы №" + recipes.Values.Min(item => item.Number) + "–" + recipes.Values.Max(item => item.Number) +
                (containsOriginal ? " · эталон отмечен рамкой." : " · эталон — справа вверху."), 8f, usable - 18);
            AddText(layer, range, left, top - 16, "Эталон: " + FormatValues(original, options.Cmyk), 7f, usable - 18);
            dynamic referencePatch = layer.CreateRectangle2(left + usable - 12, top - 12, 12, 12);
            range.Add(referencePatch); referencePatch.Fill.ApplyUniformFill(CreateColor((object)app, original, options.Cmyk));
            referencePatch.Outline.SetNoOutline(); referencePatch.Name = catalog.Id + " · исходный эталон";
            AddText(layer, range, left + usable - 15, top - 13, "Эталон", 6f, 18, true);
            foreach (ColorProofGridPatch patch in plan.Patches)
            {
                ColorProofCatalogPatch recipe = recipes[patch.Row + ":" + patch.Column + ":" + patch.Quadrant];
                dynamic color = CreateColor((object)app, patch.Values, options.Cmyk);
                dynamic shape = layer.CreateRectangle2(gridLeft + patch.Xmm,
                    gridTop - patch.Ymm - patch.HeightMm, patch.WidthMm, patch.HeightMm);
                range.Add(shape); shape.Fill.ApplyUniformFill(color); shape.Outline.SetNoOutline();
                shape.Name = catalog.Id + " №" + recipe.Number + " " + FormatValues(patch.Values, options.Cmyk);
                AddText(layer, range, gridLeft + patch.Xmm, gridTop - patch.Ymm - patch.HeightMm - .4,
                    recipe.Number.ToString(CultureInfo.InvariantCulture), 6.5f, patch.WidthMm, true);
                if (containsOriginal && patch.Row == plan.ReferenceRow && patch.Column == plan.ReferenceColumn &&
                    patch.Quadrant == plan.ReferenceQuadrant && patch.Values.SequenceEqual(original))
                    AddReferenceFrame((object)app, (object)layer, (object)range, gridLeft, gridTop, patch);
            }
            ColorProofGridLegendPlan legend = ColorProofGridLegendPlan.Build(plan, recipes.Values.Min(item => item.Number));
            double legendLeft = gridLeft + plan.WidthMm + ColorProofGridLegendPlan.GapMm;
            AddText(layer, range, legendLeft, gridTop, "Рецептуры " + (options.Cmyk ? "CMYK" : "RGB"),
                8f, ColorProofGridLegendPlan.WidthMm);
            foreach (ColorProofGridLegendPlan.Entry entry in legend.Entries)
                AddText(layer, range, legendLeft + entry.Xmm, gridTop - entry.Ymm,
                    entry.Text, ColorProofGridLegendPlan.FontPoints, ColorProofGridLegendPlan.ColumnWidthMm);
            double footer = gridTop - ColorProofGridLegendPlan.ContentHeight(plan) - 3;
            AddText(layer, range, left, footer, catalog.Id + " · выбранный образец № __________________", 8f, usable);
            AddText(layer, range, left, footer - 5, "Для повторного заказа укажите этот код и номер. Сравнивайте цвета по напечатанным образцам.", 7f, usable);
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
            HeaderHeightMm + ColumnLabelsHeightMm + ColorProofGridLegendPlan.ContentHeight(plan) + FooterHeightMm;
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
