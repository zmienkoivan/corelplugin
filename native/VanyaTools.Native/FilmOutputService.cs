using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    internal sealed class FilmOutputOptions
    {
        public double FilmWidth = 605, BandGap = 20, BandWidth = 0.3, MarkOffset = 25, MarkStroke = 0.15;
        public bool FitBands = true, ExportBands = true;
        internal void Validate()
        {
            if (!Valid(FilmWidth, 20, 2000) || !Valid(BandGap, 2, 100) || !Valid(BandWidth, .05, 2) ||
                !Valid(MarkOffset, 10, 100) || !Valid(MarkStroke, .05, 1))
                throw new InvalidOperationException("Проверьте размеры: плёнка 20–2000 мм, зазор 2–100 мм, полосы 0,05–2 мм, отступ метки 10–100 мм, контур 0,05–1 мм.");
        }
        private static bool Valid(double v, double min, double max) => !Double.IsNaN(v) && !Double.IsInfinity(v) && v >= min && v <= max;
    }

    internal sealed class FilmBounds
    {
        internal double Left, Bottom, Width, Height;
        internal double Right => Left + Width;
        internal double Top => Bottom + Height;
        internal double CenterX => Left + Width / 2;
        internal FilmBounds Copy() => new FilmBounds { Left = Left, Bottom = Bottom, Width = Width, Height = Height };
    }

    internal sealed class FilmCaption
    {
        public string ObjectName { get; set; }
        public string Color { get; set; }
        public int Number { get; set; }
        internal string Text => (Color ?? "").Trim() + " · слой " + Number.ToString(CultureInfo.InvariantCulture);
    }

    internal sealed class FilmTarget
    {
        internal object Document, Existing;
        internal List<object> Shapes;
        internal FilmBounds Bounds;
        internal FilmCaption Caption;
        internal bool HasCaption;
    }

    internal static class FilmOutputService
    {
        internal const string Prefix = "VT_FILM_JOB|", Art = "VT_FILM_ART", Marks = "VT_FILM_MARKS",
            Caption = "VT_FILM_CAPTION", Bands = "VT_FILM_BANDS";

        internal static List<FilmTarget> Capture(bool eachBitmap)
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте CDR и выделите макет или растры фотовывода.");
            var selected = new List<object>();
            foreach (dynamic shape in app.ActiveSelectionRange) selected.Add((object)shape);
            if (selected.Count == 0) throw new InvalidOperationException("Выделите макет или готовые полутоновые растры.");
            int unit = Convert.ToInt32(doc.Unit);
            bool dirty = Convert.ToBoolean(doc.Dirty);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                var targets = new List<FilmTarget>();
                if (eachBitmap)
                {
                    foreach (dynamic shape in selected)
                    {
                        if (Convert.ToInt32(shape.Type) != CorelConstants.CdrBitmapShape && !IsJob((object)shape))
                            throw new InvalidOperationException("Для режима «Каждый растр» выделите только растры или уже размеченные комплекты фотовывода.");
                        targets.Add(Target((object)doc, new List<object> { (object)shape }));
                    }
                    targets = targets.OrderByDescending(t => t.Bounds.Top).ThenBy(t => t.Bounds.Left).ToList();
                }
                else targets.Add(Target((object)doc, selected));
                for (int i = 0; i < targets.Count; i++)
                    if (!targets[i].HasCaption) targets[i].Caption.Number = i + 1;
                return targets;
            }
            finally { doc.Unit = unit; doc.Dirty = dirty; }
        }

        private static FilmTarget Target(object document, List<object> shapes)
        {
            object existing = shapes.Count == 1 && IsJob(shapes[0]) ? shapes[0] : null;
            var caption = new FilmCaption { Number = 1, Color = "Чёрный", ObjectName = shapes.Count == 1
                ? Convert.ToString(((dynamic)shapes[0]).Name) : "Выделение · " + shapes.Count + " объектов" };
            if (existing != null)
            {
                string[] parts = Convert.ToString(((dynamic)existing).Name).Split('|');
                if (parts.Length >= 3)
                {
                    caption.Color = Uri.UnescapeDataString(parts[1]);
                    if (Int32.TryParse(parts[2], out int number)) caption.Number = number;
                }
                caption.ObjectName = caption.Color + " · слой " + caption.Number;
            }
            if (String.IsNullOrWhiteSpace(caption.ObjectName)) caption.ObjectName = "Рисунок";
            var selectedPairs = existing == null ? shapes.Where(FilmMarkPairs.IsPair).ToList() : new List<object>();
            if (selectedPairs.Count == 1)
            {
                string objectName = caption.ObjectName;
                caption = FilmMarkPairs.ReadCaption(selectedPairs[0], Children(selectedPairs[0]).FirstOrDefault(s => Name(s) == Caption));
                caption.ObjectName = objectName;
            }
            // Single-object groups can be flattened by Corel or by color separation.
            // Only our auxiliary objects are excluded; a named ART wrapper is optional.
            var artwork = existing == null ? shapes : Artwork(Children(existing));
            if (artwork.Count == 0) throw new InvalidOperationException("В выделении остались только метки и полосы. Добавьте к выделению сам рисунок или растр.");
            return new FilmTarget { Document = document, Existing = existing, Shapes = artwork,
                Bounds = Measure(document, Range(artwork)), Caption = caption, HasCaption = existing != null || selectedPairs.Count == 1 };
        }

        internal static bool IsJob(object shape) => Convert.ToInt32(((dynamic)shape).Type) == CorelConstants.CdrGroupShape &&
            Name(shape).StartsWith(Prefix, StringComparison.Ordinal);
        internal static string Name(object shape) => Convert.ToString(((dynamic)shape).Name) ?? "";
        internal static List<object> Children(object group)
        {
            var result = new List<object>();
            foreach (dynamic shape in ((dynamic)group).Shapes) result.Add((object)shape);
            return result;
        }
        internal static object Range(IEnumerable<object> shapes)
        {
            dynamic range = CorelApp.Get().CreateShapeRange();
            foreach (object shape in shapes) range.Add(shape);
            return (object)range;
        }
        private static List<object> Artwork(IEnumerable<object> shapes) => shapes.Where(s =>
            Name(s) != Marks && !FilmMarkPairs.IsPair(s) && Name(s) != Caption && Name(s) != Bands).ToList();

        private static object GroupArtwork(List<object> shapes)
        {
            dynamic art = shapes.Count == 1 ? shapes[0] : (object)((dynamic)Range(shapes)).Group();
            art.Name = Art;
            return (object)art;
        }

        internal static FilmBounds Measure(object document, object shapeOrRange)
        {
            // Every caller receives millimetres, including preflight outside Change().
            // The source document is explicit because export also has a temporary document.
            dynamic doc = document;
            int unit = Convert.ToInt32(doc.Unit);
            if (unit == CorelConstants.CdrMillimeter) return ReadBounds(shapeOrRange);
            bool dirty = Convert.ToBoolean(doc.Dirty);
            try { doc.Unit = CorelConstants.CdrMillimeter; return ReadBounds(shapeOrRange); }
            finally { doc.Unit = unit; doc.Dirty = dirty; }
        }

        private static FilmBounds ReadBounds(object shapeOrRange)
        {
            double x, y, w, h;
            ((dynamic)shapeOrRange).GetBoundingBox(out x, out y, out w, out h, true);
            if (w <= 0 || h <= 0 || Double.IsNaN(x + y + w + h) || Double.IsInfinity(x + y + w + h))
                throw new InvalidOperationException("Не удалось определить габариты фотовывода.");
            return new FilmBounds { Left = x, Bottom = y, Width = w, Height = h };
        }
        // Read-only polling only needs relative ordering. Do not switch document
        // units or Dirty on a timer while the operator is working in Corel.
        internal static FilmBounds BoundsForSorting(object shapeOrRange) => ReadBounds(shapeOrRange);
        internal static List<object> PrintShapes(FilmTarget target) => target.Existing == null ? target.Shapes :
            Children(target.Existing).Where(s => Name(s) != Bands).ToList();

        internal static FilmBounds PrintBounds(FilmTarget target)
        {
            FilmBounds bounds = Measure(target.Document, Range(PrintShapes(target)));
            foreach (object marks in FilmMarkPairs.Geometries(PrintShapes(target)))
            {
                // Reserve a fixed caption bottom, independent of the glyphs
                // in each color name. Equal artwork retains equal PDF length.
                double bottom = Math.Min(bounds.Bottom, Measure(target.Document, marks).Bottom - 5);
                bounds.Height = bounds.Top - bottom; bounds.Bottom = bottom;
            }
            return bounds;
        }

        internal static List<object> Mark(List<FilmTarget> targets, FilmOutputOptions options)
        {
            options.Validate();
            foreach (FilmTarget target in targets)
            {
                if (target.Caption.Number < 1 || target.Caption.Number > 9999 || String.IsNullOrWhiteSpace(target.Caption.Color))
                    throw new InvalidOperationException("Укажите цвет и номер слоя от 1 до 9999 для каждой подписи.");
                if (target.Existing != null && Children(target.Existing).Count(s => Name(s) == Marks || FilmMarkPairs.IsPair(s)) > 1)
                    throw new InvalidOperationException("В комплекте несколько пар меток. Меняйте их подписи в таблице пар.");
                if (target.Existing == null && FilmMarkPairs.Geometries(target.Shapes).Count > 0)
                    throw new InvalidOperationException("В выделении уже есть пара меток. Измените её подпись в таблице пар или выделите только рисунок для новой разметки.");
            }
            return Change(targets, "Vanya Tools — метки фотовывода", touched => {
                var jobs = new List<object>();
                foreach (FilmTarget target in targets)
                {
                    dynamic art;
                    object existingMarks = null;
                    bool preserveBands = false;
                    if (target.Existing != null)
                    {
                        var old = Children(target.Existing);
                        var artwork = Artwork(old);
                        var obsolete = old.Where(s => Name(s) == Caption || Name(s) == Bands).ToList();
                        object existingPair = old.FirstOrDefault(FilmMarkPairs.IsPair);
                        existingMarks = existingPair == null ? old.FirstOrDefault(s => Name(s) == Marks) : FilmMarkPairs.Geometry(existingPair);
                        preserveBands = old.Any(s => Name(s) == Bands);
                        ((dynamic)target.Existing).UngroupEx(); touched();
                        if (existingPair != null)
                        {
                            var pairParts = Children(existingPair);
                            ((dynamic)existingPair).UngroupEx();
                            foreach (object label in pairParts.Where(s => Name(s) == Caption)) ((dynamic)label).Delete();
                        }
                        art = GroupArtwork(artwork);
                        foreach (object child in obsolete) ((dynamic)child).Delete();
                    }
                    else { touched(); art = GroupArtwork(target.Shapes); }
                    dynamic layer = art.Layer;
                    FilmBounds bounds = Measure(target.Document, (object)art);
                    var markShapes = new List<object>();
                    if (existingMarks == null) foreach (double cy in new[] { bounds.Top + options.MarkOffset, bounds.Bottom - options.MarkOffset })
                    {
                        foreach (double radius in new[] { 4.0, 2.5 })
                        {
                            dynamic circle = layer.CreateEllipse2(bounds.CenterX, cy, radius, radius);
                            markShapes.Add((object)circle); Outline((object)circle, options.MarkStroke);
                        }
                        double d = 5 / Math.Sqrt(2);
                        foreach (double[] line in new[] {
                            new[] { bounds.CenterX - 5, cy, bounds.CenterX + 5, cy },
                            new[] { bounds.CenterX, cy - 5, bounds.CenterX, cy + 5 },
                            new[] { bounds.CenterX - d, cy - d, bounds.CenterX + d, cy + d } })
                        {
                            dynamic shape = layer.CreateLineSegment(line[0], line[1], line[2], line[3]);
                            markShapes.Add((object)shape); Outline((object)shape, options.MarkStroke);
                        }
                    }
                    dynamic marks = existingMarks ?? (object)((dynamic)Range(markShapes)).Group(); marks.Name = Marks;
                    object labelShape = FilmMarkPairs.CreateLabel(target.Document, (object)marks, target.Caption);
                    object markPair = FilmMarkPairs.Wrap((object)marks, labelShape, target.Caption);
                    var parts = new List<object> { (object)art, markPair };
                    if (preserveBands)
                    {
                        FilmBounds frame = Measure(target.Document, Range(parts));
                        double bottom = Math.Min(frame.Bottom, Measure(target.Document, (object)marks).Bottom - 5);
                        frame.Height = frame.Top - bottom; frame.Bottom = bottom;
                        parts.Add(CreateBands((object)layer, frame, BandGap(frame, options), options.BandWidth));
                    }
                    dynamic job = ((dynamic)Range(parts)).Group();
                    job.Name = JobName(target.Caption); jobs.Add((object)job);
                }
                return jobs;
            });
        }

        internal static string JobName(FilmCaption caption) => Prefix + Uri.EscapeDataString((caption.Color ?? "").Trim()) + "|" + caption.Number;

        internal static double BandGap(FilmBounds bounds, FilmOutputOptions options)
        {
            double available = (options.FilmWidth - bounds.Width - 2 * options.BandWidth) / 2;
            if (available + .00001 >= options.BandGap) return options.BandGap;
            if (options.FitBands && available >= 2) return available;
            throw new InvalidOperationException("Фотовывод с полосами не помещается на плёнке " +
                options.FilmWidth.ToString("0.##", CultureInfo.CurrentCulture) + " мм. Ширина рисунка с метками и подписью: " +
                bounds.Width.ToString("0.##", CultureInfo.CurrentCulture) + " мм. Минимальный зазор до полос — 2 мм. Масштаб макета не изменён.");
        }

        internal static object CreateBands(object layerObject, FilmBounds bounds, double gap, double width)
        {
            dynamic layer = layerObject;
            var shapes = new List<object>();
            foreach (double left in new[] { bounds.Left - gap - width, bounds.Right + gap })
            {
                dynamic stripe = layer.CreateRectangle2(left, bounds.Bottom, width, bounds.Height);
                shapes.Add((object)stripe); BlackFill((object)stripe);
                double tickLeft = left < bounds.Left ? left : left + width - 2;
                foreach (double y in new[] { bounds.Bottom, bounds.Top - width })
                {
                    dynamic tick = layer.CreateRectangle2(tickLeft, y, 2.0, width);
                    shapes.Add((object)tick); BlackFill((object)tick);
                }
            }
            dynamic group = ((dynamic)Range(shapes)).Group(); group.Name = Bands; return (object)group;
        }

        internal static List<object> AddBands(List<FilmTarget> targets, FilmOutputOptions options)
        {
            options.Validate();
            var bounds = targets.Select(PrintBounds).ToList();
            var gaps = bounds.Select(b => BandGap(b, options)).ToList(); // Preflight every target before mutation.
            return Change(targets, "Vanya Tools — полосы фотовывода", touched => {
                var jobs = new List<object>();
                for (int i = 0; i < targets.Count; i++)
                {
                    FilmTarget target = targets[i]; List<object> parts;
                    if (target.Existing != null)
                    {
                        parts = Children(target.Existing); ((dynamic)target.Existing).UngroupEx(); touched();
                        var oldBands = parts.Where(s => Name(s) == Bands).ToList();
                        parts = parts.Except(oldBands).ToList();
                        foreach (object old in oldBands) ((dynamic)old).Delete();
                    }
                    else
                    {
                        touched(); dynamic art = GroupArtwork(target.Shapes);
                        parts = new List<object> { (object)art };
                    }
                    parts.Add(CreateBands((object)((dynamic)parts[0]).Layer, bounds[i], gaps[i], options.BandWidth));
                    dynamic job = ((dynamic)Range(parts)).Group(); job.Name = JobName(target.Caption); jobs.Add((object)job);
                }
                return jobs;
            });
        }

        private static List<object> Change(List<FilmTarget> targets, string title, Func<Action, List<object>> action)
        {
            dynamic app = CorelApp.Get(), doc = targets[0].Document;
            int unit = Convert.ToInt32(doc.Unit); bool optimization = Convert.ToBoolean(app.Optimization);
            bool started = false, touched = false;
            try
            {
                doc.Activate(); doc.Unit = CorelConstants.CdrMillimeter; app.Optimization = true;
                doc.BeginCommandGroup(title); started = true;
                List<object> result = action(() => touched = true);
                ((dynamic)Range(result)).CreateSelection(); return result;
            }
            catch
            {
                if (started) { doc.EndCommandGroup(); started = false; if (touched) doc.Undo(); }
                throw;
            }
            finally
            {
                if (started) doc.EndCommandGroup();
                doc.Unit = unit; app.Optimization = optimization;
            }
        }

        private static void Outline(object shape, double width)
        {
            dynamic item = shape; item.Fill.ApplyNoFill(); item.Outline.Width = width;
            item.Outline.Color.CMYKAssign(0, 0, 0, 100);
        }
        internal static void BlackFill(object shape)
        {
            dynamic item = shape; item.Fill.UniformColor.CMYKAssign(0, 0, 0, 100); item.Outline.SetNoOutline();
        }
    }
}
