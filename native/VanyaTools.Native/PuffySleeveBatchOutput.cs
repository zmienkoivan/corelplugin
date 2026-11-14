using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace VanyaTools.Native
{
    internal static partial class PuffySleeveOutput
    {
        private static XDocument BatchQuilting(PuffySleevePattern p)
        {
            var doc = Document(p.BatchWidth + 2 * Margin, p.BatchHeight + LabelHeight + 3 * Margin,
                "Общая заготовка 2×2 до стёжки — четыре боковинки, два изделия");
            Text(doc.Root, Margin, Margin, "Одна заготовка " + N(p.BatchWidth) + " × " + N(p.BatchHeight) + " мм · шаг " + N(p.QuiltStep) + " мм");
            var sheet = Group(doc.Root, "batch-quilting", Margin, Margin + LabelHeight);
            Rect(sheet, 0, 0, p.BatchWidth, p.BatchHeight, "#222222", "none");
            for (int i = 0; i < 4; i++)
            {
                var cell = Group(sheet, "quilt-cell-" + i, (i % 2) * p.BlankWidth, (i / 2) * p.BlankHeight);
                Text(cell, 3, 5, CellName(i));
                var axes = Group(cell, "cell-axes-" + i, 0, 0);
                axes.SetAttributeValue("transform", "translate(" + N(p.BlankMarginX * p.ScaleX) + " " + N(p.BlankMarginY * p.ScaleY) +
                    ") scale(" + N(p.ScaleX) + " " + N(p.ScaleY) + ")");
                StrokePath(axes, "final-cut-reference", p.CutPath, "#aaaaaa", .2, "3 2");
                for (int j = 0; j < p.QuiltPaths.Count; j++) StrokePath(axes, "quilt-" + j, p.QuiltPaths[j], "#376a8b", .35);
            }
            Division(sheet, p.BatchWidth, p.BatchHeight);
            Text(doc.Root, Margin, p.BatchHeight + LabelHeight + 2 * Margin,
                "Простегать четыре ячейки; затем зафиксировать формы и разделить ткань по центральным линиям.");
            return doc;
        }

        private static XDocument BatchShaping(PuffySleevePattern p)
        {
            double w = p.BlankWidth / p.ScaleX, h = p.BlankHeight / p.ScaleY;
            var doc = Document(2 * w + 2 * Margin, 2 * h + LabelHeight + 3 * Margin,
                "Четыре формы на общей заготовке после стёжки — 1:1");
            Text(doc.Root, Margin, Margin, "Фиксация четырёх боковинок · разделять после обшивки форм");
            var sheet = Group(doc.Root, "batch-shaping", Margin, Margin + LabelHeight);
            Rect(sheet, 0, 0, 2 * w, 2 * h, "#aaaaaa", "none");
            for (int i = 0; i < 4; i++)
            {
                var cell = Group(sheet, "shape-cell-" + i, (i % 2) * w, (i / 2) * h);
                Text(cell, 3, 5, CellName(i));
                var shape = Group(cell, "shaping-" + i, p.BlankMarginX, p.BlankMarginY);
                StrokePath(shape, "scissor-cut", p.CutPath, "#777777", .25, "3 2");
                StrokePath(shape, "assembly-seam", p.SeamPath, "#aaaaaa", .25, "2 2");
                StrokePath(shape, "foam-position", p.FoamPath, "#376a8b", .25, "3 2");
                StrokePath(shape, "fixing-stitch", p.FixingPath, "#398269", .6);
                Marks(shape, p);
            }
            Division(sheet, 2 * w, 2 * h);
            Text(doc.Root, Margin, 2 * h + LabelHeight + 2 * Margin,
                "Четыре прохода на форму, всего 16. Центральные линии — разделение ткани, не резка пластика.");
            return doc;
        }

        private static XDocument BatchPrintLayout(PuffySleevePattern p)
        {
            double bandW = p.BottomBandLength + 2 * p.Allowance + 2 * p.Bleed;
            double bandH = p.ZipperTapeWidth + 2 * p.Bleed;
            double y = Margin + LabelHeight + p.BatchPrintHeight + Margin;
            var doc = Document(Math.Max(p.BatchPrintWidth, bandW) + 2 * Margin,
                y + 2 * (bandH + LabelHeight + Margin), "Печать общей заготовки 2×2 — служебные линии удалить");
            Text(doc.Root, Margin, Margin, "Два переда и две спинки · общий вылет " + N(p.Bleed) + " мм");
            var sheet = Group(doc.Root, "print-batch-layout", Margin, Margin + LabelHeight);
            Rect(sheet, 0, 0, p.BatchPrintWidth, p.BatchPrintHeight, "#b04096", "none");
            var blank = Group(sheet, "batch-blank", p.Bleed, p.Bleed);
            Rect(blank, 0, 0, p.BatchWidth, p.BatchHeight, "#aaaaaa", "none");
            for (int i = 0; i < 4; i++)
            {
                var cell = Group(blank, "print-cell-" + i, (i % 2) * p.BlankWidth, (i / 2) * p.BlankHeight);
                Text(cell, 3, 5, CellName(i));
                var cut = Group(cell, "print-form-" + i, 0, 0);
                cut.SetAttributeValue("transform", "translate(" + N(p.BlankMarginX * p.ScaleX) + " " + N(p.BlankMarginY * p.ScaleY) +
                    ") scale(" + N(p.ScaleX) + " " + N(p.ScaleY) + ")");
                StrokePath(cut, "cut", p.CutPath, "#777777", .25, "3 2");
            }
            Division(blank, p.BatchWidth, p.BatchHeight);
            for (int i = 0; i < 2; i++)
                Band(doc.Root, "print-bottom-band-" + i, Margin, y + i * (bandH + LabelHeight + Margin), bandW, bandH, p.Bleed,
                    "Периметр изделия " + (i + 1) + " · плащёвка · вылет " + N(p.Bleed) + " мм");
            return doc;
        }

        private static string CellName(int i) => (i + 1) + " · " + (i % 2 == 0 ? "перед" : "спинка") + " · изделие " + (i / 2 + 1);
        private static void Division(XElement root, double w, double h) => StrokePath(root, "separate-fabric",
            "M " + N(w / 2) + " 0 V " + N(h) + " M 0 " + N(h / 2) + " H " + N(w), "#b04096", .3, "4 2");

        private static void PrintBatch(PuffySleevePattern p, string front, string back, bool mirror, string folder)
        {
            BitmapSource a = String.IsNullOrWhiteSpace(front) ? null : ReadBitmap(front);
            BitmapSource b = String.IsNullOrWhiteSpace(back) ? null :
                String.Equals(front, back, StringComparison.OrdinalIgnoreCase) ? a : ReadBitmap(back);
            int px = (int)Math.Ceiling(p.BatchPrintWidth / 25.4 * 300), py = (int)Math.Ceiling(p.BatchPrintHeight / 25.4 * 300);
            if ((long)px * py > 70000000)
                throw new InvalidOperationException("Печать общей заготовки 300 dpi превышает 70 Мп. Уменьшите ячейки или отключите раскладку 2×2.");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(96.0 / 25.4, 96.0 / 25.4));
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, p.BatchPrintWidth, p.BatchPrintHeight));
                if (mirror) dc.PushTransform(new ScaleTransform(-1, 1, p.BatchPrintWidth / 2, 0));
                for (int i = 0; i < 4; i++)
                {
                    var bitmap = i % 2 == 0 ? a : b;
                    if (bitmap == null) continue;
                    int col = i % 2, row = i / 2;
                    double x = col * p.BlankWidth, y = row * p.BlankHeight;
                    // Only the outside of the entire sheet has bleed. At shared
                    // borders clip each image to its own cell to avoid overprint.
                    dc.PushClip(new RectangleGeometry(new Rect(x + (col == 0 ? 0 : p.Bleed), y + (row == 0 ? 0 : p.Bleed),
                        p.BlankWidth + p.Bleed, p.BlankHeight + p.Bleed)));
                    dc.PushTransform(new TranslateTransform(x, y));
                    dc.PushTransform(new ScaleTransform(p.ScaleX, p.ScaleY));
                    double fw = p.PrintWidth / p.ScaleX, fh = p.PrintHeight / p.ScaleY;
                    double scale = Math.Max(fw / bitmap.PixelWidth, fh / bitmap.PixelHeight);
                    double iw = bitmap.PixelWidth * scale, ih = bitmap.PixelHeight * scale;
                    dc.DrawImage(bitmap, new Rect((fw - iw) / 2, (fh - ih) / 2, iw, ih));
                    dc.Pop(); dc.Pop(); dc.Pop();
                }
                if (mirror) dc.Pop(); dc.Pop();
            }
            var render = new RenderTargetBitmap(px, py, 300, 300, PixelFormats.Pbgra32);
            render.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
            string png = Path.Combine(folder, "print-batch-4-300dpi.png");
            using (var stream = File.Create(png)) encoder.Save(stream);
            var svg = Document(p.BatchPrintWidth, p.BatchPrintHeight, "Сублимация · общая заготовка на четыре боковинки");
            svg.Root.Add(new XElement(Svg + "image", new XAttribute("width", N(p.BatchPrintWidth)), new XAttribute("height", N(p.BatchPrintHeight)),
                new XAttribute("preserveAspectRatio", "none"), new XAttribute(Xlink + "href", "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(png)))));
            svg.Save(Path.Combine(folder, "print-batch-4.svg"));
        }
    }
}
