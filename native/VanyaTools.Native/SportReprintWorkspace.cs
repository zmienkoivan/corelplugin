using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed partial class SportLayoutService
    {
        private const string ReprintOwner = "85C0D8D8-17EA-4DE5-9519-943864893216";
        private const string ReprintPageName = "Допечатка · рабочий лист";
        private const string ReprintShapePrefix = "VT_REPRINT_";
        private const string ReprintFrameName = "VT_REPRINT_PRESS_SHEET";

        // Geometry and artwork stay in the CDR. Only their identity and order
        // metadata are stored here, so closing the docker cannot lose the set.
        private sealed class ReprintState
        {
            public int Version { get; set; } = 1;
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public List<ReprintEntry> Entries { get; set; } = new List<ReprintEntry>();
            internal string LayerName => "VT_REPRINT_WORK_" + Id;
        }

        private sealed class ReprintEntry
        {
            public long ShapeId { get; set; }
            public string ShapeName { get; set; }
            public string PlayerKey { get; set; }
            public string Size { get; set; }
            public string Name { get; set; }
            public string Number { get; set; }
            public int Part { get; set; }
        }

        private sealed class ReprintLocation
        {
            internal object Page, Layer;
        }

        private sealed class ReprintLive
        {
            internal object Shape, Page, Layer;
            internal ReprintEntry Entry;
            internal NestedPiece Piece;
        }

        private static string ReadReprintJson(object document)
        {
            object properties = ((dynamic)document).Properties;
            if (!Convert.ToBoolean(properties.GetType().InvokeMember("Exists", BindingFlags.InvokeMethod,
                null, properties, new object[] { ReprintOwner, 1 }))) return null;
            return Convert.ToString(properties.GetType().InvokeMember("Item", BindingFlags.GetProperty,
                null, properties, new object[] { ReprintOwner, 1 }));
        }

        private static ReprintState ReadReprintState(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return new ReprintState();
            var state = new JavaScriptSerializer { MaxJsonLength = 16777216 }.Deserialize<ReprintState>(json);
            if (state == null || state.Version != 1 || !Guid.TryParseExact(state.Id, "N", out _) ||
                state.Entries == null || state.Entries.Count > 1500 || state.Entries.Any(entry => entry == null ||
                    String.IsNullOrWhiteSpace(entry.Size) || String.IsNullOrWhiteSpace(entry.ShapeName) ||
                    entry.Part < 0 || entry.Part > 4) ||
                state.Entries.Select(entry => entry.ShapeName).Distinct(StringComparer.Ordinal).Count() != state.Entries.Count)
                throw new InvalidOperationException("Не удалось прочитать рабочий набор допечатки из CDR.");
            return state;
        }

        private static void WriteReprintState(object document, ReprintState state)
        {
            WriteReprintJson(document, new JavaScriptSerializer { MaxJsonLength = 16777216 }.Serialize(state));
        }

        private static void WriteReprintJson(object document, string json)
        {
            object properties = ((dynamic)document).Properties;
            properties.GetType().InvokeMember("Item", BindingFlags.SetProperty, null, properties,
                new object[] { ReprintOwner, 1, json });
            ((dynamic)document).Dirty = true;
        }

        private static List<ReprintLocation> ReprintPages(object document, ReprintState state)
        {
            var result = new List<ReprintLocation>();
            foreach (dynamic page in ((dynamic)document).Pages)
                foreach (dynamic layer in page.Layers)
                    if (String.Equals(Convert.ToString(layer.Name), state.LayerName, StringComparison.Ordinal))
                    { result.Add(new ReprintLocation { Page = (object)page, Layer = (object)layer }); break; }
            return result;
        }

        private static List<ReprintLive> ReprintPieces(object document, ReprintState state)
        {
            var names = state.Entries.ToDictionary(entry => entry.ShapeName, StringComparer.Ordinal);
            var ids = state.Entries.Where(entry => entry.ShapeId > 0).GroupBy(entry => entry.ShapeId)
                .ToDictionary(group => group.Key, group => group.First());
            var result = new List<ReprintLive>();
            // Names also identify a manually copied part. Its current StaticID
            // differs from the saved original, but its editable objects remain.
            foreach (var location in ReprintPages(document, state))
            {
                dynamic page = location.Page;
                foreach (dynamic layer in page.Layers)
                    foreach (dynamic shape in layer.Shapes)
                    {
                        ReprintEntry entry;
                        if (!ids.TryGetValue(Convert.ToInt64(shape.StaticID), out entry) &&
                            !names.TryGetValue(Convert.ToString(shape.Name) ?? "", out entry)) continue;
                        result.Add(new ReprintLive { Shape = (object)shape, Page = (object)page,
                            Layer = (object)layer, Entry = entry });
                    }
            }
            if (result.Count > 1500) throw new InvalidOperationException("В рабочем наборе допечатки больше 1500 деталей.");
            return result.OrderBy(piece => state.Entries.IndexOf(piece.Entry)).ToList();
        }

        private static void ValidateReprintOptions(SportLayoutOptions options)
        {
            if (options == null || options.SheetWidthMm < 300 || options.SheetWidthMm > 4000 ||
                options.MaxPageLengthMm < 500 || options.MaxPageLengthMm > 45000 || options.GapMm < 0 || options.GapMm > 100 ||
                Double.IsNaN(options.SheetWidthMm + options.MaxPageLengthMm + options.GapMm) ||
                Double.IsInfinity(options.SheetWidthMm + options.MaxPageLengthMm + options.GapMm))
                throw new InvalidOperationException("Проверьте ширину печати, максимальную длину и расстояние между деталями.");
        }

        private static void ValidateReprintPages(IList<ReprintLocation> locations, IList<ReprintLive> parts)
        {
            var known = new HashSet<long>(parts.Select(part => (long)Convert.ToInt64(((dynamic)part.Shape).StaticID)));
            foreach (var location in locations)
                foreach (dynamic layer in ((dynamic)location.Page).Layers)
                    foreach (dynamic shape in layer.Shapes)
                        if (!known.Contains(Convert.ToInt64(shape.StaticID)) &&
                            !String.Equals(Convert.ToString(shape.Name), ReprintFrameName, StringComparison.Ordinal))
                            throw new InvalidOperationException("На рабочем листе допечатки есть объект вне группы детали: «" +
                                (Convert.ToString(shape.Name) ?? "без имени") + "». Добавьте его внутрь нужной группы перед расстановкой.");
        }

        private SportLayoutResult AddReprintPiece(SportLayoutPlayer row, IList<SportLayoutTemplate> templates,
            SportLayoutOptions options, SportLayoutPart part, string playerKey)
        {
            if (row == null) throw new InvalidOperationException("Выберите игрока.");
            if (!Enum.IsDefined(typeof(SportLayoutPart), part)) throw new ArgumentOutOfRangeException(nameof(part));
            ValidateReprintOptions(options);
            string size = NormalizeSize(row.Size);
            if (size.Length == 0 || (row.Name ?? "").Length > 100 || (row.Number ?? "").Length > 20)
                throw new InvalidOperationException("Проверьте размер, имя и номер игрока для допечатки.");
            var bySize = new Dictionary<string, SportLayoutTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var template in templates ?? new SportLayoutTemplate[0])
            {
                if (template == null || String.IsNullOrWhiteSpace(template.Path)) continue;
                string key = NormalizeSize(template.Size);
                if (key.Length == 0) continue;
                if (bySize.ContainsKey(key)) throw new InvalidOperationException("Шаблон размера " + key + " добавлен дважды.");
                bySize.Add(key, template);
            }
            if (!bySize.ContainsKey(size)) throw new InvalidOperationException("Нет шаблона для размера " + size + ".");

            dynamic app = CorelApp.Get(), document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте CDR проекта формы.");
            string previousJson = ReadReprintJson((object)document);
            var state = ReadReprintState(previousJson);
            var existing = ReprintPieces((object)document, state);
            if (existing.Count >= 1500) throw new InvalidOperationException("Сначала разделите набор допечатки: максимум 1500 деталей.");
            var locations = ReprintPages((object)document, state);
            ValidateReprintPages(locations, existing);
            object previousPage = (object)document.ActivePage;
            int previousUnit = Convert.ToInt32(document.Unit);
            bool previousOptimization = Convert.ToBoolean(app.Optimization), previousDirty = Convert.ToBoolean(document.Dirty);
            bool started = false, touched = false, succeeded = false, stateWritten = false;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                double scale;
                dynamic sourcePage = FindMasterPage(document, size, bySize, out scale);
                Garment source = ReadPreparedGarment(sourcePage, size);
                if (source.FrontCut != null && Math.Abs(scale - 1) > .0001)
                    throw new InvalidOperationException("Подготовьте размер " + size + " на этапе 1 перед допечаткой.");
                if (part == SportLayoutPart.Neck && source.Neck == null)
                    throw new InvalidOperationException("У размера " + size + " нет детали горловины.");
                app.Optimization = true;
                document.BeginCommandGroup("Vanya Tools — добавить деталь в допечатку"); started = true;
                ReprintLocation location;
                if (locations.Count == 0)
                {
                    touched = true;
                    dynamic page = document.AddPages(1); page.Activate(); page.Name = ReprintPageName;
                    page.SetSize(options.SheetWidthMm, 1000.0);
                    location = new ReprintLocation { Page = (object)page,
                        Layer = (object)page.CreateLayer(state.LayerName) };
                }
                else location = locations[0];
                dynamic workPage = location.Page, workLayer = location.Layer; touched = true;
                workPage.Activate(); workLayer.Activate(); workLayer.Visible = true; workLayer.Printable = true;
                var build = options.Copy(); build.NestingMode = SportNestingMode.FullContour;
                var snapshot = new SportLayoutPlayer { Size = size, Name = row.Name, Number = row.Number, Quantity = 1 };
                touched = true;
                var garment = CopyGarment(source, workLayer, part, snapshot);
                var kit = BuildNestedPieces(app, workLayer, garment, snapshot, size, existing.Count + 1,
                    build, scale, part, new Dictionary<string, SportPrintFootprint.Body>());
                if (kit.Count != 1) throw new InvalidOperationException("Не удалось подготовить одну выбранную деталь допечатки.");
                var piece = kit[0];
                var entry = new ReprintEntry { ShapeName = ReprintShapePrefix + Guid.NewGuid().ToString("N"),
                    PlayerKey = playerKey ?? "", Size = size, Name = row.Name ?? "", Number = row.Number ?? "", Part = (int)part };
                piece.Shape.Name = entry.ShapeName; entry.ShapeId = Convert.ToInt64(piece.Shape.StaticID);

                // A quick shelf only collects parts. It never moves or rebuilds
                // an earlier part; the real contour search runs on Расставить.
                double pageLeft = Convert.ToDouble(workPage.LeftX), pageTop = Convert.ToDouble(workPage.TopY);
                double width = Math.Max(Convert.ToDouble(workPage.SizeWidth), Math.Max(options.SheetWidthMm, piece.Width));
                var earlier = existing.Where(item => SportProjectPlayers.SameDocument(item.Page, location.Page)).ToList();
                var boxes = earlier.Select(item => SportPrintFootprint.Measure(item.Shape)).ToList();
                double left = 0, top = 0;
                if (boxes.Count > 0)
                {
                    var last = boxes[boxes.Count - 1];
                    left = last.Right - pageLeft + options.GapMm; top = pageTop - last.Top;
                    bool clear = left + piece.Width <= width + .01 && boxes.All(box =>
                        pageLeft + left + piece.Width + options.GapMm <= box.Left + .01 ||
                        box.Right + options.GapMm <= pageLeft + left + .01 ||
                        pageTop - top + options.GapMm <= box.Bottom + .01 ||
                        box.Top + options.GapMm <= pageTop - top - piece.Height + .01);
                    if (!clear) { left = 0; top = boxes.Max(box => pageTop - box.Bottom) + options.GapMm; }
                }
                double height = Math.Max(Convert.ToDouble(workPage.SizeHeight), top + piece.Height);
                if (width > 45000 || height > 45000)
                    throw new InvalidOperationException("Рабочий лист достиг 45 м. Нажмите «Расставить допечатку» перед добавлением новых деталей.");
                var anchors = earlier.Select(item => new { item.Shape, Bounds = SportPrintFootprint.Measure(item.Shape, false) }).ToList();
                workPage.SetSize(width, Math.Max(10, height));
                double dx = Convert.ToDouble(workPage.LeftX) - pageLeft, dy = Convert.ToDouble(workPage.TopY) - pageTop;
                foreach (var anchor in anchors)
                {
                    // Corel versions differ in how resizing anchors contents.
                    // Preserve each earlier part's coordinates on the paper.
                    var current = SportPrintFootprint.Measure(anchor.Shape, false);
                    if (Math.Abs(current.Width - anchor.Bounds.Width) > .05 || Math.Abs(current.Height - anchor.Bounds.Height) > .05)
                        throw new InvalidOperationException("Corel изменил размер прежней детали при расширении рабочего листа.");
                    double moveX = anchor.Bounds.Left + dx - current.Left, moveY = anchor.Bounds.Bottom + dy - current.Bottom;
                    if (Math.Abs(moveX) > .0001 || Math.Abs(moveY) > .0001) ((dynamic)anchor.Shape).Move(moveX, moveY);
                }
                SportPrintFootprint.Place((object)piece.Shape, piece.PrintGeometry, location.Layer, false,
                    new SportSheetNesting.Placement { Left = left, Top = top, Width = piece.Width, Height = piece.Height },
                    Convert.ToDouble(workPage.LeftX), Convert.ToDouble(workPage.TopY));
                // Drop metadata only for parts actually deleted from this CDR.
                state.Entries = state.Entries.Where(item => existing.Any(live => ReferenceEquals(live.Entry, item))).ToList();
                state.Entries.Add(entry); stateWritten = true; WriteReprintState((object)document, state);
                workPage.Activate(); succeeded = true;
                return new SportLayoutResult { PieceCount = existing.Count + 1, PageCount = Math.Max(1, locations.Count),
                    SheetHeightMm = height, GarmentCount = 1 };
            }
            catch
            {
                if (started) { document.EndCommandGroup(); started = false; if (touched) document.Undo(); }
                if (stateWritten) WriteReprintJson((object)document, previousJson ??
                    new JavaScriptSerializer().Serialize(new ReprintState { Id = state.Id }));
                if (touched) document.Dirty = previousDirty;
                throw;
            }
            finally
            {
                try { if (started) document.EndCommandGroup(); } finally
                {
                    document.Unit = previousUnit; app.Optimization = previousOptimization;
                    if (!succeeded) try { ((dynamic)previousPage).Activate(); } catch { }
                }
            }
        }

        public SportLayoutResult ArrangeReprintWorkspace(SportLayoutOptions options,
            Func<IList<SportSheetNesting.Item>, IList<string>, SportNestingChoice> preview)
        {
            ValidateReprintOptions(options);
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            dynamic app = CorelApp.Get(), document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте CDR проекта формы.");
            var state = ReadReprintState(ReadReprintJson((object)document));
            var live = ReprintPieces((object)document, state);
            if (live.Count == 0) throw new InvalidOperationException("Добавьте детали кнопками ↗ в таблице игроков. Они собираются на одном рабочем листе допечатки.");
            var locations = ReprintPages((object)document, state);
            ValidateReprintPages(locations, live);
            object previousPage = (object)document.ActivePage;
            int previousUnit = Convert.ToInt32(document.Unit);
            bool previousOptimization = Convert.ToBoolean(app.Optimization), succeeded = false, started = false, touched = false;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                foreach (var page in live.GroupBy(item => item.Page))
                {
                    ((dynamic)page.Key).Activate();
                    foreach (var item in page) item.Piece = ReadReprintGeometry(item);
                }
                int garment = 0;
                foreach (var player in live.GroupBy(item => String.Join("\u001F", new[] {
                    item.Entry.PlayerKey, item.Entry.Size, item.Entry.Name, item.Entry.Number })))
                {
                    var counts = new int[5]; int first = garment;
                    foreach (var item in player)
                    {
                        item.Piece.GarmentIndex = first + ++counts[item.Entry.Part];
                    }
                    garment += counts.Max();
                }
                var inputs = live.Select(item => new SportSheetNesting.Item { Width = item.Piece.Width, Height = item.Piece.Height,
                    Bands = item.Piece.ContourLayout.Bands, SmallPart = item.Piece.SmallPart,
                    Allow90 = item.Piece.SmallPart, Garment = item.Piece.GarmentIndex, Part = item.Piece.PartIndex,
                    Size = item.Piece.Size, Label = item.Piece.Label }).ToList();
                ((dynamic)previousPage).Activate(); document.Unit = previousUnit; app.Optimization = previousOptimization;
                // The preview receives managed snapshots only. No Corel proxy
                // is captured by its background nesting calculations.
                SportNestingChoice choice = preview(inputs, live.Select(item => item.Piece.Label).ToList());
                if (choice == null) throw new OperationCanceledException();
                SportNestingPreview.Validate(choice, live.Count);
                if (!SportProjectPlayers.SameDocument((object)document, (object)app.ActiveDocument))
                    throw new InvalidOperationException("Активный CDR изменился во время подбора допечатки. Откройте нужный проект и повторите.");
                document.Unit = CorelConstants.CdrMillimeter; app.Optimization = true;
                document.BeginCommandGroup("Vanya Tools — расставить рабочий лист допечатки"); started = true;
                foreach (var location in locations)
                {
                    var frames = new List<object>();
                    foreach (dynamic shape in ((dynamic)location.Layer).Shapes)
                        if (String.Equals(Convert.ToString(shape.Name), ReprintFrameName, StringComparison.Ordinal)) frames.Add((object)shape);
                    foreach (dynamic frame in frames) { touched = true; frame.Delete(); }
                }
                while (locations.Count < choice.Layout.PageHeights.Count)
                {
                    touched = true;
                    dynamic page = document.AddPages(1); page.Activate();
                    locations.Add(new ReprintLocation { Page = (object)page,
                        Layer = (object)page.CreateLayer(state.LayerName) });
                }
                for (int index = 0; index < choice.Layout.PageHeights.Count; index++)
                {
                    dynamic page = locations[index].Page; page.Activate(); touched = true;
                    page.SetSize(choice.Settings.Width, choice.Layout.PageHeights[index]);
                    page.Name = ReprintPageName + (choice.Layout.PageHeights.Count > 1 ? " · " + (index + 1) + "/" + choice.Layout.PageHeights.Count : "");
                    ((dynamic)locations[index].Layer).Visible = true; ((dynamic)locations[index].Layer).Printable = true;
                }
                foreach (var placed in choice.Layout.Placements)
                {
                    var item = live[placed.Index]; var location = locations[placed.Page];
                    dynamic page = location.Page; page.Activate(); touched = true;
                    SportPrintFootprint.Place(item.Shape, item.Piece.PrintGeometry, location.Layer, true, placed,
                        Convert.ToDouble(page.LeftX), Convert.ToDouble(page.TopY));
                }
                if (choice.Settings.Borders)
                    foreach (var panel in choice.Panels)
                    {
                        var location = locations[panel.Page]; dynamic page = location.Page, layer = location.Layer;
                        page.Activate();
                        dynamic border = layer.CreateRectangle2(Convert.ToDouble(page.LeftX) + panel.Left + .05,
                            Convert.ToDouble(page.TopY) - panel.Top - panel.Height + .05, panel.Width - .1, panel.Height - .1);
                        border.Name = ReprintFrameName; border.Fill.ApplyNoFill();
                        border.Outline.Width = .1; border.Outline.Color.RGBAssign(190, 190, 190);
                    }
                // Reuse earlier workspace pages; delete only surplus pages
                // that are completely empty, including all user layers.
                for (int index = locations.Count - 1; index >= choice.Layout.PageHeights.Count; index--)
                {
                    dynamic page = locations[index].Page; bool empty = true;
                    foreach (dynamic layer in page.Layers) if (Convert.ToInt32(layer.Shapes.Count) != 0) { empty = false; break; }
                    if (empty) document.DeletePages(Convert.ToInt32(page.Index));
                }
                choice.Settings.Apply(options, false);
                ((dynamic)locations[0].Page).Activate(); succeeded = true;
                return new SportLayoutResult { PieceCount = live.Count, GarmentCount = garment,
                    PageCount = choice.Layout.PageHeights.Count, SheetHeightMm = choice.Layout.PageHeights.Sum() };
            }
            catch
            {
                if (started) { document.EndCommandGroup(); started = false; if (touched) document.Undo(); }
                throw;
            }
            finally
            {
                try { if (started) document.EndCommandGroup(); } finally
                {
                    document.Unit = previousUnit; app.Optimization = previousOptimization;
                    if (!succeeded) try { ((dynamic)previousPage).Activate(); } catch { }
                }
            }
        }

        private static NestedPiece ReadReprintGeometry(ReprintLive live)
        {
            dynamic root = live.Shape;
            var children = new List<object>();
            if (Convert.ToInt32(root.Type) == CorelConstants.CdrGroupShape)
                foreach (dynamic child in root.Shapes) children.Add((object)child);
            else children.Add(live.Shape);
            string[] containers = { "VT_ПЕРЕД", "VT_СПИНА", "VT_ЛЕВЫЙ_РУКАВ", "VT_ПРАВЫЙ_РУКАВ", "VT_ГОРЛОВИНА" };
            object container = Convert.ToInt32(root.Type) != CorelConstants.CdrGroupShape ? live.Shape :
                children.FirstOrDefault(child => String.Equals(Convert.ToString(((dynamic)child).Name),
                    containers[live.Entry.Part], StringComparison.Ordinal));
            IList<object> ink, extras;
            if (container == null) { container = live.Shape; ink = new[] { live.Shape }; extras = new object[0]; }
            else
            {
                ink = children.Where(child => ReferenceEquals(child, container) || !BlankReprintGuide(child)).ToList();
                extras = ink.Where(child => !ReferenceEquals(child, container)).ToList();
            }
            var layout = SportPrintFootprint.Build(container, extras, ink,
                new Dictionary<string, SportPrintFootprint.Body>(), "current", container != live.Shape || children.Count == 1);
            return new NestedPiece { Shape = root, PrintGeometry = SportPrintFootprint.Capture(live.Shape, ink),
                ContourLayout = layout, Width = layout.Width, Height = layout.Height, PartIndex = live.Entry.Part,
                SmallPart = live.Entry.Part >= 2, Size = live.Entry.Size,
                Label = "Допечатка · " + live.Entry.Size + " · " + live.Entry.Name + " · " +
                    new[] { "перед", "спина", "левый рукав", "правый рукав", "горловина" }[live.Entry.Part] };
        }

        private static bool BlankReprintGuide(object shapeObject)
        {
            dynamic shape = shapeObject;
            if (!(Convert.ToString(shape.Name) ?? "").StartsWith("VT_FRAME_", StringComparison.Ordinal)) return false;
            return Convert.ToInt32(shape.Fill.Type) == 0 && Convert.ToInt32(shape.Outline.Type) == 0;
        }
    }
}
