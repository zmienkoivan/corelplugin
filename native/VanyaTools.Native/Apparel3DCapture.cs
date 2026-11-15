using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class ApparelOrderSnapshot
    {
        public ApparelViewMap Front, Back;
        public string SourceName;
    }
    internal sealed class ApparelShapeBox
    {
        public object Source;
        public int Id, Type;
        public double UnitScale = 1;
        public Rect Bounds;
    }
    internal sealed class ApparelViewCandidate
    {
        public ApparelShapeBox Group, Body, Left, Right;
        public double NeckDepth;
    }
    internal sealed class Apparel3DSource
    {
        private readonly object _application, _document, _page;
        private readonly int[] _viewIds;
        private Apparel3DSource(object app, object doc, object page, int[] ids)
        { _application = app; _document = doc; _page = page; _viewIds = ids; }

        public static Apparel3DSource Bind() => Bind((object)CorelApp.Get());
        internal static Apparel3DSource Bind(object application)
        {
            dynamic app = application, doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте CDR заказа с передом и спинкой футболки.");
            dynamic selected = app.ActiveSelectionRange;
            var ids = new List<int>();
            if (selected != null && Convert.ToInt32(selected.Count) == 2)
                foreach (dynamic shape in selected) if (Convert.ToInt32(shape.Type) == CorelConstants.CdrGroupShape) ids.Add(Convert.ToInt32(shape.StaticID));
            return new Apparel3DSource(application, (object)doc, (object)doc.ActivePage, ids.Count == 2 ? ids.ToArray() : null);
        }

        public ApparelOrderSnapshot Capture()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Обновляйте 3D-примерку из интерфейса CorelDRAW.");
            dynamic app = _application, doc = _document, page = _page;
            object oldSelection = null;
            bool? sourceDirty = null;
            string path = Path.Combine(Path.GetTempPath(), "Vanya-Apparel3D-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                RequireSource();
                oldSelection = CopyRange((object)app, (object)app.ActiveSelectionRange);
                sourceDirty = Convert.ToBoolean(app.ActiveDocument.Dirty);
                // Convert measurements without changing document units.
                double unitScale = Convert.ToDouble(_application.GetType().InvokeMember("ConvertUnits", BindingFlags.InvokeMethod, null,
                    _application, new object[] { 1d, Convert.ToInt32(doc.Unit), CorelConstants.CdrMillimeter }, CultureInfo.InvariantCulture));
                var tops = new List<ApparelShapeBox>();
                foreach (dynamic shape in page.Shapes)
                {
                    try { if (!(bool)shape.Layer.Visible || !(bool)shape.Layer.Printable) continue; } catch { }
                    tops.Add(Box((object)shape, unitScale));
                }
                var candidates = new List<ApparelViewCandidate>(); int visited = 0;
                foreach (var top in tops.Where(s => _viewIds == null || _viewIds.Contains(s.Id))) Find(top, candidates, 0, ref visited);
                if (candidates.Count != 2)
                    throw new InvalidOperationException("Нужны две группы видов футболки с отдельными контурами корпуса и двух рукавов. " +
                        "Выделите группы переда и спинки и нажмите «Взять заказ из Corel». Обнаружено видов: " + candidates.Count + ".");
                candidates.Sort((a, b) => a.Group.Bounds.Left.CompareTo(b.Group.Bounds.Left));
                dynamic all = app.CreateShapeRange(); foreach (var top in tops) all.Add((dynamic)top.Source);
                if (Convert.ToInt32(all.Count) == 0) throw new InvalidOperationException("На странице нет печатаемых объектов.");
                all.CreateSelection();
                double left = 0, bottom = 0, width = 0, height = 0;
                all.GetBoundingBox(ref left, ref bottom, ref width, ref height, true);
                left *= unitScale; bottom *= unitScale; width *= unitScale; height *= unitScale;
                var bounds = new Rect(left, -bottom - height, width, height);
                ValidateBounds(bounds);
                if (width < .1 || height < .1) throw new InvalidOperationException("Пустая область заказа.");
                double pixelsPerMm = Math.Min(4096 / Math.Max(width, height), Math.Sqrt(12000000 / (width * height)));
                int dpi = Math.Max(1, Math.Min(300, (int)Math.Floor(pixelsPerMm * 25.4 * .98)));
                int px = (int)Math.Ceiling(width * dpi / 25.4), py = (int)Math.Ceiling(height * dpi / 25.4);
                if (px > 4096 || py > 4096 || (long)px * py > 12000000)
                    throw new InvalidOperationException("Страница слишком велика для примерки. Разместите пару видов на отдельной странице.");
                object options = (object)app.CreateStructExportOptions();
                Set(options, "SizeX", px); Set(options, "SizeY", py); Set(options, "ResolutionX", dpi); Set(options, "ResolutionY", dpi);
                Set(options, "ImageType", CorelConstants.CdrRgbColorImage); Set(options, "AntiAliasingType", CorelConstants.CdrNormalAntiAliasing);
                Set(options, "Transparent", false); Set(options, "Overwrite", true); Set(options, "UseColorProfile", true);
                RequireSource();
                object filter = _document.GetType().InvokeMember("ExportEx", BindingFlags.InvokeMethod, null, _document,
                    new object[] { new BStrWrapper(path), CorelConstants.CdrPng, CorelConstants.CdrSelection, options, (object)app.CreateStructPaletteOptions() });
                if (filter == null) throw new InvalidOperationException("CorelDRAW не открыл фильтр PNG для примерки.");
                filter.GetType().InvokeMember("Finish", BindingFlags.InvokeMethod, null, filter, null);
                RequireSource();
                BitmapSource image;
                using (var stream = File.OpenRead(path))
                {
                    var header = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (Math.Abs(header.PixelWidth - px) > 2 || Math.Abs(header.PixelHeight - py) > 2)
                        throw new InvalidOperationException("Размер PNG не совпал с областью заказа. Повторите обновление.");
                    stream.Position = 0;
                    image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Freeze();
                }
                return new ApparelOrderSnapshot { Front = View(candidates[0], image, bounds, "Перед"),
                    Back = View(candidates[1], image, bounds, "Спинка"), SourceName = Convert.ToString(doc.Name) + " · " + Convert.ToString(page.Name) };
            }
            catch (Exception error)
            {
                if (error is InvalidOperationException) throw;
                throw new InvalidOperationException("Не удалось прочитать CDR заказа. Проверьте, что исходный файл и страница открыты. " + error.GetBaseException().Message, error);
            }
            finally
            {
                try
                {
                    if (oldSelection != null)
                    {
                        RequireSource(); doc.ClearSelection();
                        if (Convert.ToInt32(((dynamic)oldSelection).Count) > 0) ((dynamic)oldSelection).CreateSelection();
                        if (sourceDirty.HasValue) app.ActiveDocument.Dirty = sourceDirty.Value;
                    }
                }
                catch (Exception error) { Log.Error("Could not restore source selection after 3D capture.", error); }
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
        private void RequireSource()
        {
            dynamic app = _application;
            if (Convert.ToInt32(app.ActiveDocument.Index) != Convert.ToInt32(((dynamic)_document).Index) ||
                !String.Equals(Convert.ToString(app.ActiveDocument.FullFileName), Convert.ToString(((dynamic)_document).FullFileName), StringComparison.OrdinalIgnoreCase) ||
                Convert.ToInt32(app.ActivePage.Index) != Convert.ToInt32(((dynamic)_page).Index))
                throw new InvalidOperationException("Вернитесь к исходному CDR и странице заказа, затем нажмите «Обновить». Для другого заказа используйте «Взять заказ из Corel».");
        }
        private static object CopyRange(object app, object range)
        {
            dynamic copy = ((dynamic)app).CreateShapeRange();
            if (range != null) foreach (dynamic shape in (dynamic)range) copy.Add(shape);
            return (object)copy;
        }
        private static void Find(ApparelShapeBox group, List<ApparelViewCandidate> found, int depth, ref int visited)
        {
            if (group.Type != CorelConstants.CdrGroupShape || depth > 4 || ++visited > 200) return;
            dynamic shapes = ((dynamic)group.Source).Shapes;
            int count = Convert.ToInt32(shapes.Count);
            // Illustration groups often contain hundreds of tiny paths. Do not
            // mistake their bounding boxes for a garment or traverse all nodes.
            if (count < 3 || count > 40) return;
            var children = new List<ApparelShapeBox>(); foreach (dynamic shape in shapes) children.Add(Box((object)shape, group.UnitScale));
            var candidate = Detect(group, children);
            if (candidate != null) { found.Add(candidate); return; }
            foreach (var child in children) Find(child, found, depth + 1, ref visited);
        }
        internal static ApparelViewCandidate Detect(ApparelShapeBox group, IList<ApparelShapeBox> children)
        {
            var g = group.Bounds;
            if (g.Width < 100 || g.Height < 100) return null;
            var body = children.Where(s => s.Type == CorelConstants.CdrCurveShape && s.Bounds.Height >= g.Height * .75 &&
                s.Bounds.Width >= g.Width * .4 && s.Bounds.Width <= g.Width * .85).OrderByDescending(s => s.Bounds.Height).FirstOrDefault();
            if (body == null) return null;
            double center = body.Bounds.X + body.Bounds.Width / 2;
            var sleeves = children.Where(s => s.Type == CorelConstants.CdrCurveShape && s.Bounds.Height > g.Height * .15 &&
                s.Bounds.Height < g.Height * .55 && s.Bounds.Width > g.Width * .12 && s.Bounds.Width < g.Width * .4).ToArray();
            var left = sleeves.Where(s => s.Bounds.X + s.Bounds.Width / 2 < body.Bounds.Left).OrderByDescending(s => s.Bounds.Width).FirstOrDefault();
            var right = sleeves.Where(s => s.Bounds.X + s.Bounds.Width / 2 > body.Bounds.Right).OrderByDescending(s => s.Bounds.Width).FirstOrDefault();
            if (left == null || right == null) return null;
            double neck = children.Where(s => s.Type == CorelConstants.CdrCurveShape && s.Bounds.Width > body.Bounds.Width * .18 &&
                s.Bounds.Width < body.Bounds.Width * .5 && Math.Abs(s.Bounds.X + s.Bounds.Width / 2 - center) < body.Bounds.Width * .1 &&
                s.Bounds.Top < body.Bounds.Top + body.Bounds.Height * .1 && s.Bounds.Height < body.Bounds.Height * .22)
                .Select(s => s.Bounds.Height).DefaultIfEmpty(body.Bounds.Height * .08).Max();
            return new ApparelViewCandidate { Group = group, Body = body, Left = left, Right = right, NeckDepth = neck };
        }
        private static ApparelShapeBox Box(object source, double scale)
        {
            dynamic s = source;
            var box = new Rect(Convert.ToDouble(s.LeftX) * scale, -Convert.ToDouble(s.TopY) * scale,
                Convert.ToDouble(s.SizeWidth) * scale, Convert.ToDouble(s.SizeHeight) * scale);
            ValidateBounds(box);
            return new ApparelShapeBox { Source = source, Id = Convert.ToInt32(s.StaticID), Type = Convert.ToInt32(s.Type), Bounds = box, UnitScale = scale };
        }
        private static ApparelViewMap View(ApparelViewCandidate c, BitmapSource image, Rect all, string name)
        {
            var view = c.Group.Bounds;
            int x = Math.Max(0, (int)Math.Floor((view.X - all.X) / all.Width * image.PixelWidth));
            int y = Math.Max(0, (int)Math.Floor((view.Y - all.Y) / all.Height * image.PixelHeight));
            int right = Math.Min(image.PixelWidth, (int)Math.Ceiling((view.Right - all.X) / all.Width * image.PixelWidth));
            int bottom = Math.Min(image.PixelHeight, (int)Math.Ceiling((view.Bottom - all.Y) / all.Height * image.PixelHeight));
            if (right <= x || bottom <= y) throw new InvalidOperationException("Вид футболки выходит за область изображения.");
            var cropped = new CroppedBitmap(image, new Int32Rect(x, y, right - x, bottom - y)); cropped.Freeze();
            // Normalise against the actual rounded crop, not an ideal rectangle.
            var actual = new Rect(all.X + x * all.Width / image.PixelWidth, all.Y + y * all.Height / image.PixelHeight,
                cropped.PixelWidth * all.Width / image.PixelWidth, cropped.PixelHeight * all.Height / image.PixelHeight);
            Point Uv(Point p) => new Point((p.X - actual.X) / actual.Width, (p.Y - actual.Y) / actual.Height);
            return new ApparelViewMap { Name = name, Image = cropped, BodyWidthMm = c.Body.Bounds.Width, BodyHeightMm = c.Body.Bounds.Height,
                NeckDepthMm = c.NeckDepth, Body = new Rect(Uv(c.Body.Bounds.TopLeft), Uv(c.Body.Bounds.BottomRight)),
                ImageLeft = Sleeve(c.Left, c.Body.Bounds, Uv), ImageRight = Sleeve(c.Right, c.Body.Bounds, Uv) };
        }
        private static ApparelSleeveMap Sleeve(ApparelShapeBox shape, Rect body, Func<Point, Point> uv)
        {
            dynamic paths = ((dynamic)shape.Source).DisplayCurve.SubPaths;
            if (Convert.ToInt32(paths.Count) != 1) throw new InvalidOperationException("Рукав должен иметь один внешний контур.");
            dynamic path = paths[1]; var nodes = new List<Point>();
            foreach (dynamic node in path.Nodes) nodes.Add(new Point(Convert.ToDouble(node.PositionX) * shape.UnitScale, -Convert.ToDouble(node.PositionY) * shape.UnitScale));
            return SleeveCorners(nodes, body.X + body.Width / 2, uv);
        }
        internal static ApparelSleeveMap SleeveCorners(IList<Point> nodes, double bodyCenter, Func<Point, Point> uv)
        {
            if (nodes.Count != 4) throw new InvalidOperationException("В первой версии нужен рукав с четырьмя угловыми узлами, как в шаблоне заказа.");
            var ordered = nodes.OrderBy(p => Math.Abs(p.X - bodyCenter)).ToArray();
            var root = ordered.Take(2).OrderBy(p => p.Y).ToArray();
            var cuff = ordered.Skip(2).OrderBy(p => p.Y).ToArray();
            return new ApparelSleeveMap { RootTop = uv(root[0]), RootBottom = uv(root[1]), CuffTop = uv(cuff[0]), CuffBottom = uv(cuff[1]) };
        }
        private static void ValidateBounds(Rect r)
        {
            foreach (double d in new[] { r.X, r.Y, r.Width, r.Height }) if (Double.IsNaN(d) || Double.IsInfinity(d)) throw new InvalidOperationException("Некорректные координаты объектов заказа.");
            if (r.Width < 0 || r.Height < 0) throw new InvalidOperationException("Некорректные размеры объектов заказа.");
        }
        private static void Set(object target, string property, object value) => target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);
    }
}
