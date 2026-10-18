using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    // A shared illustrative scene makes the local tone changes visible before a photo is loaded.
    internal static class LocalPhotoStyleSample
    {
        public static BitmapSource Create()
        {
            var visual = new DrawingVisual();
            using (DrawingContext draw = visual.RenderOpen())
            {
                draw.DrawRectangle(new LinearGradientBrush(Color.FromRgb(62, 151, 209),
                    Color.FromRgb(212, 234, 240), 90), null, new Rect(0, 0, 160, 110));
                draw.DrawEllipse(Brushes.Goldenrod, null, new Point(125, 25), 13, 13);
                draw.DrawGeometry(new SolidColorBrush(Color.FromRgb(81, 113, 125)), null,
                    Geometry.Parse("M0,72 L36,20 L77,70 L112,42 L160,79 L160,110 L0,110 Z"));
                draw.DrawGeometry(Brushes.WhiteSmoke, null, Geometry.Parse("M24,37 L36,20 L50,39 L37,34 L32,40 Z"));
                draw.DrawRectangle(new LinearGradientBrush(Color.FromRgb(42, 135, 171),
                    Color.FromRgb(25, 81, 105), 90), null, new Rect(0, 72, 160, 38));
                draw.DrawGeometry(new SolidColorBrush(Color.FromRgb(83, 131, 62)), null,
                    Geometry.Parse("M0,79 Q40,64 88,89 L160,103 L160,110 L0,110 Z"));
                draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(229, 189, 133)), null, new Rect(22, 70, 31, 26));
                draw.DrawGeometry(new SolidColorBrush(Color.FromRgb(166, 60, 44)), null,
                    Geometry.Parse("M17,71 L37,53 L58,71 Z"));
                draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(66, 49, 36)), null, new Rect(34, 81, 9, 15));
                draw.DrawRectangle(Brushes.LightGoldenrodYellow, null, new Rect(25, 77, 6, 6));
                draw.DrawGeometry(new SolidColorBrush(Color.FromRgb(34, 78, 47)), null,
                    Geometry.Parse("M65,89 L76,55 L87,89 Z M102,100 L112,68 L123,100 Z"));
            }
            var image = new RenderTargetBitmap(160, 110, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual); image.Freeze();
            return image;
        }
    }
}
