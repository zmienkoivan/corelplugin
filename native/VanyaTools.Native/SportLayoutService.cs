using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal sealed class SportLayoutPlayer
    {
        public string Size { get; set; }
        public string Name { get; set; }
        public string Number { get; set; }
        public int Quantity { get; set; }
    }

    internal sealed class SportLayoutTemplate
    {
        public string Size { get; set; }
        public string Path { get; set; }
        public string LayerName { get; set; }
        public double ScalePercent { get; set; }
    }

    internal sealed class SportLayoutOptions
    {
        public string FrontArtworkPath;
        public string BackArtworkPath;
        public string LeftSleeveArtworkPath;
        public string RightSleeveArtworkPath;
        public string NeckArtworkPath;
        public SportTextStyle NameStyle;
        public SportTextStyle NumberStyle;
        public bool FrontNameOnRight;
        public bool WhiteText;
        public bool KeepGuides;
        public double SheetWidthMm;
        public double GapMm;
        public bool RotateSleeves;
    }

    internal sealed class SportTextStyle
    {
        public string FontName;
        public float MaxPoints;
        public bool Bold;
        public bool Italic;
        public string FillHex;
        public string OutlineHex;
        public double OutlineWidthMm;
    }

    internal sealed class SportLayoutResult
    {
        public int GarmentCount;
        public int PieceCount;
        public double SheetHeightMm;
    }

    internal enum SportLayoutPart { Front, Back, LeftSleeve, RightSleeve, Neck }

    // The supplied M template has four large orange garment contours and five
    // empty red rectangles; no shapes have names. Geometry identifies the
    // parts before copying them into the current document.
    internal sealed class SportLayoutService
    {
        private sealed class Piece
        {
            public dynamic Shape;
            public double Left, Bottom, Width, Height;
            public double Right => Left + Width;
            public double Top => Bottom + Height;
            public double CenterX => Left + Width / 2;
            public double CenterY => Bottom + Height / 2;
            public double Area => Width * Height;
            public string Name;
            public bool Contains(Piece other)
            {
                return other.CenterX >= Left && other.CenterX <= Right &&
                    other.CenterY >= Bottom && other.CenterY <= Top;
            }
        }

        private sealed class Garment
        {
            public Piece Front, Back, LeftSleeve, RightSleeve, Neck;
            public Piece FrontLeft, FrontRight, BackText, BackNumber,
                LeftSleeveFrame, RightSleeveFrame, NeckFrame;
            public Piece FrontArtwork, BackArtwork, LeftSleeveArtwork,
                RightSleeveArtwork, NeckArtwork;
        }

        private const string MasterPrefix = "Спорт · макет · ";
        private const string OverviewName = "Спорт · макеты · все размеры";
        private const string SizeLayerPrefix = "VT_SPORT_SIZE_";
        private const string ZonesLayerName = "VT_ZONES";

        private sealed class TemplateSession
        {
            public dynamic Document;
            public bool OpenedHere;
            public int OriginalUnit;
        }

        private sealed class NestedPiece
        {
            public dynamic Shape;
            public double Width, Height, Left, Top;
            public bool CanRotate { get; set; }
            public bool Rotated { get; set; }
        }

        private sealed class SkylineSegment
        {
            public double Left, Right, Top;
        }

        public string AssignSelectedTextBox(string zoneName)
        {
            dynamic app = CorelApp.Get();
            dynamic document = app.ActiveDocument;
            string pageName = document == null ? "" : Convert.ToString(document.ActivePage.Name);
            if (document == null || (!pageName.StartsWith(MasterPrefix,
                StringComparison.OrdinalIgnoreCase) && !String.Equals(pageName,
                OverviewName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Откройте страницу-макет с размерами.");
            dynamic selection = app.ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) != 1)
                throw new InvalidOperationException("Выделите один нарисованный прямоугольник.");
            dynamic chosen = null;
            foreach (dynamic shape in selection.Shapes) { chosen = shape; break; }
            if (Convert.ToInt32(chosen.Type) != 1)
                throw new InvalidOperationException("Для бокса нарисуйте прямоугольник.");
            string previousName = Convert.ToString(chosen.Name) ?? "";
            if (previousName.StartsWith("VT_SPORT_ZONE_", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(previousName, zoneName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Эта рамка уже назначена другой зоне.");
            dynamic currentLayer = chosen.Layer;
            if (String.Equals(pageName, OverviewName, StringComparison.OrdinalIgnoreCase) &&
                !Convert.ToString(currentLayer.Name).StartsWith(SizeLayerPrefix,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Нарисуйте бокс на слое нужного размера.");
            dynamic previous = null;
            foreach (dynamic layer in document.ActivePage.Layers)
            {
                if (String.Equals(pageName, OverviewName, StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(Convert.ToString(layer.Name),
                        Convert.ToString(currentLayer.Name), StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (dynamic shape in layer.Shapes)
                    if (String.Equals(Convert.ToString(shape.Name), zoneName,
                        StringComparison.OrdinalIgnoreCase)) previous = shape;
            }
            document.BeginCommandGroup("Vanya Tools - бокс надписи спортформы");
            try
            {
                if (previous != null && !String.Equals(previousName, zoneName,
                    StringComparison.OrdinalIgnoreCase)) previous.Delete();
                chosen.Fill.ApplyNoFill();
                chosen.Outline.Width = 0.5;
                chosen.Outline.Color.RGBAssign(255, 0, 0);
                chosen.Name = zoneName;
                return Convert.ToString(document.ActivePage.Name);
            }
            finally { document.EndCommandGroup(); }
        }

        // The master remains editable in the current CDR. Artwork is placed
        // behind the outlines and red zones; generation reads these edited
        // shapes instead of reopening the original template or PNG files.
        public string PrepareDesign(SportLayoutTemplate template, SportLayoutOptions options)
        {
            if (template == null || String.IsNullOrWhiteSpace(template.Path) ||
                !File.Exists(template.Path) ||
                !String.Equals(System.IO.Path.GetExtension(template.Path), ".cdr",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выберите существующий CDR-шаблон лекала.");
            string size = NormalizeSize(template.Size);
            if (size.Length == 0) throw new InvalidOperationException("Укажите размер лекала.");
            double scale = template.ScalePercent <= 0 ? 1 : template.ScalePercent / 100.0;
            if (scale < 0.5 || scale > 2)
                throw new InvalidOperationException("Масштаб лекала: от 50 до 200%.");
            if (options == null) throw new InvalidOperationException("Укажите рисунки для деталей.");
            foreach (string path in new[] { options.FrontArtworkPath, options.BackArtworkPath,
                options.LeftSleeveArtworkPath, options.RightSleeveArtworkPath })
                if (!String.IsNullOrWhiteSpace(path) && !File.Exists(path))
                    throw new FileNotFoundException("Рисунок для детали не найден.", path);

            dynamic app = CorelApp.Get();
            dynamic target = app.ActiveDocument;
            if (target == null) throw new InvalidOperationException("Откройте рабочий CDR.");
            bool previousOptimization = false;
            try { previousOptimization = Convert.ToBoolean(app.Optimization); } catch { }
            dynamic previousPage = target.ActivePage;
            int previousUnit = Convert.ToInt32(target.Unit);
            string targetPath = null;
            try { targetPath = System.IO.Path.GetFullPath(Convert.ToString(target.FullFileName)); }
            catch { }
            string templatePath = System.IO.Path.GetFullPath(template.Path);
            bool sameDocument = String.Equals(templatePath, targetPath,
                StringComparison.OrdinalIgnoreCase);
            var sessions = new Dictionary<string, TemplateSession>(StringComparer.OrdinalIgnoreCase);
            dynamic masterPage = null;
            bool commandStarted = false;
            try
            {
                Log.Info("Sport legacy master: start, size=" + size);
                try { app.Optimization = true; } catch { }
                target.Unit = CorelConstants.CdrMillimeter;
                target.BeginCommandGroup("Vanya Tools - макет спортформы " + size);
                commandStarted = true;
                masterPage = target.AddPages(1);
                TemplateSession session = sameDocument ? null :
                    GetTemplateSession(app, templatePath, sessions);
                if (!sameDocument && Convert.ToInt32(session.Document.Pages.Count) != 1)
                    throw new InvalidOperationException("CDR-шаблон должен содержать одну страницу.");
                dynamic sourcePage = sameDocument ? previousPage : session.Document.ActivePage;
                if (sameDocument && Convert.ToString(sourcePage.Name).StartsWith(MasterPrefix,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Откройте страницу исходного лекала, а не готовый макет.");
                Garment source = ReadGarment(sourcePage);
                target.Activate();
                masterPage.Activate();
                masterPage.SizeWidth = Convert.ToDouble(sourcePage.SizeWidth) * scale;
                masterPage.SizeHeight = Convert.ToDouble(sourcePage.SizeHeight) * scale;
                dynamic layer = target.ActiveLayer;
                Garment master = sameDocument ? CopyGarment(source, layer) :
                    PasteGarment(app, source, sourcePage, target, masterPage, layer);
                SplitBackTextZone(master);
                NameMasterGarment(master);
                master.FrontArtwork = AddMasterArtwork(app, layer, options.FrontArtworkPath,
                    master.Front, "FRONT");
                master.BackArtwork = AddMasterArtwork(app, layer, options.BackArtworkPath,
                    master.Back, "BACK");
                master.LeftSleeveArtwork = AddMasterArtwork(app, layer,
                    options.LeftSleeveArtworkPath, master.LeftSleeve, "LEFT_SLEEVE");
                master.RightSleeveArtwork = AddMasterArtwork(app, layer,
                    String.IsNullOrWhiteSpace(options.RightSleeveArtworkPath)
                        ? options.LeftSleeveArtworkPath : options.RightSleeveArtworkPath,
                    master.RightSleeve, "RIGHT_SLEEVE");
                if (Math.Abs(scale - 1) > 0.0001)
                    StretchMaster(app, master, scale);
                foreach (dynamic page in target.Pages)
                    if (!Object.ReferenceEquals(page, masterPage) &&
                        String.Equals(Convert.ToString(page.Name), MasterPrefix + size,
                            StringComparison.OrdinalIgnoreCase))
                        page.Name = "Спорт · прежний макет · " + size + " · " +
                            Convert.ToInt32(page.Index);
                masterPage.Name = MasterPrefix + size;
                return size;
            }
            catch (Exception error)
            {
                try
                {
                    target.Activate();
                    if (masterPage != null) target.DeletePages(Convert.ToInt32(masterPage.Index));
                    previousPage.Activate();
                }
                catch (Exception rollbackError)
                {
                    Log.Error("Could not remove failed sport master page.", rollbackError);
                }
                throw new InvalidOperationException("Макет не подготовлен: " + error.Message, error);
            }
            finally
            {
                foreach (TemplateSession session in sessions.Values)
                    try
                    {
                        if (session.OpenedHere)
                        {
                            try { session.Document.Dirty = false; } catch { }
                            session.Document.Close();
                        }
                        else session.Document.Unit = session.OriginalUnit;
                    }
                    catch (Exception closeError) { Log.Error("Could not close sport template.", closeError); }
                try { target.Activate(); } catch { }
                try { target.Unit = previousUnit; } catch { }
                try { if (commandStarted) target.EndCommandGroup(); } catch { }
                try { app.Optimization = previousOptimization; } catch { }
            }
        }

        public IList<string> ScanLayeredModel(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Выберите файл модели CDR.", path);
            dynamic app = CorelApp.Get();
            dynamic previous = app.ActiveDocument;
            string fullPath = System.IO.Path.GetFullPath(path);
            var sessions = new Dictionary<string, TemplateSession>(StringComparer.OrdinalIgnoreCase);
            try
            {
                TemplateSession session = GetTemplateSession(app, fullPath, sessions);
                dynamic page = OnlyTemplatePage(session.Document);
                var names = new List<string>();
                bool zonesFound = false;
                foreach (dynamic layer in page.Layers)
                {
                    string name = Convert.ToString(layer.Name).Trim();
                    if (String.Equals(name, ZonesLayerName, StringComparison.OrdinalIgnoreCase))
                    { zonesFound = true; continue; }
                    foreach (dynamic shape in layer.Shapes)
                        if (String.Equals(Convert.ToString(shape.Name), "VT_TOP",
                            StringComparison.OrdinalIgnoreCase))
                        { names.Add(name); break; }
                }
                if (!zonesFound || names.Count == 0)
                    throw new InvalidOperationException("В CDR нужен верхний слой VT_ZONES и слои размеров с группой VT_TOP.");
                if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                    throw new InvalidOperationException("Названия слоёв размеров должны быть уникальными.");
                return names;
            }
            finally
            {
                CloseTemplateSessions(sessions);
                try { previous?.Activate(); } catch { }
            }
        }

        public string PrepareOverview(IList<SportLayoutTemplate> templates, SportLayoutOptions options)
        {
            if (templates == null || templates.Count == 0)
                throw new InvalidOperationException("Добавьте размеры лекал.");
            if (options == null) throw new InvalidOperationException("Укажите рисунки для деталей.");
            var sizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SportLayoutTemplate template in templates)
            {
                string size = NormalizeSize(template?.Size);
                if (size.Length == 0 || !sizes.Add(size))
                    throw new InvalidOperationException("Размеры должны быть заполнены и уникальны.");
                if (String.IsNullOrWhiteSpace(template.Path) || !File.Exists(template.Path))
                    throw new InvalidOperationException("Не найден шаблон размера " + size + ".");
                if (template.ScalePercent > 0 && (template.ScalePercent < 50 || template.ScalePercent > 200))
                    throw new InvalidOperationException("Масштаб размера " + size + ": от 50 до 200%.");
            }
            foreach (string path in new[] { options.FrontArtworkPath, options.BackArtworkPath,
                options.LeftSleeveArtworkPath, options.RightSleeveArtworkPath, options.NeckArtworkPath })
                if (!String.IsNullOrWhiteSpace(path) && !File.Exists(path))
                    throw new FileNotFoundException("Рисунок для детали не найден.", path);

            dynamic app = CorelApp.Get();
            dynamic target = app.ActiveDocument;
            if (target == null) throw new InvalidOperationException("Откройте рабочий CDR.");
            bool previousOptimization = false;
            try { previousOptimization = Convert.ToBoolean(app.Optimization); } catch { }
            dynamic previousPage = target.ActivePage;
            int previousUnit = Convert.ToInt32(target.Unit);
            string targetPath = null;
            try { targetPath = System.IO.Path.GetFullPath(Convert.ToString(target.FullFileName)); }
            catch { }
            var sessions = new Dictionary<string, TemplateSession>(StringComparer.OrdinalIgnoreCase);
            dynamic overview = null;
            bool commandStarted = false;
            try
            {
                Log.Info("Sport overview: start, sizes=" + templates.Count);
                try { app.Optimization = true; } catch { }
                target.Unit = CorelConstants.CdrMillimeter;
                target.BeginCommandGroup("Vanya Tools - обзор спортформы");
                commandStarted = true;
                overview = target.AddPages(1);
                overview.Activate();
                double nextX = 0, maxHeight = 0;
                foreach (SportLayoutTemplate template in templates)
                {
                    string size = NormalizeSize(template.Size);
                    Log.Info("Sport overview: preparing size " + size);
                    string path = System.IO.Path.GetFullPath(template.Path);
                    bool same = String.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase);
                    dynamic sourcePage = same ? previousPage :
                        OnlyTemplatePage(GetTemplateSession(app, path, sessions).Document);
                    if (same && String.Equals(Convert.ToString(sourcePage.Name), OverviewName,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Выберите исходный шаблон, а не обзорную страницу.");
                    target.Activate();
                    overview.Activate();
                    dynamic layer = overview.CreateLayer(SizeLayerPrefix + size);
                    Garment garment;
                    if (!String.IsNullOrWhiteSpace(template.LayerName))
                        garment = CopyLayeredGarment(app, sourcePage, template.LayerName,
                            target, overview, layer, same);
                    else
                    {
                        Garment source = ReadGarment(sourcePage);
                        garment = same ? CopyGarment(source, layer) :
                            PasteGarment(app, source, sourcePage, target, overview, layer);
                        SplitBackTextZone(garment);
                    }
                    NameMasterGarment(garment);
                    Log.Info("Sport overview: contours and boxes copied, size " + size);
                    garment.FrontArtwork = AddMasterArtwork(app, layer,
                        options.FrontArtworkPath, garment.Front, "FRONT");
                    garment.BackArtwork = AddMasterArtwork(app, layer,
                        options.BackArtworkPath, garment.Back, "BACK");
                    garment.LeftSleeveArtwork = AddMasterArtwork(app, layer,
                        options.LeftSleeveArtworkPath, garment.LeftSleeve, "LEFT_SLEEVE");
                    garment.RightSleeveArtwork = AddMasterArtwork(app, layer,
                        String.IsNullOrWhiteSpace(options.RightSleeveArtworkPath)
                            ? options.LeftSleeveArtworkPath : options.RightSleeveArtworkPath,
                        garment.RightSleeve, "RIGHT_SLEEVE");
                    if (garment.Neck != null)
                        garment.NeckArtwork = AddMasterArtwork(app, layer,
                            options.NeckArtworkPath, garment.Neck, "NECK");
                    Log.Info("Sport overview: artwork imported, size " + size);
                    double scale = template.ScalePercent <= 0 ? 1 : template.ScalePercent / 100.0;
                    if (Math.Abs(scale - 1) > 0.0001) StretchMaster(app, garment, scale);
                    dynamic tile = app.CreateShapeRange();
                    foreach (dynamic shape in layer.Shapes) tile.Add(shape);
                    if (Convert.ToInt32(tile.Count) == 0)
                        throw new InvalidOperationException("Пустой слой размера " + size + ".");
                    double tileWidth = Convert.ToDouble(tile.SizeWidth);
                    double tileHeight = Convert.ToDouble(tile.SizeHeight);
                    tile.Move(nextX - Convert.ToDouble(tile.LeftX),
                        35 - Convert.ToDouble(tile.BottomY));
                    dynamic label = layer.CreateArtisticTextWide(nextX, 15,
                        size, 0, 0, "Arial", 24);
                    label.Name = "VT_SPORT_LABEL_" + size;
                    nextX += tileWidth + 40;
                    maxHeight = Math.Max(maxHeight, tileHeight);
                }
                overview.SizeWidth = Math.Max(100, nextX - 40);
                overview.SizeHeight = maxHeight + 70;
                foreach (dynamic page in target.Pages)
                    if (!Object.ReferenceEquals(page, overview) &&
                        String.Equals(Convert.ToString(page.Name), OverviewName,
                            StringComparison.OrdinalIgnoreCase))
                        page.Name = "Спорт · прежние макеты · " + Convert.ToInt32(page.Index);
                overview.Name = OverviewName;
                Log.Info("Sport overview: complete");
                return String.Join(", ", templates.Select(x => NormalizeSize(x.Size)));
            }
            catch (Exception error)
            {
                try
                {
                    target.Activate();
                    if (overview != null) target.DeletePages(Convert.ToInt32(overview.Index));
                    previousPage.Activate();
                }
                catch (Exception rollbackError) { Log.Error("Could not remove sport overview.", rollbackError); }
                throw new InvalidOperationException("Макеты не подготовлены: " + error.Message, error);
            }
            finally
            {
                CloseTemplateSessions(sessions);
                try { target.Activate(); } catch { }
                try { target.Unit = previousUnit; } catch { }
                try { if (commandStarted) target.EndCommandGroup(); } catch { }
                try { app.Optimization = previousOptimization; } catch { }
            }
        }

        private static dynamic OnlyTemplatePage(dynamic document)
        {
            if (Convert.ToInt32(document.Pages.Count) != 1)
                throw new InvalidOperationException("CDR модели должен содержать одну страницу.");
            return document.ActivePage;
        }

        private static void CloseTemplateSessions(IDictionary<string, TemplateSession> sessions)
        {
            foreach (TemplateSession session in sessions.Values)
                try
                {
                    if (session.OpenedHere)
                    {
                        try { session.Document.Dirty = false; } catch { }
                        session.Document.Close();
                    }
                    else session.Document.Unit = session.OriginalUnit;
                }
                catch (Exception error) { Log.Error("Could not close sport template.", error); }
        }

        public SportLayoutResult GenerateInCurrentDocument(IList<SportLayoutPlayer> rows,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            Action<int, int, string> progress)
        {
            return Generate(rows, templates, options, progress, null);
        }

        public SportLayoutResult GenerateReplacementPiece(SportLayoutPlayer row,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            SportLayoutPart part)
        {
            if (row == null) throw new InvalidOperationException("Выберите игрока.");
            return Generate(new[] { new SportLayoutPlayer { Size = row.Size,
                Name = row.Name, Number = row.Number, Quantity = 1 } },
                templates, options, null, part);
        }

        private SportLayoutResult Generate(IList<SportLayoutPlayer> rows,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            Action<int, int, string> progress, SportLayoutPart? selectedPart)
        {
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("Добавьте игроков в таблицу.");
            if (templates == null || templates.Count == 0)
                throw new InvalidOperationException("Добавьте CDR-шаблоны для размеров.");
            if (options == null || options.SheetWidthMm < 300 || options.SheetWidthMm > 2000)
                throw new InvalidOperationException("Ширина полотна: от 300 до 2000 мм.");
            if (options.GapMm < 0 || options.GapMm > 100)
                throw new InvalidOperationException("Расстояние между деталями: от 0 до 100 мм.");
            if (options.NameStyle == null || options.NumberStyle == null ||
                String.IsNullOrWhiteSpace(options.NameStyle.FontName) ||
                String.IsNullOrWhiteSpace(options.NumberStyle.FontName))
                throw new InvalidOperationException("Выберите шрифты для имён и номера.");
            var bySize = new Dictionary<string, SportLayoutTemplate>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var template in templates)
            {
                if (template == null) continue;
                string size = NormalizeSize(template.Size);
                if (size.Length == 0 || String.IsNullOrWhiteSpace(template.Path)) continue;
                if (!String.Equals(System.IO.Path.GetExtension(template.Path), ".cdr",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Для размера " + size + " нужен файл CDR.");
                if (bySize.ContainsKey(size))
                    throw new InvalidOperationException("Шаблон размера " + size + " добавлен дважды.");
                double scale = template.ScalePercent <= 0 ? 100 : template.ScalePercent;
                if (scale < 50 || scale > 200)
                    throw new InvalidOperationException("Масштаб размера " + size + ": от 50 до 200%.");
                bySize.Add(size, template);
            }
            int total = 0;
            foreach (var row in rows)
            {
                if (row == null) continue;
                string size = NormalizeSize(row.Size);
                if (!bySize.ContainsKey(size))
                    throw new InvalidOperationException("Нет шаблона для размера " + size + ".");
                if (row.Quantity < 1 || row.Quantity > 100)
                    throw new InvalidOperationException("Количество в строке: от 1 до 100.");
                if ((row.Name ?? "").Length > 100 || (row.Number ?? "").Length > 20)
                    throw new InvalidOperationException("Слишком длинное имя или номер в таблице.");
                total += row.Quantity;
            }
            if (total < 1 || total > 300)
                throw new InvalidOperationException("За один запуск можно подготовить от 1 до 300 комплектов.");

            dynamic app = CorelApp.Get();
            dynamic target = app.ActiveDocument;
            if (target == null)
                throw new InvalidOperationException("Откройте CDR с рисунками для раскладки.");
            bool previousOptimization = false;
            try { previousOptimization = Convert.ToBoolean(app.Optimization); } catch { }
            dynamic previousPage = target.ActivePage;
            int previousUnit = Convert.ToInt32(target.Unit);
            dynamic outputPage = null;
            bool commandStarted = false;
            var nested = new List<NestedPiece>();
            int complete = 0;
            try
            {
                Log.Info("Sport layout: start, garments=" + total);
                try { app.Optimization = true; } catch { }
                target.Unit = CorelConstants.CdrMillimeter;
                target.BeginCommandGroup("Vanya Tools - раскладка спортформы");
                commandStarted = true;
                outputPage = target.AddPages(1);
                outputPage.Activate();
                outputPage.SizeWidth = options.SheetWidthMm;
                outputPage.SizeHeight = 1000.0;
                dynamic outputLayer = target.ActiveLayer;

                foreach (var row in rows)
                {
                    if (row == null) continue;
                    string size = NormalizeSize(row.Size);
                    double scale;
                    dynamic sourcePage = FindMasterPage(target, size, bySize, out scale);
                    Garment source = ReadPreparedGarment(sourcePage, size);
                    Log.Info("Sport layout: source ready, size " + size);
                    if (selectedPart == SportLayoutPart.Neck && source.Neck == null)
                        throw new InvalidOperationException("Для размера " + size +
                            " в шаблоне нет детали горловины.");
                    for (int copy = 1; copy <= row.Quantity; copy++)
                    {
                        target.Activate();
                        outputPage.Activate();
                        Garment garment = CopyGarment(source, outputLayer, selectedPart);
                        Log.Info("Sport layout: garment copied, item " + (complete + 1));
                        nested.AddRange(BuildNestedPieces(app, outputLayer, garment, row, size,
                            complete + 1, options, scale, selectedPart));
                        Log.Info("Sport layout: pieces built, item " + (complete + 1));
                        complete++;
                        progress?.Invoke(complete, total, size + " · " + row.Name);
                    }
                }

                Log.Info("Sport layout: nesting " + nested.Count + " pieces");
                double sheetHeight = Nest(nested, options.SheetWidthMm, options.GapMm);
                outputPage.SizeHeight = sheetHeight;
                foreach (var item in nested)
                {
                    if (item.Rotated)
                        item.Shape.RotateEx(90.0, Convert.ToDouble(item.Shape.CenterX),
                            Convert.ToDouble(item.Shape.CenterY));
                    item.Shape.Move(item.Left - Convert.ToDouble(item.Shape.LeftX),
                        sheetHeight - item.Top - item.Height - Convert.ToDouble(item.Shape.BottomY));
                }
                try { outputPage.Name = selectedPart.HasValue
                    ? "Допечатка · " + rows[0].Name + " · " + selectedPart.Value
                    : "Спортформа · " + complete + " комплектов"; } catch { }
                Log.Info("Sport layout: complete");
                return new SportLayoutResult { GarmentCount = complete,
                    PieceCount = nested.Count, SheetHeightMm = sheetHeight };
            }
            catch (Exception error)
            {
                try
                {
                    target.Activate();
                    if (outputPage != null) target.DeletePages(Convert.ToInt32(outputPage.Index));
                    previousPage.Activate();
                }
                catch (Exception rollbackError)
                {
                    Log.Error("Could not remove failed sport layout page.", rollbackError);
                }
                throw new InvalidOperationException("Раскладка не завершена: " + error.Message, error);
            }
            finally
            {
                try { target.Activate(); } catch { }
                try { target.Unit = previousUnit; } catch { }
                try { if (commandStarted) target.EndCommandGroup(); } catch { }
                try { app.Optimization = previousOptimization; } catch { }
            }
        }

        private static TemplateSession GetTemplateSession(dynamic app,
            string path, IDictionary<string, TemplateSession> sessions)
        {
            if (sessions.TryGetValue(path, out TemplateSession cached)) return cached;
            dynamic document = null;
            bool openedHere = false;
            foreach (dynamic candidate in app.Documents)
            {
                try
                {
                    if (String.Equals(System.IO.Path.GetFullPath(
                        Convert.ToString(candidate.FullFileName)), path,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        document = candidate;
                        break;
                    }
                }
                catch { }
            }
            if (document == null)
            {
                document = app.OpenDocument(path);
                openedHere = true;
            }
            if (document == null)
                throw new InvalidOperationException("Не удалось открыть шаблон " + path);
            var session = new TemplateSession { Document = document, OpenedHere = openedHere,
                OriginalUnit = Convert.ToInt32(document.Unit) };
            document.Unit = CorelConstants.CdrMillimeter;
            sessions.Add(path, session);
            return session;
        }

        private static Garment CopyLayeredGarment(dynamic app, dynamic sourcePage,
            string sourceLayerName, dynamic target, dynamic outputPage,
            dynamic outputLayer, bool sameDocument)
        {
            dynamic sizeLayer = null, zonesLayer = null;
            foreach (dynamic layer in sourcePage.Layers)
            {
                string name = Convert.ToString(layer.Name);
                if (String.Equals(name, sourceLayerName, StringComparison.OrdinalIgnoreCase))
                    sizeLayer = layer;
                if (String.Equals(name, ZonesLayerName, StringComparison.OrdinalIgnoreCase))
                    zonesLayer = layer;
            }
            if (sizeLayer == null || zonesLayer == null)
                throw new InvalidOperationException("Нужны слой размера «" + sourceLayerName +
                    "» и общий слой VT_ZONES.");
            dynamic top = null;
            foreach (dynamic shape in sizeLayer.Shapes)
                if (String.Equals(Convert.ToString(shape.Name), "VT_TOP",
                    StringComparison.OrdinalIgnoreCase)) { top = shape; break; }
            if (top == null)
                throw new InvalidOperationException("На слое «" + sourceLayerName +
                    "» не найдена группа VT_TOP.");
            dynamic copiedTop = TransferShape(app, top, sourcePage, target,
                outputPage, outputLayer, sameDocument);
            dynamic parts = copiedTop.UngroupEx();
            if (Convert.ToInt32(parts.Count) != 5)
                throw new InvalidOperationException("Группа VT_TOP на слое «" +
                    sourceLayerName + "» должна содержать ровно пять деталей.");
            var named = new Dictionary<string, Piece>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic shape in parts)
            {
                string name = Convert.ToString(shape.Name);
                if (!String.IsNullOrWhiteSpace(name)) named[name] = Describe(shape);
            }
            foreach (dynamic shape in zonesLayer.Shapes)
            {
                string name = Convert.ToString(shape.Name);
                if (String.IsNullOrWhiteSpace(name) ||
                    !name.StartsWith("VT_SPORT_ZONE_", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (named.ContainsKey(name))
                    throw new InvalidOperationException("Бокс «" + name + "» на VT_ZONES повторяется.");
                named[name] = Describe(TransferShape(app, shape, sourcePage, target,
                    outputPage, outputLayer, sameDocument));
            }
            Piece Required(string key)
            {
                if (named.TryGetValue(key, out Piece found)) return found;
                throw new InvalidOperationException("В модели не найден объект «" + key +
                    "» для размера «" + sourceLayerName + "».");
            }
            Piece Optional(string key) => named.TryGetValue(key, out Piece found) ? found : null;
            return new Garment {
                Front = Required("VT_FRONT"), Back = Required("VT_BACK"),
                LeftSleeve = Required("VT_SLEEVE_LEFT"),
                RightSleeve = Required("VT_SLEEVE_RIGHT"), Neck = Required("VT_NECK"),
                FrontLeft = Required("VT_SPORT_ZONE_FRONT_LEFT"),
                FrontRight = Required("VT_SPORT_ZONE_FRONT_RIGHT"),
                BackText = Required("VT_SPORT_ZONE_BACK"),
                BackNumber = Required("VT_SPORT_ZONE_BACK_NUMBER"),
                LeftSleeveFrame = Required("VT_SPORT_ZONE_LEFT_SLEEVE"),
                RightSleeveFrame = Required("VT_SPORT_ZONE_RIGHT_SLEEVE"),
                NeckFrame = Optional("VT_SPORT_ZONE_NECK")
            };
        }

        private static dynamic TransferShape(dynamic app, dynamic source,
            dynamic sourcePage, dynamic target, dynamic outputPage,
            dynamic outputLayer, bool sameDocument)
        {
            if (sameDocument) return source.CopyToLayer(outputLayer);
            sourcePage.Activate();
            dynamic range = app.CreateShapeRange();
            range.Add(source);
            range.Copy();
            target.Activate();
            outputPage.Activate();
            dynamic pasted = outputLayer.PasteEx(app.CreateStructPasteOptions());
            if (Convert.ToInt32(pasted.Count) != 1)
                throw new InvalidOperationException("Не удалось перенести объект из файла модели.");
            foreach (dynamic shape in pasted) return shape;
            throw new InvalidOperationException("CorelDRAW не вставил объект из файла модели.");
        }

        private static Garment CopyGarment(Garment source, dynamic layer,
            SportLayoutPart? selectedPart = null)
        {
            bool front = !selectedPart.HasValue || selectedPart == SportLayoutPart.Front;
            bool back = !selectedPart.HasValue || selectedPart == SportLayoutPart.Back;
            bool left = !selectedPart.HasValue || selectedPart == SportLayoutPart.LeftSleeve;
            bool right = !selectedPart.HasValue || selectedPart == SportLayoutPart.RightSleeve;
            bool neck = !selectedPart.HasValue || selectedPart == SportLayoutPart.Neck;
            return new Garment {
                Front = CopyOptional(front ? source.Front : null, layer),
                Back = CopyOptional(back ? source.Back : null, layer),
                LeftSleeve = CopyOptional(left ? source.LeftSleeve : null, layer),
                RightSleeve = CopyOptional(right ? source.RightSleeve : null, layer),
                Neck = CopyOptional(neck ? source.Neck : null, layer),
                FrontLeft = CopyOptional(front ? source.FrontLeft : null, layer),
                FrontRight = CopyOptional(front ? source.FrontRight : null, layer),
                BackText = CopyOptional(back ? source.BackText : null, layer),
                BackNumber = CopyOptional(back ? source.BackNumber : null, layer),
                LeftSleeveFrame = CopyOptional(left ? source.LeftSleeveFrame : null, layer),
                RightSleeveFrame = CopyOptional(right ? source.RightSleeveFrame : null, layer),
                NeckFrame = CopyOptional(neck ? source.NeckFrame : null, layer),
                FrontArtwork = CopyOptional(front ? source.FrontArtwork : null, layer),
                BackArtwork = CopyOptional(back ? source.BackArtwork : null, layer),
                LeftSleeveArtwork = CopyOptional(left ? source.LeftSleeveArtwork : null, layer),
                RightSleeveArtwork = CopyOptional(right ? source.RightSleeveArtwork : null, layer),
                NeckArtwork = CopyOptional(neck ? source.NeckArtwork : null, layer)
            };
        }

        private static Piece CopyOptional(Piece source, dynamic layer)
        {
            return source == null ? null : Describe(source.Shape.CopyToLayer(layer));
        }

        private static void SplitBackTextZone(Garment garment)
        {
            Piece original = garment.BackText;
            // Keep the template's red rectangle as the name box. Its duplicate
            // becomes a separately editable number box on the prepared page.
            dynamic number = original.Shape.Duplicate();
            double gap = original.Height * 0.04;
            double nameHeight = original.Height * 0.40;
            double numberHeight = original.Height - nameHeight - gap;
            original.Shape.SetSize(original.Width, nameHeight);
            original.Shape.Move(original.Left - Convert.ToDouble(original.Shape.LeftX),
                original.Top - nameHeight - Convert.ToDouble(original.Shape.BottomY));
            number.SetSize(original.Width, numberHeight);
            number.Move(original.Left - Convert.ToDouble(number.LeftX),
                original.Bottom - Convert.ToDouble(number.BottomY));
            garment.BackText = Describe(original.Shape);
            garment.BackNumber = Describe(number);
        }

        private static void NameMasterGarment(Garment garment)
        {
            garment.Front.Shape.Name = "VT_SPORT_CONTOUR_FRONT";
            garment.Back.Shape.Name = "VT_SPORT_CONTOUR_BACK";
            garment.LeftSleeve.Shape.Name = "VT_SPORT_CONTOUR_LEFT_SLEEVE";
            garment.RightSleeve.Shape.Name = "VT_SPORT_CONTOUR_RIGHT_SLEEVE";
            if (garment.Neck != null) garment.Neck.Shape.Name = "VT_SPORT_CONTOUR_NECK";
            garment.FrontLeft.Shape.Name = "VT_SPORT_ZONE_FRONT_LEFT";
            garment.FrontRight.Shape.Name = "VT_SPORT_ZONE_FRONT_RIGHT";
            garment.BackText.Shape.Name = "VT_SPORT_ZONE_BACK";
            garment.BackNumber.Shape.Name = "VT_SPORT_ZONE_BACK_NUMBER";
            garment.LeftSleeveFrame.Shape.Name = "VT_SPORT_ZONE_LEFT_SLEEVE";
            garment.RightSleeveFrame.Shape.Name = "VT_SPORT_ZONE_RIGHT_SLEEVE";
            if (garment.NeckFrame != null)
                garment.NeckFrame.Shape.Name = "VT_SPORT_ZONE_NECK";
        }

        private static Piece AddMasterArtwork(dynamic app, dynamic layer,
            string path, Piece part, string key)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            dynamic image = ImportPattern(app, layer, path);
            PositionPattern(image, part);
            image.Name = "VT_SPORT_ART_" + key;
            image.OrderToBack();
            return Describe(image);
        }

        private static void StretchMaster(dynamic app, Garment garment, double scale)
        {
            dynamic range = app.CreateShapeRange();
            foreach (Piece piece in new[] { garment.Front, garment.Back,
                garment.LeftSleeve, garment.RightSleeve, garment.Neck, garment.FrontLeft,
                garment.FrontRight, garment.BackText, garment.BackNumber,
                garment.LeftSleeveFrame,
                garment.RightSleeveFrame, garment.NeckFrame, garment.FrontArtwork,
                garment.BackArtwork, garment.LeftSleeveArtwork,
                garment.RightSleeveArtwork, garment.NeckArtwork })
                if (piece != null) range.Add(piece.Shape);
            range.Stretch(scale, scale, true);
        }

        private static dynamic FindMasterPage(dynamic document, string size,
            IDictionary<string, SportLayoutTemplate> templates, out double scale)
        {
            scale = 1;
            foreach (dynamic page in document.Pages)
            {
                if (!String.Equals(Convert.ToString(page.Name), OverviewName,
                    StringComparison.OrdinalIgnoreCase)) continue;
                foreach (dynamic layer in page.Layers)
                    if (String.Equals(Convert.ToString(layer.Name), SizeLayerPrefix + size,
                        StringComparison.OrdinalIgnoreCase)) return page;
            }
            dynamic exact = FindNamedMaster(document, size);
            if (exact != null) return exact;
            if (size != "M" && templates.TryGetValue("M", out SportLayoutTemplate medium) &&
                String.Equals(System.IO.Path.GetFullPath(medium.Path),
                    System.IO.Path.GetFullPath(templates[size].Path),
                    StringComparison.OrdinalIgnoreCase))
            {
                dynamic fallback = FindNamedMaster(document, "M");
                if (fallback != null)
                {
                    double requested = templates[size].ScalePercent <= 0 ? 100 :
                        templates[size].ScalePercent;
                    double baseScale = medium.ScalePercent <= 0 ? 100 : medium.ScalePercent;
                    scale = requested / baseScale;
                    return fallback;
                }
            }
            throw new InvalidOperationException("Для размера " + size +
                " нет страницы-макета. Выберите его шаблон и нажмите «Подготовить макет». ");
        }

        private static dynamic FindNamedMaster(dynamic document, string size)
        {
            dynamic latest = null;
            foreach (dynamic page in document.Pages)
                if (String.Equals(Convert.ToString(page.Name), MasterPrefix + size,
                    StringComparison.OrdinalIgnoreCase)) latest = page;
            return latest;
        }

        private static Garment ReadPreparedGarment(dynamic page, string size)
        {
            var named = new Dictionary<string, Piece>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic layer in page.Layers)
            {
                if (String.Equals(Convert.ToString(page.Name), OverviewName,
                    StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(Convert.ToString(layer.Name), SizeLayerPrefix + size,
                        StringComparison.OrdinalIgnoreCase)) continue;
                foreach (dynamic shape in layer.Shapes)
                {
                    string name = Convert.ToString(shape.Name);
                    if (!String.IsNullOrWhiteSpace(name) && name.StartsWith("VT_SPORT_",
                        StringComparison.OrdinalIgnoreCase)) named[name] = Describe(shape);
                }
            }
            Piece Required(string name)
            {
                if (named.TryGetValue(name, out Piece found)) return found;
                throw new InvalidOperationException("На странице «" + page.Name +
                    "» не найден объект " + name + ". Верните его или подготовьте макет заново.");
            }
            Piece Optional(string name) => named.TryGetValue(name, out Piece found) ? found : null;
            return new Garment {
                Front = Required("VT_SPORT_CONTOUR_FRONT"),
                Back = Required("VT_SPORT_CONTOUR_BACK"),
                LeftSleeve = Required("VT_SPORT_CONTOUR_LEFT_SLEEVE"),
                RightSleeve = Required("VT_SPORT_CONTOUR_RIGHT_SLEEVE"),
                Neck = Optional("VT_SPORT_CONTOUR_NECK"),
                FrontLeft = Required("VT_SPORT_ZONE_FRONT_LEFT"),
                FrontRight = Required("VT_SPORT_ZONE_FRONT_RIGHT"),
                BackText = Required("VT_SPORT_ZONE_BACK"),
                BackNumber = Optional("VT_SPORT_ZONE_BACK_NUMBER"),
                LeftSleeveFrame = Required("VT_SPORT_ZONE_LEFT_SLEEVE"),
                RightSleeveFrame = Required("VT_SPORT_ZONE_RIGHT_SLEEVE"),
                NeckFrame = Optional("VT_SPORT_ZONE_NECK"),
                FrontArtwork = Optional("VT_SPORT_ART_FRONT"),
                BackArtwork = Optional("VT_SPORT_ART_BACK"),
                LeftSleeveArtwork = Optional("VT_SPORT_ART_LEFT_SLEEVE"),
                RightSleeveArtwork = Optional("VT_SPORT_ART_RIGHT_SLEEVE"),
                NeckArtwork = Optional("VT_SPORT_ART_NECK")
            };
        }

        // CopyToLayer only works inside one CDR. Use Corel's clipboard transfer
        // for a template opened as a different document, then identify only the
        // shapes returned by PasteEx (the output page may already have garments).
        private static Garment PasteGarment(dynamic app, Garment source,
            dynamic sourcePage, dynamic target, dynamic outputPage, dynamic outputLayer)
        {
            sourcePage.Activate();
            dynamic range = app.CreateShapeRange();
            range.Add(source.Front.Shape);
            range.Add(source.Back.Shape);
            range.Add(source.LeftSleeve.Shape);
            range.Add(source.RightSleeve.Shape);
            range.Add(source.FrontLeft.Shape);
            range.Add(source.FrontRight.Shape);
            range.Add(source.BackText.Shape);
            range.Add(source.LeftSleeveFrame.Shape);
            range.Add(source.RightSleeveFrame.Shape);
            range.Copy();

            target.Activate();
            outputPage.Activate();
            // The COM dynamic binder marshals the omitted optional argument as
            // 0xFFFFFFFF on some Corel versions. Pass a real options object.
            dynamic pasted = outputLayer.PasteEx(app.CreateStructPasteOptions());
            var shapes = new List<dynamic>();
            foreach (dynamic shape in pasted) shapes.Add(shape);
            if (shapes.Count != 9)
                throw new InvalidOperationException("CorelDRAW вставил " + shapes.Count +
                    " объектов лекала вместо 9.");
            return ReadGarmentShapes(shapes);
        }

        private static IList<NestedPiece> BuildNestedPieces(dynamic app, dynamic layer,
            Garment garment, SportLayoutPlayer row, string size, int index,
            SportLayoutOptions options, double scale, SportLayoutPart? selectedPart)
        {
            var pieces = new List<NestedPiece>();
            if (!selectedPart.HasValue || selectedPart == SportLayoutPart.Front)
            {
                // The garment faces the viewer: the wearer's left chest is on
                // the right side of the template page.
                Piece frontName = options.FrontNameOnRight ? garment.FrontLeft : garment.FrontRight;
                Piece extra = options.FrontNameOnRight ? garment.FrontRight : garment.FrontLeft;
                pieces.Add(BuildNestedPart(app, layer, garment.Front,
                    new[] { frontName, extra }, garment.FrontArtwork, "ПЕРЕД", row, size,
                    index, options, scale, frontName, null, null));
            }
            if (!selectedPart.HasValue || selectedPart == SportLayoutPart.Back)
                pieces.Add(BuildNestedPart(app, layer, garment.Back,
                    garment.BackNumber == null ? new[] { garment.BackText } :
                        new[] { garment.BackText, garment.BackNumber },
                    garment.BackArtwork, "СПИНА", row, size,
                    index, options, scale, null, garment.BackText, garment.BackNumber));
            if (!selectedPart.HasValue || selectedPart == SportLayoutPart.LeftSleeve)
            {
                var sleeve = BuildNestedPart(app, layer, garment.LeftSleeve,
                    new[] { garment.LeftSleeveFrame }, garment.LeftSleeveArtwork,
                    "ЛЕВЫЙ РУКАВ", row, size, index, options, scale, null, null, null);
                sleeve.CanRotate = options.RotateSleeves;
                pieces.Add(sleeve);
            }
            if (!selectedPart.HasValue || selectedPart == SportLayoutPart.RightSleeve)
            {
                var sleeve = BuildNestedPart(app, layer, garment.RightSleeve,
                    new[] { garment.RightSleeveFrame }, garment.RightSleeveArtwork,
                    "ПРАВЫЙ РУКАВ", row, size, index, options, scale, null, null, null);
                sleeve.CanRotate = options.RotateSleeves;
                pieces.Add(sleeve);
            }
            if (garment.Neck != null &&
                (!selectedPart.HasValue || selectedPart == SportLayoutPart.Neck))
            {
                var neck = BuildNestedPart(app, layer, garment.Neck,
                    garment.NeckFrame == null ? new Piece[0] : new[] { garment.NeckFrame },
                    garment.NeckArtwork, "ГОРЛОВИНА", row, size, index, options,
                    scale, null, null, null);
                neck.CanRotate = options.RotateSleeves;
                pieces.Add(neck);
            }
            return pieces;
        }

        private static NestedPiece BuildNestedPart(dynamic app, dynamic layer,
            Piece part, IEnumerable<Piece> frames, Piece artwork, string partName,
            SportLayoutPlayer row, string size, int index, SportLayoutOptions options,
            double scale, Piece frontName, Piece backText, Piece backNumber)
        {
            part.Shape.Name = "VT_" + partName.Replace(' ', '_');
            if (artwork != null) artwork.Shape.AddToPowerClip(part.Shape, 0);
            dynamic groupRange = app.CreateShapeRange();
            groupRange.Add(part.Shape);
            foreach (Piece frame in frames)
            {
                string key = partName + "_" + (frame == frontName || frame == backText
                    ? "ИМЯ" : frame == backNumber ? "НОМЕР" : "ГРАФИКА");
                FinishGuide(frame, "VT_FRAME_" + key.Replace(' ', '_'), options.KeepGuides);
                groupRange.Add(frame.Shape);
            }
            groupRange.Add(AddCaption(layer, part, partName, row, size, options.WhiteText));
            if (frontName != null)
            {
                dynamic text = AddCenteredText(layer, frontName, row.Name,
                    options.NameStyle, "VT_PLAYER_NAME_FRONT");
                if (text != null) groupRange.Add(text);
            }
            if (backText != null)
            {
                if (backNumber == null)
                    foreach (dynamic text in AddLegacyBackText(layer, backText, row.Name,
                        row.Number, options)) groupRange.Add(text);
                else
                {
                    dynamic name = AddCenteredText(layer, backText, row.Name,
                        options.NameStyle, "VT_PLAYER_NAME_BACK");
                    dynamic number = AddCenteredText(layer, backNumber, row.Number,
                        options.NumberStyle, "VT_PLAYER_NUMBER_BACK");
                    if (name != null) groupRange.Add(name);
                    if (number != null) groupRange.Add(number);
                }
            }
            dynamic group = groupRange.Group();
            group.Name = "VT_" + index.ToString("000", CultureInfo.InvariantCulture) +
                "_" + Clean(size) + "_" + Clean(row.Name) + "_" + partName.Replace(' ', '_');
            if (Math.Abs(scale - 1) > 0.0001)
            {
                dynamic scaled = app.CreateShapeRange();
                scaled.Add(group);
                scaled.Stretch(scale, scale, true);
            }
            return new NestedPiece { Shape = group,
                Width = Convert.ToDouble(group.SizeWidth),
                Height = Convert.ToDouble(group.SizeHeight) };
        }

        private static double Nest(IList<NestedPiece> pieces, double sheetWidth, double gap)
        {
            // Track occupied height across the roll width. A sleeve may turn
            // 90 degrees as a complete grouped detail; its design rotates with
            // the contour, keeping their alignment. No edge margin is added.
            var skyline = new List<SkylineSegment> {
                new SkylineSegment { Left = 0, Right = sheetWidth, Top = 0 }
            };
            double usedHeight = 0;
            foreach (NestedPiece piece in pieces.OrderByDescending(item => item.Height)
                .ThenByDescending(item => item.Width))
            {
                if (piece.Width > sheetWidth + 0.001 &&
                    (!piece.CanRotate || piece.Height > sheetWidth + 0.001))
                    throw new InvalidOperationException("Деталь шириной " +
                        piece.Width.ToString("0.#", CultureInfo.CurrentCulture) +
                        " мм не помещается на полотно " +
                        sheetWidth.ToString("0.#", CultureInfo.CurrentCulture) + " мм.");
                double originalWidth = piece.Width, originalHeight = piece.Height;
                double bestX = 0, bestTop = 0, bestBottom = Double.MaxValue,
                    bestWaste = Double.MaxValue;
                bool bestRotated = false;
                foreach (bool rotated in piece.CanRotate ? new[] { false, true } :
                    new[] { false })
                {
                    double width = rotated ? originalHeight : originalWidth;
                    double height = rotated ? originalWidth : originalHeight;
                    if (width > sheetWidth + 0.001) continue;
                    var candidates = new SortedSet<double> { 0,
                        Math.Max(0, sheetWidth - width) };
                    foreach (SkylineSegment segment in skyline)
                    {
                        if (segment.Left <= sheetWidth - width + 0.001)
                            candidates.Add(segment.Left);
                        double rightAligned = segment.Right - width;
                        if (rightAligned >= -0.001 &&
                            rightAligned <= sheetWidth - width + 0.001)
                            candidates.Add(Math.Max(0, rightAligned));
                    }
                    foreach (double x in candidates)
                    {
                        double right = Math.Min(sheetWidth, x + width + gap);
                        double top = SkylineTop(skyline, x, right, out double waste);
                        double bottom = top + height;
                        if (bottom < bestBottom - 0.001 ||
                            (Math.Abs(bottom - bestBottom) <= 0.001 &&
                            (waste < bestWaste - 0.001 ||
                            (Math.Abs(waste - bestWaste) <= 0.001 &&
                            ((!rotated && bestRotated) ||
                            (rotated == bestRotated && x < bestX))))))
                        {
                            bestX = x;
                            bestTop = top;
                            bestBottom = bottom;
                            bestWaste = waste;
                            bestRotated = rotated;
                        }
                    }
                }
                piece.Left = bestX;
                piece.Top = bestTop;
                piece.Rotated = bestRotated;
                piece.Width = bestRotated ? originalHeight : originalWidth;
                piece.Height = bestRotated ? originalWidth : originalHeight;
                usedHeight = Math.Max(usedHeight, bestTop + piece.Height);
                RaiseSkyline(skyline, bestX,
                    Math.Min(sheetWidth, bestX + piece.Width + gap),
                    bestTop + piece.Height + gap);
            }
            return pieces.Count == 0 ? 1000 : usedHeight;
        }

        private static double SkylineTop(IList<SkylineSegment> skyline,
            double left, double right, out double wastedArea)
        {
            int low = 0, high = skyline.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (skyline[middle].Right <= left + 0.000001) low = middle + 1;
                else high = middle;
            }
            double top = 0;
            for (int i = low; i < skyline.Count &&
                skyline[i].Left < right - 0.000001; i++)
                top = Math.Max(top, skyline[i].Top);
            wastedArea = 0;
            for (int i = low; i < skyline.Count &&
                skyline[i].Left < right - 0.000001; i++)
            {
                SkylineSegment segment = skyline[i];
                double overlap = Math.Min(right, segment.Right) - Math.Max(left, segment.Left);
                if (overlap > 0) wastedArea += overlap * (top - segment.Top);
            }
            return top;
        }

        private static void RaiseSkyline(List<SkylineSegment> skyline,
            double left, double right, double top)
        {
            var updated = new List<SkylineSegment>();
            foreach (SkylineSegment segment in skyline)
            {
                if (segment.Right <= left || segment.Left >= right)
                {
                    updated.Add(segment);
                    continue;
                }
                if (segment.Left < left)
                    updated.Add(new SkylineSegment { Left = segment.Left,
                        Right = left, Top = segment.Top });
                updated.Add(new SkylineSegment { Left = Math.Max(left, segment.Left),
                    Right = Math.Min(right, segment.Right), Top = Math.Max(top, segment.Top) });
                if (segment.Right > right)
                    updated.Add(new SkylineSegment { Left = right,
                        Right = segment.Right, Top = segment.Top });
            }
            skyline.Clear();
            foreach (SkylineSegment segment in updated)
            {
                if (segment.Right - segment.Left < 0.000001) continue;
                SkylineSegment previous = skyline.Count == 0 ? null : skyline[skyline.Count - 1];
                if (previous != null && Math.Abs(previous.Top - segment.Top) < 0.000001)
                    previous.Right = segment.Right;
                else skyline.Add(segment);
            }
        }

        internal static string NormalizeSize(string value)
        {
            string size = (value ?? "").Trim().ToUpperInvariant();
            // Common Cyrillic labels: М, С, ХЛ. С is the spoken Russian
            // letter for size S, not the Latin letter C.
            return size.Replace('М', 'M').Replace('С', 'S')
                .Replace('Л', 'L').Replace('Х', 'X');
        }

        private static string Clean(string value)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            string text = new string((value ?? "").Trim().Select(ch =>
                invalid.Contains(ch) || Char.IsControl(ch) ? '_' : ch).ToArray());
            return String.Join("_", text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static Garment ReadGarment(dynamic page)
        {
            var shapes = new List<dynamic>();
            foreach (dynamic layer in page.Layers)
            foreach (dynamic shape in layer.Shapes)
                shapes.Add(shape);
            return ReadGarmentShapes(shapes);
        }

        private static Garment ReadGarmentShapes(IEnumerable<dynamic> shapes)
        {
            var outlines = new List<Piece>();
            var frames = new List<Piece>();
            foreach (dynamic shape in shapes)
            {
                Piece part = Describe(shape);
                int type = Convert.ToInt32(shape.Type);
                int fill = Convert.ToInt32(shape.Fill.Type);
                if (type == CorelConstants.CdrCurveShape && fill == 0 &&
                    part.Width > 150 && part.Height > 120)
                    outlines.Add(part);
                else if (type == 1 && fill == 0 && part.Width > 25 && part.Height > 20)
                    frames.Add(part);
            }
            if (outlines.Count != 4 || frames.Count != 5)
                throw new InvalidOperationException("В шаблоне найдено " + outlines.Count +
                    " крупных контуров и " + frames.Count +
                    " зон. Ожидаются 4 контура (перед, спина, 2 рукава) и 5 красных зон.");

            var bodies = outlines.OrderByDescending(x => x.Area).Take(2)
                .OrderBy(x => x.CenterX).ToArray();
            var sleeves = outlines.Except(bodies).OrderByDescending(x => x.CenterY).ToArray();
            var garment = new Garment { Front = bodies[0], Back = bodies[1],
                LeftSleeve = sleeves[0], RightSleeve = sleeves[1] };
            var frontFrames = frames.Where(garment.Front.Contains).OrderBy(x => x.CenterX).ToArray();
            var backFrames = frames.Where(garment.Back.Contains).ToArray();
            var leftFrames = frames.Where(garment.LeftSleeve.Contains).ToArray();
            var rightFrames = frames.Where(garment.RightSleeve.Contains).ToArray();
            if (frontFrames.Length != 2 || backFrames.Length != 1 ||
                leftFrames.Length != 1 || rightFrames.Length != 1)
                throw new InvalidOperationException("Не удалось сопоставить красные зоны с деталями лекала.");
            garment.FrontLeft = frontFrames[0];
            garment.FrontRight = frontFrames[1];
            garment.BackText = backFrames[0];
            garment.LeftSleeveFrame = leftFrames[0];
            garment.RightSleeveFrame = rightFrames[0];
            return garment;
        }

        private static Piece Describe(dynamic shape)
        {
            return new Piece { Shape = shape,
                Left = Convert.ToDouble(shape.LeftX), Bottom = Convert.ToDouble(shape.BottomY),
                Width = Convert.ToDouble(shape.SizeWidth), Height = Convert.ToDouble(shape.SizeHeight),
                Name = Convert.ToString(shape.Name) };
        }

        private static dynamic ImportPattern(dynamic app, dynamic layer, string path)
        {
            object targetLayer = layer;
            object importOptions = app.CreateStructImportOptions();
            dynamic import = targetLayer.GetType().InvokeMember("ImportEx", BindingFlags.InvokeMethod,
                null, targetLayer, new object[] { new BStrWrapper(System.IO.Path.GetFullPath(path)), 0, importOptions });
            import.Finish();
            dynamic image = app.ActiveShape;
            if (image == null || Convert.ToInt32(image.Type) != CorelConstants.CdrBitmapShape)
                throw new InvalidOperationException("CorelDRAW не импортировал рисунок как растр.");
            return image;
        }

        private static void PositionPattern(dynamic image, Piece piece)
        {
            double width = Convert.ToDouble(image.SizeWidth);
            double height = Convert.ToDouble(image.SizeHeight);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("У рисунка нет размера.");
            double scale = Math.Max(piece.Width / width, piece.Height / height);
            image.SetSize(width * scale, height * scale);
            image.Move(piece.CenterX - Convert.ToDouble(image.CenterX),
                piece.CenterY - Convert.ToDouble(image.CenterY));
        }

        private static dynamic AddCaption(dynamic layer, Piece piece, string part,
            SportLayoutPlayer row, string size, bool white)
        {
            string caption = String.Join(" · ", new[] { row.Name,
                String.IsNullOrWhiteSpace(row.Number) ? "" : "№" + row.Number,
                size, part }
                .Where(value => !String.IsNullOrWhiteSpace(value)));
            dynamic text = layer.CreateArtisticTextWide(piece.Left + 8,
                piece.Bottom + 4, caption, 0, -1, "Arial", 20);
            text.Name = "VT_CAPTION_" + part.Replace(' ', '_');
            if (white) text.Fill.UniformColor.RGBAssign(255, 255, 255);
            else text.Fill.UniformColor.RGBAssign(0, 0, 0);
            text.Outline.SetNoOutline();
            double width = Convert.ToDouble(text.SizeWidth);
            double allowed = piece.Width - 16;
            if (width > allowed)
                text.SetSize(allowed, Convert.ToDouble(text.SizeHeight) * allowed / width);
            text.Move(piece.Left + 8 - Convert.ToDouble(text.LeftX),
                piece.Bottom + 4 - Convert.ToDouble(text.BottomY));
            return text;
        }

        private static dynamic AddCenteredText(dynamic layer, Piece zone, string value,
            SportTextStyle style, string shapeName)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            dynamic text = layer.CreateArtisticTextWide(zone.Left, zone.Bottom,
                value.Trim(), 0, -1, style.FontName, style.MaxPoints,
                style.Bold ? -1 : 0, style.Italic ? -1 : 0);
            text.Name = shapeName;
            ApplyHex(text.Fill.UniformColor, style.FillHex);
            if (style.OutlineWidthMm > 0)
            {
                // Corel's Outline.Width is measured in points, independently
                // from the document's current millimetre unit.
                text.Outline.Width = style.OutlineWidthMm * 72.0 / 25.4;
                ApplyHex(text.Outline.Color, style.OutlineHex);
            }
            else text.Outline.SetNoOutline();
            Fit(text, zone.Width, zone.Height);
            text.Move(zone.CenterX - Convert.ToDouble(text.CenterX),
                zone.CenterY - Convert.ToDouble(text.CenterY));
            return text;
        }

        private static IList<dynamic> AddLegacyBackText(dynamic layer, Piece zone,
            string name, string number, SportLayoutOptions options)
        {
            var created = new List<dynamic>();
            bool hasName = !String.IsNullOrWhiteSpace(name);
            bool hasNumber = !String.IsNullOrWhiteSpace(number);
            if (!hasName && !hasNumber) return created;
            if (hasName && hasNumber)
            {
                var upper = new Piece { Left = zone.Left, Bottom = zone.Bottom + zone.Height * 0.48,
                    Width = zone.Width, Height = zone.Height * 0.52 };
                var lower = new Piece { Left = zone.Left, Bottom = zone.Bottom,
                    Width = zone.Width, Height = zone.Height * 0.48 };
                created.Add(AddCenteredText(layer, upper, name,
                    options.NameStyle, "VT_PLAYER_NAME_BACK"));
                created.Add(AddCenteredText(layer, lower, number,
                    options.NumberStyle, "VT_PLAYER_NUMBER_BACK"));
            }
            else created.Add(AddCenteredText(layer, zone, hasName ? name : number,
                hasName ? options.NameStyle : options.NumberStyle,
                hasName ? "VT_PLAYER_NAME_BACK" : "VT_PLAYER_NUMBER_BACK"));
            return created;
        }

        private static void ApplyHex(dynamic color, string hex)
        {
            string value = (hex ?? "").Trim().TrimStart('#');
            if (value.Length != 6 || !Int32.TryParse(value, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out int rgb))
                throw new InvalidOperationException("Цвет надписи должен быть в формате #RRGGBB.");
            color.RGBAssign((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        private static void Fit(dynamic text, double maxWidth, double maxHeight)
        {
            double width = Convert.ToDouble(text.SizeWidth);
            double height = Convert.ToDouble(text.SizeHeight);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("CorelDRAW не измерил надпись.");
            double scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
            if (scale < 1) text.SetSize(width * scale, height * scale);
        }

        private static void FinishGuide(Piece zone, string name, bool keepGuide)
        {
            zone.Shape.Name = name;
            if (!keepGuide) zone.Shape.Outline.SetNoOutline();
        }

    }
}
