using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace VanyaTools.Native
{
    internal static class AcrylicTrayOutput
    {
        private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
        private const double Margin = 10;
        private static string N(double v) { return AcrylicTrayPattern.N(v); }

        public static XDocument Layout(AcrylicTrayPattern p, bool labels)
        {
            return Layout(AcrylicTrayCutLayout.Build(p), labels);
        }
        private static XDocument Layout(AcrylicTrayCutLayout layout, bool labels)
        {
            var root = new XElement(Svg + "svg");
            var cuts = new XElement(Svg + "g", new XAttribute("id", "cut"),
                new XAttribute("fill", "none"), new XAttribute("stroke", "#ff0000"),
                new XAttribute("stroke-width", "0.01"), new XAttribute("fill-rule", "evenodd"));
            var notes = new XElement(Svg + "g", new XAttribute("id", "labels-do-not-cut"),
                new XAttribute("fill", "#315b78"), new XAttribute("font-family", "Arial"),
                new XAttribute("font-size", "3"));
            root.Add(cuts);
            if (labels) root.Add(notes);
            double legendX = layout.Width + 4;
            foreach (var placement in layout.Parts)
            {
                var part = placement.Part; var m = placement.Transform;
                cuts.Add(new XElement(Svg + "path", new XAttribute("id", part.Id),
                    new XAttribute("d", part.Path), new XAttribute("transform", "matrix(" + N(m.M11) + " " + N(m.M12) + " " +
                        N(m.M21) + " " + N(m.M22) + " " + N(m.OffsetX) + " " + N(m.OffsetY) + ")")));
                if (labels)
                {
                    double y = 4 + (placement.Number - 1) * 9;
                    Text(notes, legendX, y, placement.Number + ". " + part.Name);
                    Text(notes, legendX, y + 4, N(part.Width) + " × " + N(part.Height) + " мм");
                    var center = LabelPoint(placement.Geometry);
                    notes.Add(new XElement(Svg + "circle", new XAttribute("cx", N(center.X)), new XAttribute("cy", N(center.Y)),
                        new XAttribute("r", "2"), new XAttribute("fill", "white"), new XAttribute("stroke", "#315b78"), new XAttribute("stroke-width", "0.15")));
                    Text(notes, center.X - placement.Number.ToString(CultureInfo.InvariantCulture).Length * 0.8, center.Y + 1, placement.Number.ToString(CultureInfo.InvariantCulture));
                }
            }
            SetSize(root, layout.Width + (labels ? 105 : 0), Math.Max(layout.Height, labels ? layout.Parts.Count * 9 + 6 : 0));
            root.AddFirst(new XElement(Svg + "title", labels ? "Карта деталей лотка — не для резки" : "Лоток — замкнутые контуры резки 1:1, мм"));
            return new XDocument(root);
        }
        private static System.Windows.Point LabelPoint(System.Windows.Media.Geometry geometry)
        {
            var b = geometry.Bounds; var center = new System.Windows.Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            if (geometry.FillContains(center)) return center;
            var best = center; double distance = Double.PositiveInfinity;
            for (int y = 1; y < 20; y++) for (int x = 1; x < 20; x++)
            {
                var point = new System.Windows.Point(b.X + b.Width * x / 20, b.Y + b.Height * y / 20);
                if ((point - center).LengthSquared < distance && geometry.FillContains(point))
                { best = point; distance = (point - center).LengthSquared; }
            }
            return best;
        }

        public static XDocument Assembly(AcrylicTrayPattern p)
        {
            double t = p.Options.Thickness, h = p.Options.BoardHeight;
            double scaleWidth = Math.Max(180, Math.Max(p.Depth, p.OverallWidth));
            double planY = Margin + p.Height + 22;
            var root = new XElement(Svg + "svg");
            SetSize(root, scaleWidth + 2 * Margin, planY + p.Depth + 20);
            root.Add(new XElement(Svg + "title", "Лоток — боковой разрез и план. Схема сборки, не для резки."));
            Text(root, Margin, 6, "Боковой разрез · перегородки вертикальные");
            var side = new XElement(Svg + "g", new XAttribute("transform", "translate(" +
                N(Margin - p.Floors[0].StartY) + " " + N(Margin + p.Height) + ")"));
            root.Add(side);
            side.Add(new XElement(Svg + "path", new XAttribute("d", AcrylicTrayPattern.SvgPath(p.Parts[0].AssemblyGeometry)),
                new XAttribute("fill", "#d7f1f3"), new XAttribute("fill-rule", "evenodd"),
                new XAttribute("stroke", "#487985"), new XAttribute("stroke-width", "0.3")));
            foreach (var f in p.Floors)
            {
                string points = N(f.StartY) + "," + N(-f.FrontZ) + " " + N(f.EndY) + "," + N(-f.Top(f.EndY)) + " " +
                    N(f.EndY + t * f.Sine) + "," + N(-f.Top(f.EndY) + t * f.Cosine) + " " +
                    N(f.StartY + t * f.Sine) + "," + N(-f.FrontZ + t * f.Cosine);
                side.Add(new XElement(Svg + "polygon", new XAttribute("points", points), new XAttribute("fill", "#a855db")));
            }
            foreach (var cross in p.Crosses)
                side.Add(new XElement(Svg + "rect", new XAttribute("x", N(cross.Y - t / 2)),
                    new XAttribute("y", N(-cross.Bottom - cross.Height)), new XAttribute("width", N(t)),
                    new XAttribute("height", N(cross.Height)), new XAttribute("fill", "#20c878")));
            Text(root, Margin, planY - 4, "План · размеры ячеек в горизонтальной проекции");
            var plan = new XElement(Svg + "g", new XAttribute("transform", "translate(" + N(Margin + t) + " " + N(planY - p.Floors[0].StartY) + ")"));
            root.Add(plan);
            foreach (var f in p.Floors)
                Rect(plan, 0, f.StartY, p.InnerWidth, f.EndY - f.StartY, "#f0e6fa");
            Rect(plan, -t, p.Floors[0].StartY, t, p.Depth, "#a7dfe4");
            Rect(plan, p.InnerWidth, p.Floors[0].StartY, t, p.Depth, "#a7dfe4");
            foreach (double v in p.Longitudinals)
                Rect(plan, v - t / 2, p.Floors[0].StartY, t, p.Depth, "#a7dfe4");
            foreach (var cross in p.Crosses) Rect(plan, 0, cross.Y - t / 2, p.InnerWidth, t, "#20c878");
            Text(root, Margin, planY + p.Depth + 9, "Ячейка: " + N(p.Options.CellWidth) + " × " + N(p.Options.CellDepth) +
                " мм · борт " + N(h) + " мм · акрил " + N(t) + " мм");
            return new XDocument(root);
        }

        public static string Save(string parent, AcrylicTrayPattern p)
        {
            string folder = Path.Combine(parent, "Лоток-" + p.Options.Columns + "x" + p.Options.Rows + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            string incomplete = Path.Combine(folder, "НЕ_ЗАВЕРШЕНО.txt");
            File.WriteAllText(incomplete, "Экспорт не завершён. Повторите сохранение комплекта.");
            var layout = AcrylicTrayCutLayout.Build(p);
            Layout(layout, false).Save(Path.Combine(folder, "01-cut.svg"));
            Layout(layout, true).Save(Path.Combine(folder, "02-parts-map.svg"));
            Assembly(p).Save(Path.Combine(folder, "03-assembly.svg"));
            File.WriteAllText(Path.Combine(folder, "parameters.json"), new JavaScriptSerializer().Serialize(new
            {
                schema = 1, product = "acrylic-divider-tray", options = p.Options,
                cellDimensions = "internal-horizontal-projection-mm", widthMm = p.OverallWidth,
                depthMm = p.OverallDepth, heightMm = p.Height, crossFloorSlotMm = p.CrossSlotWidth,
                cutLayout = new { gapMm = AcrylicTrayCutLayout.Gap, widthMm = layout.Width, heightMm = layout.Height },
                fixing = p.Options.UseWedges ? "same-acrylic-mushroom-keys" : "glue",
                sideLug = p.Options.UseWedges ? new { projectionMm = p.SideLugProjection, widthMm = p.SideLugWidth,
                    radiusMm = p.SideLugRadius, materialWebMm = p.Options.Thickness,
                    windowThicknessMm = p.Options.Thickness + p.Options.RetainerThicknessClearance, windowLengthMm = p.Options.RetainerWindowLength } : null,
                wedges = p.Wedges.Select(w => new { id = w.Id, parent = w.ParentId, floor = w.FloorIndex + 1, tab = w.TabIndex + 1, side = w.Side,
                    headWidthMm = w.HeadWidth, heightMm = w.Shape.Bounds.Width, thicknessMm = p.Options.Thickness,
                    headHeightMm = w.HeadLength, gripWindowWidthMm = w.GripWidth, gripWindowHeightMm = w.GripHeight,
                    tipWidthMm = w.TipWidth, neckWidthMm = w.NeckWidth }).ToArray(),
                parts = p.Parts.Select((v, i) => new { number = i + 1, id = v.Id, name = v.Name, widthMm = v.Width, heightMm = v.Height }).ToArray()
            }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "СБОРКА.txt"), Instructions(p), new UTF8Encoding(true));
            File.Delete(incomplete);
            return folder;
        }

        private static string Instructions(AcrylicTrayPattern p)
        {
            var o = p.Options;
            var b = new StringBuilder();
            b.AppendLine("АКРИЛОВЫЙ ЛОТОК С РАЗДЕЛИТЕЛЯМИ");
            b.AppendLine("Конструкция: " + (o.FloorMode == AcrylicTrayFloorMode.Plane
                ? "одно плоское дно, угол " + N(p.Angle) + "°" : "отдельные ступени, подъём ряда " + N(o.RowRise) + " мм"));
            b.AppendLine(o.Columns + " столбцов × " + o.Rows + " рядов; ячейка внутри " + N(o.CellWidth) + " × " + N(o.CellDepth) + " мм.");
            b.AppendLine("Глубина ячейки измеряется по горизонтали; на наклонном дне её длина больше в 1/cos(угла) раз.");
            b.AppendLine("Высота борта по вертикали: " + N(o.BoardHeight) + " мм; толщина акрила: " + N(o.Thickness) + " мм.");
            b.AppendLine("Последняя задняя стенка: " + N(o.RearWallHeight) + " мм от её нижней кромки. Высота остальных бортов сохраняется.");
            b.AppendLine("Боковины: " + (o.DecorativeSides ? "витринные, с плавными уступами и скруглениями" : "прямой профиль"));
            b.AppendLine("Габариты с боковинами: " + N(p.OverallWidth) + " × " + N(p.OverallDepth) + " × " + N(p.Height) + " мм.");
            b.AppendLine("Зазор пазов (полный): " + N(o.SlotClearance) + " мм; это не компенсация ширины лазерного реза.");
            if (o.UseWedges) b.AppendLine("Разборная сборка: " + p.Wedges.Count + " фиксаторов-грибочков из того же акрила. Шаг " + o.WedgeEvery + " шипа, первый и последний шипы фиксируются всегда.");
            if (o.UseWedges)
            {
                var sideKey = p.Wedges.FirstOrDefault(key => key.Side);
                if (sideKey != null) b.AppendLine("Боковой грибочек: шляпка " + N(sideKey.HeadWidth) + " мм, полная высота " + N(sideKey.Shape.Bounds.Width) + " мм. Цельная жёсткая деталь; удержание самого грибочка — посадкой в окошке.");
                if (sideKey != null) b.AppendLine("В шляпке грибочка сквозное окошко для захвата " + N(sideKey.GripWidth) + " × " + N(sideKey.GripHeight) + " мм со скруглёнными концами. Перемычки вокруг окошка не тоньше листа.");
            }
            if (o.UseWedges) b.AppendLine("Ушко дна: выступ за боковину " + N(p.SideLugProjection) + " мм, ширина " + N(p.SideLugWidth) +
                " мм, скругление " + N(p.SideLugRadius) + " мм. Перемычки у свободных краёв не тоньше " + N(o.Thickness) + " мм. Ушко вырезается вместе с дном.");
            if (o.UseWedges) b.AppendLine("Окно в ушке под широкую ножку грибочка: " + N(o.Thickness + o.RetainerThicknessClearance) + " × " + N(o.RetainerWindowLength) +
                " мм. На поперечных перегородках наклонный проход по толщине увеличивается по углу дна. Это отдельное окно от паза под сам шип в дне.");
            b.AppendLine("Паз поперечной полоски в дне: " + N(p.CrossSlotWidth) + " мм вдоль наклона.");
            b.AppendLine("Расчёт учитывает t/cos(угла) + t·tan(угла), толщину дна и смещение центра отверстия.");
            b.AppendLine();
            b.AppendLine("01-cut.svg: только замкнутые красные контуры резки, мм, 1:1. Зазор между деталями 3 мм.");
            b.AppendLine("Сверху боковины и повёрнутые продольные перегородки уложены по контуру, ниже — поперечные полоски стопкой, затем дно.");
            b.AppendLine("02-parts-map.svg: та же раскладка с номерами; подписи не резать.");
            b.AppendLine("03-assembly.svg: боковой разрез и план, не для резки.");
            b.AppendLine();
            b.AppendLine("1. Проверьте толщину своего листа. Сделайте пробу посадки шипа/паза; компенсацию реза задайте в программе лазера.");
            b.AppendLine("2. Совместите верхние пазы продольных перегородок с нижними пазами поперечных полосок.");
            b.AppendLine("3. Вставьте нижние шипы всех внутренних полосок в ответные отверстия дна. Наклонное дно одно, перегородки вертикальны.");
            b.AppendLine("4. Добавьте боковины: в них входят боковые шипы дна и торцевые шипы поперечных полосок. Нижняя грань боковин стоит на столе.");
            if (o.UseWedges)
            {
                b.AppendLine("5. В окошки нижних шипов вставьте цельные грибочки ножкой вперёд вдоль дна. Их широкая плоскость параллельна дну; толщина грибочка удерживает дно на шипе. Короткая ножка слегка расширяется к шляпке. Сам грибочек удерживается посадкой в окошке.");
                b.AppendLine("6. В окошки боковых шипов дна вставьте оставшиеся грибочки сверху. Плоскость шляпки параллельна боковине, толщина фиксатора упирается в наружную грань стенки и удерживает её от раздвижения. Клей не нужен.");
                b.AppendLine("Толщина грибочков равна толщине акрила лотка. Устанавливайте рукой, без ударов. Сначала вырежьте пробный стык под свой лист и ширину реза; акрил не должен изгибаться.");
                foreach (var wedge in p.Wedges) b.AppendLine("Грибочек " + wedge.Number + " → " + wedge.ParentId + ", шип " + (wedge.TabIndex + 1) + (wedge.Side ? " (боковой)" : " (под дном)"));
            }
            else b.AppendLine("5. Проверьте геометрию без клея, затем зафиксируйте клеем для выбранного акрила. При наклоне прямые поперечные шипы немного выступают снизу.");
            b.AppendLine();
            for (int i = 0; i < p.Parts.Count; i++)
                b.AppendLine((i + 1) + ". " + p.Parts[i].Name + ": " + N(p.Parts[i].Width) + " × " + N(p.Parts[i].Height) + " мм");
            return b.ToString();
        }

        public static void OpenInCorel(string path)
        {
            object app = CorelApp.Get();
            app.GetType().InvokeMember("OpenDocument", BindingFlags.InvokeMethod, null, app,
                new object[] { new BStrWrapper(Path.GetFullPath(path)), 0 });
        }
        private static void SetSize(XElement root, double width, double height)
        {
            root.SetAttributeValue("version", "1.1");
            root.SetAttributeValue("width", N(width) + "mm"); root.SetAttributeValue("height", N(height) + "mm");
            root.SetAttributeValue("viewBox", "0 0 " + N(width) + " " + N(height));
        }
        private static void Text(XElement root, double x, double y, string value)
        {
            root.Add(new XElement(Svg + "text", new XAttribute("x", N(x)), new XAttribute("y", N(y)),
                new XAttribute("font-family", "Arial"), new XAttribute("font-size", "3"), value));
        }
        private static void Rect(XElement root, double x, double y, double w, double h, string color)
        {
            root.Add(new XElement(Svg + "rect", new XAttribute("x", N(x)), new XAttribute("y", N(y)),
                new XAttribute("width", N(w)), new XAttribute("height", N(h)), new XAttribute("fill", color)));
        }
    }
}
