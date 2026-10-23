using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class SportArtworkThumbnail : Button
    {
        private static readonly SemaphoreSlim Readers = new SemaphoreSlim(2);
        private readonly TextBox _path;
        private readonly string _title;
        private readonly Image _image = new Image { Stretch = Stretch.Uniform };
        private readonly TextBlock _placeholder = new TextBlock { Text = "—", FontSize = 15,
            Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        private readonly DispatcherTimer _delay;
        private CancellationTokenSource _request;
        private BitmapSource _bitmap;
        private int _revision;

        internal SportArtworkThumbnail(TextBox path, string title)
        {
            _path = path; _title = title;
            Width = 68; Height = 68; Padding = new Thickness(2);
            Margin = new Thickness(0, 2, 6, 4); VerticalAlignment = VerticalAlignment.Top;
            Cursor = Cursors.Hand;
            var content = new Grid { Background = Checker() };
            content.Children.Add(_image); content.Children.Add(_placeholder); Content = content;
            ToolTip = "Рисунок не выбран";
            _delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _delay.Tick += (_, __) => { _delay.Stop(); LoadPreview(); };
            _path.TextChanged += (_, __) => Refresh();
            Loaded += (_, __) => Refresh();
            Unloaded += (_, __) => { _delay.Stop(); Cancel(); };
            Click += (_, __) => ShowPreview();
        }

        internal void Refresh()
        {
            Cancel(); _delay.Stop();
            _bitmap = null; _image.Source = null;
            _placeholder.Visibility = Visibility.Visible;
            bool empty = String.IsNullOrWhiteSpace(_path.Text);
            _placeholder.Text = empty ? "—" : "…";
            ToolTip = empty ? "Рисунок не выбран" : "Загружаю миниатюру…";
            if (!empty && IsLoaded) _delay.Start();
        }

        private void Cancel()
        {
            ++_revision;
            _request?.Cancel();
        }

        private async void LoadPreview()
        {
            string path = _path.Text.Trim();
            int revision = _revision;
            var request = new CancellationTokenSource();
            _request = request;
            CancellationToken token = request.Token;
            try
            {
                await Readers.WaitAsync(token);
                BitmapSource bitmap;
                try { bitmap = await Task.Run(() => { token.ThrowIfCancellationRequested();
                    return SportArtworkPreview.Load(path); }, token); }
                finally { Readers.Release(); }
                if (token.IsCancellationRequested || revision != _revision || !IsLoaded) return;
                _bitmap = bitmap; _image.Source = bitmap;
                _placeholder.Visibility = Visibility.Collapsed;
                ToolTip = _title + " · " + Path.GetFileName(path) + "\nНажмите, чтобы увеличить";
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (token.IsCancellationRequested || revision != _revision || !IsLoaded) return;
                _placeholder.Text = "?";
                ToolTip = "Миниатюра недоступна: " + error.GetBaseException().Message;
                Log.Error("Could not load sport artwork thumbnail.", error);
            }
            finally
            {
                if (ReferenceEquals(_request, request)) _request = null;
                request.Dispose();
            }
        }

        private void ShowPreview()
        {
            if (_bitmap == null) { Refresh(); return; }
            var window = new Window { Title = _title + " · " + Path.GetFileName(_path.Text.Trim()),
                Width = 620, Height = 660, MinWidth = 260, MinHeight = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Content = new Border { Background = Checker(), Padding = new Thickness(12),
                    Child = new Image { Source = _bitmap, Stretch = Stretch.Uniform } } };
            Window owner = Window.GetWindow(this);
            if (owner != null) window.Owner = owner;
            else if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
                new System.Windows.Interop.WindowInteropHelper(window).Owner = source.Handle;
            window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
            window.ShowDialog();
        }

        private static Brush Checker()
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.WhiteSmoke, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            var squares = new GeometryGroup();
            squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
            squares.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
            drawing.Children.Add(new GeometryDrawing(Brushes.LightGray, null, squares));
            var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16), Stretch = Stretch.None };
            brush.Freeze(); return brush;
        }
    }
}
