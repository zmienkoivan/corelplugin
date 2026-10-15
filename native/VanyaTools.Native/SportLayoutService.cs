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
        public double ScalePercent { get; set; }
    }

    internal sealed class SportLayoutOptions
    {
        public string FrontArtworkPath;
        public string BackArtworkPath;
        public string LeftSleeveArtworkPath;
        public string RightSleeveArtworkPath;
        public SportTextStyle NameStyle;
        public SportTextStyle NumberStyle;
        public bool FrontNameOnRight;
        public bool WhiteText;
        public bool KeepGuides;
        public double SheetWidthMm;
        public double GapMm;
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

    internal enum SportLayoutPart { Front, Back, LeftSleeve, RightSleeve }

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
            public Piece Front, Back, LeftSleeve, RightSleeve;
            public Piece FrontLeft, FrontRight, BackText, BackNumber,
                LeftSleeveFrame, RightSleeveFrame;
            public Piece FrontArtwork, BackArtwork, LeftSleeveArtwork, RightSleeveArtwork;
        }

        private const string MasterPrefix = "Спорт · макет · ";

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
        }

        private sealed class Shelf
        {
            public double Top, Height, Right;
            public bool Empty = true;
        }

        public string AssignSelectedTextBox(string zoneName)
        {
            dynamic app = CorelApp.Get();
            dynamic document = app.ActiveDocument;
            if (document == null || !Convert.ToString(document.ActivePage.Name)
                .StartsWith(MasterPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Откройте страницу-макет нужного размера.");
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
            dynamic previous = null;
            foreach (dynamic layer in document.ActivePage.Layers)
            foreach (dynamic shape in layer.Shapes)
                if (String.Equals(Convert.ToString(shape.Name), zoneName,
                    StringComparison.OrdinalIgnoreCase)) previous = shape;
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
            }
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
            dynamic previousPage = target.ActivePage;
            int previousUnit = Convert.ToInt32(target.Unit);
            dynamic outputPage = null;
            bool commandStarted = false;
            var nested = new List<NestedPiece>();
            int complete = 0;
            try
            {
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
                    Garment source = ReadPreparedGarment(sourcePage);
                    for (int copy = 1; copy <= row.Quantity; copy++)
                    {
                        target.Activate();
                        outputPage.Activate();
                        Garment garment = CopyGarment(source, outputLayer, selectedPart);
                        nested.AddRange(BuildNestedPieces(app, outputLayer, garment, row, size,
                            complete + 1, options, scale, selectedPart));
                        complete++;
                        progress?.Invoke(complete, total, size + " · " + row.Name);
                    }
                }

                double sheetHeight = Nest(nested, options.SheetWidthMm, options.GapMm);
                outputPage.SizeHeight = sheetHeight;
                foreach (var item in nested)
                    item.Shape.Move(item.Left - Convert.ToDouble(item.Shape.LeftX),
                        sheetHeight - item.Top - item.Height - Convert.ToDouble(item.Shape.BottomY));
                try { outputPage.Name = selectedPart.HasValue
                    ? "Допечатка · " + rows[0].Name + " · " + selectedPart.Value
                    : "Спортформа · " + complete + " комплектов"; } catch { }
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

        private static Garment CopyGarment(Garment source, dynamic layer,
            SportLayoutPart? selectedPart = null)
        {
            bool front = !selectedPart.HasValue || selectedPart == SportLayoutPart.Front;
            bool back = !selectedPart.HasValue || selectedPart == SportLayoutPart.Back;
            bool left = !selectedPart.HasValue || selectedPart == SportLayoutPart.LeftSleeve;
            bool right = !selectedPart.HasValue || selectedPart == SportLayoutPart.RightSleeve;
            return new Garment {
                Front = CopyOptional(front ? source.Front : null, layer),
                Back = CopyOptional(back ? source.Back : null, layer),
                LeftSleeve = CopyOptional(left ? source.LeftSleeve : null, layer),
                RightSleeve = CopyOptional(right ? source.RightSleeve : null, layer),
                FrontLeft = CopyOptional(front ? source.FrontLeft : null, layer),
                FrontRight = CopyOptional(front ? source.FrontRight : null, layer),
                BackText = CopyOptional(back ? source.BackText : null, layer),
                BackNumber = CopyOptional(back ? source.BackNumber : null, layer),
                LeftSleeveFrame = CopyOptional(left ? source.LeftSleeveFrame : null, layer),
                RightSleeveFrame = CopyOptional(right ? source.RightSleeveFrame : null, layer),
                FrontArtwork = CopyOptional(front ? source.FrontArtwork : null, layer),
                BackArtwork = CopyOptional(back ? source.BackArtwork : null, layer),
                LeftSleeveArtwork = CopyOptional(left ? source.LeftSleeveArtwork : null, layer),
                RightSleeveArtwork = CopyOptional(right ? source.RightSleeveArtwork : null, layer)
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
            garment.FrontLeft.Shape.Name = "VT_SPORT_ZONE_FRONT_LEFT";
            garment.FrontRight.Shape.Name = "VT_SPORT_ZONE_FRONT_RIGHT";
            garment.BackText.Shape.Name = "VT_SPORT_ZONE_BACK";
            garment.BackNumber.Shape.Name = "VT_SPORT_ZONE_BACK_NUMBER";
            garment.LeftSleeveFrame.Shape.Name = "VT_SPORT_ZONE_LEFT_SLEEVE";
            garment.RightSleeveFrame.Shape.Name = "VT_SPORT_ZONE_RIGHT_SLEEVE";
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
                garment.LeftSleeve, garment.RightSleeve, garment.FrontLeft,
                garment.FrontRight, garment.BackText, garment.BackNumber,
                garment.LeftSleeveFrame,
                garment.RightSleeveFrame, garment.FrontArtwork,
                garment.BackArtwork, garment.LeftSleeveArtwork,
                garment.RightSleeveArtwork })
                if (piece != null) range.Add(piece.Shape);
            range.Stretch(scale, scale, true);
        }

        private static dynamic FindMasterPage(dynamic document, string size,
            IDictionary<string, SportLayoutTemplate> templates, out double scale)
        {
            scale = 1;
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

        private static Garment ReadPreparedGarment(dynamic page)
        {
            var named = new Dictionary<string, Piece>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic layer in page.Layers)
            foreach (dynamic shape in layer.Shapes)
            {
                string name = Convert.ToString(shape.Name);
                if (!String.IsNullOrWhiteSpace(name) && name.StartsWith("VT_SPORT_",
                    StringComparison.OrdinalIgnoreCase)) named[name] = Describe(shape);
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
                FrontLeft = Required("VT_SPORT_ZONE_FRONT_LEFT"),
                FrontRight = Required("VT_SPORT_ZONE_FRONT_RIGHT"),
                BackText = Required("VT_SPORT_ZONE_BACK"),
                BackNumber = Optional("VT_SPORT_ZONE_BACK_NUMBER"),
                LeftSleeveFrame = Required("VT_SPORT_ZONE_LEFT_SLEEVE"),
                RightSleeveFrame = Required("VT_SPORT_ZONE_RIGHT_SLEEVE"),
                FrontArtwork = Optional("VT_SPORT_ART_FRONT"),
                BackArtwork = Optional("VT_SPORT_ART_BACK"),
                LeftSleeveArtwork = Optional("VT_SPORT_ART_LEFT_SLEEVE"),
                RightSleeveArtwork = Optional("VT_SPORT_ART_RIGHT_SLEEVE")
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
                pieces.Add(BuildNestedPart(app, layer, garment.LeftSleeve,
                    new[] { garment.LeftSleeveFrame }, garment.LeftSleeveArtwork,
                    "ЛЕВЫЙ РУКАВ", row, size, index, options, scale, null, null, null));
            if (!selectedPart.HasValue || selectedPart == SportLayoutPart.RightSleeve)
                pieces.Add(BuildNestedPart(app, layer, garment.RightSleeve,
                    new[] { garment.RightSleeveFrame }, garment.RightSleeveArtwork,
                    "ПРАВЫЙ РУКАВ", row, size, index, options, scale, null, null, null));
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
            double margin = Math.Max(5, gap);
            var shelves = new List<Shelf>();
            foreach (NestedPiece piece in pieces.OrderByDescending(item => item.Height)
                .ThenByDescending(item => item.Width))
            {
                if (piece.Width > sheetWidth - margin * 2 + 0.001)
                    throw new InvalidOperationException("Деталь шириной " +
                        piece.Width.ToString("0.#", CultureInfo.CurrentCulture) +
                        " мм не помещается на полотно " +
                        sheetWidth.ToString("0.#", CultureInfo.CurrentCulture) + " мм.");
                Shelf chosen = null;
                double chosenX = 0, bestWaste = Double.MaxValue;
                foreach (Shelf shelf in shelves)
                {
                    if (piece.Height > shelf.Height + 0.001) continue;
                    double x = shelf.Empty ? margin : shelf.Right + gap;
                    double waste = sheetWidth - margin - (x + piece.Width);
                    if (waste >= -0.001 && waste < bestWaste)
                    {
                        chosen = shelf;
                        chosenX = x;
                        bestWaste = waste;
                    }
                }
                if (chosen == null)
                {
                    double top = shelves.Count == 0 ? margin :
                        shelves[shelves.Count - 1].Top + shelves[shelves.Count - 1].Height + gap;
                    chosen = new Shelf { Top = top, Height = piece.Height, Right = margin };
                    shelves.Add(chosen);
                    chosenX = margin;
                }
                piece.Left = chosenX;
                piece.Top = chosen.Top;
                chosen.Right = chosenX + piece.Width;
                chosen.Empty = false;
            }
            return shelves.Count == 0 ? 1000 :
                shelves[shelves.Count - 1].Top + shelves[shelves.Count - 1].Height + margin;
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
