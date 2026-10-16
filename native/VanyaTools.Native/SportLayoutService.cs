using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
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
        public bool CutAllow180;
        public NamesGridLetterCase FrontNameCase;
        public bool FrontNameTwoLines;
        public double FrontLineSpacingPercent;
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

    // Named models retain their original cutting groups. Print containers are
    // separate curves built from silhouette, never from notches or other lines.
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
            public double? CaptionLeft, CaptionRight, CaptionBottom;
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
            public Piece FrontCut, BackCut, LeftSleeveCut, RightSleeveCut, NeckCut;
            public Piece FrontCaption, BackCaption, LeftSleeveCaption, RightSleeveCaption, NeckCaption;
            public bool CaptionReferences;
        }

        private const string MasterPrefix = "Спорт · макет · ";
        private const string OverviewName = "Спорт · макеты · все размеры";
        private const string SizeLayerPrefix = "VT_SPORT_SIZE_";
        private const string ZonesLayerName = "VT_ZONES";
        private const double PrintBleedMm = 6;
        private const string CuttingPagePrefix = "Резка спортформы · ";
        private const string CaptionReferenceMarker = "VT_SPORT_META_CAPTION_REFERENCES";

        public int ClearPreparedArtwork(SportLayoutPart part)
        {
            dynamic app = CorelApp.Get();
            dynamic document = app.ActiveDocument;
            if (document == null) return 0;
            dynamic overview = null;
            foreach (dynamic page in document.Pages)
                if (String.Equals(Convert.ToString(page.Name), OverviewName,
                    StringComparison.OrdinalIgnoreCase)) overview = page;
            if (overview == null) return 0;
            string key = part == SportLayoutPart.Front ? "FRONT" :
                part == SportLayoutPart.Back ? "BACK" :
                part == SportLayoutPart.LeftSleeve ? "LEFT_SLEEVE" :
                part == SportLayoutPart.RightSleeve ? "RIGHT_SLEEVE" : "NECK";
            dynamic previousPage = document.ActivePage;
            int previousUnit = Convert.ToInt32(document.Unit);
            int changed = 0;
            bool commandStarted = false;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                document.BeginCommandGroup("Vanya Tools - убрать рисунок " + key);
                commandStarted = true;
                overview.Activate();
                foreach (dynamic layer in overview.Layers)
                {
                    if (!Convert.ToString(layer.Name).StartsWith(SizeLayerPrefix,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    dynamic contour = null, artwork = null;
                    foreach (dynamic shape in layer.Shapes)
                    {
                        string name = Convert.ToString(shape.Name);
                        if (String.Equals(name, "VT_SPORT_CONTOUR_" + key,
                            StringComparison.OrdinalIgnoreCase)) contour = shape;
                        else if (String.Equals(name, "VT_SPORT_ART_" + key,
                            StringComparison.OrdinalIgnoreCase)) artwork = shape;
                    }
                    if (contour == null) continue;
                    if (artwork != null) artwork.Delete();
                    AddMasterArtwork(app, layer, null, Describe(contour), key);
                    changed++;
                }
            }
            finally
            {
                try { if (commandStarted) document.EndCommandGroup(); } catch { }
                try { document.Unit = previousUnit; } catch { }
                try { previousPage.Activate(); } catch { }
            }
            return changed;
        }

        private sealed class TemplateSession
        {
            public dynamic Document;
            public bool OpenedHere;
            public int OriginalUnit;
            public bool OriginalDirty;
        }

        private sealed class NestedPiece
        {
            public dynamic Shape;
            public double Width, Height, Left, Top;
            public bool CanRotate { get; set; }
            public bool Rotated { get; set; }
            public double RotationDegrees { get; set; }
            public SportContourNesting.Item CuttingLayout;
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
                throw new InvalidOperationException("Выделите один текстовый образец или прямоугольник.");
            dynamic chosen = null;
            foreach (dynamic shape in selection.Shapes) { chosen = shape; break; }
            bool textSample = Convert.ToInt32(chosen.Type) == CorelConstants.CdrTextShape;
            if (!textSample && Convert.ToInt32(chosen.Type) != 1)
                throw new InvalidOperationException("Создайте фигурный текст с образцом имени или номера.");
            if (textSample && (Convert.ToInt32(chosen.Text.Type) != CorelConstants.CdrArtisticText ||
                String.IsNullOrWhiteSpace(Convert.ToString(chosen.Text.Story.WideText))))
                throw new InvalidOperationException("Нужен непустой фигурный текст, не переведённый в кривые.");
            string previousName = Convert.ToString(chosen.Name) ?? "";
            if (previousName.StartsWith("VT_SPORT_ZONE_", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(previousName, zoneName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Этот объект уже назначен другой надписи.");
            dynamic currentLayer = chosen.Layer;
            if (String.Equals(pageName, OverviewName, StringComparison.OrdinalIgnoreCase) &&
                !Convert.ToString(currentLayer.Name).StartsWith(SizeLayerPrefix,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Разместите образец на слое нужного размера.");
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
            document.BeginCommandGroup("Vanya Tools - образец надписи спортформы");
            try
            {
                if (previous != null && !String.Equals(previousName, zoneName,
                    StringComparison.OrdinalIgnoreCase)) previous.Delete();
                if (!textSample)
                {
                    chosen.Fill.ApplyNoFill();
                    chosen.Outline.Width = 0.5;
                    chosen.Outline.Color.RGBAssign(255, 0, 0);
                }
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
                if (Math.Abs(scale - 1) > 0.0001)
                    StretchMaster(app, master, scale);
                CreatePrintContainers(app, layer, master);
                NameMasterGarment(master);
                master.FrontArtwork = AddMasterArtwork(app, layer, options.FrontArtworkPath,
                    master.Front, "FRONT");
                master.BackArtwork = AddMasterArtwork(app, layer, options.BackArtworkPath,
                    master.Back, "BACK");
                master.LeftSleeveArtwork = AddMasterArtwork(app, layer,
                    options.LeftSleeveArtworkPath, master.LeftSleeve, "LEFT_SLEEVE");
                master.RightSleeveArtwork = AddMasterArtwork(app, layer,
                    options.RightSleeveArtworkPath, master.RightSleeve, "RIGHT_SLEEVE");
                AddMasterCaptions(app, layer, master, size, options.WhiteText);
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
                RestoreTemplateSessions(sessions);
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
                RestoreTemplateSessions(sessions);
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
            dynamic previousPage = target.ActivePage;
            int previousUnit = Convert.ToInt32(target.Unit);
            string targetPath = null;
            try { targetPath = System.IO.Path.GetFullPath(Convert.ToString(target.FullFileName)); }
            catch { }
            var sessions = new Dictionary<string, TemplateSession>(StringComparer.OrdinalIgnoreCase);
            var artworkCache = new Dictionary<string, Piece>(StringComparer.OrdinalIgnoreCase);
            dynamic overview = null;
            bool clipboardUsed = false;
            try
            {
                Log.Info("Sport overview: start, sizes=" + templates.Count);
                target.Unit = CorelConstants.CdrMillimeter;
                // Keep source CDR switches outside a target undo group.
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
                    clipboardUsed |= !same;
                    double scale = template.ScalePercent <= 0 ? 1 : template.ScalePercent / 100.0;
                    if (Math.Abs(scale - 1) > 0.0001) StretchMaster(app, garment, scale);
                    CreatePrintContainers(app, layer, garment);
                    NameMasterGarment(garment);
                    Log.Info("Sport overview: contours and boxes copied, size " + size);
                    garment.FrontArtwork = AddOrCopyMasterArtwork(app, layer,
                        options.FrontArtworkPath, garment.Front, "FRONT", artworkCache);
                    garment.BackArtwork = AddOrCopyMasterArtwork(app, layer,
                        options.BackArtworkPath, garment.Back, "BACK", artworkCache);
                    garment.LeftSleeveArtwork = AddOrCopyMasterArtwork(app, layer,
                        options.LeftSleeveArtworkPath, garment.LeftSleeve, "LEFT_SLEEVE", artworkCache);
                    garment.RightSleeveArtwork = AddOrCopyMasterArtwork(app, layer,
                        options.RightSleeveArtworkPath, garment.RightSleeve, "RIGHT_SLEEVE", artworkCache);
                    if (garment.Neck != null)
                        garment.NeckArtwork = AddOrCopyMasterArtwork(app, layer,
                            options.NeckArtworkPath, garment.Neck, "NECK", artworkCache);
                    AddMasterCaptions(app, layer, garment, size, options.WhiteText);
                    Log.Info("Sport overview: artwork imported, size " + size);
                    LogOverviewMemory(size);
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
                Log.Info("Sport overview: cleanup start");
                if (clipboardUsed)
                {
                    try
                    {
                        // Release copied model data while its source CDR is
                        // still alive. The operation replaced the clipboard.
                        System.Windows.Clipboard.Clear();
                        Log.Info("Sport overview: model clipboard cleared");
                    }
                    catch (Exception error) { Log.Error("Could not clear sport model clipboard.", error); }
                }
                try { target.Activate(); Log.Info("Sport overview: target active"); }
                catch (Exception error) { Log.Error("Sport overview: target activation failed.", error); }
                try { target.Unit = previousUnit; Log.Info("Sport overview: units restored"); }
                catch (Exception error) { Log.Error("Sport overview: unit cleanup failed.", error); }
                Log.Info("Sport overview: template cleanup start");
                RestoreTemplateSessions(sessions);
                Log.Info("Sport overview: cleanup complete");
            }
        }

        private static dynamic OnlyTemplatePage(dynamic document)
        {
            if (Convert.ToInt32(document.Pages.Count) != 1)
                throw new InvalidOperationException("CDR модели должен содержать одну страницу.");
            return document.ActivePage;
        }

        private static void RestoreTemplateSessions(IDictionary<string, TemplateSession> sessions)
        {
            foreach (TemplateSession session in sessions.Values)
                try
                {
                    // Managed wrappers may outlive this method. Keep the source
                    // CDR alive until Corel releases its native object references.
                    session.Document.Unit = session.OriginalUnit;
                    if (session.OpenedHere) session.Document.Dirty = session.OriginalDirty;
                    Log.Info("Sport template: settings restored, document retained open");
                }
                catch (Exception error) { Log.Error("Could not restore sport template settings.", error); }
        }

        public SportLayoutResult GenerateInCurrentDocument(IList<SportLayoutPlayer> rows,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            Action<int, int, string> progress)
        {
            return Generate(rows, templates, options, progress, null, false);
        }

        public SportLayoutResult GenerateCuttingInCurrentDocument(IList<SportLayoutPlayer> rows,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            Action<int, int, string> progress)
        {
            return Generate(rows, templates, options, progress, null, true);
        }

        public void SaveCurrentCuttingPage(string path)
        {
            dynamic document = CorelApp.Get().ActiveDocument;
            if (document == null || !Convert.ToString(document.ActivePage.Name)
                .StartsWith(CuttingPagePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Откройте страницу раскладки резки.");
            int count = 0;
            foreach (dynamic layer in document.ActivePage.Layers)
                foreach (dynamic shape in layer.Shapes)
                {
                    // Also reject accidental bitmaps/text from master layers.
                    var curves = new List<dynamic>();
                    CollectCutCurves(shape, curves);
                    count += curves.Count;
                }
            if (count == 0) throw new InvalidOperationException("Страница резки пуста.");
            CorelDocumentSaver.SaveCurrentPageCopy(document, path);
        }

        public SportLayoutResult GenerateReplacementPiece(SportLayoutPlayer row,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            SportLayoutPart part)
        {
            if (row == null) throw new InvalidOperationException("Выберите игрока.");
            return Generate(new[] { new SportLayoutPlayer { Size = row.Size,
                Name = row.Name, Number = row.Number, Quantity = 1 } },
                templates, options, null, part, false);
        }

        private SportLayoutResult Generate(IList<SportLayoutPlayer> rows,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            Action<int, int, string> progress, SportLayoutPart? selectedPart, bool cutting)
        {
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("Добавьте игроков в таблицу.");
            if (templates == null || templates.Count == 0)
                throw new InvalidOperationException("Добавьте CDR-шаблоны для размеров.");
            if (options == null || options.SheetWidthMm < 300 || options.SheetWidthMm > 2000)
                throw new InvalidOperationException("Ширина полотна: от 300 до 2000 мм.");
            if (options.GapMm < 0 || options.GapMm > 100)
                throw new InvalidOperationException("Расстояние между деталями: от 0 до 100 мм.");
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
                if (size.Length == 0)
                    throw new InvalidOperationException("Назначьте размер для игрока «" +
                        (row.Name ?? "") + "» в таблице перед генерацией.");
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
            var cutGeometry = new Dictionary<string, IList<SportContourNesting.Band>>();
            int complete = 0;
            try
            {
                Log.Info("Sport layout: start, cutting=" + cutting + ", garments=" + total);
                try { app.Optimization = true; } catch { }
                target.Unit = CorelConstants.CdrMillimeter;
                target.BeginCommandGroup("Vanya Tools - " +
                    (cutting ? "раскладка резки" : "раскладка спортформы"));
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
                    Garment source = ReadPreparedGarment(sourcePage, size, cutting);
                    if (!cutting && source.FrontCut != null && Math.Abs(scale - 1) > 0.0001)
                        throw new InvalidOperationException("Подготовьте макет размера " + size +
                            " на этапе 1, чтобы припуск печати оставался ровно 6 мм.");
                    Log.Info("Sport layout: source ready, size " + size);
                    if (selectedPart == SportLayoutPart.Neck && source.Neck == null)
                        throw new InvalidOperationException("Для размера " + size +
                            " в шаблоне нет детали горловины.");
                    for (int copy = 1; copy <= row.Quantity; copy++)
                    {
                        target.Activate();
                        outputPage.Activate();
                        if (cutting)
                            nested.AddRange(BuildCuttingPieces(app, outputLayer, source, row, size,
                                complete + 1, options, scale, cutGeometry));
                        else
                        {
                            Garment garment = CopyGarment(source, outputLayer, selectedPart);
                            Log.Info("Sport layout: garment copied, item " + (complete + 1));
                            nested.AddRange(BuildNestedPieces(app, outputLayer, garment, row, size,
                                complete + 1, options, scale, selectedPart));
                        }
                        Log.Info("Sport layout: pieces built, item " + (complete + 1));
                        complete++;
                        progress?.Invoke(complete, total, size + " · " + row.Name);
                    }
                }

                Log.Info("Sport layout: nesting " + nested.Count + " pieces");
                double sheetHeight = cutting ? NestCutting(nested, options.SheetWidthMm, options.GapMm) :
                    Nest(nested, options.SheetWidthMm, options.GapMm);
                outputPage.SizeHeight = sheetHeight;
                foreach (var item in nested)
                {
                    double rotation = item.RotationDegrees + (item.Rotated ? 90 : 0);
                    if (rotation != 0)
                        item.Shape.RotateEx(rotation, Convert.ToDouble(item.Shape.CenterX),
                            Convert.ToDouble(item.Shape.CenterY));
                    item.Shape.Move(item.Left - Convert.ToDouble(item.Shape.LeftX),
                        sheetHeight - item.Top - item.Height - Convert.ToDouble(item.Shape.BottomY));
                }
                try { outputPage.Name = cutting ? CuttingPagePrefix + complete + " комплектов" : selectedPart.HasValue
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
                OriginalUnit = Convert.ToInt32(document.Unit),
                OriginalDirty = Convert.ToBoolean(document.Dirty) };
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
            var sources = new List<dynamic> { top };
            var zoneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic shape in zonesLayer.Shapes)
            {
                string name = Convert.ToString(shape.Name);
                if (String.IsNullOrWhiteSpace(name) ||
                    !name.StartsWith("VT_SPORT_ZONE_", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!zoneNames.Add(name))
                    throw new InvalidOperationException("Бокс «" + name + "» на VT_ZONES повторяется.");
                sources.Add(shape);
            }
            // One clipboard round trip per size is much less fragile than
            // copying the top group and each shared zone independently.
            var copied = new List<dynamic>();
            if (sameDocument)
            {
                foreach (dynamic shape in sources)
                    copied.Add(shape.CopyToLayer(outputLayer));
            }
            else
            {
                Log.Info("Sport model transfer: copying " + sources.Count +
                    " shapes from layer " + sourceLayerName);
                sourcePage.Activate();
                dynamic range = app.CreateShapeRange();
                foreach (dynamic shape in sources) range.Add(shape);
                Log.Info("Sport model transfer: clipboard copy start, layer " + sourceLayerName);
                range.Copy();
                Log.Info("Sport model transfer: clipboard copy complete, layer " + sourceLayerName);
                target.Activate();
                outputPage.Activate();
                Log.Info("Sport model transfer: paste start, layer " + sourceLayerName);
                dynamic pasted = outputLayer.PasteEx(app.CreateStructPasteOptions());
                foreach (dynamic shape in pasted)
                {
                    copied.Add(shape);
                    Log.Info("Sport model transfer: pasted root type=" +
                        Convert.ToInt32(shape.Type) + ", name=" + Convert.ToString(shape.Name));
                }
                Log.Info("Sport model transfer: pasted " + copied.Count + " shapes");
            }
            var transferred = new List<dynamic>();
            foreach (dynamic shape in copied)
                UnwrapTransferredShape(shape, transferred, 0);
            Log.Info("Sport model transfer: unpacked " + transferred.Count +
                " shapes from " + copied.Count + " pasted roots, layer " + sourceLayerName);
            if (transferred.Count != sources.Count)
                throw new InvalidOperationException("CorelDRAW перенёс " + transferred.Count +
                    " из " + sources.Count + " объектов модели «" + sourceLayerName +
                    "» после раскрытия групп.");
            dynamic copiedTop = null;
            var named = new Dictionary<string, Piece>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic shape in transferred)
            {
                string name = Convert.ToString(shape.Name);
                if (String.Equals(name, "VT_TOP", StringComparison.OrdinalIgnoreCase))
                    copiedTop = shape;
                else if (name != null && name.StartsWith("VT_SPORT_ZONE_",
                    StringComparison.OrdinalIgnoreCase)) named[name] = Describe(shape);
            }
            if (copiedTop == null)
                throw new InvalidOperationException("CorelDRAW не перенёс группу VT_TOP из слоя «" +
                    sourceLayerName + "».");
            dynamic parts = copiedTop.UngroupEx();
            if (Convert.ToInt32(parts.Count) != 5)
                throw new InvalidOperationException("Группа VT_TOP на слое «" +
                    sourceLayerName + "» должна содержать ровно пять деталей.");
            foreach (dynamic shape in parts)
            {
                string name = Convert.ToString(shape.Name);
                if (!String.IsNullOrWhiteSpace(name)) named[name] = Describe(shape);
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
                FrontLeft = Optional("VT_SPORT_ZONE_FRONT_LEFT"),
                FrontRight = Optional("VT_SPORT_ZONE_FRONT_RIGHT"),
                BackText = Optional("VT_SPORT_ZONE_BACK"),
                BackNumber = Optional("VT_SPORT_ZONE_BACK_NUMBER"),
                LeftSleeveFrame = Optional("VT_SPORT_ZONE_LEFT_SLEEVE"),
                RightSleeveFrame = Optional("VT_SPORT_ZONE_RIGHT_SLEEVE"),
                NeckFrame = Optional("VT_SPORT_ZONE_NECK")
            };
        }

        private static void UnwrapTransferredShape(dynamic shape, List<dynamic> result,
            int depth)
        {
            if (depth > 8)
                throw new InvalidOperationException("Слишком глубокая вложенность объектов модели.");
            string name = Convert.ToString(shape.Name);
            int type = Convert.ToInt32(shape.Type);
            if (String.Equals(name, "VT_TOP", StringComparison.OrdinalIgnoreCase) &&
                type == CorelConstants.CdrGroupShape &&
                Convert.ToInt32(shape.Shapes.Count) == 5)
            {
                result.Add(shape);
                return;
            }
            if (type != CorelConstants.CdrGroupShape && name != null &&
                name.StartsWith("VT_SPORT_ZONE_", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(shape);
                return;
            }
            if (type == CorelConstants.CdrGroupShape)
            {
                dynamic children = shape.UngroupEx();
                foreach (dynamic child in children)
                    UnwrapTransferredShape(child, result, depth + 1);
                return;
            }
            result.Add(shape);
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
                NeckArtwork = CopyOptional(neck ? source.NeckArtwork : null, layer),
                FrontCaption = CopyOptional(front ? source.FrontCaption : null, layer),
                BackCaption = CopyOptional(back ? source.BackCaption : null, layer),
                LeftSleeveCaption = CopyOptional(left ? source.LeftSleeveCaption : null, layer),
                RightSleeveCaption = CopyOptional(right ? source.RightSleeveCaption : null, layer),
                NeckCaption = CopyOptional(neck ? source.NeckCaption : null, layer),
                CaptionReferences = source.CaptionReferences
            };
        }

        private static Piece CopyOptional(Piece source, dynamic layer)
        {
            if (source == null) return null;
            Piece copy = Describe(source.Shape.CopyToLayer(layer));
            double dx = copy.Left - source.Left, dy = copy.Bottom - source.Bottom;
            copy.CaptionLeft = source.CaptionLeft + dx;
            copy.CaptionRight = source.CaptionRight + dx;
            copy.CaptionBottom = source.CaptionBottom + dy;
            return copy;
        }

        private static void CollectCutCurves(dynamic shape, ICollection<object> curves, int depth = 0)
        {
            if (depth > 12)
                throw new InvalidOperationException("Слишком глубокая вложенность группы детали.");
            int type = Convert.ToInt32(shape.Type);
            if (type == CorelConstants.CdrGroupShape)
            {
                foreach (dynamic child in shape.Shapes) CollectCutCurves((object)child, curves, depth + 1);
            }
            // Force the managed collection call to bind statically. A dynamic
            // COM argument makes the binder search IList<object> for Add,
            // which is inherited from ICollection<object> and can be missed.
            else if (IsCutGeometry(type)) curves.Add((object)shape);
            else throw new InvalidOperationException("В группе детали «" +
                Convert.ToString(shape.Name) + "» найден объект типа " + type +
                ". Для резки нужны кривые или векторные фигуры лекала и надсечек.");
        }

        private static bool IsCutGeometry(int type)
        {
            // Corel primitives retain exact cutting geometry without conversion.
            // The supplied VT_NECK/silhouette is a rectangle in all five sizes.
            return type == CorelConstants.CdrCurveShape || type == 1 || type == 2 ||
                type == 4 || type == 26; // rectangle, ellipse, polygon, Perfect Shape
        }

        private static void CollectNamedSilhouettes(dynamic shape, ICollection<object> matches,
            int depth = 0)
        {
            if (depth > 12)
                throw new InvalidOperationException("Слишком глубокая вложенность группы детали.");
            if (String.Equals(Convert.ToString(shape.Name), "silhouette",
                StringComparison.OrdinalIgnoreCase)) matches.Add((object)shape);
            if (Convert.ToInt32(shape.Type) == CorelConstants.CdrGroupShape)
                foreach (dynamic child in shape.Shapes)
                    CollectNamedSilhouettes((object)child, matches, depth + 1);
        }

        private static dynamic FindSilhouette(Piece original)
        {
            // Printing only needs the named silhouette. Other members of the
            // original detail group are collected separately when making cuts.
            var silhouettes = new List<object>();
            CollectNamedSilhouettes((object)original.Shape, silhouettes);
            if (silhouettes.Count == 0 && IsCutGeometry(Convert.ToInt32(original.Shape.Type)))
                silhouettes.Add((object)original.Shape); // legacy single-shape template
            if (silhouettes.Count != 1)
                throw new InvalidOperationException("В детали «" + Convert.ToString(original.Shape.Name) +
                    "» нужен ровно один контур silhouette.");
            dynamic silhouette = silhouettes[0];
            int type = Convert.ToInt32(silhouette.Type);
            if (!IsCutGeometry(type))
                throw new InvalidOperationException("Объект silhouette в детали «" +
                    Convert.ToString(original.Shape.Name) + "» должен быть векторным контуром.");
            if (type == CorelConstants.CdrCurveShape && !Convert.ToBoolean(silhouette.Curve.Closed))
                throw new InvalidOperationException("Кривая silhouette в детали «" +
                    Convert.ToString(original.Shape.Name) + "» должна быть замкнута.");
            return silhouette;
        }

        private static void CreatePrintContainers(dynamic app, dynamic layer, Garment garment)
        {
            garment.FrontCut = garment.Front;
            garment.BackCut = garment.Back;
            garment.LeftSleeveCut = garment.LeftSleeve;
            garment.RightSleeveCut = garment.RightSleeve;
            garment.NeckCut = garment.Neck;
            // Validate the whole model before applying any contour effects.
            foreach (Piece part in new[] { garment.FrontCut, garment.BackCut,
                garment.LeftSleeveCut, garment.RightSleeveCut, garment.NeckCut })
                if (part != null) FindSilhouette(part);
            garment.Front = CreateBleedContainer(app, layer, garment.FrontCut);
            garment.Back = CreateBleedContainer(app, layer, garment.BackCut);
            garment.LeftSleeve = CreateBleedContainer(app, layer, garment.LeftSleeveCut);
            garment.RightSleeve = CreateBleedContainer(app, layer, garment.RightSleeveCut);
            garment.Neck = garment.NeckCut == null ? null :
                CreateBleedContainer(app, layer, garment.NeckCut);
        }

        private static Piece CreateBleedContainer(dynamic app, dynamic layer, Piece original)
        {
            dynamic silhouette = FindSilhouette(original);
            dynamic control = silhouette.CopyToLayer(layer);
            // Convert only the disposable print copy. The original rectangle,
            // silhouette curve and all notches remain intact for cutting.
            if (Convert.ToInt32(control.Type) != CorelConstants.CdrCurveShape)
                control.ConvertToCurves();
            if (Convert.ToInt32(control.Type) != CorelConstants.CdrCurveShape ||
                !Convert.ToBoolean(control.Curve.Closed))
                throw new InvalidOperationException("Для печати silhouette в детали «" +
                    Convert.ToString(original.Shape.Name) + "» должна задавать замкнутый контур.");
            long controlId = Convert.ToInt64(control.StaticID);
            dynamic orange = app.CreateRGBColor(255, 128, 0);
            dynamic white = app.CreateRGBColor(255, 255, 255);
            // Pass every optional COM argument explicitly (the dynamic binder
            // can otherwise marshal an omitted argument as 0xFFFFFFFF).
            dynamic effect = control.CreateContour(CorelConstants.CdrContourOutside,
                PrintBleedMm, 1, 0, orange, white, white, 0, 0,
                CorelConstants.CdrContourRoundCap, CorelConstants.CdrContourCornerRound, 15.0);
            dynamic separated = effect.Separate();
            var curves = new List<dynamic>();
            foreach (dynamic shape in separated)
                UnpackContour((object)shape, controlId, curves);
            if (curves.Count == 0)
                throw new InvalidOperationException("CorelDRAW не создал контур +6 мм для «" +
                    Convert.ToString(original.Shape.Name) + "».");
            dynamic container;
            if (curves.Count == 1) container = curves[0];
            else
            {
                dynamic range = app.CreateShapeRange();
                foreach (dynamic curve in curves) range.Add(curve);
                container = range.Combine();
            }
            control.Delete();
            if (Convert.ToInt32(container.Type) != CorelConstants.CdrCurveShape ||
                !Convert.ToBoolean(container.Curve.Closed))
                throw new InvalidOperationException("Контур +6 мм получился незамкнутым.");
            container.Fill.ApplyNoFill();
            container.Outline.Width = 0.15;
            container.Outline.Color.RGBAssign(255, 128, 0);
            container.OrderToBack();
            Piece result = Describe(container);
            SetCaptionBoundary(result, Describe(silhouette));
            Log.Info("Sport print container: +6 mm, source=" + Convert.ToString(original.Shape.Name));
            return result;
        }

        private static void UnpackContour(dynamic shape, long controlId, ICollection<object> curves)
        {
            if (Convert.ToInt64(shape.StaticID) == controlId) return;
            if (Convert.ToInt32(shape.Type) == CorelConstants.CdrGroupShape)
            {
                dynamic children = shape.UngroupEx();
                foreach (dynamic child in children) UnpackContour((object)child, controlId, curves);
            }
            else if (Convert.ToInt32(shape.Type) == CorelConstants.CdrCurveShape) curves.Add((object)shape);
            else throw new InvalidOperationException("CorelDRAW вернул неподходящий объект контура.");
        }

        private static void SetCaptionBoundary(Piece printed, Piece original)
        {
            GetBottomSpan(original, out double left, out double right);
            printed.CaptionLeft = left;
            printed.CaptionRight = right;
            printed.CaptionBottom = original.Bottom;
        }

        private static void SplitBackTextZone(Garment garment)
        {
            Piece original = garment.BackText;
            if (original == null) return;
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
            if (garment.FrontCut != null) garment.FrontCut.Shape.Name = "VT_SPORT_CUT_FRONT";
            if (garment.BackCut != null) garment.BackCut.Shape.Name = "VT_SPORT_CUT_BACK";
            if (garment.LeftSleeveCut != null) garment.LeftSleeveCut.Shape.Name = "VT_SPORT_CUT_LEFT_SLEEVE";
            if (garment.RightSleeveCut != null) garment.RightSleeveCut.Shape.Name = "VT_SPORT_CUT_RIGHT_SLEEVE";
            if (garment.NeckCut != null) garment.NeckCut.Shape.Name = "VT_SPORT_CUT_NECK";
            if (garment.FrontLeft != null) garment.FrontLeft.Shape.Name = "VT_SPORT_ZONE_FRONT_LEFT";
            if (garment.FrontRight != null) garment.FrontRight.Shape.Name = "VT_SPORT_ZONE_FRONT_RIGHT";
            if (garment.BackText != null) garment.BackText.Shape.Name = "VT_SPORT_ZONE_BACK";
            if (garment.BackNumber != null) garment.BackNumber.Shape.Name = "VT_SPORT_ZONE_BACK_NUMBER";
            if (garment.LeftSleeveFrame != null) garment.LeftSleeveFrame.Shape.Name = "VT_SPORT_ZONE_LEFT_SLEEVE";
            if (garment.RightSleeveFrame != null) garment.RightSleeveFrame.Shape.Name = "VT_SPORT_ZONE_RIGHT_SLEEVE";
            if (garment.NeckFrame != null)
                garment.NeckFrame.Shape.Name = "VT_SPORT_ZONE_NECK";
        }

        private static void AddMasterCaptions(dynamic app, dynamic layer, Garment garment,
            string size, bool white)
        {
            var parts = new[] { garment.Front, garment.Back, garment.LeftSleeve,
                garment.RightSleeve, garment.Neck };
            var keys = new[] { "FRONT", "BACK", "LEFT_SLEEVE", "RIGHT_SLEEVE", "NECK" };
            var names = new[] { "ПЕРЕД", "СПИНА", "ЛЕВЫЙ РУКАВ", "ПРАВЫЙ РУКАВ", "ГОРЛОВИНА" };
            var captions = new Piece[5];
            var preview = new SportLayoutPlayer { Name = "XXXXXXXXX", Number = "XX", Quantity = 1 };
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null) continue;
                dynamic text = AddCaption(layer, parts[i], names[i], preview, size, white);
                text.Name = "VT_SPORT_CAPTION_" + keys[i];
                captions[i] = Describe(text);
            }
            garment.FrontCaption = captions[0]; garment.BackCaption = captions[1];
            garment.LeftSleeveCaption = captions[2]; garment.RightSleeveCaption = captions[3];
            garment.NeckCaption = captions[4]; garment.CaptionReferences = true;
            // Distinguish a deliberately deleted caption from a legacy master
            // that predates editable captions. The unpainted marker is inside
            // the garment bounds and is never copied into print/cut output.
            dynamic marker = layer.CreateRectangle2(garment.Front.CenterX, garment.Front.CenterY, 1, 1);
            marker.Fill.ApplyNoFill(); marker.Outline.SetNoOutline();
            marker.Name = CaptionReferenceMarker;
        }

        private static Piece AddMasterArtwork(dynamic app, dynamic layer,
            string path, Piece part, string key)
        {
            dynamic image = null;
            if (!String.IsNullOrWhiteSpace(path))
            {
                image = ImportPattern(app, layer, path);
                PositionPattern(image, part);
            }
            // Corel needs at least two shapes for a group. Unpainted shapes
            // keep the group editable even when this part has no source art.
            dynamic frame = layer.CreateRectangle2(image == null ? part.Left : Convert.ToDouble(image.LeftX),
                image == null ? part.Bottom : Convert.ToDouble(image.BottomY),
                image == null ? part.Width : Convert.ToDouble(image.SizeWidth),
                image == null ? part.Height : Convert.ToDouble(image.SizeHeight));
            frame.Fill.ApplyNoFill();
            frame.Outline.SetNoOutline();
            dynamic range = app.CreateShapeRange();
            if (image != null) range.Add(image);
            range.Add(frame);
            if (image == null)
            {
                dynamic anchor = layer.CreateRectangle2(part.CenterX, part.CenterY, 1, 1);
                anchor.Fill.ApplyNoFill();
                anchor.Outline.SetNoOutline();
                range.Add(anchor);
            }
            dynamic group = range.Group();
            group.Name = "VT_SPORT_ART_" + key;
            group.OrderToBack();
            return Describe(group);
        }

        private static Piece AddOrCopyMasterArtwork(dynamic app, dynamic layer,
            string path, Piece part, string key, IDictionary<string, Piece> cache)
        {
            // Import each large bitmap once per part. Subsequent sizes copy the
            // prepared group within the same CDR instead of importing the PNG.
            if (cache.TryGetValue(key, out Piece source))
            {
                dynamic copy = source.Shape.CopyToLayer(layer);
                PositionPattern(copy, part);
                copy.Name = "VT_SPORT_ART_" + key;
                copy.OrderToBack();
                Log.Info("Sport overview: copied existing artwork group " + key);
                return Describe(copy);
            }
            Piece created = AddMasterArtwork(app, layer, path, part, key);
            cache.Add(key, created);
            return created;
        }

        private static void LogOverviewMemory(string size)
        {
            using (Process process = Process.GetCurrentProcess())
            {
                process.Refresh();
                Log.Info("Sport overview: process memory after " + size + ": " +
                    (process.PrivateMemorySize64 / (1024 * 1024)) + " MiB private");
            }
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

        private static Garment ReadPreparedGarment(dynamic page, string size, bool cutting = false)
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
            if (cutting)
            {
                if (!named.ContainsKey("VT_SPORT_CUT_FRONT"))
                    throw new InvalidOperationException("Подготовьте макеты заново на этапе 1: " +
                        "на прежней странице ещё нет исходных групп резки.");
                return new Garment {
                    FrontCut = Required("VT_SPORT_CUT_FRONT"),
                    BackCut = Required("VT_SPORT_CUT_BACK"),
                    LeftSleeveCut = Required("VT_SPORT_CUT_LEFT_SLEEVE"),
                    RightSleeveCut = Required("VT_SPORT_CUT_RIGHT_SLEEVE"),
                    NeckCut = Optional("VT_SPORT_CUT_NECK")
                };
            }
            var garment = new Garment {
                Front = Required("VT_SPORT_CONTOUR_FRONT"),
                Back = Required("VT_SPORT_CONTOUR_BACK"),
                LeftSleeve = Required("VT_SPORT_CONTOUR_LEFT_SLEEVE"),
                RightSleeve = Required("VT_SPORT_CONTOUR_RIGHT_SLEEVE"),
                Neck = Optional("VT_SPORT_CONTOUR_NECK"),
                FrontLeft = Optional("VT_SPORT_ZONE_FRONT_LEFT"),
                FrontRight = Optional("VT_SPORT_ZONE_FRONT_RIGHT"),
                BackText = Optional("VT_SPORT_ZONE_BACK"),
                BackNumber = Optional("VT_SPORT_ZONE_BACK_NUMBER"),
                LeftSleeveFrame = Optional("VT_SPORT_ZONE_LEFT_SLEEVE"),
                RightSleeveFrame = Optional("VT_SPORT_ZONE_RIGHT_SLEEVE"),
                NeckFrame = Optional("VT_SPORT_ZONE_NECK"),
                FrontArtwork = Optional("VT_SPORT_ART_FRONT"),
                BackArtwork = Optional("VT_SPORT_ART_BACK"),
                LeftSleeveArtwork = Optional("VT_SPORT_ART_LEFT_SLEEVE"),
                RightSleeveArtwork = Optional("VT_SPORT_ART_RIGHT_SLEEVE"),
                NeckArtwork = Optional("VT_SPORT_ART_NECK"),
                FrontCut = Optional("VT_SPORT_CUT_FRONT"),
                BackCut = Optional("VT_SPORT_CUT_BACK"),
                LeftSleeveCut = Optional("VT_SPORT_CUT_LEFT_SLEEVE"),
                RightSleeveCut = Optional("VT_SPORT_CUT_RIGHT_SLEEVE"),
                NeckCut = Optional("VT_SPORT_CUT_NECK"),
                FrontCaption = Optional("VT_SPORT_CAPTION_FRONT"),
                BackCaption = Optional("VT_SPORT_CAPTION_BACK"),
                LeftSleeveCaption = Optional("VT_SPORT_CAPTION_LEFT_SLEEVE"),
                RightSleeveCaption = Optional("VT_SPORT_CAPTION_RIGHT_SLEEVE"),
                NeckCaption = Optional("VT_SPORT_CAPTION_NECK"),
                CaptionReferences = Optional(CaptionReferenceMarker) != null
            };
            var printed = new[] { garment.Front, garment.Back, garment.LeftSleeve,
                garment.RightSleeve, garment.Neck };
            var originals = new[] { garment.FrontCut, garment.BackCut, garment.LeftSleeveCut,
                garment.RightSleeveCut, garment.NeckCut };
            for (int i = 0; i < printed.Length; i++)
                if (printed[i] != null && originals[i] != null)
                    SetCaptionBoundary(printed[i], Describe(FindSilhouette(originals[i])));
            return garment;
        }

        private static IList<NestedPiece> BuildCuttingPieces(dynamic app, dynamic layer,
            Garment source, SportLayoutPlayer row, string size, int index,
            SportLayoutOptions options, double scale,
            IDictionary<string, IList<SportContourNesting.Band>> geometryCache)
        {
            var result = new List<NestedPiece>();
            var originals = new[] { source.FrontCut, source.BackCut, source.LeftSleeveCut,
                source.RightSleeveCut, source.NeckCut };
            var keys = new[] { "FRONT", "BACK", "LEFT_SLEEVE", "RIGHT_SLEEVE", "NECK" };
            for (int i = 0; i < originals.Length; i++)
            {
                Piece original = originals[i];
                if (original == null) continue;
                var curves = new List<dynamic>();
                CollectCutCurves(original.Shape, curves);
                if (curves.Count == 0) throw new InvalidOperationException("Пустая группа резки.");
                // Copy the intact group: silhouette and every notch retain their
                // geometry and relative positions. No text/print/container added.
                dynamic copy = original.Shape.CopyToLayer(layer);
                if (Math.Abs(scale - 1) > 0.0001)
                {
                    dynamic range = app.CreateShapeRange();
                    range.Add(copy);
                    range.Stretch(scale, scale, true);
                }
                copy.Name = "VT_CUT_" + index.ToString("000", CultureInfo.InvariantCulture) +
                    "_" + Clean(size) + "_" + Clean(row.Name) + "_" + keys[i];
                double width = Convert.ToDouble(copy.SizeWidth), height = Convert.ToDouble(copy.SizeHeight);
                string key = size + "|" + keys[i] + "|" + scale.ToString("R", CultureInfo.InvariantCulture);
                if (!geometryCache.TryGetValue(key, out IList<SportContourNesting.Band> bands))
                {
                    bands = ReadCutGeometry(original, curves, scale);
                    geometryCache.Add(key, bands);
                }
                result.Add(new NestedPiece { Shape = copy, Width = width, Height = height,
                    CanRotate = false, CuttingLayout = new SportContourNesting.Item {
                        Width = width, Height = height, Bands = bands, Allow180 = options.CutAllow180 } });
            }
            return result;
        }

        private static IList<SportContourNesting.Band> ReadCutGeometry(Piece original,
            IList<dynamic> curves, double scale)
        {
            var bands = new List<SportContourNesting.Band>();
            SportContourNesting.Point Point(double x, double y) =>
                new SportContourNesting.Point((x - original.Left) * scale,
                    (original.Top - y) * scale);
            try
            {
                foreach (dynamic shape in curves)
                {
                    dynamic curve = shape.DisplayCurve;
                    foreach (dynamic segment in curve.Segments)
                    {
                        var a = Point((double)Convert.ToDouble(segment.StartNode.PositionX),
                            (double)Convert.ToDouble(segment.StartNode.PositionY));
                        var d = Point((double)Convert.ToDouble(segment.EndNode.PositionX),
                            (double)Convert.ToDouble(segment.EndNode.PositionY));
                        if (!WithinCutBounds(a, original.Width * scale, original.Height * scale) ||
                            !WithinCutBounds(d, original.Width * scale, original.Height * scale))
                            throw new InvalidOperationException("CorelDRAW вернул координаты сегмента " +
                                "за пределами габаритов детали.");
                        if (Convert.ToInt32(segment.Type) == 0)
                            SportContourNesting.AddLine(bands, a, d);
                        else
                        {
                            var b = Point((double)Convert.ToDouble(segment.StartingControlPointX),
                                (double)Convert.ToDouble(segment.StartingControlPointY));
                            var c = Point((double)Convert.ToDouble(segment.EndingControlPointX),
                                (double)Convert.ToDouble(segment.EndingControlPointY));
                            if (Double.IsNaN(b.X) || Double.IsInfinity(b.X) ||
                                Double.IsNaN(b.Y) || Double.IsInfinity(b.Y) ||
                                Double.IsNaN(c.X) || Double.IsInfinity(c.X) ||
                                Double.IsNaN(c.Y) || Double.IsInfinity(c.Y))
                                throw new InvalidOperationException("Некорректные координаты управляющих точек.");
                            SportContourNesting.AddCubic(bands, a, b, c, d);
                        }
                    }
                }
                if (bands.Count == 0) throw new InvalidOperationException("Пустой профиль кривых.");
                Log.Info("Sport cutting: contour profile read, " + original.Name + ", bands=" + bands.Count);
                return bands;
            }
            catch (Exception error)
            {
                // Native geometry access must not make a valid cutting group
                // unusable. A full bounding box is a conservative fallback.
                Log.Error("Sport cutting: using bounds for " + original.Name, error);
                return new[] { new SportContourNesting.Band { Left = 0, Top = 0,
                    Right = original.Width * scale, Bottom = original.Height * scale } };
            }
        }

        private static bool WithinCutBounds(SportContourNesting.Point point, double width, double height)
        {
            return !Double.IsNaN(point.X) && !Double.IsNaN(point.Y) &&
                point.X >= -0.001 && point.X <= width + 0.001 &&
                point.Y >= -0.001 && point.Y <= height + 0.001;
        }

        private static double NestCutting(IList<NestedPiece> pieces, double sheetWidth, double gap)
        {
            // Keep the existing valid placement as a baseline. The contour
            // search can improve it, but never returns a longer sheet.
            double baseline = Nest(pieces, sheetWidth, gap);
            double dense = SportContourNesting.Place(pieces.Select(p => p.CuttingLayout).ToList(),
                sheetWidth, gap);
            if (dense >= baseline - 0.000001)
            {
                Log.Info("Sport cutting: kept bounds placement, height=" + baseline);
                return baseline;
            }
            foreach (NestedPiece piece in pieces)
            {
                piece.Left = piece.CuttingLayout.Left;
                piece.Top = piece.CuttingLayout.Top;
                piece.RotationDegrees = piece.CuttingLayout.Rotation;
            }
            Log.Info("Sport cutting: contour nesting, height=" + dense + ", previous=" + baseline);
            return dense;
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
                Piece frontName = options.FrontNameOnRight ? garment.FrontRight : garment.FrontLeft;
                Piece extra = options.FrontNameOnRight ? garment.FrontLeft : garment.FrontRight;
                pieces.Add(BuildNestedPart(app, layer, garment.Front,
                    new[] { frontName, extra }, garment.FrontArtwork, "ПЕРЕД", row, size,
                    index, options, scale, frontName, null, null,
                    garment.FrontCaption, garment.CaptionReferences));
            }
            if (!selectedPart.HasValue || selectedPart == SportLayoutPart.Back)
                pieces.Add(BuildNestedPart(app, layer, garment.Back,
                    garment.BackNumber == null ? new[] { garment.BackText } :
                        new[] { garment.BackText, garment.BackNumber },
                    garment.BackArtwork, "СПИНА", row, size,
                    index, options, scale, null, garment.BackText, garment.BackNumber,
                    garment.BackCaption, garment.CaptionReferences));
            if (!selectedPart.HasValue || selectedPart == SportLayoutPart.LeftSleeve)
            {
                var sleeve = BuildNestedPart(app, layer, garment.LeftSleeve,
                    new[] { garment.LeftSleeveFrame }, garment.LeftSleeveArtwork,
                    "ЛЕВЫЙ РУКАВ", row, size, index, options, scale, null, null,
                    IsTextSample(garment.LeftSleeveFrame) ? garment.LeftSleeveFrame : null,
                    garment.LeftSleeveCaption, garment.CaptionReferences);
                sleeve.CanRotate = options.RotateSleeves;
                pieces.Add(sleeve);
            }
            if (!selectedPart.HasValue || selectedPart == SportLayoutPart.RightSleeve)
            {
                var sleeve = BuildNestedPart(app, layer, garment.RightSleeve,
                    new[] { garment.RightSleeveFrame }, garment.RightSleeveArtwork,
                    "ПРАВЫЙ РУКАВ", row, size, index, options, scale, null, null,
                    IsTextSample(garment.RightSleeveFrame) ? garment.RightSleeveFrame : null,
                    garment.RightSleeveCaption, garment.CaptionReferences);
                sleeve.CanRotate = options.RotateSleeves;
                pieces.Add(sleeve);
            }
            if (garment.Neck != null &&
                (!selectedPart.HasValue || selectedPart == SportLayoutPart.Neck))
            {
                var neck = BuildNestedPart(app, layer, garment.Neck,
                    garment.NeckFrame == null ? new Piece[0] : new[] { garment.NeckFrame },
                    garment.NeckArtwork, "ГОРЛОВИНА", row, size, index, options,
                    scale, null, null, IsTextSample(garment.NeckFrame) ? garment.NeckFrame : null,
                    garment.NeckCaption, garment.CaptionReferences);
                neck.CanRotate = options.RotateSleeves;
                pieces.Add(neck);
            }
            return pieces;
        }

        private static NestedPiece BuildNestedPart(dynamic app, dynamic layer,
            Piece part, IEnumerable<Piece> frames, Piece artwork, string partName,
            SportLayoutPlayer row, string size, int index, SportLayoutOptions options,
            double scale, Piece frontName, Piece backText, Piece backNumber,
            Piece captionSample, bool captionReferences)
        {
            part.Shape.Name = "VT_" + partName.Replace(' ', '_');
            if (artwork != null) artwork.Shape.AddToPowerClip(part.Shape, 0);
            dynamic groupRange = app.CreateShapeRange();
            groupRange.Add(part.Shape);
            foreach (Piece frame in frames.Where(frame => frame != null))
            {
                if (IsTextSample(frame))
                {
                    // Named samples are replaced below. A sample on the unused
                    // chest side must not appear as Xs in the printed garment.
                    if (frame != frontName && frame != backText && frame != backNumber)
                        frame.Shape.Delete();
                    continue;
                }
                string key = partName + "_" + (frame == frontName || frame == backText
                    ? "ИМЯ" : frame == backNumber ? "НОМЕР" : "ГРАФИКА");
                FinishGuide(frame, "VT_FRAME_" + key.Replace(' ', '_'), options.KeepGuides);
                groupRange.Add(frame.Shape);
            }
            if (captionSample != null)
            {
                if (!IsTextSample(captionSample))
                    throw new InvalidOperationException("Подпись детали должна оставаться фигурным текстом.");
                dynamic caption = ReplaceTextSample(layer, captionSample,
                    FormatCaption(partName, row, size), "VT_CAPTION_" + partName.Replace(' ', '_'), true);
                if (caption != null) groupRange.Add(caption);
            }
            else if (!captionReferences)
                groupRange.Add(AddCaption(layer, part, partName, row, size, options.WhiteText));
            if (frontName != null)
            {
                string chestName = FormatFrontName(row.Name, options,
                    IsTextSample(frontName) ? SampleHasTwoLines(frontName) : options.FrontNameTwoLines);
                dynamic text = AddCenteredText(layer, frontName, chestName,
                    options.NameStyle, "VT_PLAYER_NAME_FRONT",
                    options.FrontNameTwoLines ? options.FrontLineSpacingPercent : 0);
                if (text != null) groupRange.Add(text);
            }
            if (backText != null && backNumber == null && !IsTextSample(backText) && !captionReferences)
            {
                foreach (dynamic text in AddLegacyBackText(layer, backText,
                    (row.Name ?? "").Replace('|', ' '), row.Number, options)) groupRange.Add(text);
            }
            else
            {
                if (backText != null)
                {
                    dynamic name = AddCenteredText(layer, backText,
                        FormatNameLines(row.Name, IsTextSample(backText) && SampleHasTwoLines(backText)),
                        options.NameStyle, "VT_PLAYER_NAME_BACK");
                    if (name != null) groupRange.Add(name);
                }
                if (backNumber != null)
                {
                    string key = partName == "СПИНА" ? "BACK" :
                        partName == "ЛЕВЫЙ РУКАВ" ? "LEFT_SLEEVE" :
                        partName == "ПРАВЫЙ РУКАВ" ? "RIGHT_SLEEVE" : "NECK";
                    dynamic number = AddCenteredText(layer, backNumber, row.Number,
                        options.NumberStyle, "VT_PLAYER_NUMBER_" + key);
                    if (number != null) groupRange.Add(number);
                }
            }
            dynamic group = Convert.ToInt32(groupRange.Count) == 1 ? part.Shape : groupRange.Group();
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
            string caption = FormatCaption(part, row, size);
            GetBottomSpan(piece, out double bottomLeft, out double bottomRight);
            bottomLeft = piece.CaptionLeft ?? bottomLeft;
            bottomRight = piece.CaptionRight ?? bottomRight;
            double hemBottom = piece.CaptionBottom ?? piece.Bottom;
            double left = bottomLeft + 10;
            double allowed = Math.Max(1, bottomRight - bottomLeft - 20);
            dynamic text = layer.CreateArtisticTextWide(left,
                hemBottom + 4, caption, 0, -1, "Arial", 20);
            text.Name = "VT_CAPTION_" + part.Replace(' ', '_');
            if (white) text.Fill.UniformColor.RGBAssign(255, 255, 255);
            else text.Fill.UniformColor.RGBAssign(0, 0, 0);
            text.Outline.SetNoOutline();
            double width = Convert.ToDouble(text.SizeWidth);
            if (width > allowed)
                text.SetSize(allowed, Convert.ToDouble(text.SizeHeight) * allowed / width);
            text.Move(left - Convert.ToDouble(text.LeftX),
                hemBottom + 4 - Convert.ToDouble(text.BottomY));
            return text;
        }

        private static string FormatCaption(string part, SportLayoutPlayer row, string size)
        {
            return String.Join(" · ", new[] { (row.Name ?? "").Replace('|', ' '),
                String.IsNullOrWhiteSpace(row.Number) ? "" : "№" + row.Number,
                size, ShortPartName(part) }
                .Where(value => !String.IsNullOrWhiteSpace(value)));
        }

        private static string ShortPartName(string part)
        {
            switch (part)
            {
                case "ПЕРЕД": return "п.";
                case "СПИНА": return "с.";
                case "ЛЕВЫЙ РУКАВ": return "л.рук.";
                case "ПРАВЫЙ РУКАВ": return "п.рук.";
                case "ГОРЛОВИНА": return "горл.";
                default: return part;
            }
        }

        private static void GetBottomSpan(Piece piece, out double left, out double right)
        {
            left = piece.Left;
            right = piece.Right;
            try
            {
                var xs = new List<double>();
                double band = Math.Min(10, piece.Height * 0.08);
                foreach (dynamic node in piece.Shape.Curve.Nodes)
                    if (Convert.ToDouble(node.PositionY) <= piece.Bottom + band)
                        xs.Add(Convert.ToDouble(node.PositionX));
                if (xs.Count >= 2 && xs.Max() - xs.Min() > 20)
                {
                    left = xs.Min();
                    right = xs.Max();
                }
            }
            catch { /* Non-curve parts retain their bounding box span. */ }
        }

        private static string FormatFrontName(string value, SportLayoutOptions options, bool twoLines)
        {
            string text = (value ?? "").Trim();
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            switch (options.FrontNameCase)
            {
                case NamesGridLetterCase.Lower: text = text.ToLower(culture); break;
                case NamesGridLetterCase.Title:
                    text = culture.TextInfo.ToTitleCase(text.ToLower(culture)); break;
                case NamesGridLetterCase.Upper: text = text.ToUpper(culture); break;
            }
            return FormatNameLines(text, twoLines);
        }

        private static string FormatNameLines(string value, bool twoLines)
        {
            string text = (value ?? "").Trim().Replace("\r\n", "|")
                .Replace('\r', '|').Replace('\n', '|').Replace('\v', '|');
            if (!twoLines) return text.Replace('|', ' ');
            int manual = text.IndexOf('|');
            if (manual >= 0)
                return text.Substring(0, manual).Trim() + "\r" +
                    text.Substring(manual + 1).Replace('|', ' ').Trim();
            string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length < 2 ? text : words[0] + "\r" +
                String.Join(" ", words.Skip(1));
        }

        private static dynamic AddCenteredText(dynamic layer, Piece zone, string value,
            SportTextStyle style, string shapeName, double lineSpacingPercent = 0)
        {
            if (IsTextSample(zone)) return ReplaceTextSample(layer, zone, value, shapeName);
            if (String.IsNullOrWhiteSpace(value)) return null;
            if (style == null || String.IsNullOrWhiteSpace(style.FontName))
                throw new InvalidOperationException("Замените прямоугольник области надписи " +
                    "фигурным текстом и назначьте его образцом имени или номера.");
            dynamic text = layer.CreateArtisticTextWide(zone.Left, zone.Bottom,
                value.Trim(), 0, -1, style.FontName, style.MaxPoints,
                style.Bold ? -1 : 0, style.Italic ? -1 : 0);
            text.Name = shapeName;
            if (value.Contains("\r") || value.Contains("\n"))
            {
                text.Text.Story.Alignment = 3;
                if (lineSpacingPercent > 0)
                    text.Text.Story.SetLineSpacing(2, (float)lineSpacingPercent);
            }
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

        private static bool IsTextSample(Piece zone)
        {
            return zone != null && zone.Shape != null &&
                Convert.ToInt32(zone.Shape.Type) == CorelConstants.CdrTextShape;
        }

        private static bool SampleHasTwoLines(Piece zone)
        {
            return TextSampleHasTwoLines(Convert.ToString(zone.Shape.Text.Story.WideText));
        }

        private static bool TextSampleHasTwoLines(string value)
        {
            return (value ?? "").TrimEnd('\r', '\n').IndexOfAny(new[] { '\r', '\n', '\v' }) >= 0;
        }

        private static dynamic ReplaceTextSample(dynamic layer, Piece zone, string value, string shapeName,
            bool captionAnchor = false)
        {
            if (Convert.ToInt32(zone.Shape.Text.Type) != CorelConstants.CdrArtisticText)
                throw new InvalidOperationException("Для образца имени или номера используйте фигурный текст.");
            if (String.IsNullOrWhiteSpace(value))
            {
                zone.Shape.Delete();
                return null;
            }
            // Copy the Corel shape, including its fill and outline, and keep the
            // source alive until each paragraph's text attributes are restored.
            dynamic text = zone.Shape.CopyToLayer(layer);
            text.Text.Story.WideText = value.Trim();
            var sourceParagraphs = new List<dynamic>();
            foreach (dynamic paragraph in zone.Shape.Text.Story.Paragraphs)
                sourceParagraphs.Add(paragraph);
            if (sourceParagraphs.Count == 0)
                throw new InvalidOperationException("Текстовый образец имени или номера пуст.");
            int index = 0;
            foreach (dynamic paragraph in text.Text.Story.Paragraphs)
            {
                paragraph.CopyAttributes(sourceParagraphs[Math.Min(index, sourceParagraphs.Count - 1)]);
                index++;
            }
            double width = Convert.ToDouble(text.SizeWidth);
            double height = Convert.ToDouble(text.SizeHeight);
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("CorelDRAW не измерил надпись из образца.");
            // The sample's point size is authoritative. Short names keep that
            // size; long names shrink uniformly to the sample's maximum width.
            if (width > zone.Width)
                text.SetSize(zone.Width, height * zone.Width / width);
            if (captionAnchor)
                text.Move(zone.Left - Convert.ToDouble(text.LeftX), zone.Bottom - Convert.ToDouble(text.BottomY));
            else
                text.Move(zone.CenterX - Convert.ToDouble(text.CenterX),
                    zone.CenterY - Convert.ToDouble(text.CenterY));
            text.Name = shapeName;
            zone.Shape.Delete();
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
            // Fill the box height first; reduce only when that would exceed its width.
            double scale = Math.Min(maxHeight / height, maxWidth / width);
            if (Math.Abs(scale - 1) > 0.0001)
                text.SetSize(width * scale, height * scale);
        }

        private static void FinishGuide(Piece zone, string name, bool keepGuide)
        {
            zone.Shape.Name = name;
            if (!keepGuide) zone.Shape.Outline.SetNoOutline();
        }

    }
}
