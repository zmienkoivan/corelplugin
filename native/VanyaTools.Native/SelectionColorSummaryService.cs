using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace VanyaTools.Native
{
    internal sealed class SelectionColorSummaryResult
    {
        public string Message { get; internal set; }
        public string Text { get; internal set; }
        public string UsageText { get; internal set; }
        public int ColorCount { get; internal set; }
        public int ApproximateRasterCount { get; internal set; }
        public bool Incomplete { get; internal set; }
    }

    /// <summary>Reads native colors without exporting, selecting, duplicating or changing source shapes.</summary>
    internal sealed class SelectionColorSummaryService
    {
        private const int VectorLines = 24, RasterColors = 8, SampleAxis = 12;
        private const int MaxShapes = 10000, MaxCharacters = 5000, MaxRasters = 16, MaxDepth = 100;

        public SelectionColorSummaryResult InsertSummary()
        { RequireUiThread(); return InsertSummary((object)CorelApp.Get()); }

        // Kept injectable for checks: tests supply plain fake objects, never a live Corel instance.
        internal SelectionColorSummaryResult InsertSummary(object application)
        {
            RequireUiThread();
            if (application == null) throw new InvalidOperationException("CorelDRAW недоступен.");
            dynamic app = application, doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ и выделите объекты.");
            object sourceDocument = (object)doc;
            dynamic selected = app.ActiveSelectionRange;
            if (selected == null || Count((object)selected) == 0)
                throw new InvalidOperationException("Выделите объекты, для которых нужна сводка цветов.");
            int oldUnit = Convert.ToInt32(doc.Unit);
            bool oldDirty = Convert.ToBoolean(doc.Dirty), started = false, succeeded = false, cleanupFailed = false;
            var created = new List<object>();
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double left, bottom, width, height;
                selected.GetBoundingBox(out left, out bottom, out width, out height, true);
                if (!Finite(left) || !Finite(bottom) || !Finite(width) || !Finite(height))
                    throw new InvalidOperationException("Не удалось определить границы выделения.");
                var collector = new Collector(application);
                collector.VisitCollection((object)selected, 0);
                SelectionColorSummaryResult result = collector.Result();
                if (result.ColorCount == 0 && result.ApproximateRasterCount == 0)
                    throw new InvalidOperationException("В выделении не удалось прочитать цвета. " + collector.WarningText());
                if (!SameObject((object)app.ActiveDocument, sourceDocument))
                    throw new InvalidOperationException("Активный документ изменился. Вернитесь в нужный CDR и повторите сводку.");
                doc.BeginCommandGroup("Vanya Tools — сводка цветов"); started = true;
                dynamic text = CreateText((object)doc, result.Text, left, bottom - 5,
                    "Vanya Tools · сводка цветов", created);
                if (!String.IsNullOrWhiteSpace(result.UsageText))
                {
                    if (!SameObject((object)app.ActiveDocument, sourceDocument))
                        throw new InvalidOperationException("Активный документ изменился. Вернитесь в нужный CDR и повторите сводку.");
                    CreateText((object)doc, result.UsageText, left, Convert.ToDouble(text.BottomY) - 2,
                        "Vanya Tools · заливки и контуры", created);
                }
                doc.EndCommandGroup(); started = false;
                succeeded = true;
                return result;
            }
            catch
            {
                for (int index = created.Count - 1; index >= 0; index--)
                    try { ((dynamic)created[index]).Delete(); }
                    catch (Exception error)
                    {
                        cleanupFailed = true;
                        Log.Error("Color summary rollback failed; the document remains modified.", error);
                        try { doc.Dirty = true; } catch { }
                    }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); }
                catch (Exception error) { Log.Error("Color summary command group failed.", error); }
                try { doc.Unit = oldUnit; }
                finally { if (!succeeded && !cleanupFailed) doc.Dirty = oldDirty; }
            }
        }

        private static object CreateText(object document, string value, double left, double top,
            string name, ICollection<object> created)
        {
            dynamic text = ((dynamic)document).ActiveLayer.CreateArtisticTextWide(0, 0, value, 0, -1, "Arial", 14f);
            created.Add((object)text);
            text.Fill.UniformColor.CMYKAssign(0, 0, 0, 100); text.Outline.SetNoOutline();
            text.Name = name;
            // Anchor the actual top-left, not the font baseline or document reference point.
            text.Move(left - Convert.ToDouble(text.LeftX), top - Convert.ToDouble(text.TopY));
            return (object)text;
        }

        private sealed class ColorValue
        {
            internal string Key, Text, Model, Warning;
            internal double[] Values;
            internal bool InFill, InOutline;
            internal string Bucket => Model + ":" + String.Join("/", Values.Select(value =>
                (Model == "BW" ? value : Math.Floor(value / (Model == "CMYK" || Model == "CMY" ? 5 : 16))).ToString(CultureInfo.InvariantCulture)));
        }

        private sealed class RasterBucket
        {
            internal ColorValue Color;
            internal double Weight;
        }

        private sealed class Collector
        {
            private readonly object _app;
            private readonly HashSet<int> _seen = new HashSet<int>();
            private readonly Dictionary<string, ColorValue> _vectors = new Dictionary<string, ColorValue>(StringComparer.Ordinal);
            private readonly Dictionary<string, RasterBucket> _raster = new Dictionary<string, RasterBucket>(StringComparer.Ordinal);
            private readonly SortedDictionary<string, int> _warnings = new SortedDictionary<string, int>(StringComparer.Ordinal);
            private int _objects, _rasters, _characters;
            private bool _gradients;

            internal Collector(object app) { _app = app; }

            internal void VisitCollection(object collection, int depth)
            {
                int count = Count(collection);
                for (int index = 1; index <= count; index++)
                {
                    if (_objects >= MaxShapes) { Warn("Достигнут предел " + MaxShapes + " объектов; часть выделения не проверена"); return; }
                    Read(() => Visit(Item(collection, index), depth), "Не удалось прочитать объект или его вложенные элементы");
                }
            }

            private void Visit(object shapeObject, int depth)
            {
                if (depth > MaxDepth) { Warn("Слишком глубокая вложенность; часть объектов не проверена"); return; }
                dynamic shape = shapeObject;
                int id = Convert.ToInt32(shape.StaticID);
                if (!_seen.Add(id)) return;
                _objects++;
                int type = Convert.ToInt32(shape.Type);
                if (type == CorelConstants.CdrGroupShape)
                    Read(() => VisitCollection((object)shape.Shapes, depth + 1), "Не удалось прочитать часть группы");
                else if (type == CorelConstants.CdrBitmapShape)
                {
                    if (_rasters >= MaxRasters) Warn("Растров больше " + MaxRasters + "; часть растров не проверена");
                    else { _rasters++; Read(() => Raster(shapeObject), "Не удалось прочитать основные цвета растра"); }
                    Read(() => Outline((object)shape.Outline), "Не удалось прочитать обводку растра");
                }
                else if (type == CorelConstants.CdrTextShape)
                    Read(() => Text((object)shape.Text.Story), "Не удалось полностью прочитать цвета текста");
                else
                {
                    Read(() => Fill((object)shape.Fill), "Не удалось прочитать заливку");
                    Read(() => Outline((object)shape.Outline), "Не удалось прочитать обводку");
                }
                object clip = null;
                try { clip = (object)shape.PowerClip; } catch (COMException) { }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                if (clip != null) Read(() => VisitCollection((object)((dynamic)clip).Shapes, depth + 1), "Не удалось прочитать часть PowerClip");
                try { if (Convert.ToInt32(shape.Effects.Count) > 0) Warn("Эффекты: указаны исходные цвета, без учёта результата эффекта"); }
                catch (COMException) { }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            }

            private void Read(Action action, string warning)
            {
                try { action(); }
                catch (Exception error) { Warn(warning); if (_warnings[warning] == 1) Log.Error("Color summary: " + warning, error); }
            }

            private void Text(object storyObject)
            {
                dynamic story = storyObject;
                object characters = (object)story.Characters;
                int count = Count(characters);
                int available = Math.Max(0, MaxCharacters - _characters);
                if (count > available) Warn("Достигнут предел " + MaxCharacters + " текстовых знаков; часть символов не проверена");
                for (int index = 1; index <= Math.Min(count, available); index++)
                {
                    _characters++;
                    dynamic character = Item(characters, index);
                    Read(() => Fill((object)character.Fill), "Не удалось прочитать заливку символа");
                    Read(() => Outline((object)character.Outline), "Не удалось прочитать обводку символа");
                }
            }

            private void Fill(object fillObject)
            {
                dynamic fill = fillObject;
                int type = Convert.ToInt32(fill.Type);
                if (type == 0) return;
                if (type == 1) { Vector((object)fill.UniformColor, false); return; }
                if (type != 2) { Warn("Сложные заливки не включены в список цветов"); return; }
                _gradients = true;
                dynamic fountain = fill.Fountain;
                Vector((object)fountain.StartColor, false); Vector((object)fountain.EndColor, false);
                object stops = (object)fountain.Colors;
                for (int index = 1; index <= Count(stops); index++) Vector((object)((dynamic)Item(stops, index)).Color, false);
            }

            private void Outline(object outlineObject)
            { dynamic outline = outlineObject; if (Convert.ToInt32(outline.Type) != 0) Vector((object)outline.Color, true); }

            private void Vector(object color, bool outline)
            {
                ColorValue value = Snapshot(_app, color);
                if (value.Warning != null) Warn(value.Warning);
                if (!_vectors.TryGetValue(value.Key, out ColorValue existing)) _vectors.Add(value.Key, existing = value);
                if (outline) existing.InOutline = true;
                else existing.InFill = true;
            }

            private void Raster(object shapeObject)
            {
                dynamic shape = shapeObject, bitmap = shape.Bitmap, image = bitmap.Image;
                int width = Convert.ToInt32(image.Width), height = Convert.ToInt32(image.Height);
                if (width <= 0 || height <= 0) throw new InvalidOperationException("Corel вернул пустой растр.");
                object alpha = null;
                if (Convert.ToBoolean(bitmap.Transparent))
                {
                    alpha = (object)bitmap.ImageAlpha;
                    if (alpha == null || Convert.ToInt32(((dynamic)alpha).Width) != width || Convert.ToInt32(((dynamic)alpha).Height) != height)
                        throw new InvalidOperationException("Не удалось совместить прозрачность растра с его пикселями.");
                }
                int columns = Math.Min(SampleAxis, width), rows = Math.Min(SampleAxis, height);
                double area = Convert.ToDouble(shape.SizeWidth) * Convert.ToDouble(shape.SizeHeight);
                if (!Finite(area) || area <= 0) area = 1;
                var local = new Dictionary<string, RasterBucket>(StringComparer.Ordinal);
                for (int row = 0; row < rows; row++) for (int column = 0; column < columns; column++)
                {
                    // Corel BitmapImage pixels use zero-based bottom-origin coordinates.
                    int x = Math.Min(width - 1, (int)((column + 0.5) * width / columns));
                    int y = Math.Min(height - 1, (int)((row + 0.5) * height / rows));
                    int opacity = 255;
                    if (alpha != null)
                    {
                        dynamic pixel = Pixel(alpha, x, y);
                        try { opacity = Convert.ToInt32(pixel.Gray); }
                        catch { opacity = Convert.ToInt32(pixel.RGBRed); }
                        if (opacity < 0 || opacity > 255) throw new InvalidOperationException("Некорректная прозрачность пикселя.");
                        if (opacity == 0) continue;
                    }
                    ColorValue color = Snapshot(_app, Pixel((object)image, x, y));
                    double weight = area / (columns * rows) * opacity / 255.0;
                    if (!local.TryGetValue(color.Bucket, out RasterBucket bucket))
                        local.Add(color.Bucket, bucket = new RasterBucket { Color = color });
                    bucket.Weight += weight;
                    // Keep an actual sampled color; do not invent an averaged RGB/CMYK recipe.
                    if (StringComparer.Ordinal.Compare(color.Key, bucket.Color.Key) < 0) bucket.Color = color;
                }
                // Do not merge a partially read raster if an alpha or pixel read above failed.
                foreach (var pair in local)
                {
                    if (!_raster.TryGetValue(pair.Key, out RasterBucket bucket))
                        _raster.Add(pair.Key, bucket = new RasterBucket { Color = pair.Value.Color });
                    bucket.Weight += pair.Value.Weight;
                    if (StringComparer.Ordinal.Compare(pair.Value.Color.Key, bucket.Color.Key) < 0) bucket.Color = pair.Value.Color;
                }
            }

            private void Warn(string warning)
            { _warnings[warning] = _warnings.TryGetValue(warning, out int count) ? count + 1 : 1; }

            internal string WarningText() => String.Join("; ", _warnings.Select(pair => pair.Key + (pair.Value > 1 ? " (" + pair.Value + ")" : "")));

            internal SelectionColorSummaryResult Result()
            {
                var lines = new List<string>();
                var usage = new List<string>();
                var vectors = _vectors.Values.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
                foreach (ColorValue color in vectors.Take(VectorLines))
                {
                    lines.Add(color.Text);
                    usage.Add(color.Text + " — " + (color.InFill && color.InOutline ? "заливка и контур" : color.InOutline ? "контур" : "заливка"));
                }
                if (vectors.Length > VectorLines) usage.Add("Ещё " + (vectors.Length - VectorLines) + " векторных цветов.");
                if (_gradients) usage.Add("Градиенты: перечислены цвета узлов, без промежуточных оттенков.");
                var raster = _raster.OrderByDescending(pair => pair.Value.Weight).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(RasterColors).Select(pair => pair.Value.Color).ToArray();
                if (raster.Length > 0)
                {
                    foreach (ColorValue color in raster)
                    {
                        lines.Add("≈ " + color.Text);
                        usage.Add("≈ " + color.Text + " — основной цвет растра, приблизительно");
                    }
                    if (_raster.Count > RasterColors) usage.Add("Показаны " + RasterColors + " основных групп цветов.");
                }
                if (_warnings.Count > 0) usage.Add("Сводка неполная: " + WarningText() + ".");
                return new SelectionColorSummaryResult { Text = String.Join("\n", lines), UsageText = String.Join("\n", usage), ColorCount = vectors.Length,
                    ApproximateRasterCount = raster.Length, Incomplete = _warnings.Count > 0,
                    Message = "Сводка добавлена: " + vectors.Length + " векторных цветов" +
                        (raster.Length > 0 ? ", основных цветов растра: " + raster.Length + " (приблизительно)" : "") +
                        (_warnings.Count > 0 ? ". Есть непроверенные элементы — пояснение в отдельном тексте снизу." : ".") };
            }
        }

        private static ColorValue Snapshot(object application, object colorObject)
        {
            dynamic color = colorObject;
            int type = Convert.ToInt32(color.Type);
            bool spot = Convert.ToBoolean(color.IsSpot);
            string identity = "", prefix = "", warning = null;
            if (spot)
            {
                string name = Clean(Convert.ToString(color.SpotColorName));
                if (String.IsNullOrWhiteSpace(name)) name = Clean(Convert.ToString(color.Name));
                string palette = OptionalString(() => color.PaletteIdentifier);
                int? spotId = OptionalInt(() => color.SpotColorID), tint = OptionalInt(() => color.Tint);
                if (tint < 0 || tint > 100) tint = null;
                identity = "spot:" + palette + ":" + spotId + ":" + name + ":" + tint;
                prefix = name + " · " + (tint.HasValue ? tint.Value + "%" : "плашечный, оттенок не прочитан") + " · ";
                if (!tint.HasValue) warning = "Для части плашечных цветов Corel не вернул процент оттенка";
                if (type != 2 && type != 4 && type != 5 && type != 6 && type != 7 && type != 8 && type != 9 && type != 11 && type != 12)
                {
                    dynamic copy = ((dynamic)application).CreateColor(); copy.CopyAssign(color); copy.ConvertToLab();
                    color = copy; type = Convert.ToInt32(color.Type);
                    prefix += "Lab эквивалент по Corel · ";
                }
            }
            string model; double[] values; string details;
            switch (type)
            {
                case 2: model = "CMYK"; values = new double[] { Convert.ToDouble(color.CMYKCyan), Convert.ToDouble(color.CMYKMagenta), Convert.ToDouble(color.CMYKYellow), Convert.ToDouble(color.CMYKBlack) };
                    details = Components(new[] { "C", "M", "Y", "K" }, values); break;
                case 4: model = "CMY"; values = new double[] { Convert.ToDouble(color.CMYCyan), Convert.ToDouble(color.CMYMagenta), Convert.ToDouble(color.CMYYellow) };
                    details = Components(new[] { "C", "M", "Y" }, values); break;
                case 5: model = "RGB"; values = new double[] { Convert.ToDouble(color.RGBRed), Convert.ToDouble(color.RGBGreen), Convert.ToDouble(color.RGBBlue) };
                    details = Components(new[] { "R", "G", "B" }, values); break;
                case 6: model = "HSB"; values = new double[] { Convert.ToDouble(color.HSBHue), Convert.ToDouble(color.HSBSaturation), Convert.ToDouble(color.HSBBrightness) };
                    details = Components(new[] { "H", "S", "B" }, values); break;
                case 7: model = "HLS"; values = new double[] { Convert.ToDouble(color.HLSHue), Convert.ToDouble(color.HLSLightness), Convert.ToDouble(color.HLSSaturation) };
                    details = Components(new[] { "H", "L", "S" }, values); break;
                case 8: model = "BW"; values = new[] { Convert.ToBoolean(color.BW) ? 1.0 : 0.0 }; details = "BW " + values[0]; break;
                case 9: model = "Gray"; values = new double[] { Convert.ToDouble(color.Gray) }; details = "Gray " + Number(values[0]); break;
                case 11: model = "YIQ"; values = new double[] { Convert.ToDouble(color.YIQLuminanceY), Convert.ToDouble(color.YIQChromaI), Convert.ToDouble(color.YIQChromaQ) };
                    details = Components(new[] { "Y", "I", "Q" }, values); break;
                case 12: model = "Lab"; values = new double[] { Convert.ToDouble(color.LabLuminance) * 100 / 255, Convert.ToDouble(color.LabComponentA), Convert.ToDouble(color.LabComponentB) };
                    details = Components(new[] { "L*", "a*", "b*" }, values); break;
                case 20: model = "Registration"; values = new double[0]; details = "Совмещение · все печатные каналы"; break;
                default: throw new InvalidOperationException("Модель цвета Corel " + type + " не поддерживается в сводке.");
            }
            if (values.Any(value => !Finite(value))) throw new InvalidOperationException("Corel вернул некорректные компоненты цвета.");
            string componentKey = model + ":" + String.Join("/", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
            return new ColorValue { Model = model, Values = values, Warning = warning, Text = prefix +
                (model == "BW" || model == "Gray" || model == "Registration" ? "" : model + " ") + details,
                Key = (spot ? identity + ":" : "process:") + componentKey };
        }

        private static string Components(string[] names, double[] values) => String.Join(" ", values.Select((value, index) => names[index] + Number(value)));
        private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string OptionalString(Func<object> reader)
        { try { return Convert.ToString(reader()) ?? ""; } catch { return ""; } }
        private static int? OptionalInt(Func<object> reader)
        { try { return Convert.ToInt32(reader()); } catch { return null; } }
        private static string Clean(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        private static int Count(object collection) => Convert.ToInt32(((dynamic)collection).Count);
        private static object Item(object collection, int index)
        {
            try { return (object)((dynamic)collection)[index]; }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return (object)((dynamic)collection).Item[index]; }
        }
        private static object Pixel(object image, int x, int y) => image.GetType().InvokeMember("Pixel", BindingFlags.GetProperty,
            null, image, new object[] { x, y }, CultureInfo.InvariantCulture);
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        private static void RequireUiThread()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Сводку цветов нужно вызывать в интерфейсе CorelDRAW, а не в фоновом потоке.");
        }
        private static bool SameObject(object first, object second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first == null || second == null || !Marshal.IsComObject(first) || !Marshal.IsComObject(second)) return false;
            IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try { a = Marshal.GetIUnknownForObject(first); b = Marshal.GetIUnknownForObject(second); return a == b; }
            finally { if (a != IntPtr.Zero) Marshal.Release(a); if (b != IntPtr.Zero) Marshal.Release(b); }
        }
    }
}
