using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class FilmColorReport
    {
        internal int Errors, Warnings, Objects;
        internal long Pixels;
        private readonly List<string> _details = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>();
        internal void Add(bool error, string message)
        {
            if (!_seen.Add((error ? "E" : "W") + message)) return;
            if (error) Errors++; else Warnings++;
            if (_details.Count < 80) _details.Add((error ? "✗ " : "⚠ ") + message);
        }
        public override string ToString() => (Errors == 0 && Warnings == 0 ? "✓ Цвета допустимы." :
            "Ошибок: " + Errors + "; предупреждений: " + Warnings + ".") +
            " Объектов: " + Objects + ", пикселей просмотрено: " + Pixels.ToString("N0") + ".\n" +
            String.Join("\n", _details) + (Errors + Warnings > _details.Count ? "\nПоказаны первые 80 замечаний." : "");
    }

    internal static class FilmColorValidator
    {
        // 0: allowed; 1: neutral half-tone / RGB raster, warn; 2: wrong ink.
        private static int ColorGrade(object colorObject, bool bitmap, out string description)
        {
            dynamic c = colorObject;
            if (Convert.ToBoolean(c.IsSpot)) { description = "плашечный цвет «" + Convert.ToString(c.Name) + "»"; return 2; }
            int type = Convert.ToInt32(c.Type);
            if (type == 8)
            { description = "монохромный цвет"; return bitmap ? 0 : 2; }
            if (type == CorelConstants.CdrColorCmyk)
            {
                int cyan = Convert.ToInt32(c.CMYKCyan), magenta = Convert.ToInt32(c.CMYKMagenta),
                    yellow = Convert.ToInt32(c.CMYKYellow), black = Convert.ToInt32(c.CMYKBlack);
                description = "C" + cyan + " M" + magenta + " Y" + yellow + " K" + black;
                return cyan != 0 || magenta != 0 || yellow != 0 ? 2 : black == 0 || black == 100 ? 0 : 1;
            }
            if (type == CorelConstants.CdrColorRgb)
            {
                int r = Convert.ToInt32(c.RGBRed), g = Convert.ToInt32(c.RGBGreen), b = Convert.ToInt32(c.RGBBlue);
                description = "RGB " + r + "/" + g + "/" + b;
                if (r == 255 && g == 255 && b == 255) return 0;
                if (r != g || g != b) return 2;
                if (!bitmap && r == 0) { description += " — чёрный должен быть C0 M0 Y0 K100"; return 2; }
                return 1;
            }
            if (type == CorelConstants.CdrColorGray)
            {
                int gray = Convert.ToInt32(c.Gray); description = "оттенок серого " + gray;
                return bitmap && (gray == 0 || gray == 255) ? 0 : 1;
            }
            description = "неподдерживаемая цветовая модель " + type; return 2;
        }

        internal static async Task<FilmColorReport> Check(IEnumerable<object> roots, Action<string> progress)
        {
            var report = new FilmColorReport(); var leaves = new List<object>(); var seen = new HashSet<int>();
            foreach (object root in roots) Collect(root, leaves, seen);
            foreach (object shapeObject in leaves)
            {
                dynamic shape = shapeObject;
                string name = FilmOutputService.Name(shapeObject);
                if (name == FilmOutputService.Caption) name = "Подпись";
                if (name == FilmOutputService.Art) name = "Рисунок";
                name = (String.IsNullOrWhiteSpace(name) ? "Объект" : name) + " #" + Convert.ToInt32(shape.StaticID);
                report.Objects++; progress?.Invoke("Проверка цветов: " + report.Objects + "/" + leaves.Count + " · " + name);
                try
                {
                    int transparency = Convert.ToInt32(shape.Transparency.Type);
                    if (transparency != 0 && (transparency != 1 || Convert.ToInt32(shape.Transparency.Uniform) != 0))
                        report.Add(false, name + ": прозрачность — итоговые полутона зависят от наложения.");
                    if (Convert.ToInt32(shape.Type) == CorelConstants.CdrBitmapShape)
                        await Bitmap(shapeObject, name, report);
                    else if (Convert.ToInt32(shape.Type) == CorelConstants.CdrTextShape)
                    {
                        dynamic characters = shape.Text.Story.Characters;
                        int count = Convert.ToInt32(characters.Count);
                        if (count > 100000) report.Add(true, name + ": слишком большой текст для полной проверки; преобразуйте его в кривые.");
                        else for (int i = 1; i <= count; i++)
                        {
                            Fill((object)characters[i].Fill, name + ": текст", report);
                            if (i % 100 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
                        }
                    }
                    else if (Convert.ToInt32(shape.Type) != CorelConstants.CdrGroupShape) Fill((object)shape.Fill, name, report);
                    if (Convert.ToInt32(shape.Outline.Type) != 0)
                        Color((object)shape.Outline.Color, false, name + ": обводка", report);
                    if (Convert.ToInt32(shape.Effects.Count) != 0)
                        report.Add(true, name + ": цвета эффектов не проверены; преобразуйте эффекты в кривые или ЧБ-растр.");
                }
                catch (Exception error)
                {
                    Log.Error("Film color inspection failed for " + name, error);
                    report.Add(true, name + ": не удалось полностью проверить цвета: " + error.GetBaseException().Message);
                }
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            return report;
        }

        private static void Collect(object itemObject, List<object> result, HashSet<int> seen)
        {
            dynamic item = itemObject;
            if (!seen.Add(Convert.ToInt32(item.StaticID))) return;
            if (Convert.ToInt32(item.Type) == CorelConstants.CdrGroupShape)
            {
                // Group transparency/effects are relevant as well as leaf fills.
                if (Convert.ToInt32(item.Transparency.Type) != 0 || Convert.ToInt32(item.Effects.Count) != 0)
                    result.Add(itemObject);
                foreach (object child in FilmOutputService.Children(itemObject)) Collect(child, result, seen);
            }
            else result.Add(itemObject);
            object clip = null;
            try { clip = (object)item.PowerClip; } catch (System.Runtime.InteropServices.COMException) { }
            if (clip != null) foreach (dynamic child in ((dynamic)clip).Shapes) Collect((object)child, result, seen);
        }

        private static void Color(object color, bool bitmap, string name, FilmColorReport report)
        {
            int grade = ColorGrade(color, bitmap, out string description);
            if (grade > 0) report.Add(grade == 2, name + ": " + description + (grade == 1 ? " — нейтральные полутона." : "."));
        }
        private static void Fill(object fillObject, string name, FilmColorReport report, bool bitmap = false)
        {
            dynamic fill = fillObject; int type = Convert.ToInt32(fill.Type);
            if (type == 0) return;
            if (type == 1) { Color((object)fill.UniformColor, bitmap, name + ": заливка", report); return; }
            if (type == 2)
            {
                report.Add(false, name + ": градиент — содержит полутона.");
                foreach (dynamic stop in fill.Fountain.Colors) Color((object)stop.Color, false, name + ": градиент", report);
                return;
            }
            report.Add(true, name + ": неоднородная заливка; преобразуйте её в обычные кривые или проверенный ЧБ-растр.");
        }

        private static async Task Bitmap(object shapeObject, string name, FilmColorReport report)
        {
            dynamic bitmap = ((dynamic)shapeObject).Bitmap;
            int mode = Convert.ToInt32(bitmap.Mode);
            if (mode == 0)
            {
                Fill((object)((dynamic)shapeObject).Fill, name + ": цветизация монохромного растра", report, true);
                report.Add(false, name + ": монохромный растр; проверьте режим ЧБ-печати в RIP."); return;
            }
            if (mode != 2 && mode != 4 && mode != 5)
            { report.Add(true, name + ": для растра нужна модель Grayscale, RGB или CMYK; текущая модель " + mode + "."); return; }
            dynamic image = bitmap.Image;
            if (Convert.ToInt32(image.Type) != mode)
                throw new InvalidOperationException("Corel вернул пиксели другой цветовой модели; исходный состав красок не подтверждён.");
            dynamic tiles = image.Tiles;
            dynamic alphaTiles = Convert.ToBoolean(bitmap.Transparent) ? bitmap.ImageAlpha.Tiles : null;
            int count = Convert.ToInt32(tiles.Count);
            if (count == 0) throw new InvalidOperationException("Нет доступных тайлов исходного растра.");
            var cache = new Dictionary<ulong, int>(); bool warned = false, partial = false;
            for (int i = 1; i <= count; i++)
            {
                dynamic tile = tiles.Item[i];
                int left = Convert.ToInt32(tile.Left), top = Convert.ToInt32(tile.Top),
                    width = Convert.ToInt32(tile.Width), height = Convert.ToInt32(tile.Height),
                    stride = Convert.ToInt32(tile.BytesPerLine), bpp = Convert.ToInt32(tile.BytesPerPixel);
                byte[] data = (byte[])tile.PixelData;
                if (bpp < 1 || bpp > 4 || stride < width * bpp || data.Length < (long)stride * height)
                    throw new InvalidOperationException("Неподдерживаемый формат тайла растра.");
                byte[] alpha = null; int alphaStride = 0, alphaBpp = 0;
                if (alphaTiles != null)
                {
                    if (Convert.ToInt32(alphaTiles.Count) != count) throw new InvalidOperationException("Сетки цвета и прозрачности различаются; проверка не завершена.");
                    dynamic a = alphaTiles.Item[i];
                    if (Convert.ToInt32(a.Left) != left || Convert.ToInt32(a.Top) != top || Convert.ToInt32(a.Width) != width || Convert.ToInt32(a.Height) != height)
                        throw new InvalidOperationException("Не удалось совместить тайлы цвета и прозрачности.");
                    alpha = (byte[])a.PixelData; alphaStride = Convert.ToInt32(a.BytesPerLine); alphaBpp = Convert.ToInt32(a.BytesPerPixel);
                    if (alphaBpp < 1 || alphaStride < width * alphaBpp || alpha.Length < (long)alphaStride * height)
                        throw new InvalidOperationException("Некорректный альфа-канал.");
                }
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    if (alpha != null)
                    {
                        byte a = alpha[y * alphaStride + x * alphaBpp];
                        if (a == 0) continue; if (a < 255) partial = true;
                    }
                    report.Pixels++;
                    int offset = y * stride + x * bpp; ulong key = 0;
                    for (int channel = 0; channel < bpp; channel++) key |= (ulong)data[offset + channel] << (channel * 8);
                    if (!cache.TryGetValue(key, out int grade))
                    {
                        // Native channel order is deliberately not guessed.
                        // Ask Corel once for each distinct raw pixel value;
                        // neutral images need at most 256 such COM calls.
                        object color = image.GetType().InvokeMember("Pixel", BindingFlags.GetProperty, null,
                            (object)image, new object[] { left + x, top - height + 1 + y });
                        if (mode == 5 && Convert.ToInt32(((dynamic)color).Type) != CorelConstants.CdrColorCmyk)
                            throw new InvalidOperationException("Corel не вернул исходные CMYK-компоненты пикселя.");
                        grade = ColorGrade(color, true, out string description); cache[key] = grade;
                        if (grade == 2) { report.Add(true, name + ": пиксель " + description + "; проверка этого растра остановлена на первом недопустимом цвете."); return; }
                        if (grade == 1 && !warned) { report.Add(false, name + ": нейтральные полутона / RGB-растр. Проверьте режим ЧБ-печати в RIP."); warned = true; }
                    }
                }
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            if (partial) report.Add(false, name + ": есть полупрозрачные пиксели.");
        }
    }
}
