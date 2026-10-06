using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class ScreenColorPicker
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PixelPoint { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out PixelPoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr dc, int x, int y);

        public static Task<Color?> PickAsync()
        {
            var result = new TaskCompletionSource<Color?>();
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Cursor = Cursors.Cross,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(37, 60, 76)),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(18),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "Кликните по цвету на экране · Esc — отмена",
                        Foreground = Brushes.White, FontSize = 12 }
                }
            };
            overlay.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                result.TrySetResult(null);
                overlay.Close();
                e.Handled = true;
            };
            overlay.PreviewMouseRightButtonDown += (_, e) =>
            {
                result.TrySetResult(null);
                overlay.Close();
                e.Handled = true;
            };
            overlay.PreviewMouseLeftButtonDown += async (_, e) =>
            {
                e.Handled = true;
                PixelPoint point;
                if (!GetCursorPos(out point))
                {
                    result.TrySetException(new InvalidOperationException("Не удалось определить положение курсора."));
                    overlay.Close();
                    return;
                }
                overlay.Hide();
                // Sample after the transparent pick window has disappeared.
                await Task.Delay(100);
                try { result.TrySetResult(ReadPixel(point)); }
                catch (Exception error) { result.TrySetException(error); }
                finally { overlay.Close(); }
            };
            overlay.Closed += (_, __) => result.TrySetResult(null);
            overlay.Show();
            overlay.Activate();
            overlay.Focus();
            return result.Task;
        }

        private static Color ReadPixel(PixelPoint point)
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
                throw new InvalidOperationException("Не удалось прочитать цвет экрана.");
            try
            {
                uint value = GetPixel(dc, point.X, point.Y);
                if (value == 0xFFFFFFFF)
                    throw new InvalidOperationException("Цвет в этой точке экрана недоступен.");
                return Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16));
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
    }
}
