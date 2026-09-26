using System;
using System.IO;

namespace VanyaTools.Native
{
    /// <summary>
    /// Captures the current CorelDRAW selection as a transparent PNG without
    /// changing the selected artwork. Corel COM calls must run on the UI/STA thread.
    /// </summary>
    internal static class AiSelectionCapture
    {
        public static string Capture()
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Сначала откройте документ CorelDRAW.");

            dynamic sourceRange = app.ActiveSelectionRange;
            if (sourceRange == null || Convert.ToInt32(sourceRange.Count) == 0)
                throw new InvalidOperationException("Выделите принт или несколько объектов на холсте CorelDRAW.");

            var originals = new System.Collections.Generic.List<dynamic>();
            foreach (dynamic shape in sourceRange.Shapes) originals.Add(shape);

            dynamic workingRange = null;
            dynamic workingShape = null;
            dynamic rasterShape = null;
            dynamic exportFilter = null;
            bool oldOptimization = false;
            string path = Path.Combine(Path.GetTempPath(), "Vanya-AI-Selection-" + Guid.NewGuid().ToString("N") + ".png");
            bool commandGroupStarted = false;

            try
            {
                try { oldOptimization = Convert.ToBoolean(app.Optimization); } catch { }
                doc.BeginCommandGroup("Vanya Tools - capture AI selection");
                commandGroupStarted = true;
                app.Optimization = true;

                // Duplicate first: grouping and rasterising only affect this temporary copy.
                workingRange = sourceRange.Duplicate(0, 0);
                if (workingRange == null || Convert.ToInt32(workingRange.Count) == 0)
                    throw new InvalidOperationException("Не удалось создать временную копию выделения.");
                workingShape = workingRange.Group();
                rasterShape = workingShape.ConvertToBitmapEx(
                    CorelConstants.CdrRgbColorImage,
                    true,  // transparent background
                    true,  // anti-aliasing
                    300,
                    CorelConstants.CdrNormalAntiAliasing,
                    true);
                if (rasterShape == null)
                    throw new InvalidOperationException("CorelDRAW не смог растрировать выделение.");

                // Save the generated bitmap itself. Document.ExportBitmap has
                // version-dependent optional COM arguments and can reject late-bound calls.
                exportFilter = rasterShape.Bitmap.SaveAs(path, CorelConstants.CdrPng);
                if (exportFilter != null) exportFilter.Finish();
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new InvalidOperationException("CorelDRAW не создал PNG выделения.");
                return path;
            }
            catch
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                throw;
            }
            finally
            {
                TryDelete(rasterShape);
                TryDelete(workingShape);
                TryDeleteRange(workingRange);
                try
                {
                    for (int i = 0; i < originals.Count; i++)
                    {
                        if (i == 0) originals[i].CreateSelection();
                        else originals[i].AddToSelection();
                    }
                }
                catch { }
                try { if (commandGroupStarted) doc.EndCommandGroup(); } catch { }
                try { app.Optimization = oldOptimization; } catch { }
                try { app.ActiveWindow.Refresh(); } catch { }
            }
        }

        private static void TryDelete(dynamic shape)
        {
            try { if (shape != null) shape.Delete(); } catch { }
        }

        private static void TryDeleteRange(dynamic range)
        {
            try { if (range != null) range.Delete(); } catch { }
        }
    }
}
