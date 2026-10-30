using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace VanyaTools.Native
{
    // Corel-native image buffers avoid PNG/TIFF import and color-profile round trips.
    // Capture/Apply must run on the UI STA. Processing NativeRasterBuffer is managed.
    internal static class NativeRasterModelIO
    {
        private const int MaximumTiles = 100000;
        private sealed class Tile
        {
            internal object Native;
            internal int Left, Bottom, Width, Height, Stride, Bpp;
            internal byte[] Data;
            internal string Description;
        }

        internal static NativeRasterBuffer Capture(object bitmapObject, int maxPixels = 70000000)
        {
            RequireSta();
            if (bitmapObject == null) throw new InvalidOperationException("Исходный растр недоступен.");
            dynamic bitmap = bitmapObject;
            int mode = Convert.ToInt32(bitmap.Mode), channels = NativeRasterBuffer.ChannelCount(mode);
            object color = (object)bitmap.Image;
            if (color == null || Convert.ToInt32(((dynamic)color).Type) != mode)
                throw new InvalidOperationException("Corel вернул цветовые данные другой модели. Обработка остановлена.");
            int width = Convert.ToInt32(((dynamic)color).Width), height = Convert.ToInt32(((dynamic)color).Height);
            if (width <= 0 || height <= 0 || (long)width * height > maxPixels || (long)width * height > Int32.MaxValue / channels)
                throw new InvalidOperationException("Исходный растр слишком велик для обработки без изменения разрешения.");
            var colors = new byte[checked(width * height * channels)];
            ReadPlane(color, colors, width, height, channels);
            var alpha = new byte[checked(width * height)];
            if (Convert.ToBoolean(bitmap.Transparent))
            {
                object mask = (object)bitmap.ImageAlpha;
                if (mask == null)
                    throw new InvalidOperationException("Corel не вернул исходный серый альфа-канал.");
                ReadPlane(mask, alpha, width, height, 1);
            }
            else for (int index = 0; index < alpha.Length; index++) alpha[index] = 255;
            return new NativeRasterBuffer(width, height, mode, Convert.ToDouble(bitmap.ResolutionX),
                Convert.ToDouble(bitmap.ResolutionY), colors, alpha);
        }

        internal static void Apply(object documentObject, object bitmapObject, NativeRasterBuffer buffer,
            int offsetX = 0, int offsetY = 0)
        {
            RequireSta();
            if (documentObject == null || bitmapObject == null || buffer == null)
                throw new ArgumentNullException("Не указан документ, целевой растр или цветовые данные.");
            dynamic document = documentObject, bitmap = bitmapObject;
            var timer = Stopwatch.StartNew();
            Log.Info("Native raster Corel apply started: " + buffer.Width + "x" + buffer.Height + ", model=" + buffer.Mode + ".");
            object color = CreateImage(documentObject, buffer.Mode, buffer.Width, buffer.Height);
            WritePlane(color, buffer.Colors, buffer.Width, buffer.Height, buffer.Channels);
            long colorMs = timer.ElapsedMilliseconds;
            Log.Info("Native raster Corel color plane written: " + colorMs + " ms.");
            // The native dispatch route requires a real optional image pointer.
            // An all-255 mask is fully opaque and preserves the exact source alpha
            // without relying on null/missing interface argument marshalling.
            object alpha = CreateImage(documentObject, 2, buffer.Width, buffer.Height);
            WritePlane(alpha, buffer.Alpha, buffer.Width, buffer.Height, 1);
            Log.Info("Native raster Corel alpha plane written: " + (timer.ElapsedMilliseconds - colorMs) + " ms.");
            // The caller owns the target (normally a duplicate/new result), never
            // the source. No SetImageData occurs until both planes are complete.
            bitmap.SetImageData((dynamic)color, (dynamic)alpha, offsetX, offsetY);
            if (Convert.ToInt32(bitmap.Mode) != buffer.Mode ||
                Convert.ToInt32(bitmap.Image.Width) != buffer.Width || Convert.ToInt32(bitmap.Image.Height) != buffer.Height)
                throw new InvalidOperationException("Corel изменил модель или размер результата. Проверьте вставленный растр.");
            Log.Info("Native raster Corel apply finished: " + timer.ElapsedMilliseconds + " ms.");
        }

        internal static object CreateImage(object documentObject, int mode, int width, int height)
        {
            RequireSta();
            if (documentObject == null) throw new ArgumentNullException(nameof(documentObject));
            int channels = NativeRasterBuffer.ChannelCount(mode);
            if (width <= 0 || height <= 0 || (long)width * height > Int32.MaxValue / channels)
                throw new InvalidOperationException("Некорректный размер native-буфера растра.");
            dynamic document = documentObject, application = document.Application;
            // This Corel dispatch path rejects both explicit null and an omitted
            // optional FillColor pointer. Supply a real color in the same model.
            object fill = mode == 4 ? (object)application.CreateRGBColor(0, 0, 0) :
                mode == 5 ? (object)application.CreateCMYKColor(0, 0, 0, 0) : (object)application.CreateGrayColor(0);
            try
            {
                object image = (object)document.CreateImage(mode, width, height, (dynamic)fill);
                RequireImage(image, mode, width, height);
                return image;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Corel CreateImage: модель=" + mode + ", размер=" + width + "×" + height +
                    ", заполнение=CorelColor той же модели. " + error.Message, error);
            }
        }

        // Let Corel encode a chosen color in the source model once. No native
        // CMYK channel order or document profile conversion is guessed here.
        internal static byte[] EncodeColor(object documentObject, int mode, object colorObject)
        {
            RequireSta();
            if (documentObject == null || colorObject == null) throw new ArgumentNullException();
            int channels = NativeRasterBuffer.ChannelCount(mode);
            // Use a small aligned sample instead of relying on one-pixel tiles.
            // It is uniformly filled; reading its first sample is sufficient.
            const int sample = 8;
            object image = (object)((dynamic)documentObject).CreateImage(mode, sample, sample, (dynamic)colorObject);
            RequireImage(image, mode, sample, sample);
            var encoded = new byte[sample * sample * channels];
            ReadPlane(image, encoded, sample, sample, channels);
            return encoded.Take(channels).ToArray();
        }

        internal static int[] CalibrateCmykOrder(object documentObject, object applicationObject)
        {
            RequireSta();
            if (documentObject == null || applicationObject == null) throw new ArgumentNullException();
            dynamic application = applicationObject;
            var order = new int[4];
            int used = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                object color = (object)application.CreateCMYKColor(channel == 0 ? 100 : 0, channel == 1 ? 100 : 0,
                    channel == 2 ? 100 : 0, channel == 3 ? 100 : 0);
                byte[] encoded = EncodeColor(documentObject, 5, color);
                int position = -1;
                for (int index = 0; index < 4; index++)
                {
                    if (encoded[index] == 255 && position < 0) position = index;
                    else if (encoded[index] != 0) throw new InvalidOperationException("Corel изменил компоненты контрольного CMYK-цвета.");
                }
                if (position < 0 || (used & (1 << position)) != 0)
                    throw new InvalidOperationException("Не удалось определить порядок исходных CMYK-каналов.");
                order[channel] = position; used |= 1 << position;
            }
            return order;
        }

        private static void RequireImage(object image, int mode, int width, int height)
        {
            if (image == null || Convert.ToInt32(((dynamic)image).Type) != mode ||
                Convert.ToInt32(((dynamic)image).Width) != width || Convert.ToInt32(((dynamic)image).Height) != height)
                throw new InvalidOperationException("Corel не создал буфер нужной цветовой модели и размера.");
        }

        private static void ReadPlane(object image, byte[] destination, int width, int height, int channels)
        {
            foreach (Tile tile in Tiles(image, width, height, channels, false))
                for (int y = 0; y < tile.Height; y++)
                {
                    int targetRow = checked(((height - 1 - tile.Bottom - y) * width + tile.Left) * channels);
                    int sourceRow = checked(y * tile.Stride);
                    if (tile.Bpp == channels) Buffer.BlockCopy(tile.Data, sourceRow, destination, targetRow, tile.Width * channels);
                    else for (int x = 0; x < tile.Width; x++)
                        for (int channel = 0; channel < channels; channel++)
                            destination[targetRow + x * channels + channel] = tile.Data[sourceRow + x * tile.Bpp + channel];
                }
        }

        private static void WritePlane(object image, byte[] source, int width, int height, int channels)
        {
            if (Convert.ToBoolean(((dynamic)image).ReadOnly))
                throw new InvalidOperationException("Corel создал буфер только для чтения.");
            foreach (Tile tile in Tiles(image, width, height, channels, true))
            {
                for (int y = 0; y < tile.Height; y++)
                {
                    int sourceRow = checked(((height - 1 - tile.Bottom - y) * width + tile.Left) * channels);
                    int targetRow = checked(y * tile.Stride);
                    if (tile.Bpp == channels) Buffer.BlockCopy(source, sourceRow, tile.Data, targetRow, tile.Width * channels);
                    else for (int x = 0; x < tile.Width; x++)
                        for (int channel = 0; channel < channels; channel++)
                            tile.Data[targetRow + x * tile.Bpp + channel] = source[sourceRow + x * channels + channel];
                }
                try { ((dynamic)tile.Native).PixelData = tile.Data; }
                catch (Exception error) { throw new InvalidOperationException("Corel PixelData.set: " + tile.Description + ". " + error.Message, error); }
            }
        }

        private static List<Tile> Tiles(object imageObject, int width, int height, int channels, bool writable)
        {
            dynamic image = imageObject;
            if (Convert.ToInt32(image.Width) != width || Convert.ToInt32(image.Height) != height)
                throw new InvalidOperationException("Размер альфа-канала не совпадает с цветовым изображением.");
            dynamic collection = image.Tiles;
            int count = Convert.ToInt32(collection.Count);
            if (count <= 0 || count > MaximumTiles) throw new InvalidOperationException("Corel не вернул поддерживаемую сетку тайлов.");
            var tiles = new List<Tile>(count);
            for (int index = 1; index <= count; index++)
            {
                object native = (object)collection.Item[index]; dynamic tile = native;
                int left = Convert.ToInt32(tile.Left), top = Convert.ToInt32(tile.Top),
                    tw = Convert.ToInt32(tile.Width), th = Convert.ToInt32(tile.Height),
                    stride = Convert.ToInt32(tile.BytesPerLine), bpp = Convert.ToInt32(tile.BytesPerPixel);
                long bottom = (long)top - th + 1;
                if (tw <= 0 || th <= 0 || left < 0 || bottom < 0 || (long)left + tw > width || top >= height ||
                    bpp < channels || bpp > 4 || stride <= 0 || (long)stride < (long)tw * bpp)
                    throw new InvalidOperationException("Неполный или неподдерживаемый тайл исходного растра.");
                string description = "модель=" + Convert.ToInt32(image.Type) + ", тайл=" + index + ", X=" + left + ", Y=" + top +
                    ", размер=" + tw + "×" + th + ", bpp=" + bpp + ", stride=" + stride + ", запись=" + writable;
                if (writable && Convert.ToBoolean(tile.ReadOnly))
                    throw new InvalidOperationException("Corel создал тайл только для чтения.");
                byte[] bytes;
                long minimum = (long)stride * (th - 1) + (long)tw * bpp;
                if (writable)
                {
                    // All image samples are overwritten below. Never read a newly
                    // created standalone RGB tile: its getter can fault in Corel.
                    int size = Convert.ToInt32(tile.BytesPerTile);
                    if (size < minimum) throw new InvalidOperationException("Некорректный размер native-тайла: " + description);
                    bytes = new byte[size];
                }
                else
                {
                    try { bytes = (byte[])tile.PixelData; }
                    catch (Exception error) { throw new InvalidOperationException("Corel PixelData.get: " + description + ". " + error.Message, error); }
                    if (bytes == null || bytes.LongLength < minimum)
                        throw new InvalidOperationException("Неполный буфер native-тайла: " + description);
                }
                tiles.Add(new Tile { Native = native, Left = left, Bottom = (int)bottom, Width = tw, Height = th,
                    Stride = stride, Bpp = bpp, Data = bytes, Description = description });
            }
            // Coverage must include every pixel; overlapping tiles are harmless.
            var boundaries = new SortedDictionary<int, List<int>>();
            void Add(int y, int value)
            {
                if (!boundaries.TryGetValue(y, out var events)) boundaries.Add(y, events = new List<int>());
                events.Add(value);
            }
            for (int index = 0; index < tiles.Count; index++)
            { Add(tiles[index].Bottom, index + 1); Add(tiles[index].Bottom + tiles[index].Height, -(index + 1)); }
            var active = new Dictionary<int, Tile>();
            int previous = 0;
            foreach (var boundary in boundaries)
            {
                if (boundary.Key > previous) CheckBand(active.Values, width);
                foreach (int value in boundary.Value)
                {
                    if (value > 0) active[value - 1] = tiles[value - 1];
                    else active.Remove(-value - 1);
                }
                previous = boundary.Key;
            }
            if (previous < height) CheckBand(active.Values, width);
            return tiles;
        }

        private static void CheckBand(IEnumerable<Tile> tiles, int width)
        {
            int covered = 0;
            foreach (Tile tile in tiles.OrderBy(value => value.Left))
            {
                if (tile.Left > covered) break;
                covered = Math.Max(covered, tile.Left + tile.Width);
            }
            if (covered != width) throw new InvalidOperationException("Corel вернул неполное изображение растра.");
        }

        private static void RequireSta()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Читать и записывать исходный растр нужно в интерфейсе CorelDRAW.");
        }
    }
}
