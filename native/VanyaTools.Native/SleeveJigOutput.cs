using System;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml.Linq;

namespace VanyaTools.Native
{
    internal static class SleeveJigOutput
    {
        private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
        private static string N(double n) => PuffySleevePattern.N(n);

        public static XDocument SvgDocument(SleeveJigPlan p, bool upper, bool engraving = false)
        {
            var root = new XElement(Svg + "svg", new XAttribute("version", "1.1"),
                new XAttribute("width", N(p.PageWidth) + "mm"), new XAttribute("height", N(p.PageHeight) + "mm"),
                new XAttribute("viewBox", "0 0 " + N(p.PageWidth) + " " + N(p.PageHeight)),
                new XElement(Svg + "title", p.Title + " · " + (upper ? "верхний" : "нижний") + " лист · " + (engraving ? "гравировка" : "резка 1:1")),
                new XElement(Svg + "metadata", "Пластик " + N(p.Options.PlasticThickness) + " мм; готовый паз " + N(p.SlotWidth) +
                    " мм; учтённая ширина реза " + N(p.Options.Kerf) + " мм; оси совпадают с центром иглы."));
            var layer = new XElement(Svg + "g", new XAttribute("id", engraving ? "ENGRAVE" : "CUT"),
                new XAttribute("transform", "translate(" + N(p.Origin) + " " + N(p.Origin) + ")"),
                new XAttribute("fill", "none"), new XAttribute("stroke", engraving ? "#0066cc" : "#ff0000"),
                new XAttribute("stroke-width", engraving ? "0.1" : "0.01"));
            foreach (SleeveJigLine line in engraving ? p.Engraving : p.Cuts)
                layer.Add(new XElement(Svg + "path", new XAttribute("data-role", engraving ? "engrave" : "cut"),
                    new XAttribute("data-name", line.Name), new XAttribute("d", SleeveJigPlan.Data(line.Geometry))));
            root.Add(layer);
            return new XDocument(root);
        }

        public static void Save(string folder, SleeveJigPlan p)
        {
            Directory.CreateDirectory(folder);
            string prefix = p.Quilting ? "05-quilting-jig" : "06-shaping-jig";
            SvgDocument(p, false).Save(Path.Combine(folder, prefix + "-bottom-CUT.svg"));
            SvgDocument(p, true).Save(Path.Combine(folder, prefix + "-top-CUT.svg"));
            SvgDocument(p, false, true).Save(Path.Combine(folder, prefix + "-ENGRAVE.svg"));
            File.WriteAllText(Path.Combine(folder, prefix + "-settings.txt"),
                p.Title + " · два листа по " + N(p.Options.PlasticThickness) + " мм\r\n" +
                "Габарит готового листа: " + N(p.Width) + " × " + N(p.Height) + " мм.\r\n" +
                "Боковинок в кассете: " + p.PanelCount + (p.PanelCount == 4 ? " (2×2, на два изделия). Разделительные линии — только ориентиры для ткани, не сквозная резка пластика." : ".") + "\r\n" +
                "Направляющая: диаметр " + N(p.Options.GuideDiameter) + "; высота " + N(p.Options.GuideHeight) + " мм.\r\n" +
                "Рабочий зазор: " + N(p.Options.Clearance) + " мм; ширина готового паза: " + N(p.SlotWidth) + " мм.\r\n" +
                "Ширина лазерного реза, уже учтённая в геометрии: " + N(p.Options.Kerf) + " мм. Не компенсировать повторно в ПО лазера.\r\n" +
                "При нуле компенсация здесь отключена: задайте её один раз в ПО лазера либо в генераторе.\r\n" +
                "Ширина перемычки формы: " + N(p.Options.Bridge) + " мм. Отверстия совмещения: " + N(p.Options.RegistrationHole) + " мм.\r\n" +
                "CUT-файлы содержат только замкнутые контуры резки. ENGRAVE — отдельные ориентиры, их не резать насквозь.\r\n" +
                "В CorelDRAW слои ENGRAVE и INFO по умолчанию непечатаемые; CUT — красный, 0,01 мм.\r\n" +
                "Масштаб 1:1. Перед полным комплектом проверьте короткий паз и перемычку на выбранном пластике.\r\n",
                new UTF8Encoding(true));
        }

        // Native Corel curves, not a raster or an SVG import: one page per sheet.
        public static object CreateInCorel(SleeveJigPlan p) => CreateInCorel((object)CorelApp.Get(), p);
        public static object CreateInCorel(object application, SleeveJigPlan p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            dynamic app = application, doc = null;
            bool optimized = Convert.ToBoolean(app.Optimization), begun = false, succeeded = false;
            try
            {
                doc = app.CreateDocument();
                doc.Unit = CorelConstants.CdrMillimeter;
                doc.Rulers.HUnits = CorelConstants.CdrMillimeter;
                doc.Rulers.VUnits = CorelConstants.CdrMillimeter;
                app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools · пластиковый шаблон " + p.Title); begun = true;
                for (int sheet = 0; sheet < 2; sheet++)
                {
                    dynamic page = sheet == 0 ? doc.ActivePage : doc.AddPages(1);
                    page.Activate(); page.Name = p.Title + (sheet == 0 ? " · нижний" : " · верхний");
                    page.SetSize(p.PageWidth, p.PageHeight);
                    double left = 0, bottom = 0, width = 0, height = 0;
                    page.GetBoundingBox(ref left, ref bottom, ref width, ref height);
                    double x = left + p.Origin, y = bottom + height - p.Origin;
                    dynamic cut = page.CreateLayer("CUT · резка пластика"), engrave = page.CreateLayer("ENGRAVE · ориентиры"),
                        info = page.CreateLayer("INFO · подписи");
                    cut.Printable = true;
                    engrave.Printable = false; info.Printable = false;
                    foreach (SleeveJigLine line in p.Cuts) Curve(doc, cut, line, x, y, true);
                    foreach (SleeveJigLine line in p.Engraving) Curve(doc, engrave, line, x, y, false);
                    dynamic title = info.CreateArtisticTextWide(0, 0,
                        p.Title + (sheet == 0 ? " · нижний лист" : " · верхний лист") + " · пластик " + N(p.Options.PlasticThickness) +
                        " мм\rПаз " + N(p.SlotWidth) + " мм · рез " + N(p.Options.Kerf) + " мм · масштаб 1:1",
                        0, -1, "Arial", 8f);
                    title.Fill.UniformColor.RGBAssign(0, 90, 160); title.Outline.SetNoOutline();
                    title.Move(x + 18 - Convert.ToDouble(title.LeftX), y - 2 - Convert.ToDouble(title.TopY));
                    title.Name = "Параметры — не резать";
                    cut.Activate();
                }
                doc.Pages[1].Activate();
                succeeded = true;
                return (object)doc;
            }
            finally
            {
                if (begun) try { doc.EndCommandGroup(); } catch (Exception e) { Log.Error("Jig command group failed.", e); }
                if (!succeeded && doc != null)
                    try { doc.Dirty = false; doc.Close(); } catch (Exception e) { Log.Error("Could not close incomplete jig document.", e); }
                app.Optimization = optimized;
                if (!optimized) try { app.ActiveWindow.Refresh(); } catch { }
            }
        }

        private static void Curve(dynamic doc, dynamic layer, SleeveJigLine line, double x, double y, bool cut)
        {
            dynamic curve = doc.CreateCurve();
            foreach (PathFigure figure in line.Geometry.Figures)
            {
                dynamic path = curve.CreateSubPath(x + figure.StartPoint.X, y - figure.StartPoint.Y);
                foreach (var point in SleeveJigPlan.Points(figure)) path.AppendLineSegment(x + point.X, y - point.Y);
                path.Closed = figure.IsClosed;
            }
            dynamic shape = layer.CreateCurve(curve);
            shape.Fill.ApplyNoFill(); shape.Outline.Width = cut ? .01 : .1;
            shape.Outline.Color.RGBAssign(cut ? 255 : 0, cut ? 0 : 100, cut ? 0 : 200);
            shape.Name = line.Name;
        }
    }
}
