using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VanyaTools.Native
{
    internal static class ThumbnailTabs
    {
        public static TabItem Create(string title, string icon, Color color, object content)
        {
            var canvas = new Canvas { Width = 26, Height = 26 };
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse(icon),
                Stroke = Brushes.White,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent
            });
            var header = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                Child = new Viewbox { Width = 22, Height = 22, Child = canvas },
                ToolTip = title,
                Margin = new Thickness(0)
            };
            AutomationProperties.SetName(header, title);
            return new TabItem { Header = header, Content = content, ToolTip = title,
                Padding = new Thickness(2, 2, 2, 2) };
        }

        public const string Crop = "M4,2 L4,18 L20,18 M2,6 L18,6 L18,22";
        public const string Stickers = "M4,2 L14,2 L20,8 L20,21 L4,21 Z M14,2 L14,8 L20,8 M7,12 L16,12 M7,16 L16,16";
        public const string Shirt = "M7,3 L2,7 L4,12 L7,10 L7,21 L17,21 L17,10 L20,12 L22,7 L17,3 L14,5 L10,5 Z";
        public const string Names = "M5,5 L19,5 M5,9 L16,9 M5,15 L19,15 M5,19 L16,19";
        public const string Style = "M4,19 L18,5 M3,21 C5,21 8,20 8,17 M15,4 L20,9 M4,19 L8,20";
        public const string Publish = "M3,5 L21,5 L21,19 L3,19 Z M3,16 L8,11 L12,15 L15,12 L21,18 M17,8 L18,9";
        public const string Settings = "M12,3 L14,5 L17,4 L18,7 L21,8 L20,11 L22,13 L20,15 L21,18 L18,19 L17,22 L14,21 L12,23 L10,21 L7,22 L6,19 L3,18 L4,15 L2,13 L4,11 L3,8 L6,7 L7,4 L10,5 Z M12,10 A3,3 0 1 0 12,16 A3,3 0 1 0 12,10";
        public const string Restore = "M4,20 L9,5 L15,5 L20,20 M6,15 L18,15 M20,3 L20,9 M17,6 L23,6";
    }
}
