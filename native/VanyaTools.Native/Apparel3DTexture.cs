using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class Apparel3DTexture
    {
        private static readonly Lazy<byte[]> Shade = new Lazy<byte[]>(ReadShade);
        public static BitmapSource[] Create(ApparelViewMap front, ApparelViewMap back, Color fabric)
        {
            var result = new BitmapSource[6];
            var frontPixels = Pixels(front.Image); var backPixels = Pixels(back.Image);
            for (int part = 0; part < result.Length; part++)
            {
                var map = part % 2 == 0 ? front : back;
                var pixels = part % 2 == 0 ? frontPixels : backPixels;
                bool body = part < 2;
                // Wearer's right appears on the left of the front flat view.
                var sleeve = (part < 4) != (part % 2 == 1) ? map.ImageLeft : map.ImageRight;
                int width = body ? Math.Min(1400, Math.Max(768, (int)((map.Image?.PixelWidth ?? 1024) * map.Body.Width))) : 640;
                int height = body ? Math.Min(2000, Math.Max(768, (int)Math.Round(width * map.BodyHeightMm / map.BodyWidthMm))) : 640;
                var output = new byte[width * height * 4];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    double u = (x + .5) / width, v = (y + .5) / height;
                    var uv = body ? map.BodyUv(u, v) : sleeve.At(u, v);
                    int index = (y * width + x) * 4;
                    int shadeIndex = (part * 512 + Math.Min(511, (int)(v * 512))) * 512 + Math.Min(511, (int)(u * 512));
                    double light = .72 + .28 * Shade.Value[shadeIndex] / 255;
                    if (pixels == null)
                    { output[index] = (byte)(fabric.B * light); output[index + 1] = (byte)(fabric.G * light); output[index + 2] = (byte)(fabric.R * light); }
                    else Sample(map.Image, pixels, uv, output, index, light);
                    output[index + 3] = 255;
                }
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, output, width * 4);
                image.Freeze(); result[part] = image;
            }
            return result;
        }
        private static byte[] Pixels(BitmapSource image)
        {
            if (image == null) return null;
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
        }
        private static void Sample(BitmapSource image, byte[] input, Point uv, byte[] output, int index, double light)
        {
            double x = Math.Max(0, Math.Min(image.PixelWidth - 1, uv.X * image.PixelWidth - .5));
            double y = Math.Max(0, Math.Min(image.PixelHeight - 1, uv.Y * image.PixelHeight - .5));
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(image.PixelWidth - 1, x0 + 1), y1 = Math.Min(image.PixelHeight - 1, y0 + 1);
            double dx = x - x0, dy = y - y0;
            int a = (y0 * image.PixelWidth + x0) * 4, b = (y0 * image.PixelWidth + x1) * 4;
            int c = (y1 * image.PixelWidth + x0) * 4, d = (y1 * image.PixelWidth + x1) * 4;
            for (int k = 0; k < 3; k++)
            {
                double channel = (input[a + k] * (1 - dx) + input[b + k] * dx) * (1 - dy) + (input[c + k] * (1 - dx) + input[d + k] * dx) * dy;
                double alpha = ((input[a + 3] * (1 - dx) + input[b + 3] * dx) * (1 - dy) + (input[c + 3] * (1 - dx) + input[d + 3] * dx) * dy) / 255;
                output[index + k] = (byte)Math.Round((channel * alpha + 255 * (1 - alpha)) * light);
            }
        }
        private static byte[] ReadShade()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VanyaTools.Native.Assets.Apparel.shade.png"))
            {
                if (stream == null) throw new InvalidOperationException("В установщике отсутствуют материалы 3D-модели.");
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (image.PixelWidth != 512 || image.PixelHeight != 3072) throw new InvalidOperationException("Повреждены материалы 3D-модели.");
                var gray = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
                var pixels = new byte[512 * 3072]; gray.CopyPixels(pixels, 512, 0); return pixels;
            }
        }
    }
}
