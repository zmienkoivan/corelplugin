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
        public string FontName;
        public bool FrontNameOnRight;
        public bool WhiteText;
        public bool KeepGuides;
        public double SheetWidthMm;
        public double GapMm;
    }

    internal sealed class SportLayoutResult
    {
        public int GarmentCount;
        public int PieceCount;
        public double SheetHeightMm;
    }

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
            public Piece FrontLeft, FrontRight, BackText, LeftSleeveFrame, RightSleeveFrame;
        }

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

        // The source art stays on the existing page. A new roll-width page in
        // the active document receives all generated, separately grouped parts.
        public SportLayoutResult GenerateInCurrentDocument(IList<SportLayoutPlayer> rows,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            Action<int, int, string> progress)
        {
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("Добавьте игроков в таблицу.");
            if (templates == null || templates.Count == 0)
                throw new InvalidOperationException("Добавьте CDR-шаблоны для размеров.");
            if (options == null || options.SheetWidthMm < 300 || options.SheetWidthMm > 2000)
                throw new InvalidOperationException("Ширина полотна: от 300 до 2000 мм.");
            if (options.GapMm < 0 || options.GapMm > 100)
                throw new InvalidOperationException("Расстояние между деталями: от 0 до 100 мм.");
            if (String.IsNullOrWhiteSpace(options.FontName))
                throw new InvalidOperationException("Выберите шрифт для имён и номеров.");
            foreach (string path in new[] { options.FrontArtworkPath, options.BackArtworkPath,
                options.LeftSleeveArtworkPath, options.RightSleeveArtworkPath })
                if (!String.IsNullOrWhiteSpace(path) && !File.Exists(path))
                    throw new FileNotFoundException("Рисунок для детали не найден.", path);

            var bySize = new Dictionary<string, SportLayoutTemplate>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var template in templates)
            {
                if (template == null) continue;
                string size = NormalizeSize(template.Size);
                if (size.Length == 0 || String.IsNullOrWhiteSpace(template.Path)) continue;
                if (!File.Exists(template.Path))
                    throw new FileNotFoundException("Шаблон размера " + size + " не найден.", template.Path);
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
            string targetPath = null;
            try { targetPath = System.IO.Path.GetFullPath(Convert.ToString(target.FullFileName)); }
            catch { }
            var sessions = new Dictionary<string, TemplateSession>(StringComparer.OrdinalIgnoreCase);
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
                    var template = bySize[size];
                    string templatePath = System.IO.Path.GetFullPath(template.Path);
                    TemplateSession session = GetTemplateSession(app, templatePath, sessions);
                    bool templateIsTarget = String.Equals(templatePath, targetPath,
                        StringComparison.OrdinalIgnoreCase);
                    if (Convert.ToInt32(session.Document.Pages.Count) !=
                        (templateIsTarget ? 2 : 1))
                        throw new InvalidOperationException("Шаблон " + size + " должен содержать одну страницу.");
                    dynamic sourcePage = templateIsTarget ? previousPage :
                        session.Document.ActivePage;
                    Garment source = ReadGarment(sourcePage);
                    for (int copy = 1; copy <= row.Quantity; copy++)
                    {
                        target.Activate();
                        outputPage.Activate();
                        Garment garment = CopyGarment(source, outputLayer);
                        double scale = template.ScalePercent <= 0 ? 1 : template.ScalePercent / 100.0;
                        nested.AddRange(BuildNestedPieces(app, outputLayer, garment, row, size,
                            complete + 1, options, scale));
                        complete++;
                        progress?.Invoke(complete, total, size + " · " + row.Name);
                    }
                }

                double sheetHeight = Nest(nested, options.SheetWidthMm, options.GapMm);
                outputPage.SizeHeight = sheetHeight;
                foreach (var item in nested)
                    item.Shape.Move(item.Left - Convert.ToDouble(item.Shape.LeftX),
                        sheetHeight - item.Top - item.Height - Convert.ToDouble(item.Shape.BottomY));
                try { outputPage.Name = "Спортформа · " + complete + " комплектов"; } catch { }
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
                foreach (TemplateSession session in sessions.Values)
                    try
                    {
                        if (session.OpenedHere)
                        {
                            try { session.Document.Dirty = false; } catch { }
                            session.Document.Close();
                        }
                        else if (!Object.ReferenceEquals(session.Document, target))
                            session.Document.Unit = session.OriginalUnit;
                    }
                    catch (Exception closeError) { Log.Error("Could not close sport template.", closeError); }
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

        private static Garment CopyGarment(Garment source, dynamic layer)
        {
            return new Garment {
                Front = Describe(source.Front.Shape.CopyToLayer(layer)),
                Back = Describe(source.Back.Shape.CopyToLayer(layer)),
                LeftSleeve = Describe(source.LeftSleeve.Shape.CopyToLayer(layer)),
                RightSleeve = Describe(source.RightSleeve.Shape.CopyToLayer(layer)),
                FrontLeft = Describe(source.FrontLeft.Shape.CopyToLayer(layer)),
                FrontRight = Describe(source.FrontRight.Shape.CopyToLayer(layer)),
                BackText = Describe(source.BackText.Shape.CopyToLayer(layer)),
                LeftSleeveFrame = Describe(source.LeftSleeveFrame.Shape.CopyToLayer(layer)),
                RightSleeveFrame = Describe(source.RightSleeveFrame.Shape.CopyToLayer(layer))
            };
        }

        private static IList<NestedPiece> BuildNestedPieces(dynamic app, dynamic layer,
            Garment garment, SportLayoutPlayer row, string size, int index,
            SportLayoutOptions options, double scale)
        {
            var pieces = new List<NestedPiece>();
            Piece frontName = options.FrontNameOnRight ? garment.FrontRight : garment.FrontLeft;
            Piece extra = options.FrontNameOnRight ? garment.FrontLeft : garment.FrontRight;
            pieces.Add(BuildNestedPart(app, layer, garment.Front,
                new[] { frontName, extra }, options.FrontArtworkPath, "ПЕРЕД", row, size,
                index, options, scale, frontName, null));
            pieces.Add(BuildNestedPart(app, layer, garment.Back,
                new[] { garment.BackText }, options.BackArtworkPath, "СПИНА", row, size,
                index, options, scale, null, garment.BackText));
            pieces.Add(BuildNestedPart(app, layer, garment.LeftSleeve,
                new[] { garment.LeftSleeveFrame }, options.LeftSleeveArtworkPath,
                "ЛЕВЫЙ РУКАВ", row, size, index, options, scale, null, null));
            pieces.Add(BuildNestedPart(app, layer, garment.RightSleeve,
                new[] { garment.RightSleeveFrame },
                String.IsNullOrWhiteSpace(options.RightSleeveArtworkPath)
                    ? options.LeftSleeveArtworkPath : options.RightSleeveArtworkPath,
                "ПРАВЫЙ РУКАВ", row, size, index, options, scale, null, null));
            return pieces;
        }

        private static NestedPiece BuildNestedPart(dynamic app, dynamic layer,
            Piece part, IEnumerable<Piece> frames, string artworkPath, string partName,
            SportLayoutPlayer row, string size, int index, SportLayoutOptions options,
            double scale, Piece frontName, Piece backText)
        {
            part.Shape.Name = "VT_" + partName.Replace(' ', '_');
            if (!String.IsNullOrWhiteSpace(artworkPath))
            {
                dynamic image = ImportPattern(app, layer, artworkPath);
                FillPattern(image, part);
            }
            dynamic groupRange = app.CreateShapeRange();
            groupRange.Add(part.Shape);
            foreach (Piece frame in frames)
            {
                string key = partName + "_" + (frame == frontName ? "ИМЯ" : "ГРАФИКА");
                FinishGuide(frame, "VT_FRAME_" + key.Replace(' ', '_'), options.KeepGuides);
                groupRange.Add(frame.Shape);
            }
            groupRange.Add(AddCaption(layer, part, partName, row, size, options.WhiteText));
            if (frontName != null)
            {
                dynamic text = AddCenteredText(layer, frontName, row.Name,
                    options.FontName, 72, options.WhiteText);
                if (text != null) groupRange.Add(text);
            }
            if (backText != null)
                foreach (dynamic text in AddBackText(layer, backText, row.Name,
                    row.Number, options.FontName, options.WhiteText)) groupRange.Add(text);
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
            var outlines = new List<Piece>();
            var frames = new List<Piece>();
            foreach (dynamic layer in page.Layers)
            foreach (dynamic shape in layer.Shapes)
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

        private static void FillPattern(dynamic image, Piece piece)
        {
            double width = Convert.ToDouble(image.SizeWidth);
            double height = Convert.ToDouble(image.SizeHeight);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("У рисунка нет размера.");
            double scale = Math.Max(piece.Width / width, piece.Height / height);
            image.SetSize(width * scale, height * scale);
            image.Move(piece.CenterX - Convert.ToDouble(image.CenterX),
                piece.CenterY - Convert.ToDouble(image.CenterY));
            image.Name = "VT_PATTERN_" + piece.Shape.Name;
            image.AddToPowerClip(piece.Shape, 0);
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
            string font, float points, bool white)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            dynamic text = layer.CreateArtisticTextWide(zone.Left, zone.Bottom,
                value.Trim(), 0, -1, font, points, -1, 0);
            text.Name = "VT_PLAYER_NAME";
            text.Outline.SetNoOutline();
            if (white) text.Fill.UniformColor.RGBAssign(255, 255, 255);
            else text.Fill.UniformColor.RGBAssign(0, 0, 0);
            Fit(text, zone.Width * 0.88, zone.Height * 0.8);
            text.Move(zone.CenterX - Convert.ToDouble(text.CenterX),
                zone.CenterY - Convert.ToDouble(text.CenterY));
            return text;
        }

        private static IList<dynamic> AddBackText(dynamic layer, Piece zone, string name,
            string number, string font, bool white)
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
                created.Add(AddCenteredText(layer, upper, name, font, 95, white));
                created.Add(AddCenteredText(layer, lower, number, font, 150, white));
            }
            else created.Add(AddCenteredText(layer, zone, hasName ? name : number, font, 120, white));
            return created;
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
