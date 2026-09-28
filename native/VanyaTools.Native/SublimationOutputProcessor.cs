using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class SublimationOutputProcessor
    {
        private const int Dpi = 300;
        private const int Overlap = 64;

        internal struct Tile
        {
            public Int32Rect Core;
            public Int32Rect Expanded;
        }

        public static Tile Region(int width, int height, int index)
        {
            if (index < 0 || index > 3) throw new ArgumentOutOfRangeException(nameof(index));
            int centerX = width / 2, centerY = height / 2;
            bool right = index % 2 == 1, bottom = index >= 2;
            int x = right ? centerX : 0, y = bottom ? centerY : 0;
            int coreWidth = right ? width - centerX : centerX;
            int coreHeight = bottom ? height - centerY : centerY;
            int left = Math.Max(0, x - Overlap), top = Math.Max(0, y - Overlap);
            int edgeX = Math.Min(width, x + coreWidth + Overlap);
            int edgeY = Math.Min(height, y + coreHeight + Overlap);
            return new Tile
            {
                Core = new Int32Rect(x, y, coreWidth, coreHeight),
                Expanded = new Int32Rect(left, top, edgeX - left, edgeY - top)
            };
        }

        public static void Prepare(string generatedPath, string outputPath, int widthMm, int heightMm,
            string[] upscaleTiles = null)
        {
            BitmapSource source = AiPrintTab.Bitmap(generatedPath);
            int targetWidth = (int)Math.Round(widthMm * Dpi / 25.4);
            int targetHeight = (int)Math.Round(heightMm * Dpi / 25.4);
            double dipPerPixel = 96.0 / Dpi;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null,
                    new Rect(0, 0, targetWidth * dipPerPixel, targetHeight * dipPerPixel));
                if (upscaleTiles == null)
                {
                    context.DrawImage(source,
                        new Rect(0, 0, targetWidth * dipPerPixel, targetHeight * dipPerPixel));
                }
                else
                {
                    if (upscaleTiles.Length != 4) throw new InvalidDataException("Нужны четыре фрагмента для сборки развёртки.");
                    for (int i = 0; i < 4; i++)
                    {
                        Tile tile = Region(source.PixelWidth, source.PixelHeight, i);
                        BitmapSource enlarged = AiPrintTab.Bitmap(upscaleTiles[i]);
                        double sx = (double)enlarged.PixelWidth / tile.Expanded.Width;
                        double sy = (double)enlarged.PixelHeight / tile.Expanded.Height;
                        int cropX = (int)Math.Round((tile.Core.X - tile.Expanded.X) * sx);
                        int cropY = (int)Math.Round((tile.Core.Y - tile.Expanded.Y) * sy);
                        int cropWidth = Math.Min(enlarged.PixelWidth - cropX, (int)Math.Round(tile.Core.Width * sx));
                        int cropHeight = Math.Min(enlarged.PixelHeight - cropY, (int)Math.Round(tile.Core.Height * sy));
                        if (cropWidth < 1 || cropHeight < 1) throw new InvalidDataException("AI-апскейл вернул неверный размер фрагмента.");
                        var coreImage = new CroppedBitmap(enlarged,
                            new Int32Rect(cropX, cropY, cropWidth, cropHeight));
                        double targetX = (double)tile.Core.X / source.PixelWidth * targetWidth;
                        double targetY = (double)tile.Core.Y / source.PixelHeight * targetHeight;
                        double edgeX = (double)(tile.Core.X + tile.Core.Width) / source.PixelWidth * targetWidth;
                        double edgeY = (double)(tile.Core.Y + tile.Core.Height) / source.PixelHeight * targetHeight;
                        context.DrawImage(coreImage, new Rect(targetX * dipPerPixel, targetY * dipPerPixel,
                            (edgeX - targetX) * dipPerPixel, (edgeY - targetY) * dipPerPixel));
                    }
                }
            }
            var output = new RenderTargetBitmap(targetWidth, targetHeight, Dpi, Dpi, PixelFormats.Pbgra32);
            output.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
            using (var stream = File.Create(outputPath)) encoder.Save(stream);
        }
    }
}
