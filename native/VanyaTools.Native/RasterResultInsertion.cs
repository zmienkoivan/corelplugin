using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class RasterInsertionShape
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public string Name { get; set; }
        public double LeftMm { get; set; }
        public double TopMm { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
    }

    internal sealed class RasterInsertionSource
    {
        public string CaptureId { get; set; }
        public string DocumentPath { get; set; }
        public long FileLength { get; set; }
        public long FileStampUtc { get; set; }
        public bool SavedDocument { get; set; }
        public int Page { get; set; }
        public double LeftMm { get; set; }
        public double TopMm { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public int InputWidth { get; set; }
        public int InputHeight { get; set; }
        public double InputDpiX { get; set; }
        public double InputDpiY { get; set; }
        public List<RasterInsertionShape> Shapes { get; set; } = new List<RasterInsertionShape>();
        // Runtime refs never enter JSON. Do not touch them from image worker threads.
        internal object Document, NativePage, TargetLayer;
        internal List<object> NativeShapes;
        internal int ThreadId;
    }

    internal sealed class RasterInsertionPlacement
    {
        public int Version { get; set; } = 1;
        public RasterInsertionSource Source { get; set; }
        public RasterInsertionMode Mode { get; set; }
        public bool Diagnostic { get; set; }
        public double LeftMm { get; set; }
        public double TopMm { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
    }

    /// <summary>Pure placement policy, shared with offline checks. Coordinates are millimetres, Y up.</summary>
    internal static class RasterInsertionGeometry
    {
        internal const double LegacyShiftMm = 10, GapMm = 10;
        internal static Tuple<double, double> ResultPosition(double sourceLeft, double sourceTop,
            double offsetX, double offsetY, RasterInsertionMode mode) => Tuple.Create(
                sourceLeft - offsetX + (mode == RasterInsertionMode.NewAside ? LegacyShiftMm : 0),
                sourceTop + offsetY + (mode == RasterInsertionMode.NewAside ? LegacyShiftMm : 0));

        internal static double OriginalShift(double sourceLeft, double sourceWidth, double resultLeft, double resultWidth) =>
            Math.Max(sourceLeft + sourceWidth, resultLeft + resultWidth) + GapMm - sourceLeft;
        internal static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    /// <summary>
    /// Imports one processed PNG against the exact captured source. A worker produces PNG pixels only;
    /// all capture/import/shape mutations here must stay on the original Corel UI/STA thread.
    /// </summary>
    internal static class RasterResultInsertion
    {
        private const double ToleranceMm = .02;
        private sealed class ResolvedSource
        {
            internal object App, Document, Page, Layer;
            internal readonly List<object> Shapes = new List<object>();
        }

        internal static RasterInsertionSource CaptureSelection()
        {
            RequireSta();
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ и выделите изображение.");
            dynamic selected = app.ActiveSelectionRange;
            if (selected == null || Convert.ToInt32(selected.Count) == 0)
                throw new InvalidOperationException("Выделите изображение для обработки.");
            var source = new RasterInsertionSource { CaptureId = Guid.NewGuid().ToString("N"), Document = (object)doc,
                NativePage = (object)doc.ActivePage, TargetLayer = (object)doc.ActiveLayer,
                Page = Convert.ToInt32(doc.ActivePage.Index), DocumentPath = DocumentPath((object)doc),
                NativeShapes = new List<object>(), ThreadId = Thread.CurrentThread.ManagedThreadId };
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                source.LeftMm = Convert.ToDouble(selected.LeftX); source.TopMm = Convert.ToDouble(selected.TopY);
                source.WidthMm = Convert.ToDouble(selected.SizeWidth); source.HeightMm = Convert.ToDouble(selected.SizeHeight);
                foreach (dynamic shape in selected.Shapes)
                { source.NativeShapes.Add((object)shape); source.Shapes.Add(ReadShape((object)shape)); }
                RequireBounds(source.LeftMm, source.TopMm, source.WidthMm, source.HeightMm);
                if (source.Shapes.Count == 0) throw new InvalidOperationException("Исходные объекты не найдены.");
            }
            finally { doc.Unit = oldUnit; }
            try
            {
                if (!String.IsNullOrEmpty(source.DocumentPath) && File.Exists(source.DocumentPath) && !Convert.ToBoolean(doc.Dirty))
                {
                    var file = new FileInfo(source.DocumentPath);
                    source.FileLength = file.Length; source.FileStampUtc = file.LastWriteTimeUtc.Ticks; source.SavedDocument = true;
                }
            }
            catch { source.SavedDocument = false; }
            return source;
        }

        internal static void SetInputRaster(RasterInsertionSource source, string path)
        {
            if (source == null) return;
            using (var stream = File.OpenRead(path))
            {
                BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                source.InputWidth = frame.PixelWidth; source.InputHeight = frame.PixelHeight;
                source.InputDpiX = frame.DpiX; source.InputDpiY = frame.DpiY;
            }
            RequireInput(source);
        }

        internal static bool HasPlacement(string output) => File.Exists(Sidecar(output));

        internal static void SaveResult(string output, RasterInsertionSource source, RasterInsertionMode mode,
            double offsetX = 0, double offsetY = 0, bool preserveSourceSize = false, bool diagnostic = false)
        {
            if (source == null) throw new InvalidOperationException("Не сохранено исходное выделение для результата.");
            if (!RasterInsertionSettings.Valid(mode) || !RasterInsertionGeometry.Finite(offsetX) || !RasterInsertionGeometry.Finite(offsetY))
                throw new InvalidOperationException("Некорректное размещение результата.");
            RequireInput(source);
            double scaleX = source.WidthMm / (source.InputWidth * 25.4 / source.InputDpiX);
            double scaleY = source.HeightMm / (source.InputHeight * 25.4 / source.InputDpiY);
            double width, height;
            using (var stream = File.OpenRead(output))
            {
                BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                width = preserveSourceSize ? source.WidthMm : frame.PixelWidth * 25.4 / frame.DpiX * scaleX;
                height = preserveSourceSize ? source.HeightMm : frame.PixelHeight * 25.4 / frame.DpiY * scaleY;
            }
            // Scale only compensates the sub-pixel rounding of Corel's original raster capture.
            // Remote upscaling keeps the original physical canvas, regardless of returned PNG DPI.
            var point = RasterInsertionGeometry.ResultPosition(source.LeftMm, source.TopMm, offsetX * scaleX, offsetY * scaleY,
                RasterInsertionMode.ReplaceOriginal);
            RequireBounds(point.Item1, point.Item2, width, height);
            var placement = new RasterInsertionPlacement { Source = source, Mode = diagnostic ? RasterInsertionMode.NewAside : mode,
                Diagnostic = diagnostic, LeftMm = point.Item1, TopMm = point.Item2, WidthMm = width, HeightMm = height };
            File.WriteAllText(Sidecar(output), new JavaScriptSerializer().Serialize(placement));
        }

        internal static string Import(string output, string operation, RasterInsertionMode? requestedMode = null,
            RasterInsertionSource liveSource = null)
        {
            RequireSta();
            if (!File.Exists(output)) throw new FileNotFoundException("Файл готового изображения не найден.", output);
            var placement = new JavaScriptSerializer().Deserialize<RasterInsertionPlacement>(File.ReadAllText(Sidecar(output)));
            if (placement == null || placement.Version != 1 || placement.Source == null || !RasterInsertionSettings.Valid(placement.Mode))
                throw new InvalidOperationException("Повреждены сохранённые данные вставки. Готовый PNG сохранён: " + output);
            RequireBounds(placement.LeftMm, placement.TopMm, placement.WidthMm, placement.HeightMm);
            RasterInsertionMode mode = placement.Diagnostic ? RasterInsertionMode.NewAside : requestedMode ?? placement.Mode;
            if (!RasterInsertionSettings.Valid(mode)) throw new InvalidOperationException("Неизвестный режим вставки.");
            RasterInsertionSource source = liveSource != null && liveSource.CaptureId == placement.Source.CaptureId ? liveSource : placement.Source;
            ResolvedSource resolved = Resolve(source, mode);
            dynamic app = resolved.App, doc = resolved.Document, page = resolved.Page;
            int oldUnit = Convert.ToInt32(doc.Unit);
            object oldReference = null;
            try { oldReference = (object)doc.ReferencePoint; } catch { }
            bool oldOptimization = Convert.ToBoolean(app.Optimization), started = false, touched = false;
            dynamic temporary = null, imported = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools — " + (operation ?? "Обработка изображения")); started = true;
                // The owned layer is a real mutation before ImportEx. An empty failed import
                // therefore cannot make rollback undo an unrelated previous user command.
                temporary = page.CreateLayer("Vanya-Result-" + Guid.NewGuid().ToString("N"));
                if (temporary == null) throw new InvalidOperationException("Corel не создал временный слой для вставки.");
                touched = true;
                object layer = (object)temporary, options = (object)app.CreateStructImportOptions();
                dynamic filter = layer.GetType().InvokeMember("ImportEx", BindingFlags.InvokeMethod, null, layer,
                    new object[] { new BStrWrapper(Path.GetFullPath(output)), 0, options });
                filter.Finish();
                dynamic shapes = temporary.Shapes;
                if (Convert.ToInt32(shapes.Count) != 1)
                    throw new InvalidOperationException("Corel не вернул один растр готового изображения.");
                foreach (dynamic shape in shapes) { imported = shape; break; }
                if (imported == null || Convert.ToInt32(imported.Type) != CorelConstants.CdrBitmapShape)
                    throw new InvalidOperationException("Corel не вернул вставленный растр.");
                imported.SetSize(placement.WidthMm, placement.HeightMm);
                double shift = mode == RasterInsertionMode.NewAside ? RasterInsertionGeometry.LegacyShiftMm : 0;
                double left = placement.LeftMm + shift, top = placement.TopMm + shift;
                imported.Move(left - Convert.ToDouble(imported.LeftX), top - Convert.ToDouble(imported.TopY));
                RequirePlaced((object)imported, left, top, placement.WidthMm, placement.HeightMm);
                imported.Name = "Vanya Tools · " + (String.IsNullOrWhiteSpace(operation) ? "Обработка растра" : operation.Trim()) +
                    " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                imported.MoveToLayer((dynamic)resolved.Layer);
                temporary.Delete(); temporary = null;
                if (mode == RasterInsertionMode.MoveOriginalAside)
                {
                    double dx = RasterInsertionGeometry.OriginalShift(source.LeftMm, source.WidthMm, left, placement.WidthMm);
                    foreach (object original in resolved.Shapes) ((dynamic)original).Move(dx, 0.0);
                }
                else if (mode == RasterInsertionMode.ReplaceOriginal)
                {
                    dynamic originals = app.CreateShapeRange();
                    foreach (object original in resolved.Shapes) originals.Add((dynamic)original);
                    originals.Delete();
                }
                imported.CreateSelection();
                doc.EndCommandGroup(); started = false;
                try { app.ActiveWindow.Refresh(); } catch { }
                Log.Info("Processed raster inserted: mode=" + mode + ", source=" + source.CaptureId + ", objects=" + source.Shapes.Count + ".");
                return null;
            }
            catch
            {
                if (started)
                {
                    try { doc.EndCommandGroup(); started = false; }
                    catch (Exception error) { Log.Error("Could not end failed raster insertion command group.", error); }
                }
                if (touched && !started)
                    try { doc.Undo(); } catch (Exception error) { Log.Error("Could not roll back processed raster insertion.", error); }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); } catch { }
                try { doc.Unit = oldUnit; } catch { }
                try { if (oldReference != null) doc.ReferencePoint = (dynamic)oldReference; } catch { }
                try { app.Optimization = oldOptimization; } catch { }
            }
        }

        private static ResolvedSource Resolve(RasterInsertionSource source, RasterInsertionMode mode)
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null || source == null || source.Page < 1)
                throw new InvalidOperationException("Откройте исходный документ для вставки готового изображения.");
            bool live = source.Document != null;
            if (live)
            {
                if (source.ThreadId != Thread.CurrentThread.ManagedThreadId || !SameObject((object)doc, source.Document) ||
                    !SameObject((object)doc.ActivePage, source.NativePage)) throw WrongDocument(source);
            }
            else if (String.IsNullOrEmpty(source.DocumentPath) || !String.Equals(DocumentPath((object)doc), source.DocumentPath, StringComparison.OrdinalIgnoreCase) ||
                Convert.ToInt32(doc.ActivePage.Index) != source.Page) throw WrongDocument(source);
            var result = new ResolvedSource { App = (object)app, Document = (object)doc, Page = (object)doc.ActivePage };
            if (!live && mode != RasterInsertionMode.NewAside)
            {
                bool unchanged = false;
                try
                {
                    var file = new FileInfo(source.DocumentPath);
                    unchanged = source.SavedDocument && !Convert.ToBoolean(doc.Dirty) && file.Exists && file.Length == source.FileLength &&
                        file.LastWriteTimeUtc.Ticks == source.FileStampUtc;
                }
                catch { }
                if (!unchanged) throw new InvalidOperationException("Не удалось подтвердить исходные объекты после перезапуска. " +
                    "Готовый PNG сохранён. Выберите вставку рядом с исходником или заново выполните обработку выделения.");
            }
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                if (source.Shapes == null || source.Shapes.Count == 0) throw ChangedSource();
                for (int index = 0; index < source.Shapes.Count; index++)
                {
                    object shape;
                    try { shape = live ? source.NativeShapes[index] : (object)doc.ActivePage.FindShape(null, 0, source.Shapes[index].Id, true); }
                    catch { throw ChangedSource(); }
                    if (shape == null) throw ChangedSource();
                    RasterInsertionShape current;
                    try { current = ReadShape(shape); } catch { throw ChangedSource(); }
                    if (!SameShape(source.Shapes[index], current)) throw ChangedSource();
                    result.Shapes.Add(shape);
                }
                result.Layer = live ? source.TargetLayer : (object)((dynamic)result.Shapes[0]).Layer;
            }
            catch (InvalidOperationException) when (mode == RasterInsertionMode.NewAside)
            {
                // A nondestructive retry can still use the recorded canvas location even if
                // the old objects cannot be proven. It never moves/deletes today's selection.
                result.Shapes.Clear(); result.Layer = (object)doc.ActiveLayer;
            }
            finally { doc.Unit = oldUnit; }
            return result;
        }

        private static RasterInsertionShape ReadShape(object value)
        {
            dynamic shape = value;
            var info = new RasterInsertionShape { Id = Convert.ToInt32(shape.StaticID), Type = Convert.ToInt32(shape.Type),
                Name = Convert.ToString(shape.Name) ?? "", LeftMm = Convert.ToDouble(shape.LeftX), TopMm = Convert.ToDouble(shape.TopY),
                WidthMm = Convert.ToDouble(shape.SizeWidth), HeightMm = Convert.ToDouble(shape.SizeHeight) };
            // A selected vector group can include a horizontal/vertical line.
            // Only the combined source canvas must have two positive dimensions.
            if (!RasterInsertionGeometry.Finite(info.LeftMm) || !RasterInsertionGeometry.Finite(info.TopMm) ||
                !RasterInsertionGeometry.Finite(info.WidthMm) || !RasterInsertionGeometry.Finite(info.HeightMm) ||
                info.WidthMm < 0 || info.HeightMm < 0) throw ChangedSource();
            return info;
        }
        private static bool SameShape(RasterInsertionShape a, RasterInsertionShape b) => a.Id == b.Id && a.Type == b.Type &&
            String.Equals(a.Name, b.Name, StringComparison.Ordinal) && Close(a.LeftMm, b.LeftMm) && Close(a.TopMm, b.TopMm) &&
            Close(a.WidthMm, b.WidthMm) && Close(a.HeightMm, b.HeightMm);
        private static void RequirePlaced(object shapeObject, double left, double top, double width, double height)
        {
            dynamic shape = shapeObject;
            if (!Close(Convert.ToDouble(shape.LeftX), left) || !Close(Convert.ToDouble(shape.TopY), top) ||
                !Close(Convert.ToDouble(shape.SizeWidth), width) || !Close(Convert.ToDouble(shape.SizeHeight), height))
                throw new InvalidOperationException("Не удалось сохранить исходный физический размер и положение результата. Исходник не заменён.");
        }
        private static bool Close(double a, double b) => RasterInsertionGeometry.Finite(a) && Math.Abs(a - b) <= ToleranceMm;
        private static void RequireBounds(double left, double top, double width, double height)
        {
            if (!RasterInsertionGeometry.Finite(left) || !RasterInsertionGeometry.Finite(top) || !RasterInsertionGeometry.Finite(width) ||
                !RasterInsertionGeometry.Finite(height) || width <= 0 || height <= 0)
                throw new InvalidOperationException("Некорректные физические границы изображения.");
        }
        private static void RequireInput(RasterInsertionSource source)
        {
            RequireBounds(source.LeftMm, source.TopMm, source.WidthMm, source.HeightMm);
            if (source.InputWidth < 1 || source.InputHeight < 1 || !RasterInsertionGeometry.Finite(source.InputDpiX) ||
                !RasterInsertionGeometry.Finite(source.InputDpiY) || source.InputDpiX <= 0 || source.InputDpiY <= 0)
                throw new InvalidOperationException("Не сохранены размеры исходного растра для вставки.");
        }
        private static string DocumentPath(object document)
        {
            try { string value = Convert.ToString(((dynamic)document).FullFileName); return Path.IsPathRooted(value) ? Path.GetFullPath(value) : ""; }
            catch { return ""; }
        }
        private static InvalidOperationException ChangedSource() => new InvalidOperationException(
            "Исходные объекты удалены или изменены после захвата. Готовый PNG сохранён; исходник не заменён.");
        private static InvalidOperationException WrongDocument(RasterInsertionSource source) => new InvalidOperationException(
            "Вернитесь в исходный документ и страницу " + source.Page + " перед вставкой. Готовый PNG сохранён." +
            (String.IsNullOrEmpty(source.DocumentPath) ? "" : "\n" + source.DocumentPath));
        private static string Sidecar(string output) => output + ".insertion.json";
        private static void RequireSta()
        { if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Вставка изображения должна выполняться в интерфейсе CorelDRAW."); }
        private static bool SameObject(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || !Marshal.IsComObject(a) || !Marshal.IsComObject(b)) return false;
            IntPtr first = IntPtr.Zero, second = IntPtr.Zero;
            try { first = Marshal.GetIUnknownForObject(a); second = Marshal.GetIUnknownForObject(b); return first == second; }
            finally { if (first != IntPtr.Zero) Marshal.Release(first); if (second != IntPtr.Zero) Marshal.Release(second); }
        }
    }
}
