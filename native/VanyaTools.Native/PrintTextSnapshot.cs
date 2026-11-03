using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal sealed class PrintTextCleanupException : Exception
    { internal PrintTextCleanupException(Exception inner) : base("Не удалось удалить временные объекты подготовки печати. PDF не отправлен; проверьте исходный CDR перед сохранением.", inner) { } }

    // Resolve glyphs in the source document's font/style context BEFORE any
    // clipboard transfer. Outlining after PasteEx would preserve a substituted
    // font too late. Originals are never converted, deleted, or moved.
    internal sealed class PrintTextSnapshot : IDisposable
    {
        private readonly object _app, _document;
        private readonly HashSet<int> _originalIds = new HashSet<int>();
        private readonly List<object> _owned = new List<object>();
        private bool _dirty, _group, _disposed;
        internal object Range { get; private set; }
        internal bool CleanupFailed { get; private set; }
        private const int SymbolShape = 23;

        private PrintTextSnapshot(object app, object document, object range)
        { _app = app; _document = document; Range = range; }

        internal static PrintTextSnapshot Prepare(object app, object document, object range)
        {
            var result = new PrintTextSnapshot(app, document, range);
            try { result.Prepare(); return result; }
            catch { result.Dispose(); throw; }
        }

        private void Prepare()
        {
            bool needed = false;
            foreach (object root in Items(Range)) Inspect(root, _originalIds, 0, ref needed);
            if (!needed) return; // Bitmap/curve-only jobs keep the old fast path.
            dynamic document = _document;
            _dirty = Convert.ToBoolean(document.Dirty);
            document.BeginCommandGroup("Vanya Tools — подготовка текста для печати"); _group = true;
            object duplicates;
            try { duplicates = (object)((dynamic)Range).Duplicate(0d, 0d); }
            catch (Exception error)
            {
                // A native failure may leave partial objects without returning
                // their identities. Never hide that uncertainty with Dirty=false.
                CleanupFailed = true; throw new PrintTextCleanupException(error);
            }
            bool hasOriginal = false;
            foreach (object root in Items(duplicates))
                if (_originalIds.Contains(Id(root))) hasOriginal = true; else _owned.Add(root);
            if (hasOriginal) throw new InvalidOperationException("Corel вернул исходный объект вместо копии. Подготовка печати отменена.");
            if (_owned.Count == 0) throw new InvalidOperationException("Не удалось создать временную копию текста для печати.");
            // A same-document duplicate must not introduce an offset or scale.
            var original = Bounds(Range); var copied = Bounds(duplicates);
            for (int i = 0; i < original.Length; i++)
                if (!PrintExportBounds.Finite(copied[i]) || Math.Abs(original[i] - copied[i]) > PrintExportBounds.TransferToleranceMm)
                    throw new InvalidOperationException("Копия для подготовки текста изменила положение или размер. PDF не отправлен.");
            var roots = new List<object>(_owned); var visited = new HashSet<int>();
            var converted = new List<object>();
            foreach (object root in roots) converted.AddRange(ConvertShape(root, visited, 0, true));
            dynamic ready = ((dynamic)_app).CreateShapeRange();
            foreach (object root in converted) ready.Add((dynamic)root);
            bool remaining = false; var checkedIds = new HashSet<int>();
            foreach (object root in Items((object)ready)) Inspect(root, checkedIds, 0, ref remaining);
            if (remaining) throw new InvalidOperationException("Часть текста не удалось перевести в кривые до переноса. PDF не отправлен.");
            Range = (object)ready;
            Log.Info("Print text outlined in source context before clipboard transfer; source originals retained.");
        }

        private IEnumerable<object> ConvertShape(object value, HashSet<int> seen, int depth, bool root)
        {
            Guard(depth, seen.Count);
            if (_originalIds.Contains(Id(value))) throw new InvalidOperationException("Попытка изменить исходный текст остановлена.");
            if (!seen.Add(Id(value))) return new[] { value };
            dynamic shape = value;
            int type = Convert.ToInt32(shape.Type);
            if (type == SymbolShape)
            {
                var shapes = Items((object)shape.Symbol.RevertToShapes());
                if (root) { _owned.Remove(value); _owned.AddRange(shapes); }
                var result = new List<object>();
                foreach (object child in shapes) result.AddRange(ConvertShape(child, seen, depth + 1, root));
                return result;
            }
            if (type == CorelConstants.CdrGroupShape)
                foreach (object child in Items((object)shape.Shapes)) ConvertShape(child, seen, depth + 1, false);
            object clip = PowerClip(value);
            if (clip != null)
                foreach (object child in Items((object)((dynamic)clip).Shapes)) ConvertShape(child, seen, depth + 1, false);
            if (type == CorelConstants.CdrTextShape) shape.ConvertToCurves();
            return new[] { value };
        }

        private static void Inspect(object value, HashSet<int> seen, int depth, ref bool needed)
        {
            Guard(depth, seen.Count);
            if (!seen.Add(Id(value))) return;
            dynamic shape = value; int type = Convert.ToInt32(shape.Type);
            if (type == CorelConstants.CdrTextShape || type == SymbolShape) needed = true;
            if (type == CorelConstants.CdrGroupShape)
                foreach (object child in Items((object)shape.Shapes)) Inspect(child, seen, depth + 1, ref needed);
            object clip = PowerClip(value);
            if (clip != null)
                foreach (object child in Items((object)((dynamic)clip).Shapes)) Inspect(child, seen, depth + 1, ref needed);
        }
        private static object PowerClip(object value)
        {
            try { return (object)((dynamic)value).PowerClip; }
            catch (COMException error) when (error.ErrorCode == unchecked((int)0x80004005) ||
                error.ErrorCode == unchecked((int)0x80020009) || error.ErrorCode == unchecked((int)0x80070057)) { return null; }
        }
        private static int Id(object value) => Convert.ToInt32(((dynamic)value).StaticID);
        private static List<object> Items(object range)
        { var result = new List<object>(); foreach (object item in (dynamic)range) result.Add(item); return result; }
        private static double[] Bounds(object range)
        {
            double x, y, w, h; ((dynamic)range).GetBoundingBox(out x, out y, out w, out h, true);
            return new[] { x, y, w, h };
        }
        private static void Guard(int depth, int count)
        { if (depth > 128 || count > 100000) throw new InvalidOperationException("Слишком сложная вложенность объектов для подготовки текста к печати."); }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (!_group) return;
            Exception failure = null;
            try
            {
                ((dynamic)_document).Activate();
                dynamic cleanup = ((dynamic)_app).CreateShapeRange();
                foreach (object root in _owned) cleanup.Add((dynamic)root);
                if (Convert.ToInt32(cleanup.Count) > 0) cleanup.Delete();
            }
            catch (Exception error) { failure = error; CleanupFailed = true; }
            finally
            {
                try { ((dynamic)_document).EndCommandGroup(); }
                catch (Exception error) { failure = failure ?? error; CleanupFailed = true; }
                try { ((dynamic)_document).Dirty = CleanupFailed || _dirty; }
                catch (Exception error) { failure = failure ?? error; CleanupFailed = true; }
            }
            if (failure != null) throw new PrintTextCleanupException(failure);
        }
    }
}
