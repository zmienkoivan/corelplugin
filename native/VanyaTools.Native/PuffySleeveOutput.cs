using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace VanyaTools.Native
{
    internal static partial class PuffySleeveOutput
    {
        private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
        private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";
        private const double Margin = 15, LabelHeight = 14;
        private static string N(double n) { return PuffySleevePattern.N(n); }

        public static string Save(string parent, PuffySleeveOptions options, string front, string back, bool mirror)
        {
            PuffySleevePattern p = PuffySleevePattern.Build(options);
            string folder = Path.Combine(parent, (p.Product == SleeveProduct.Phone ? "Мини-сумочка-" : "Чехол-") + N(p.DeviceWidth) + "x" + N(p.DeviceHeight) +
                "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            // A failed export keeps its files for diagnosis, but is never reported as complete.
            File.WriteAllText(Path.Combine(folder, "НЕ_ЗАВЕРШЕНО.txt"), "Экспорт не завершён. Повторите сохранение комплекта.");
            Patterns(p).Save(Path.Combine(folder, "01-patterns.svg"));
            Quilting(p).Save(Path.Combine(folder, "02-quilting.svg"));
            PrintLayout(p).Save(Path.Combine(folder, "03-print-layout.svg"));
            Shaping(p).Save(Path.Combine(folder, "04-shaping.svg"));
            if (options.Jigs != null)
            {
                if (p.QuiltPaths.Count > 0) SleeveJigOutput.Save(folder, SleeveJigPlan.Build(p, options.Jigs, true));
                SleeveJigOutput.Save(folder, SleeveJigPlan.Build(p, options.Jigs, false));
            }
            if (!String.IsNullOrWhiteSpace(front)) Print(p, front, mirror, folder, "front");
            if (!String.IsNullOrWhiteSpace(back)) Print(p, back, mirror, folder, "back");
            if (p.FourUp && (!String.IsNullOrWhiteSpace(front) || !String.IsNullOrWhiteSpace(back)))
                PrintBatch(p, front, back, mirror, folder);
            File.WriteAllText(Path.Combine(folder, "parameters.json"), new JavaScriptSerializer().Serialize(new
            {
                schema = 3, product = p.Product == SleeveProduct.Phone ? "puffy-phone-pouch-with-gusset" : "puffy-laptop-sleeve-with-gusset", options, mirror,
                panelsPerPass = p.PanelCount, batchWidthMm = p.BatchWidth, batchHeightMm = p.BatchHeight,
                frontArtwork = String.IsNullOrWhiteSpace(front) ? null : Path.GetFileName(front),
                backArtwork = String.IsNullOrWhiteSpace(back) ? null : Path.GetFileName(back),
                seamWidthMm = p.SeamWidth, seamHeightMm = p.SeamHeight,
                ringDepthMm = p.RingDepth, zipperSeamLengthMm = p.ZipperLength,
                zipperTapeLengthMm = p.ZipperTapeLength
            }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "ПРОЧТИ.txt"), Instructions(p, options, front, back, mirror), new UTF8Encoding(true));
            File.Delete(Path.Combine(folder, "НЕ_ЗАВЕРШЕНО.txt"));
            return folder;
        }

        public static XDocument Patterns(PuffySleevePattern p)
        {
            double stripLength = p.BottomBandLength + 2 * p.Allowance;
            double width = Math.Max(2 * p.CutWidth + 3 * Margin, stripLength + 2 * Margin);
            double row = p.CutHeight + LabelHeight + Margin;
            double bandY = Margin + 3 * row;
            double stripHeight = p.RingDepth + 2 * p.Allowance;
            XDocument doc = Document(width, bandY + stripHeight + LabelHeight + 2 * Margin,
                "Лекала после стёжки — 1:1");
            XElement root = doc.Root;
            for (int i = 0; i < 2; i++)
            {
                double x = Margin + i * (p.CutWidth + Margin), y = Margin;
                XElement outer = Group(root, "outer-" + i, x, y + LabelHeight);
                Text(root, x, y + 4, (i == 0 ? "Перед" : "Спинка") + " · наружный пакет · крой после стёжки");
                StrokePath(outer, "cut", p.CutPath, "#222222", 0.3);
                StrokePath(outer, "seam", p.SeamPath, "#777777", 0.25, "3 2");
                StrokePath(outer, "fixing", p.FixingPath, "#398269", 0.3, "2 1");
                StrokePath(outer, "zipper", p.ZipperPath, "#c04f13", 0.8);
                Marks(outer, p);
                Text(outer, p.Allowance + 15, p.CutHeight / 2,
                    N(p.CutWidth) + " × " + N(p.CutHeight) + " мм · припуск " + N(p.Allowance));
                y += row;
                Text(root, x, y + 4, "Микрофибра · " + (i == 0 ? "перед" : "спинка"));
                XElement lining = Group(root, "lining-" + i, x, y + LabelHeight);
                StrokePath(lining, "cut", p.CutPath, "#222222", 0.3);
                StrokePath(lining, "seam", p.SeamPath, "#777777", 0.25, "3 2");
                Marks(lining, p);
                y += row;
                Text(root, x, y + 4, "Пена " + N(p.FoamThickness) + " мм · " + (i == 0 ? "перед" : "спинка") + " · без припуска");
                XElement foam = Group(root, "foam-" + i, x, y + LabelHeight);
                StrokePath(foam, "cut", p.FoamPath, "#222222", 0.3);
                Text(foam, p.Allowance + 15, p.CutHeight / 2,
                    N(p.FoamWidth) + " × " + N(p.FoamHeight) + " мм");
            }
            Band(root, "bottom-band", Margin, bandY, stripLength, stripHeight, p.Allowance,
                "Периметр без молнии · по 1 из плащёвки, синтепона и микрофибры");
            return doc;
        }

        public static XDocument Quilting(PuffySleevePattern p)
        {
            if (p.FourUp) return BatchQuilting(p);
            XDocument doc = Document(2 * p.BlankWidth + 3 * Margin,
                p.BlankHeight + LabelHeight + 3 * Margin, "Заготовки до стёжки — 1:1");
            for (int i = 0; i < 2; i++)
            {
                double x = Margin + i * (p.BlankWidth + Margin);
                Text(doc.Root, x, Margin, (i == 0 ? "Перед" : "Спинка") + " · до стёжки · " +
                    N(p.BlankWidth) + " × " + N(p.BlankHeight) + " мм");
                XElement blank = Group(doc.Root, "blank-" + i, x, Margin + LabelHeight);
                Rect(blank, 0, 0, p.BlankWidth, p.BlankHeight, "#222222", "none");
                XElement group = Group(blank, "stitches-" + i, p.BlankMarginX * p.ScaleX, p.BlankMarginY * p.ScaleY);
                group.SetAttributeValue("transform", "translate(" + N(p.BlankMarginX * p.ScaleX) + " " + N(p.BlankMarginY * p.ScaleY) +
                    ") scale(" + N(p.ScaleX) + " " + N(p.ScaleY) + ")");
                StrokePath(group, "final-cut-reference", p.CutPath, "#aaaaaa", 0.2, "3 2");
                for (int j = 0; j < p.QuiltPaths.Count; j++)
                    StrokePath(group, "quilt-" + j, p.QuiltPaths[j], "#376a8b", 0.35);
            }
            Text(doc.Root, Margin, p.BlankHeight + LabelHeight + 2 * Margin,
                "Линии — оси строчек. Геометрию пазов пластикового шаблона рассчитывают под направляющую машины.");
            return doc;
        }

        public static XDocument Shaping(PuffySleevePattern p)
        {
            if (p.FourUp) return BatchShaping(p);
            double w = p.BlankWidth / p.ScaleX, h = p.BlankHeight / p.ScaleY;
            XDocument doc = Document(2 * w + 3 * Margin, h + LabelHeight + 3 * Margin,
                "Контур формы после стёжки — ось фиксирующей строчки 1:1");
            for (int i = 0; i < 2; i++)
            {
                double x = Margin + i * (w + Margin);
                Text(doc.Root, x, Margin, (i == 0 ? "Перед" : "Спинка") + " · фиксация слоёв после стёжки");
                XElement blank = Group(doc.Root, "shaping-blank-" + i, x, Margin + LabelHeight);
                Rect(blank, 0, 0, w, h, "#aaaaaa", "none");
                XElement shape = Group(blank, "shaping-" + i, p.BlankMarginX, p.BlankMarginY);
                StrokePath(shape, "scissor-cut", p.CutPath, "#777777", 0.25, "3 2");
                StrokePath(shape, "assembly-seam", p.SeamPath, "#aaaaaa", 0.25, "2 2");
                StrokePath(shape, "foam-position", p.FoamPath, "#376a8b", 0.25, "3 2");
                StrokePath(shape, "fixing-stitch", p.FixingPath, "#398269", 0.6);
                Marks(shape, p);
            }
            Text(doc.Root, Margin, h + LabelHeight + 2 * Margin,
                "Зелёный — фиксация; серый пунктир — обрезка с припуском. Это оси строчек, не контуры резки пластика.");
            return doc;
        }

        public static XDocument PrintLayout(PuffySleevePattern p)
        {
            if (p.FourUp) return BatchPrintLayout(p);
            double bottomWidth = p.BottomBandLength + 2 * p.Allowance + 2 * p.Bleed;
            double bandHeight = p.RingDepth + 2 * p.Allowance + 2 * p.Bleed;
            double width = Math.Max(2 * p.PrintWidth + 3 * Margin, bottomWidth + 2 * Margin);
            double y = Margin + LabelHeight + p.PrintHeight + Margin;
            XDocument doc = Document(width, y + bandHeight + LabelHeight + 2 * Margin,
                "Шаблон размещения принта — служебные линии удалить перед печатью");
            for (int i = 0; i < 2; i++)
            {
                double x = Margin + i * (p.PrintWidth + Margin);
                Text(doc.Root, x, Margin, (i == 0 ? "Перед" : "Спинка") + " · печать до стёжки + вылет");
                XElement panel = Group(doc.Root, "print-panel-" + i, x, Margin + LabelHeight);
                Rect(panel, 0, 0, p.PrintWidth, p.PrintHeight, "#b04096", "none");
                Rect(panel, p.Bleed, p.Bleed, p.BlankWidth, p.BlankHeight, "#aaaaaa", "none");
                XElement cut = Group(panel, "blank-" + i, p.Bleed, p.Bleed);
                cut.SetAttributeValue("transform", "translate(" + N(p.Bleed + p.BlankMarginX * p.ScaleX) + " " + N(p.Bleed + p.BlankMarginY * p.ScaleY) +
                    ") scale(" + N(p.ScaleX) + " " + N(p.ScaleY) + ")");
                StrokePath(cut, "cut", p.CutPath, "#777777", 0.25, "3 2");
            }
            Band(doc.Root, "print-bottom-band", Margin, y, bottomWidth, bandHeight, p.Bleed,
                "Печать плащёвки периметра · вылет " + N(p.Bleed) + " мм");
            return doc;
        }

        private static void Print(PuffySleevePattern p, string image, bool mirror, string folder, string name)
        {
            BitmapSource bitmap = ReadBitmap(image);
            int pixelsX = (int)Math.Ceiling(p.PrintWidth / 25.4 * 300);
            int pixelsY = (int)Math.Ceiling(p.PrintHeight / 25.4 * 300);
            if ((long)pixelsX * pixelsY > 70000000)
                throw new InvalidOperationException("Печать 300 dpi превышает 70 Мп. Уменьшите размер заготовки.");
            double w = pixelsX * 96.0 / 300, h = pixelsY * 96.0 / 300;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
                if (mirror) dc.PushTransform(new ScaleTransform(-1, 1, w / 2, 0));
                // Pre-stretch artwork for unequal quilting take-up in X/Y.
                dc.PushTransform(new ScaleTransform(p.ScaleX, p.ScaleY));
                double finalW = w / p.ScaleX, finalH = h / p.ScaleY;
                double scale = Math.Max(finalW / bitmap.PixelWidth, finalH / bitmap.PixelHeight);
                double iw = bitmap.PixelWidth * scale, ih = bitmap.PixelHeight * scale;
                dc.DrawImage(bitmap, new Rect((finalW - iw) / 2, (finalH - ih) / 2, iw, ih));
                dc.Pop();
                if (mirror) dc.Pop();
                dc.Pop();
            }
            var render = new RenderTargetBitmap(pixelsX, pixelsY, 300, 300, PixelFormats.Pbgra32);
            render.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(render));
            string png = Path.Combine(folder, "print-" + name + "-300dpi.png");
            using (var stream = File.Create(png)) encoder.Save(stream);
            // Embed the prepared PNG so opening the SVG never depends on local paths.
            XDocument svg = Document(p.PrintWidth, p.PrintHeight, "Сублимация · " + name);
            svg.Root.Add(new XElement(Svg + "image", new XAttribute("width", N(p.PrintWidth)),
                new XAttribute("height", N(p.PrintHeight)), new XAttribute("preserveAspectRatio", "none"),
                new XAttribute(Xlink + "href", "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(png)))));
            svg.Save(Path.Combine(folder, "print-" + name + ".svg"));
        }

        public static BitmapSource ReadBitmap(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Файл принта не найден.", path);
            using (var stream = File.OpenRead(path))
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                if ((long)frame.PixelWidth * frame.PixelHeight > 70000000)
                    throw new InvalidOperationException("Принт больше 70 Мп. Подготовьте файл меньшего размера.");
                frame.Freeze();
                return frame;
            }
        }

        public static void OpenPatterns(string path)
        {
            // OpenDocument gives this SVG its own document and preserves its mm page.
            object app = CorelApp.Get();
            app.GetType().InvokeMember("OpenDocument", BindingFlags.InvokeMethod, null, app,
                new object[] { new BStrWrapper(Path.GetFullPath(path)), 0 });
        }

        private static XDocument Document(double width, double height, string title)
        {
            return new XDocument(new XElement(Svg + "svg", new XAttribute("version", "1.1"),
                new XAttribute(XNamespace.Xmlns + "xlink", Xlink),
                new XAttribute("width", N(width) + "mm"), new XAttribute("height", N(height) + "mm"),
                new XAttribute("viewBox", "0 0 " + N(width) + " " + N(height)),
                new XElement(Svg + "title", title)));
        }

        private static XElement Group(XElement root, string id, double x, double y)
        {
            var g = new XElement(Svg + "g", new XAttribute("id", id),
                new XAttribute("transform", "translate(" + N(x) + " " + N(y) + ")"));
            root.Add(g); return g;
        }
        private static void StrokePath(XElement root, string id, string data, string color, double stroke, string dash = null)
        {
            var path = new XElement(Svg + "path", new XAttribute("data-role", id), new XAttribute("d", data),
                new XAttribute("fill", "none"), new XAttribute("stroke", color), new XAttribute("stroke-width", N(stroke)));
            if (dash != null) path.SetAttributeValue("stroke-dasharray", dash);
            root.Add(path);
        }
        private static void Rect(XElement root, double x, double y, double w, double h, string stroke, string fill)
        {
            root.Add(new XElement(Svg + "rect", new XAttribute("x", N(x)), new XAttribute("y", N(y)),
                new XAttribute("width", N(w)), new XAttribute("height", N(h)),
                new XAttribute("stroke", stroke), new XAttribute("stroke-width", "0.3"), new XAttribute("fill", fill)));
        }
        private static void Text(XElement root, double x, double y, string text)
        {
            root.Add(new XElement(Svg + "text", new XAttribute("x", N(x)), new XAttribute("y", N(y)),
                new XAttribute("font-family", "Arial"), new XAttribute("font-size", "3.2"), new XAttribute("fill", "#222222"), text));
        }
        private static void Band(XElement root, string id, double x, double y, double w, double h, double a, string title)
        {
            Text(root, x, y + 4, title + " · " + N(w) + " × " + N(h) + " мм");
            XElement band = Group(root, id, x, y + LabelHeight);
            Rect(band, 0, 0, w, h, "#222222", "none");
            StrokePath(band, "seam", PuffySleevePattern.RoundedRect(a, a, w - 2 * a, h - 2 * a, 0), "#777777", 0.25, "3 2");
            StrokePath(band, "centre", "M " + N(w / 2) + " 0 v " + N(h), "#376a8b", 0.25, "2 2");
        }
        private static void Marks(XElement root, PuffySleevePattern p)
        {
            double a = p.Allowance, x = p.CutWidth / 2;
            StrokePath(root, "centre-marks", "M " + N(x) + " 0 v " + N(a / 2) + " M " + N(x) + " " + N(p.CutHeight) + " v " + N(-a / 2), "#376a8b", 0.35);
            foreach (double side in new[] { a, a + p.SeamWidth })
                StrokePath(root, "zipper-end", "M " + N(side - a / 2) + " " + N(a + p.ZipperDrop) + " h " + N(a), "#c04f13", 0.5);
        }

        private static string Instructions(PuffySleevePattern p, PuffySleeveOptions o, string front, string back, bool mirror)
        {
            return "ЭКСПЕРИМЕНТ · ЧЕХОЛ С КОЛЬЦОМ ПЕРИМЕТРА\r\n\r\n" +
                (p.Product == SleeveProduct.Phone ? "Мини-сумочка для телефона. Размер корпуса с защитным чехлом: " : "Корпус ноутбука: ") + N(p.DeviceWidth) + " × " + N(p.DeviceHeight) + " × " + N(o.DeviceThickness) + " мм." +
                (o.UseDiagonal ? " Оценка по экрану и рамке; перед раскроем замените её фактическими размерами корпуса." : "") +
                "\r\nБоковинка по линии шва: " + N(p.SeamWidth) + " × " + N(p.SeamHeight) + " мм. Припуск: " + N(p.Allowance) + " мм." +
                "\r\nГлубина кольца по шву: " + N(p.RingDepth) + " мм. Расчётная необходимая глубина: " + N(p.RequiredRingDepth) + " мм = толщина устройства + 2 × (пена + сжатый стёганый слой + микрофибра) + свобода по толщине." +
                "\r\nМолния ГОЛАЯ, без тканевых планок. Полная ширина с обеими лентами: " + N(p.ZipperTapeWidth) + " мм; расстояние между сборочными строчками: " + N(p.RingDepth) + " мм. Припуск на каждой ленте " + N(p.Allowance) + " мм." +
                "\r\nМолния по линии притачивания: " + N(p.ZipperLength) + " мм. Лента с припусками поперечных швов и запасами концов: " + N(p.ZipperTapeLength) + " мм." +
                "\r\nСпуск молнии от верха: " + N(p.ZipperDrop) + " мм с каждой стороны (" + N(o.ZipperDropPercent) + "% высоты)." +
                "\r\nПериметр по шву: " + N(p.PerimeterLength) + " мм = молния " + N(p.ZipperLength) + " + нижняя полоса " + N(p.BottomBandLength) + "." +
                "\r\n\r\nСОСТАВ И ПОСЛЕДОВАТЕЛЬНОСТЬ" +
                (p.FourUp ? "\r\nПартия: четыре боковинки (два переда и две спинки) на два изделия. Лекала 01-patterns.svg даны на одно изделие; повторите их дважды." : "") +
                "\r\n1. По 02-quilting.svg подготовьте " + (p.FourUp ? "одну общую заготовку 2×2 размером " + N(p.BatchWidth) + " × " + N(p.BatchHeight) + " мм из каждого материала" : "по две ПРЯМОУГОЛЬНЫЕ заготовки") + " плащёвки, синтепона и стабилизатора (флизелин/аналог). Простегайте пакет в шаблоне стёжки. Запас за будущим припуском после усадки: " + N(p.BlankMarginX) + " мм по бокам, " + N(p.BlankMarginY) + " мм сверху/снизу каждой ячейки." +
                "\r\n2. Выкроите " + (p.FourUp ? "4" : "2") + " детали пены по отдельному контуру, без припусков. Пена отступает от линии шва внутрь на " + N(p.FoamInset) + " мм и не входит в шов." +
                "\r\n3. В отдельный шаблон формы 04-shaping.svg уложите простёганную заготовку, пену по внутреннему ориентиру и прямоугольную микрофибру с запасом (не меньше " + N(p.BatchWidth / p.ScaleX) + " × " + N(p.BatchHeight / p.ScaleY) + " мм). Без растяжения зажмите слои и обшейте зелёный контур фиксации: он на " + N(p.FixingOffset) + " мм снаружи сборочного шва, внутри припуска. " + (p.FourUp ? "Зафиксируйте все четыре формы (16 проходов через пазы), затем разделите общее полотно на четыре части по центральным линиям. " : "") + "После фиксации обрежьте ножницами по контуру кроя 01-patterns.svg, сохраняя припуск " + N(p.Allowance) + " мм от сборочного шва. Размер задаёт строчка по шаблону, а не ручная обрезка." +
                "\r\n4. Нижняя полоса периметра: на каждое изделие выкроите по одной детали из плащёвки, синтепона и микрофибры. Пены в кольце нет. Планок молнии нет." +
                "\r\n5. Соедините концы голой молнии с концами нижней полосы, замыкая кольцо. Припуски поперечных соединений добавлены к лекалу полосы и длине ленты молнии. Боковые ленты молнии притачиваются прямо к боковинкам." +
                "\r\n6. Совместите середину молнии с верхней меткой боковинки, середину нижней полосы — с нижней. Концы молнии совпадают с оранжевыми метками. Соберите кольцо с боковинками на прямострочке, ведя сборочный шов по заданной линии либо с постоянным отступом внутрь от фиксирующей строчки, а не по неровному срезу ножниц. Закройте внутренние швы окантовкой." +
                "\r\nРасчётный расход окантовки на два шва одного изделия: " + N(2 * p.PerimeterLength + 100) + " мм, включая суммарный запас 100 мм. Ширина зависит от окантователя и толщины пакета." +
                "\r\n\r\nФАЙЛЫ" +
                "\r\n01-patterns.svg — итоговые лекала 1:1; чёрный — крой, серый пунктир — шов, оранжевый — молния." +
                "\r\n02-quilting.svg — " + (p.FourUp ? "общая заготовка на четыре боковинки" : "две прямоугольные заготовки") + " с запасом и компенсацией усадки " + N(o.ShrinkX) + "% × " + N(o.ShrinkY) + "%; синий — оси строчек. Серый контур формы — ориентир, до стёжки по нему не обрезать." +
                "\r\n04-shaping.svg — отдельная схема формовочного шаблона ПОСЛЕ стёжки. Зелёный — ось фиксирующей строчки, серый пунктир — обрезка с припуском, синий — положение пены." +
                "\r\n02 и 04 — схемы строчек для ткани, не контуры резки пластика." +
                (o.Jigs == null ? " Пластиковые шаблоны в этот комплект не добавлены: задайте размеры направляющей и включите их экспорт." :
                    "\r\n05-quilting-jig и 06-shaping-jig — шаблоны пластика. Каждый CUT-SVG содержит один лист с замкнутыми контурами резки. " +
                    "ENGRAVE-SVG содержит только ориентиры. CDR создаётся в CorelDRAW с двумя страницами (верх/низ), печатается только CUT. " +
                    "Измеренная направляющая: диаметр " + N(o.Jigs.GuideDiameter) + ", высота " + N(o.Jigs.GuideHeight) +
                    " мм; зазор " + N(o.Jigs.Clearance) + "; пластик " + N(o.Jigs.PlasticThickness) + "; учтённая ширина реза " + N(o.Jigs.Kerf) +
                    " мм. Компенсацию реза применять один раз. Параметры каждого шаблона записаны в отдельном settings.txt.") +
                "\r\n03-print-layout.svg — шаблон размещения принта боковинок и нижней полосы периметра с вылетом " + N(p.Bleed) + " мм. Удалите служебные линии перед печатью." +
                "\r\nГотовые принты боковинок: перед — " + (String.IsNullOrWhiteSpace(front) ? "не задан" : "print-front.svg и PNG 300 dpi") +
                "; спинка — " + (String.IsNullOrWhiteSpace(back) ? "не задан" : "print-back.svg и PNG 300 dpi") + "." +
                (p.FourUp ? "\r\nПри выборе хотя бы одного принта также выводятся print-batch-4.svg и print-batch-4-300dpi.png — четыре ячейки на общем листе без технических линий. Невыбранный принт оставляет свои ячейки белыми." : "") +
                "\r\nПринт заполняет весь прямоугольник до стёжки с вылетами, с обрезкой по центру. Разная усадка X/Y компенсируется предварительным растяжением изображения. " +
                (mirror ? "Зеркало уже применено; повторно в драйвере не включайте." : "Зеркало не применено; задайте его в печати при необходимости.") +
                "\r\nНижняя полоса периметра оформляется отдельно в 03-print-layout.svg. Автоматическая печать этой полосы не выполняется." +
                "\r\nPNG округлён вверх до целых пикселей; точный физический размер задан SVG. SVG открывается в CorelDRAW; печатайте 100%, без подгонки к листу." +
                "\r\n\r\nПеред серией сшейте пробный образец: толщины указываются в сжатом виде, усадку измеряют на своём стёганом пакете. Нулевая усадка означает, что компенсация не задана. Это опытная конструкция, посадка и технология соединений требуют проверки на материале.\r\n";
        }
    }
}
