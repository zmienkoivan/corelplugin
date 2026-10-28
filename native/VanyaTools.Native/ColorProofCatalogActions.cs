using System;
using System.Globalization;

namespace VanyaTools.Native
{
    internal static class ColorProofCatalogActions
    {
        internal static bool IsSourceDocumentActive(object sourceDocument)
        {
            try { return SportProjectPlayers.SameDocument(sourceDocument, (object)CorelApp.Get().ActiveDocument); }
            catch { return false; }
        }

        internal static void CreateSwatch(object sourceDocument, ColorProofCatalog catalog, int number)
            => CreateSwatchForApplication((object)CorelApp.Get(), sourceDocument, catalog, number);

        // Separate entry point keeps rollback and exact native assignments testable without Corel.
        internal static void CreateSwatchForApplication(object application, object sourceDocument,
            ColorProofCatalog catalog, int number)
        {
            ColorProofCatalog snapshot = ColorProofCatalog.CopyValidated(catalog);
            ColorProofCatalogPatch patch = snapshot.FindPatch(number);
            dynamic app = application, doc = app.ActiveDocument;
            if (doc == null || !SportProjectPlayers.SameDocument(sourceDocument, (object)doc))
                throw new InvalidOperationException("Активный CDR изменился. Закройте окно и снова нажмите «По номеру» в нужном файле.");
            int oldUnit = (int)doc.Unit;
            bool oldDirty = Convert.ToBoolean(doc.Dirty), oldOptimization = Convert.ToBoolean(app.Optimization);
            object originalPage = doc.ActivePage, originalSelection = null;
            try { originalSelection = (object)app.ActiveSelectionRange; } catch { }
            dynamic page = null;
            bool started = false, completed = false, reverted = true;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter; app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools — плашка по номеру"); started = true;
                page = doc.AddPages(1); page.Activate(); page.SetSize(120d, 70d);
                page.Name = snapshot.Id + " №" + number.ToString(CultureInfo.InvariantCulture);
                double left = Convert.ToDouble(page.LeftX) + 8, top = Convert.ToDouble(page.TopY) - 8;
                dynamic layer = doc.ActiveLayer;
                AddText((object)layer, left, top, snapshot.Id + " · №" + number, 11f, 104);
                dynamic color = snapshot.Cmyk
                    ? app.CreateCMYKColor(patch.Values[0], patch.Values[1], patch.Values[2], patch.Values[3])
                    : app.CreateRGBColor(patch.Values[0], patch.Values[1], patch.Values[2]);
                dynamic rectangle = layer.CreateRectangle2(left, top - 34, 20d, 20d);
                rectangle.Fill.ApplyUniformFill(color); rectangle.Outline.SetNoOutline();
                rectangle.Name = snapshot.Id + " №" + number + " " + Describe(snapshot.Cmyk, patch.Values);
                AddText((object)layer, left + 26, top - 14, Describe(snapshot.Cmyk, patch.Values), 9f, 78);
                AddText((object)layer, left + 26, top - 21, "20 × 20 мм · точные значения из таблицы", 7f, 78);
                AddText((object)layer, left, top - 43, "Для повторной печати используйте те же условия RIP и материал.", 7f, 104);
                ColorProofCatalogStore.BindPage((object)page, snapshot.Id, patch.PageNumber);
                rectangle.CreateSelection();
                doc.Dirty = true;
                completed = true;
            }
            catch
            {
                if (page != null)
                    try { page.Delete(); }
                    catch (Exception error) { reverted = false; Log.Error("Color proof restored swatch page rollback failed.", error); }
                try { ((dynamic)originalPage).Activate(); } catch { }
                try
                {
                    if (originalSelection != null) ((dynamic)originalSelection).CreateSelection();
                    else doc.ClearSelection();
                }
                catch { }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); }
                catch (Exception error) { Log.Error("Color proof restored swatch command group failed.", error); }
                try { doc.Unit = oldUnit; } catch { }
                try { app.Optimization = oldOptimization; } catch { }
                if (!completed && reverted) try { doc.Dirty = oldDirty; } catch { }
            }
        }

        internal static string Describe(bool cmyk, int[] values) => cmyk
            ? String.Format(CultureInfo.InvariantCulture, "CMYK · C:{0} M:{1} Y:{2} K:{3}", values[0], values[1], values[2], values[3])
            : String.Format(CultureInfo.InvariantCulture, "RGB · R:{0} G:{1} B:{2}", values[0], values[1], values[2]);

        private static void AddText(object layerObject, double left, double top, string value, float points, double maxWidth)
        {
            dynamic text = ((dynamic)layerObject).CreateArtisticTextWide(0, 0, value, 0, -1, "Arial", points);
            text.Fill.UniformColor.RGBAssign(0, 0, 0); text.Outline.SetNoOutline();
            double width = Convert.ToDouble(text.SizeWidth), height = Convert.ToDouble(text.SizeHeight);
            if (width > maxWidth && width > 0) text.SetSize(maxWidth, height * maxWidth / width);
            text.Move(left - Convert.ToDouble(text.LeftX), top - Convert.ToDouble(text.TopY));
        }
    }
}
