using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class EmbroideryFrame
    {
        internal string Title, Path;
        internal double WidthMm, HeightMm;
        internal bool CanInsert;
        internal BitmapSource Preview;
    }
    internal sealed class EmbroideryResult
    {
        internal EmbroideryJob Job;
        internal EmbroideryFrame[] Frames;
        internal readonly List<string> Notes = new List<string>();
    }
    internal static class EmbroideryImages
    {
        internal static BitmapSource Load(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var first = decoder.Frames[0];
                if (first.PixelWidth > 10000 || first.PixelHeight > 10000 || (long)first.PixelWidth * first.PixelHeight > 25000000)
                    throw new InvalidDataException("Изображение больше 25 Мп. Используйте исходник до 5000 × 5000 px.");
                BitmapSource source = new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0); source.Freeze(); return source;
            }
        }
        internal static void Save(BitmapSource image, string path)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(temporary)) encoder.Save(stream);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static EmbroideryResult Prepare(EmbroideryJob job, CancellationToken cancel)
        {
            var sheet = Load(job.RawPath); int width = sheet.PixelWidth, height = sheet.PixelHeight;
            if (width < 512 || height < 512 || Math.Abs(width / (double)height - 1) > .02)
                throw new InvalidDataException("Модель вернула не квадратный коллаж 2 × 2. Исходный ответ сохранён; новую генерацию автоматически не отправляем.");
            var result = new EmbroideryResult { Job = job, Frames = new EmbroideryFrame[4] };
            if (width < 3840 || height < 3840) result.Notes.Add("Запрошено 4K, получено " + width + " × " + height + " px. Повторный платный запрос не отправлен.");
            string[] names = { "Цветной эскиз", "Цветная вышивка", "Одноцветный эскиз", "Одноцветная вышивка" };
            for (int i = 0; i < 4; i++)
            {
                cancel.ThrowIfCancellationRequested();
                int x = i % 2 == 0 ? 0 : width / 2, y = i < 2 ? 0 : height / 2;
                var quadrant = new CroppedBitmap(sheet, new Int32Rect(x, y, i % 2 == 0 ? width / 2 : width - x, i < 2 ? height / 2 : height - y));
                bool flat = i % 2 == 0, valid = true;
                BitmapSource image = quadrant;
                if (flat)
                {
                    var mask = EmbroideryCheck.Mask(quadrant);
                    Int32Rect box = EmbroideryCheck.Bounds(mask, quadrant.PixelWidth, quadrant.PixelHeight);
                    if (box.Width < 4 || box.Height < 4)
                    { valid = false; result.Notes.Add(names[i] + ": не удалось отделить рисунок от ровного фона. Проверьте исходный коллаж."); }
                    else
                    {
                        if (box.X < 2 || box.Y < 2 || box.X + box.Width > quadrant.PixelWidth - 2 || box.Y + box.Height > quadrant.PixelHeight - 2)
                            result.Notes.Add(names[i] + ": рисунок касается края кадра — проверьте целостность.");
                        int left = Math.Max(0, box.X - 2), top = Math.Max(0, box.Y - 2);
                        image = new CroppedBitmap(quadrant, new Int32Rect(left, top, Math.Min(quadrant.PixelWidth, box.X + box.Width + 2) - left, Math.Min(quadrant.PixelHeight, box.Y + box.Height + 2) - top));
                    }
                }
                double mmPerPixel = Math.Min(job.Request.WidthMm / image.PixelWidth, job.Request.HeightMm / image.PixelHeight);
                // Mockups retain a fabric margin and are presentation frames.
                // Exact physical fitting applies to the cropped flat artwork.
                if (!flat) mmPerPixel /= .74;
                byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                image = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 25.4 / mmPerPixel, 25.4 / mmPerPixel, PixelFormats.Bgra32, null, pixels, image.PixelWidth * 4); image.Freeze();
                string path = System.IO.Path.Combine(job.DirectoryPath, "frame-" + (i + 1) + ".png"); Save(image, path);
                var preview = new TransformedBitmap(image, new ScaleTransform(Math.Min(1, 480d / Math.Max(image.PixelWidth, image.PixelHeight)), Math.Min(1, 480d / Math.Max(image.PixelWidth, image.PixelHeight)))); preview.Freeze();
                result.Frames[i] = new EmbroideryFrame { Title = names[i], Path = path, WidthMm = image.PixelWidth * mmPerPixel, HeightMm = image.PixelHeight * mmPerPixel, CanInsert = valid, Preview = preview };
                if (flat && valid) result.Notes.AddRange(EmbroideryCheck.Analyze(image, mmPerPixel, job.Request, i == 2).Select(n => names[i] + ": " + n));
            }
            result.Notes.Add("Проверьте надписи, высоту букв и совпадение каждой пары эскиз/мокап. PNG сохраняет ровный фон; это эскиз для оцифровки, не программа стежков DST/EMB. Пробная вышивка обязательна.");
            return result;
        }
    }
}
