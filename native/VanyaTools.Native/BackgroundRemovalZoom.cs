using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed partial class BackgroundRemovalWindow
    {
        private bool _zoomFit = true, _layingOutZoom;
        private double _zoomScale = 1;
        private TextBlock _zoomText;
        private ScrollViewer _panScroll;
        private Point _panOrigin;
        private double _panHorizontal, _panVertical;

        private FrameworkElement CreateZoomToolbar()
        {
            var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
            var minus = ZoomButton("−", (_, __) => ChangeZoom(1 / 1.25));
            minus.ToolTip = "Уменьшить масштаб";
            var plus = ZoomButton("+", (_, __) => ChangeZoom(1.25));
            plus.ToolTip = "Увеличить масштаб";
            _zoomText = new TextBlock { Text = "—", FontSize = 11, MinWidth = 52,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0) };
            var actual = ZoomButton("1:1", (_, __) =>
            {
                if (!_loaded || _applying || _closed) return;
                _zoomFit = false; _zoomScale = 1;
                if (_quality.SelectedIndex != 4) _quality.SelectedIndex = 4;
                else ZoomTo(1, _beforeScroll, null);
            });
            actual.ToolTip = "Исходное разрешение: один пиксель изображения на один пиксель экрана";
            var fit = ZoomButton("Вписать", (_, __) =>
            {
                if (!_loaded || _applying || _closed) return;
                _zoomFit = true; ApplyPreviewZoom();
                _beforeScroll.ScrollToHorizontalOffset(0); _beforeScroll.ScrollToVerticalOffset(0);
                _afterScroll.ScrollToHorizontalOffset(0); _afterScroll.ScrollToVerticalOffset(0);
            });
            fit.ToolTip = "Вписать текущий рисунок или фрагмент в окно";
            toolbar.Children.Add(minus); toolbar.Children.Add(plus); toolbar.Children.Add(_zoomText);
            toolbar.Children.Add(actual); toolbar.Children.Add(fit);
            return toolbar;
        }

        private static Button ZoomButton(string caption, RoutedEventHandler click)
        {
            var button = new Button { Content = caption, FontSize = 11, MinWidth = 30, MinHeight = 28,
                Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 3, 0) };
            button.Click += click; return button;
        }

        private void ConfigurePreviewNavigation(ScrollViewer scroll, Image image)
        {
            image.HorizontalAlignment = HorizontalAlignment.Left; image.VerticalAlignment = VerticalAlignment.Top;
            image.SnapsToDevicePixels = true; image.UseLayoutRounding = true;
            scroll.PreviewMouseWheel += (_, e) =>
            {
                if (!_loaded || _closed || _applying || image.Source == null) return;
                double factor = Math.Pow(1.25, e.Delta / 120.0);
                ZoomTo(CurrentZoom() * factor, scroll, e.GetPosition(scroll)); e.Handled = true;
            };
            scroll.PreviewMouseDown += (_, e) =>
            {
                bool pan = e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right ||
                    (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space));
                if (!pan || !_loaded || _closed || _applying || image.Source == null) return;
                _panScroll = scroll; _panOrigin = e.GetPosition(scroll);
                _panHorizontal = scroll.HorizontalOffset; _panVertical = scroll.VerticalOffset;
                scroll.Cursor = Cursors.Hand; scroll.CaptureMouse(); e.Handled = true;
            };
            scroll.PreviewMouseMove += (_, e) =>
            {
                if (!ReferenceEquals(_panScroll, scroll)) return;
                Point current = e.GetPosition(scroll);
                scroll.ScrollToHorizontalOffset(_panHorizontal + _panOrigin.X - current.X);
                scroll.ScrollToVerticalOffset(_panVertical + _panOrigin.Y - current.Y);
                e.Handled = true;
            };
            scroll.PreviewMouseUp += (_, e) =>
            {
                if (!ReferenceEquals(_panScroll, scroll)) return;
                EndPreviewPan(scroll); e.Handled = true;
            };
            scroll.LostMouseCapture += (_, __) => { if (ReferenceEquals(_panScroll, scroll)) EndPreviewPan(scroll); };
            scroll.SizeChanged += (_, __) => { if (_zoomFit && _loaded && !_layingOutZoom) ApplyPreviewZoom(); };
            scroll.ScrollChanged += (_, e) =>
            {
                if (_zoomFit && _loaded && !_layingOutZoom &&
                    (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)) ApplyPreviewZoom();
            };
        }

        private void EndPreviewPan(ScrollViewer scroll)
        {
            _panScroll = null; scroll.ClearValue(CursorProperty);
            if (scroll.IsMouseCaptured) scroll.ReleaseMouseCapture();
        }

        private double CurrentZoom()
        {
            if (!_zoomFit) return _zoomScale;
            if (!(_before.Source is BitmapSource bitmap)) return 1;
            DpiScale dpi = VisualTreeHelper.GetDpi(_before);
            double width = Math.Max(1, _beforeScroll.ViewportWidth), height = Math.Max(1, _beforeScroll.ViewportHeight);
            return Math.Min(width * dpi.DpiScaleX / bitmap.PixelWidth, height * dpi.DpiScaleY / bitmap.PixelHeight);
        }

        private void ChangeZoom(double multiplier)
        {
            if (!_loaded || _applying || _closed || _before.Source == null) return;
            ZoomTo(CurrentZoom() * multiplier, _beforeScroll, null);
        }

        private void ZoomTo(double scale, ScrollViewer source, Point? position)
        {
            if (source == null || !(_before.Source is BitmapSource) || _layingOutZoom) return;
            Image image = ReferenceEquals(source, _afterScroll) ? _after : _before;
            if (!(image.Source is BitmapSource)) return;
            Point viewport = position ?? new Point(source.ViewportWidth / 2, source.ViewportHeight / 2);
            viewport.X = Math.Max(0, Math.Min(source.ViewportWidth, viewport.X));
            viewport.Y = Math.Max(0, Math.Min(source.ViewportHeight, viewport.Y));
            Point imageOrigin = image.TranslatePoint(new Point(0, 0), source);
            double x = Math.Max(0, Math.Min(1, (viewport.X - imageOrigin.X) / Math.Max(1, image.ActualWidth)));
            double y = Math.Max(0, Math.Min(1, (viewport.Y - imageOrigin.Y) / Math.Max(1, image.ActualHeight)));
            _zoomFit = false; _zoomScale = Math.Max(0.025, Math.Min(32, scale)); ApplyPreviewZoom();
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed) return;
                foreach (var scroll in new[] { _beforeScroll, _afterScroll })
                {
                    Image target = ReferenceEquals(scroll, _beforeScroll) ? _before : _after;
                    if (!(target.Source is BitmapSource)) continue;
                    scroll.ScrollToHorizontalOffset(target.Margin.Left + target.Width * x - viewport.X);
                    scroll.ScrollToVerticalOffset(target.Margin.Top + target.Height * y - viewport.Y);
                }
            }), DispatcherPriority.Loaded);
        }

        private void SyncResultPosition()
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed || _after.Source == null) return;
                _afterScroll.ScrollToHorizontalOffset(_beforeScroll.HorizontalOffset);
                _afterScroll.ScrollToVerticalOffset(_beforeScroll.VerticalOffset);
            }), DispatcherPriority.Loaded);
        }

        private void ApplyPreviewZoom()
        {
            if (_layingOutZoom || _beforeScroll == null || _afterScroll == null) return;
            _layingOutZoom = true;
            try
            {
                double scale = Math.Max(0.025, Math.Min(32, CurrentZoom()));
                foreach (var scroll in new[] { _beforeScroll, _afterScroll })
                {
                    Image image = ReferenceEquals(scroll, _beforeScroll) ? _before : _after;
                    if (!(image.Source is BitmapSource bitmap)) continue;
                    DpiScale dpi = VisualTreeHelper.GetDpi(image);
                    image.Stretch = Stretch.Fill;
                    image.Width = bitmap.PixelWidth * scale / dpi.DpiScaleX;
                    image.Height = bitmap.PixelHeight * scale / dpi.DpiScaleY;
                    image.Margin = new Thickness(Math.Max(0, (scroll.ViewportWidth - image.Width) / 2),
                        Math.Max(0, (scroll.ViewportHeight - image.Height) / 2), 0, 0);
                    scroll.HorizontalScrollBarVisibility = _zoomFit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
                    scroll.VerticalScrollBarVisibility = _zoomFit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
                    RenderOptions.SetBitmapScalingMode(image, scale >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                }
                _zoomText.Text = (scale * 100).ToString(scale < 0.1 ? "0.#" : "0", CultureInfo.CurrentCulture) + "%";
            }
            finally { _layingOutZoom = false; }
        }
    }
}
