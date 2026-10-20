using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    // Keep the checked output separate from the temporary selection of its errors.
    internal sealed class FilmColorScope
    {
        private readonly string _context;
        private readonly int _page;
        private readonly int[] _roots, _selection;
        private int[] _highlight;

        internal FilmColorScope(List<FilmTarget> targets)
        {
            dynamic doc = targets[0].Document;
            _context = Identity((object)doc); _page = Convert.ToInt32(doc.ActivePage.Index);
            _roots = targets.SelectMany(FilmOutputService.PrintShapes).Select(Id).Distinct().ToArray();
            _selection = Selected();
        }

        internal object Document()
        {
            dynamic app = CorelApp.Get();
            if (Convert.ToInt32(app.Documents.Count) == 0 || Identity((object)app.ActiveDocument) != _context)
                throw new InvalidOperationException("Сменился документ. Выделите фотовывод и запустите проверку цветов заново.");
            return (object)app.ActiveDocument;
        }

        private object Page()
        {
            dynamic doc = Document();
            if (_page > Convert.ToInt32(doc.Pages.Count))
                throw new InvalidOperationException("Страница проверки удалена. Проверьте выделение заново.");
            return (object)doc.Pages[_page];
        }

        private List<object> Find(IEnumerable<int> ids)
        {
            int[] requested = ids.ToArray();
            var found = FilmShapeSearch.Find(Document(), requested);
            int[] missing = requested.Where(id => !found.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                Log.Info("Film color scope lookup: page=" + _page + "; missing=" + String.Join(",", missing) +
                    "; selected=" + String.Join(",", Selected()));
                throw new InvalidOperationException("Не удалось получить проверенные объекты #" + String.Join(", #", missing.Take(6)) +
                    ". Выделите весь фотовывод и нажмите «Проверить новое выделение».");
            }
            return requested.Select(id => found[id]).ToList();
        }

        internal List<object> Roots() => Find(_roots);

        internal Dictionary<int, object> Objects(IEnumerable<int> ids)
        {
            var wanted = new HashSet<int>(ids);
            var found = FilmColorValidator.Collect(Roots()).Where(s => wanted.Contains(Id(s))).ToDictionary(Id);
            if (found.Count != wanted.Count)
                throw new InvalidOperationException("Состав проверенного вывода изменился. Повторите проверку.");
            return found;
        }

        internal int Select(IEnumerable<FilmColorIssue> issues)
        {
            var ids = issues.Select(i => i.ShapeId).Distinct().ToArray();
            if (ids.Length == 0) throw new InvalidOperationException("Нет объектов для выделения.");
            var objects = Objects(ids);
            ((dynamic)Page()).Activate();
            try
            {
                ((dynamic)FilmOutputService.Range(objects.Values)).CreateSelection();
                _highlight = Selected();
                if (!_highlight.SequenceEqual(ids.OrderBy(i => i)))
                    throw new InvalidOperationException("Corel не смог выделить точно эти объекты. Проверьте блокировку слоя или вложенность в PowerClip; " +
                        "попробуйте выбрать отдельную строку либо исправить векторные цвета кнопкой K100.");
            }
            catch
            {
                try { Restore(); } catch (Exception error) { Log.Error("Could not restore film selection after selecting color errors.", error); }
                throw;
            }
            return ids.Length;
        }

        internal void Restore()
        {
            var shapes = Find(_selection);
            ((dynamic)Page()).Activate();
            ((dynamic)FilmOutputService.Range(shapes)).CreateSelection(); _highlight = null;
        }

        internal void RestoreIfHighlighted()
        {
            if (_highlight == null) return;
            dynamic app = CorelApp.Get();
            if (Convert.ToInt32(app.Documents.Count) == 0 || Identity((object)app.ActiveDocument) != _context) return;
            if (Convert.ToInt32(app.ActiveDocument.ActivePage.Index) == _page && Selected().SequenceEqual(_highlight)) Restore();
            else _highlight = null;
        }

        private static int[] Selected()
        {
            var ids = new List<int>();
            foreach (object shape in CorelApp.Get().ActiveSelectionRange) ids.Add(Id(shape));
            return ids.OrderBy(i => i).ToArray();
        }
        private static int Id(object shape) => Convert.ToInt32(((dynamic)shape).StaticID);
        private static string Identity(object value)
        {
            IntPtr identity = Marshal.GetIUnknownForObject(value);
            try { return identity.ToInt64().ToString("X"); } finally { Marshal.Release(identity); }
        }
    }

    internal static class FilmColorCorrections
    {
        internal static int Black(FilmColorScope scope, FilmColorReport report)
        {
            var repairable = report.Issues.Where(i => i.CanBlack).ToList();
            if (repairable.Count == 0) return 0;
            var objects = scope.Objects(repairable.Select(i => i.ShapeId));
            dynamic app = CorelApp.Get(), doc = scope.Document();
            object black = app.CreateCMYKColor(0, 0, 0, 100);
            var changes = new List<Action>(); var changedObjects = new HashSet<int>();
            foreach (var group in repairable.GroupBy(i => i.ShapeId))
            {
                dynamic shape = objects[group.Key]; int before = changes.Count;
                var parts = new HashSet<FilmColorPart>(group.Select(i => i.Part));
                if (parts.Contains(FilmColorPart.Fill)) Fill((object)shape.Fill, black, changes);
                if (parts.Contains(FilmColorPart.Outline)) Outline((object)shape.Outline, black, changes);
                if (parts.Contains(FilmColorPart.TextFill) || parts.Contains(FilmColorPart.TextOutline))
                {
                    dynamic characters = shape.Text.Story.Characters; int count = Convert.ToInt32(characters.Count);
                    if (count > 100000) throw new InvalidOperationException("Текст изменился и стал слишком большим. Повторите проверку.");
                    for (int i = 1; i <= count; i++)
                    {
                        if (parts.Contains(FilmColorPart.TextFill)) Fill((object)characters[i].Fill, black, changes);
                        if (parts.Contains(FilmColorPart.TextOutline)) Outline((object)characters[i].Outline, black, changes);
                    }
                }
                if (changes.Count > before) changedObjects.Add(group.Key);
            }
            if (changes.Count == 0) return 0;
            bool optimization = Convert.ToBoolean(app.Optimization), started = false, touched = false;
            try
            {
                app.Optimization = true; doc.BeginCommandGroup("Vanya Tools — ошибки фотовывода в K100"); started = true;
                foreach (Action change in changes) { touched = true; change(); }
            }
            catch
            {
                if (started) { doc.EndCommandGroup(); started = false; if (touched) doc.Undo(); }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); }
                finally { app.Optimization = optimization; }
            }
            return changedObjects.Count;
        }

        private static bool Wrong(object color) => FilmColorValidator.ColorGrade(color, false, out _) == 2;
        private static void Outline(object value, object black, List<Action> changes)
        {
            dynamic outline = value;
            if (Convert.ToInt32(outline.Type) != 0 && Wrong((object)outline.Color)) changes.Add(() => outline.Color = black);
        }
        private static void Fill(object value, object black, List<Action> changes)
        {
            dynamic fill = value; int type = Convert.ToInt32(fill.Type);
            if (type == 1 && Wrong((object)fill.UniformColor)) changes.Add(() => fill.ApplyUniformFill(black));
            if (type != 2) return;
            dynamic fountain = fill.Fountain;
            if (Wrong((object)fountain.StartColor)) changes.Add(() => fountain.StartColor = black);
            if (Wrong((object)fountain.EndColor)) changes.Add(() => fountain.EndColor = black);
            foreach (object item in fountain.Colors)
            {
                dynamic stop = item;
                if (Wrong((object)stop.Color)) changes.Add(() => stop.Color = black);
            }
        }
    }
}
