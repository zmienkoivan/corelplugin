using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

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
        public string PatternPath;
        public string OutputFolder;
        public string FontName;
        public bool FrontNameOnRight;
        public bool WhiteText;
        public bool KeepGuides;
        public bool RepeatPattern;
        public double TileWidthMm;
    }

    // Works on a copy of each CDR. The supplied M template has four large
    // orange garment contours and five empty red rectangles; no shapes have
    // names. Geometry is used to identify the parts on each untouched copy.
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

        public IList<string> Generate(IList<SportLayoutPlayer> rows,
            IList<SportLayoutTemplate> templates, SportLayoutOptions options,
            Action<int, int, string> progress)
        {
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("Добавьте игроков в таблицу.");
            if (templates == null || templates.Count == 0)
                throw new InvalidOperationException("Добавьте CDR-шаблоны для размеров.");
            if (options == null || String.IsNullOrWhiteSpace(options.OutputFolder))
                throw new InvalidOperationException("Выберите папку для готовых CDR.");
            if (!String.IsNullOrWhiteSpace(options.PatternPath) && !File.Exists(options.PatternPath))
                throw new FileNotFoundException("Файл рисунка не найден.", options.PatternPath);
            if (options.RepeatPattern && !String.IsNullOrWhiteSpace(options.PatternPath) &&
                (options.TileWidthMm < 5 || options.TileWidthMm > 1000))
                throw new InvalidOperationException("Ширина плитки паттерна: от 5 до 1000 мм.");
            if (String.IsNullOrWhiteSpace(options.FontName))
                throw new InvalidOperationException("Выберите шрифт для имён и номеров.");

            var bySize = new Dictionary<string, SportLayoutTemplate>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var template in templates)
            {
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
                    throw new InvalidOperationException("Масштаб размера " + size +
                        " должен быть от 50 до 200%.");
                bySize.Add(size, template);
            }

            int total = 0;
            foreach (var row in rows)
            {
                if (row == null) continue;
                string size = NormalizeSize(row.Size);
                if (size.Length == 0) throw new InvalidOperationException("У одного из игроков не указан размер.");
                if (!bySize.ContainsKey(size))
                    throw new InvalidOperationException("Для размера " + size + " нет CDR-шаблона.");
                if (row.Quantity < 1 || row.Quantity > 100)
                    throw new InvalidOperationException("Количество в строке должно быть от 1 до 100.");
                if ((row.Name ?? "").Length > 100 || (row.Number ?? "").Length > 20)
                    throw new InvalidOperationException("Слишком длинное имя или номер в таблице.");
                total += row.Quantity;
            }
            if (total == 0 || total > 300)
                throw new InvalidOperationException("За один запуск можно подготовить от 1 до 300 комплектов.");

            string output = System.IO.Path.GetFullPath(options.OutputFolder);
            Directory.CreateDirectory(output);
            foreach (string templatePath in bySize.Values.Select(item =>
                System.IO.Path.GetFullPath(item.Path)))
                if (String.Equals(output, System.IO.Path.GetDirectoryName(templatePath),
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Выберите отдельную папку для готовых CDR, не папку шаблонов.");

            dynamic app = CorelApp.Get();
            dynamic previousDocument = null;
            try { previousDocument = app.ActiveDocument; } catch { }
            var created = new List<string>();
            try
            {
                foreach (var row in rows)
                {
                    if (row == null) continue;
                    string size = NormalizeSize(row.Size);
                    for (int copy = 1; copy <= row.Quantity; copy++)
                    {
                        string label = size + " · " + (row.Name ?? "") + " · №" + (row.Number ?? "");
                        progress?.Invoke(created.Count + 1, total, label);
                        string path = UniquePath(output, size, row.Name, row.Number, copy);
                        File.Copy(System.IO.Path.GetFullPath(bySize[size].Path), path);
                        dynamic document = null;
                        try
                        {
                            document = app.OpenDocument(path);
                            document.Unit = CorelConstants.CdrMillimeter;
                            if (Convert.ToInt32(document.Pages.Count) != 1)
                                throw new InvalidOperationException("Шаблон должен содержать одну страницу.");
                            Garment garment = ReadGarment(document.ActivePage);
                            Fill(document, app, garment, row, size, options);
                            double scale = bySize[size].ScalePercent <= 0 ? 1 :
                                bySize[size].ScalePercent / 100.0;
                            if (Math.Abs(scale - 1) > 0.0001)
                                ScalePage(document, app, scale);
                            CorelDocumentSaver.Save(document);
                            created.Add(path);
                        }
                        catch
                        {
                            // Only the new copy is removed; never touch the source template.
                            try { if (document != null) document.Dirty = false; } catch { }
                            try { if (document != null) document.Close(); } catch { }
                            document = null;
                            try { File.Delete(path); } catch { }
                            throw;
                        }
                        finally
                        {
                            try { if (document != null) document.Close(); } catch { }
                        }
                    }
                }
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Создано " + created.Count + " из " + total +
                    ". Остальные не подготовлены: " + error.Message, error);
            }
            finally
            {
                try { if (previousDocument != null) previousDocument.Activate(); } catch { }
            }
            return created;
        }

        internal static string NormalizeSize(string value)
        {
            string size = (value ?? "").Trim().ToUpperInvariant();
            // Common Cyrillic labels: М, С, ХЛ. С is the spoken Russian
            // letter for size S, not the Latin letter C.
            return size.Replace('М', 'M').Replace('С', 'S')
                .Replace('Л', 'L').Replace('Х', 'X');
        }

        private static string UniquePath(string folder, string size, string name,
            string number, int copy)
        {
            string stem = Clean(size) + "_" + Clean(name) + "_" + Clean(number);
            if (stem.Length > 105) stem = stem.Substring(0, 105).TrimEnd('_');
            if (stem.Trim('_').Length == 0) stem = "спортформа";
            string suffix = copy > 1 ? "_" + copy.ToString("00", CultureInfo.InvariantCulture) : "";
            for (int index = 0; index < 10000; index++)
            {
                string tail = index == 0 ? "" : "_" + index.ToString("00", CultureInfo.InvariantCulture);
                string path = System.IO.Path.Combine(folder, stem + suffix + tail + ".cdr");
                if (!File.Exists(path)) return path;
            }
            throw new IOException("Не удалось подобрать имя для выходного CDR.");
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

        private static void Fill(dynamic document, dynamic app, Garment garment,
            SportLayoutPlayer row, string size, SportLayoutOptions options)
        {
            var parts = new[] {
                new { Piece = garment.Front, Key = "ПЕРЕД" },
                new { Piece = garment.Back, Key = "СПИНА" },
                new { Piece = garment.LeftSleeve, Key = "ЛЕВЫЙ РУКАВ" },
                new { Piece = garment.RightSleeve, Key = "ПРАВЫЙ РУКАВ" }
            };
            dynamic[] patternCopies = null;
            if (!String.IsNullOrWhiteSpace(options.PatternPath) && !options.RepeatPattern)
            {
                dynamic original = ImportPattern(app, garment.Front.Shape.Layer, options.PatternPath);
                patternCopies = new dynamic[parts.Length];
                patternCopies[0] = original;
                for (int i = 1; i < patternCopies.Length; i++)
                    patternCopies[i] = original.Duplicate(0.0, 0.0);
            }
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                part.Piece.Shape.Name = "VT_" + part.Key.Replace(' ', '_');
                if (patternCopies != null)
                    FillPattern(patternCopies[i], part.Piece);
                else if (!String.IsNullOrWhiteSpace(options.PatternPath))
                    RepeatPattern(part.Piece, options.PatternPath, options.TileWidthMm);
                AddCaption(garment.Front.Shape.Layer, part.Piece, part.Key, row, size,
                    options.WhiteText);
            }

            Piece nameZone = options.FrontNameOnRight ? garment.FrontRight : garment.FrontLeft;
            Piece extraZone = options.FrontNameOnRight ? garment.FrontLeft : garment.FrontRight;
            AddCenteredText(garment.Front.Shape.Layer, nameZone, row.Name, options.FontName,
                72, options.WhiteText);
            AddBackText(garment.Front.Shape.Layer, garment.BackText, row.Name, row.Number,
                options.FontName, options.WhiteText);
            FinishGuide(nameZone, "VT_FRONT_NAME", options.KeepGuides);
            FinishGuide(garment.BackText, "VT_BACK_NAME_NUMBER", options.KeepGuides);
            FinishGuide(extraZone, "VT_FRONT_EXTRA_FRAME", options.KeepGuides);
            FinishGuide(garment.LeftSleeveFrame, "VT_LEFT_SLEEVE_FRAME", options.KeepGuides);
            FinishGuide(garment.RightSleeveFrame, "VT_RIGHT_SLEEVE_FRAME", options.KeepGuides);
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

        private static void RepeatPattern(Piece piece, string path, double tileWidth)
        {
            var frame = BitmapFrame.Create(new Uri(System.IO.Path.GetFullPath(path)),
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                throw new InvalidOperationException("Не удалось узнать размер рисунка паттерна.");
            dynamic fill = piece.Shape.Fill.ApplyPatternFill(2,
                System.IO.Path.GetFullPath(path));
            fill.TileWidth = tileWidth;
            fill.TileHeight = tileWidth * frame.PixelHeight / frame.PixelWidth;
            fill.TransformWithShape = true;
        }

        private static void AddCaption(dynamic layer, Piece piece, string part,
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
        }

        private static void AddCenteredText(dynamic layer, Piece zone, string value,
            string font, float points, bool white)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            dynamic text = layer.CreateArtisticTextWide(zone.Left, zone.Bottom,
                value.Trim(), 0, -1, font, points, -1, 0);
            text.Name = "VT_PLAYER_NAME";
            text.Outline.SetNoOutline();
            if (white) text.Fill.UniformColor.RGBAssign(255, 255, 255);
            else text.Fill.UniformColor.RGBAssign(0, 0, 0);
            Fit(text, zone.Width * 0.88, zone.Height * 0.8);
            text.Move(zone.CenterX - Convert.ToDouble(text.CenterX),
                zone.CenterY - Convert.ToDouble(text.CenterY));
        }

        private static void AddBackText(dynamic layer, Piece zone, string name,
            string number, string font, bool white)
        {
            bool hasName = !String.IsNullOrWhiteSpace(name);
            bool hasNumber = !String.IsNullOrWhiteSpace(number);
            if (!hasName && !hasNumber) return;
            if (hasName && hasNumber)
            {
                var upper = new Piece { Left = zone.Left, Bottom = zone.Bottom + zone.Height * 0.48,
                    Width = zone.Width, Height = zone.Height * 0.52 };
                var lower = new Piece { Left = zone.Left, Bottom = zone.Bottom,
                    Width = zone.Width, Height = zone.Height * 0.48 };
                AddCenteredText(layer, upper, name, font, 95, white);
                AddCenteredText(layer, lower, number, font, 150, white);
            }
            else AddCenteredText(layer, zone, hasName ? name : number, font, 120, white);
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

        private static void ScalePage(dynamic document, dynamic app, double factor)
        {
            dynamic page = document.ActivePage;
            dynamic all = app.CreateShapeRange();
            foreach (dynamic layer in page.Layers)
            foreach (dynamic shape in layer.Shapes)
                all.Add(shape);
            if (Convert.ToInt32(all.Count) == 0)
                throw new InvalidOperationException("В шаблоне нет объектов для масштабирования.");
            double left = Convert.ToDouble(all.LeftX);
            double bottom = Convert.ToDouble(all.BottomY);
            double pageWidth = Convert.ToDouble(page.SizeWidth);
            double pageHeight = Convert.ToDouble(page.SizeHeight);
            page.SizeWidth = pageWidth * factor;
            page.SizeHeight = pageHeight * factor;
            all.Stretch(factor, factor, true);
            all.Move(left * factor - Convert.ToDouble(all.LeftX),
                bottom * factor - Convert.ToDouble(all.BottomY));
        }
    }
}
