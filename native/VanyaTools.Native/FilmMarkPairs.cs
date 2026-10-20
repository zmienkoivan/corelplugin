using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal sealed class FilmMarkReference
    {
        internal int Id, LabelId, PageIndex;
        internal object Geometry, Pair, Job, Label, Page;
        internal FilmCaption Caption;
        internal FilmBounds Bounds;
        internal string Version;
    }

    internal sealed class FilmMarkSnapshot
    {
        internal object Document, Page;
        internal string Context = "", Title = "Нет открытого документа";
        internal readonly List<FilmMarkReference> Pairs = new List<FilmMarkReference>();
    }

    internal static class FilmMarkPairs
    {
        internal const string Prefix = "VT_FILM_PAIR|";
        internal static bool IsPair(object shape) => FilmOutputService.Name(shape).StartsWith(Prefix, StringComparison.Ordinal);
        internal static string Name(FilmCaption caption) => Prefix + Uri.EscapeDataString(caption.Color.Trim()) + "|" + caption.Number;

        internal static object Geometry(object pair) => FilmOutputService.Children(pair)
            .FirstOrDefault(s => FilmOutputService.Name(s) == FilmOutputService.Marks);

        internal static List<object> Geometries(IEnumerable<object> shapes)
        {
            var result = new List<object>(); var pending = new Stack<object>(shapes);
            while (pending.Count > 0)
            {
                object shape = pending.Pop();
                if (FilmOutputService.Name(shape) == FilmOutputService.Marks) result.Add(shape);
                else if (Convert.ToInt32(((dynamic)shape).Type) == CorelConstants.CdrGroupShape)
                    foreach (object child in FilmOutputService.Children(shape)) pending.Push(child);
            }
            return result;
        }

        internal static FilmCaption ReadCaption(object owner, object label)
        {
            var result = new FilmCaption { Color = "Чёрный", Number = 1 };
            if (owner != null)
            {
                string[] parts = FilmOutputService.Name(owner).Split('|');
                if (parts.Length >= 3)
                {
                    try { result.Color = Uri.UnescapeDataString(parts[1]); } catch (UriFormatException) { }
                    if (Int32.TryParse(parts[2], out int number) && number > 0) result.Number = number;
                }
            }
            // A caption changed manually in Corel takes precedence over stored metadata.
            string text = Text(label);
            int split = text.LastIndexOf(" · слой ", StringComparison.Ordinal);
            if (split > 0 && Int32.TryParse(text.Substring(split + 8), out int layer) && layer > 0)
            { result.Color = text.Substring(0, split); result.Number = layer; }
            else if (!String.IsNullOrWhiteSpace(text)) result.Color = text;
            return result;
        }

        internal static object CreateLabel(object document, object geometry, FilmCaption caption)
        {
            dynamic marks = geometry;
            FilmBounds bounds = FilmOutputService.Measure(document, geometry);
            double x = bounds.CenterX + 6;
            double y = FilmOutputService.Children(geometry).Min(s => {
                FilmBounds b = FilmOutputService.Measure(document, s); return b.Bottom + b.Height / 2;
            }) - 6;
            dynamic label = marks.Layer.CreateArtisticTextWide(x, y, caption.Text, 0, -1, "Arial", 6f);
            label.Name = FilmOutputService.Caption; FilmOutputService.BlackFill((object)label);
            label.Move(x - Convert.ToDouble(label.LeftX), y - Convert.ToDouble(label.TopY));
            return (object)label;
        }

        internal static object Wrap(object geometry, object label, FilmCaption caption)
        {
            dynamic pair = ((dynamic)FilmOutputService.Range(new[] { geometry, label })).Group();
            pair.Name = Name(caption); return (object)pair;
        }

        internal static FilmMarkSnapshot Scan()
        {
            var snapshot = new FilmMarkSnapshot();
            dynamic app = CorelApp.Get();
            if (Convert.ToInt32(app.Documents.Count) == 0) return snapshot;
            dynamic doc = app.ActiveDocument;
            if (doc == null) return snapshot;
            dynamic page = doc.ActivePage;
            snapshot.Document = (object)doc; snapshot.Page = (object)page;
            IntPtr identity = Marshal.GetIUnknownForObject((object)doc);
            try { snapshot.Context = identity.ToInt64().ToString("X"); }
            finally { Marshal.Release(identity); }
            snapshot.Title = Convert.ToString(doc.FileName) + " · все страницы";
            var ids = new HashSet<int>();
            foreach (dynamic marksPage in doc.Pages)
            {
                dynamic found = marksPage.FindShapes(FilmOutputService.Marks, CorelConstants.CdrGroupShape, true);
                foreach (dynamic geometry in found)
                {
                    int id = Convert.ToInt32(geometry.StaticID);
                    if (!ids.Add(id)) continue;
                    object parent = geometry.ParentGroup, pair = null, job = null, label = null;
                    if (parent != null)
                    {
                        var children = FilmOutputService.Children(parent);
                        // Also recognize a plain group created by pasting the old
                        // geometry and its caption together, without our pair name.
                        if (IsPair(parent) || (children.Count == 2 && children.Any(s => FilmOutputService.Name(s) == FilmOutputService.Caption)))
                            pair = parent;
                        if (pair != null) label = children.FirstOrDefault(s => FilmOutputService.Name(s) == FilmOutputService.Caption);
                    }
                    object ancestor = parent;
                    for (int depth = 0; ancestor != null && depth < 32; depth++)
                    {
                        if (FilmOutputService.IsJob(ancestor)) { job = ancestor; break; }
                        ancestor = ((dynamic)ancestor).ParentGroup;
                    }
                    if (label == null && job != null && Same(parent, job))
                    {
                        var children = FilmOutputService.Children(job);
                        if (children.Count(s => FilmOutputService.Name(s) == FilmOutputService.Marks) == 1)
                            label = children.FirstOrDefault(s => FilmOutputService.Name(s) == FilmOutputService.Caption);
                    }
                    var reference = new FilmMarkReference { Id = id, Geometry = (object)geometry, Pair = pair, Job = job, Label = label,
                        Page = (object)marksPage, PageIndex = Convert.ToInt32(marksPage.Index),
                        LabelId = label == null ? 0 : Convert.ToInt32(((dynamic)label).StaticID),
                        Bounds = FilmOutputService.BoundsForSorting((object)geometry), Caption = ReadCaption(pair ?? job, label) };
                    reference.Version = FilmOutputService.Name(pair ?? job ?? (object)geometry) + "\n" + reference.LabelId + "\n" + Text(label);
                    snapshot.Pairs.Add(reference);
                }
            }
            snapshot.Pairs.Sort((a, b) => {
                int pageOrder = a.PageIndex.CompareTo(b.PageIndex);
                if (pageOrder != 0) return pageOrder;
                int top = b.Bounds.Top.CompareTo(a.Bounds.Top);
                if (top != 0) return top;
                int left = a.Bounds.Left.CompareTo(b.Bounds.Left);
                return left != 0 ? left : a.Id.CompareTo(b.Id);
            });
            return snapshot;
        }

        internal static void Apply(string context, IList<FilmMarkReference> originals, IList<FilmCaption> captions)
        {
            FilmMarkSnapshot current = Scan();
            if (current.Context != context) throw new InvalidOperationException("Сменился документ. Правки не применены; обновите таблицу.");
            var actual = new List<FilmMarkReference>();
            for (int i = 0; i < originals.Count; i++)
            {
                FilmCaption caption = captions[i];
                if (String.IsNullOrWhiteSpace(caption.Color) || caption.Color.Trim().Length > 100 || caption.Color.Contains("\n") ||
                    caption.Color.Contains("\r") || caption.Number < 1 || caption.Number > 9999)
                    throw new InvalidOperationException("Укажите цвет до 100 символов и номер слоя от 1 до 9999.");
                FilmMarkReference pair = current.Pairs.FirstOrDefault(p => p.Id == originals[i].Id);
                if (pair == null) throw new InvalidOperationException("Пара меток удалена. Обновите таблицу.");
                if (pair.Version != originals[i].Version)
                    throw new InvalidOperationException("Подпись уже изменена в Corel. Нажмите «Сбросить правки» и повторите изменение.");
                // A new standalone copy has no caption yet. Creating a wrapper
                // inside an unrelated parent group would require ungrouping it.
                if (pair.Label == null && ((dynamic)pair.Geometry).ParentGroup != null)
                    throw new InvalidOperationException("У этой пары нет подписи внутри группы. Вынесите пару на слой или копируйте пару вместе с подписью.");
                actual.Add(pair);
            }
            dynamic app = CorelApp.Get(), doc = current.Document;
            int unit = Convert.ToInt32(doc.Unit); bool optimization = Convert.ToBoolean(app.Optimization);
            var selected = new List<int>(); foreach (dynamic s in app.ActiveSelectionRange) selected.Add(Convert.ToInt32(s.StaticID));
            bool started = false, touched = false;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter; app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools — подписи пар меток"); started = true;
                for (int i = 0; i < actual.Count; i++)
                {
                    FilmMarkReference reference = actual[i]; FilmCaption caption = captions[i]; caption.Color = caption.Color.Trim();
                    touched = true;
                    if (reference.Label == null)
                    {
                        object label = CreateLabel((object)doc, reference.Geometry, caption);
                        Wrap(reference.Geometry, label, caption);
                    }
                    else
                    {
                        dynamic label = reference.Label;
                        double left = Convert.ToDouble(label.LeftX), top = Convert.ToDouble(label.TopY);
                        label.Text.Story.WideText = caption.Text;
                        label.Move(left - Convert.ToDouble(label.LeftX), top - Convert.ToDouble(label.TopY));
                        if (reference.Pair != null) ((dynamic)reference.Pair).Name = Name(caption);
                    }
                    if (reference.Job != null && current.Pairs.Count(p => Same(p.Job, reference.Job)) == 1)
                        ((dynamic)reference.Job).Name = FilmOutputService.JobName(caption);
                }
            }
            catch
            {
                if (started) { doc.EndCommandGroup(); started = false; if (touched) doc.Undo(); }
                throw;
            }
            finally
            {
                if (started) doc.EndCommandGroup();
                try
                {
                    var selection = new List<object>();
                    ((dynamic)current.Page).Activate();
                    foreach (int id in selected)
                    { object shape = ((dynamic)current.Page).FindShape(null, 0, id, true); if (shape != null) selection.Add(shape); }
                    ((dynamic)FilmOutputService.Range(selection)).CreateSelection();
                }
                catch (Exception error) { Log.Error("Could not restore selection after film caption edit.", error); }
                doc.Unit = unit; app.Optimization = optimization;
            }
        }

        internal static void Select(string context, int id)
        {
            FilmMarkSnapshot current = Scan();
            if (current.Context != context) throw new InvalidOperationException("Сменился документ. Обновите таблицу.");
            FilmMarkReference row = current.Pairs.FirstOrDefault(p => p.Id == id);
            if (row == null) throw new InvalidOperationException("Пара меток удалена. Обновите таблицу.");
            ((dynamic)row.Page).Activate();
            var shapes = new List<object> { row.Pair ?? row.Geometry };
            if (row.Pair == null && row.Label != null) shapes.Add(row.Label);
            ((dynamic)FilmOutputService.Range(shapes)).CreateSelection();
        }

        private static bool Same(object a, object b) => a != null && b != null &&
            Convert.ToInt32(((dynamic)a).StaticID) == Convert.ToInt32(((dynamic)b).StaticID);
        private static string Text(object label) => label == null ? "" : (Convert.ToString(((dynamic)label).Text.Story.WideText) ?? "").TrimEnd('\r', '\n');
    }
}
