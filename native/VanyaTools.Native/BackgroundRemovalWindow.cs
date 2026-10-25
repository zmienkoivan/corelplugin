using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class BackgroundRemovalOptions
    {
        internal Color Color = Colors.White;
        internal int Tolerance = 10;
        internal int TrimPixels;
        internal BackgroundRemovalOptions Copy() => (BackgroundRemovalOptions)MemberwiseClone();
    }

    // This window never captures, imports, or modifies Corel objects.
    internal sealed partial class BackgroundRemovalWindow : Window
    {
        private readonly string _sourcePath;
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "Vanya-BackgroundPreview-" + Guid.NewGuid().ToString("N"));
        private readonly Func<Color, Color[]> _backgroundColors;
        private readonly StackPanel _settings;
        private readonly TextBox _hex, _tolerance, _trim;
        private readonly Slider _toleranceSlider, _trimSlider;
        private readonly Button _swatch, _apply, _cancel, _backgroundColor;
        private readonly ComboBox _quality, _background;
        private readonly TextBlock _message, _metrics, _resultHint;
        private readonly Image _before, _after;
        private readonly ScrollViewer _beforeScroll, _afterScroll;
        private readonly DispatcherTimer _delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        private CancellationTokenSource _previewCancellation, _applyCancellation;
        private BitmapSource _original;
        private PreviewInput _previewInput;
        private bool _loaded, _closed, _loading, _rendering, _applying, _syncing, _pending, _syncingScroll, _previewRequested;
        private int _revision, _displayRevision = -1;
        private Color _displayBackgroundColor = Color.FromRgb(95, 115, 130);

        internal BackgroundRemovalOptions Options { get; private set; }
        internal string OutputPath { get; private set; }

        internal BackgroundRemovalWindow(string sourcePath, BackgroundRemovalOptions initial = null,
            Func<Color, Color[]> backgroundColors = null)
        {
            _sourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
            _backgroundColors = backgroundColors;
            Options = (initial ?? new BackgroundRemovalOptions()).Copy();
            Title = "Удаление фона · Vanya Tools";
            Width = Math.Min(1220, SystemParameters.WorkArea.Width - 40);
            Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
            MinWidth = 800; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);
            DockerTheme.UseTheme(this, Control.BackgroundProperty, DockerTheme.WindowBackground);
            var root = new Grid { Margin = new Thickness(12) }; Content = root;
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _settings = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            root.Children.Add(new ScrollViewer { Content = _settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            _settings.Children.Add(Text("Удалить цвет фона", 16, true));
            _settings.Children.Add(Note("Настройки меняют только предпросмотр. Готовый результат вставляется кнопкой «Применить в Corel»."));
            var colors = new Grid();
            colors.ColumnDefinitions.Add(new ColumnDefinition());
            colors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _hex = Field(Hex(Options.Color)); colors.Children.Add(_hex);
            var swatch = new Border { Width = 24, Height = 20, Background = new SolidColorBrush(Options.Color),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            _swatch = Button("", (_, __) => ChooseColor(false)); _swatch.Content = swatch;
            _swatch.Padding = new Thickness(4); _swatch.Margin = new Thickness(5, 0, 0, 0);
            _swatch.ToolTip = "Выбрать цвет фона"; Grid.SetColumn(_swatch, 1); colors.Children.Add(_swatch);
            _settings.Children.Add(Labeled("Цвет фона", colors));
            _settings.Children.Add(Note("Щёлкните по фону на исходнике слева. Цвет будет выбран из самого изображения."));
            _settings.Children.Add(Button("Пипетка с экрана", async (_, __) =>
            {
                if (_closed || _applying) return;
                try { Color? selected = await ScreenColorPicker.PickAsync();
                    if (!_closed && selected.HasValue) SelectBackgroundColor(selected.Value); }
                catch (Exception error) { ShowMessage(error.GetBaseException().Message, true); }
            }));
            _tolerance = Field(Options.Tolerance.ToString(CultureInfo.InvariantCulture));
            _settings.Children.Add(Labeled("Допуск цвета · 0–80", _tolerance));
            _toleranceSlider = Slider(0, 80, Math.Max(0, Math.Min(80, Options.Tolerance)));
            _settings.Children.Add(_toleranceSlider);
            _trim = Field(Options.TrimPixels.ToString(CultureInfo.InvariantCulture));
            _settings.Children.Add(Labeled("Срез края · 0–8 px исходника", _trim));
            _trimSlider = Slider(0, 8, Math.Max(0, Math.Min(8, Options.TrimPixels))); _settings.Children.Add(_trimSlider);
            _settings.Children.Add(Note("Удаляется только цвет, соединённый с краем изображения. Внутренние участки сохраняются."));
            _settings.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
            _quality = Choice("Авто · до 8 Мп", "Обзор · 1600 px", "Обзор · 2800 px", "Обзор · 4000 px", "Оригинал · точно");
            _settings.Children.Add(Labeled("Точность предпросмотра", _quality));
            _metrics = Note("Загружаю исходник…"); _settings.Children.Add(_metrics);
            _background = Choice("Шашки", "Тёмно-серый", "Белый", "Цветной");
            _settings.Children.Add(Labeled("Фон просмотра", _background));
            _backgroundColor = Button("Выбрать цвет просмотра", (_, __) => ChooseColor(true));
            _backgroundColor.IsEnabled = false; _settings.Children.Add(_backgroundColor);

            var previews = new Grid(); Grid.SetColumn(previews, 1); root.Children.Add(previews);
            previews.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            previews.RowDefinitions.Add(new RowDefinition());
            var toolbar = CreateZoomToolbar(); previews.Children.Add(toolbar);
            var pair = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(pair, 1); previews.Children.Add(pair);
            pair.Children.Add(Preview("Исходник · щёлкните по фону", out _before, out _beforeScroll));
            var afterPanel = (Grid)Preview("Результат", out _after, out _afterScroll); Grid.SetColumn(afterPanel, 1); pair.Children.Add(afterPanel);
            _resultHint = Text("Щёлкните по фону на исходнике слева, чтобы увидеть результат.", 12);
            _resultHint.TextAlignment = TextAlignment.Center; _resultHint.Padding = new Thickness(12);
            _resultHint.MaxWidth = 280; _resultHint.HorizontalAlignment = HorizontalAlignment.Center;
            _resultHint.VerticalAlignment = VerticalAlignment.Center; _resultHint.IsHitTestVisible = false;
            DockerTheme.UseTheme(_resultHint, TextBlock.BackgroundProperty, DockerTheme.SurfaceBackground);
            Grid.SetRow(_resultHint, 1); afterPanel.Children.Add(_resultHint);
            ConfigurePreviewNavigation(_beforeScroll, _before); ConfigurePreviewNavigation(_afterScroll, _after);
            _beforeScroll.ScrollChanged += (_, e) => SyncScroll(_beforeScroll, _afterScroll, e);
            _afterScroll.ScrollChanged += (_, e) => SyncScroll(_afterScroll, _beforeScroll, e);
            _before.MouseLeftButtonDown += (_, e) => PickFromImage(e);
            _before.Cursor = Cursors.Cross;
            _before.ToolTip = "Щёлкните по цвету фона. Для перемещения используйте ПКМ или пробел + ЛКМ.";
            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
            Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2); root.Children.Add(footer);
            _cancel = Button("Закрыть", (_, __) => { if (_applying) _applyCancellation?.Cancel(); else Close(); });
            _cancel.MinWidth = 85; DockPanel.SetDock(_cancel, Dock.Right); footer.Children.Add(_cancel);
            _apply = DockerTheme.Primary(Button("Применить в Corel", async (_, __) => await ApplyAsync()));
            _apply.IsEnabled = false; _apply.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(_apply, Dock.Right); footer.Children.Add(_apply);
            _message = Text("", 11); _message.VerticalAlignment = VerticalAlignment.Center;
            _message.Margin = new Thickness(0, 0, 8, 0); footer.Children.Add(_message);

            _hex.TextChanged += (_, __) =>
            {
                if (TryColor(_hex.Text, out Color color)) swatch.Background = new SolidColorBrush(color);
                QueuePreview();
            };
            _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { QueuePreview(); e.Handled = true; } };
            _tolerance.TextChanged += (_, __) => SyncField(_tolerance, _toleranceSlider);
            _trim.TextChanged += (_, __) => SyncField(_trim, _trimSlider);
            _toleranceSlider.ValueChanged += (_, __) => SyncSlider(_toleranceSlider, _tolerance);
            _trimSlider.ValueChanged += (_, __) => SyncSlider(_trimSlider, _trim);
            _quality.SelectionChanged += (_, __) => QueuePreview(false);
            _background.SelectionChanged += (_, __) => UpdatePreviewBackground();
            _delay.Tick += async (_, __) => { _delay.Stop(); await PreviewAsync(); };
            Loaded += async (_, __) => await LoadAsync();
            Closed += (_, __) =>
            {
                if (_loaded) ReadOptions(out BackgroundRemovalOptions unused);
                _closed = true; ++_revision; _delay.Stop();
                _previewCancellation?.Cancel(); _applyCancellation?.Cancel(); Cleanup();
            };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_applying) _applyCancellation?.Cancel(); else Close(); e.Handled = true; } };
            UpdatePreviewBackground();
        }

        private async Task LoadAsync()
        {
            if (_loading || _loaded || _closed) return;
            _loading = true; _settings.IsEnabled = false;
            try
            {
                string source = _sourcePath, workspace = _workspace;
                BitmapSource original = await Task.Run(() => { Directory.CreateDirectory(workspace); return LoadDetached(source); }).ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (_closed) return;
                    _original = original; _loaded = true; _settings.IsEnabled = true;
                    // Choosing a background must be possible before the first successful removal.
                    _before.Source = original; ApplyPreviewZoom();
                    _metrics.Text = original.PixelWidth + " × " + original.PixelHeight + " px · исходник загружен.";
                    ShowMessage("Щёлкните по фону на исходнике слева.", false);
                }).ConfigureAwait(false);
            }
            catch (Exception error) { await OnUiAsync(() => { if (!_closed) {
                _metrics.Text = "Не удалось загрузить исходник.";
                ShowMessage(error.GetBaseException().Message, true); } }).ConfigureAwait(false); }
            finally { await OnUiAsync(() => { _loading = false; Cleanup(); }).ConfigureAwait(false); }
        }

        private void SyncField(TextBox field, Slider slider)
        {
            if (_syncing) return;
            _syncing = true;
            try { if (Int32.TryParse(field.Text, out int value) && value >= slider.Minimum && value <= slider.Maximum) slider.Value = value; }
            finally { _syncing = false; }
            QueuePreview();
        }
        private void SyncSlider(Slider slider, TextBox field)
        {
            if (_syncing) return;
            _syncing = true; try { field.Text = ((int)Math.Round(slider.Value)).ToString(CultureInfo.InvariantCulture); }
            finally { _syncing = false; }
            QueuePreview();
        }
        private bool ReadOptions(out BackgroundRemovalOptions options)
        {
            options = null;
            if (!TryColor(_hex.Text, out Color color)) { ShowMessage("Цвет фона: #RRGGBB.", true); return false; }
            if (!Int32.TryParse(_tolerance.Text, out int tolerance) || tolerance < 0 || tolerance > 80)
            { ShowMessage("Допуск цвета: от 0 до 80.", true); return false; }
            if (!Int32.TryParse(_trim.Text, out int trim) || trim < 0 || trim > 8)
            { ShowMessage("Срез края: от 0 до 8 пикселей исходника.", true); return false; }
            options = new BackgroundRemovalOptions { Color = color, Tolerance = tolerance, TrimPixels = trim };
            Options = options.Copy(); return true;
        }
        private Color[] ResolveColors(Color color)
        {
            // The callback may use Corel COM for CMYK conversion: UI thread only.
            Color[] colors = _backgroundColors?.Invoke(color);
            return colors == null || colors.Length == 0 ? new[] { color } : (Color[])colors.Clone();
        }
        private void QueuePreview(bool process = true)
        {
            if (_syncing || !_loaded || _closed || _applying) return;
            _previewRequested |= process;
            ++_revision; _apply.IsEnabled = false; _after.Opacity = 0.45;
            if (_after.Source == null) ShowResultHint("Подготавливаю предпросмотр…");
            _previewCancellation?.Cancel(); _delay.Stop(); _delay.Start();
        }
        private async Task PreviewAsync()
        {
            if (_closed || !_loaded || _applying) return;
            if (_rendering) { _pending = true; return; }
            if (!ReadOptions(out BackgroundRemovalOptions options))
            { ClearResult("Проверьте цвет фона, допуск и срез."); return; }
            Color[] colors;
            try { colors = ResolveColors(options.Color); }
            catch (Exception error) { ClearResult("Не удалось подготовить выбранный цвет."); ShowMessage(error.GetBaseException().Message, true); return; }
            int revision = _revision, quality = _quality.SelectedIndex;
            bool process = _previewRequested;
            BitmapSource original = _original; PreviewInput cached = _previewInput;
            string workspace = _workspace;
            _previewCancellation = new CancellationTokenSource();
            var cancellationSource = _previewCancellation; CancellationToken cancellation = cancellationSource.Token;
            _rendering = true; _pending = false; ShowMessage("Пересчитываю предпросмотр…", false);
            try
            {
                PreviewInput input = await Task.Run(() => PreparePreviewInput(original, cached, quality, workspace, cancellation), cancellation).ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (_closed || revision != _revision || _applying) return;
                    _previewInput = input; _before.Source = input.Bitmap;
                    if (_after.Source is BitmapSource previous &&
                        (previous.PixelWidth != input.Bitmap.PixelWidth || previous.PixelHeight != input.Bitmap.PixelHeight))
                        ClearResult("Подготавливаю предпросмотр…");
                    _metrics.Text = PreviewMetrics(original, input, options);
                    ApplyPreviewZoom();
                }).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (!process)
                {
                    await OnUiAsync(() => { if (!_closed && revision == _revision) {
                        ShowResultHint("Щёлкните по фону на исходнике слева, чтобы увидеть результат.");
                        ShowMessage("Щёлкните по фону на исходнике слева.", false);
                    } }).ConfigureAwait(false);
                    return;
                }
                PreviewResult result = await Task.Run(() => BuildPreview(input, workspace, colors, options, cancellation), cancellation).ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (_closed || revision != _revision || _applying) return;
                    _after.Source = result.Bitmap; _resultHint.Visibility = Visibility.Collapsed;
                    _after.Opacity = 1; _displayRevision = revision;
                    ApplyPreviewZoom();
                    SyncResultPosition();
                    ShowMessage("Готово. Колесо — масштаб; ПКМ или пробел + ЛКМ — перемещение.", false);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                await OnUiAsync(() => { if (!_closed && revision == _revision) {
                    ClearResult("Не удалось удалить выбранный цвет.\nВыберите фон щелчком по исходнику слева или измените допуск.");
                    ShowMessage(error.GetBaseException().Message + " Щёлкните по нужному цвету на исходнике слева.", true);
                } }).ConfigureAwait(false);
            }
            finally
            {
                await OnUiAsync(() =>
                {
                    _rendering = false;
                    if (!_closed && !_applying) _apply.IsEnabled = _displayRevision == _revision;
                    if (ReferenceEquals(_previewCancellation, cancellationSource)) _previewCancellation = null;
                    cancellationSource.Dispose();
                    if (!_closed && !_applying && (_pending || revision != _revision)) { _delay.Stop(); _delay.Start(); }
                    Cleanup();
                }).ConfigureAwait(false);
            }
        }
        private async Task ApplyAsync()
        {
            if (_closed || _applying || _rendering || _displayRevision != _revision || !ReadOptions(out BackgroundRemovalOptions options)) return;
            Color[] colors;
            try { colors = ResolveColors(options.Color); }
            catch (Exception error) { ShowMessage(error.GetBaseException().Message, true); return; }
            _applying = true; _settings.IsEnabled = false; _apply.IsEnabled = false; _cancel.Content = "Отменить";
            _delay.Stop(); _previewCancellation?.Cancel();
            _applyCancellation = new CancellationTokenSource(); var cancellationSource = _applyCancellation;
            CancellationToken cancellation = cancellationSource.Token;
            string source = _sourcePath, output = Path.Combine(Path.GetTempPath(), "Vanya-Background-" + Guid.NewGuid().ToString("N") + ".png");
            ShowMessage("Готовлю результат в полном разрешении…", false);
            bool keep = false;
            try
            {
                await Task.Run(() => BackgroundRemovalProcessor.RemoveColor(source, output, colors,
                    options.Tolerance, options.TrimPixels, cancellation), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                await OnUiAsync(() =>
                {
                    if (_closed || cancellation.IsCancellationRequested) return;
                    OutputPath = output; keep = true; DialogResult = true;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { await OnUiAsync(() => { if (!_closed) ShowMessage("Операция отменена.", false); }).ConfigureAwait(false); }
            catch (Exception error) { await OnUiAsync(() => { if (!_closed) ShowMessage(error.GetBaseException().Message, true); }).ConfigureAwait(false); }
            finally
            {
                if (!keep) DeleteFile(output);
                await OnUiAsync(() =>
                {
                    _applying = false;
                    if (ReferenceEquals(_applyCancellation, cancellationSource)) _applyCancellation = null;
                    cancellationSource.Dispose();
                    if (!_closed) { _settings.IsEnabled = true; _cancel.Content = "Закрыть"; _apply.IsEnabled = _displayRevision == _revision; }
                    Cleanup();
                }).ConfigureAwait(false);
            }
        }
        private sealed class PreviewInput
        {
            internal int Quality; internal double Scale; internal string Path; internal BitmapSource Bitmap;
        }
        private sealed class PreviewResult
        {
            internal BitmapSource Bitmap;
        }
        private static PreviewInput PreparePreviewInput(BitmapSource original, PreviewInput cached, int quality,
            string workspace, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            PreviewInput input = cached;
            if (input == null || input.Quality != quality)
            {
                double scale = quality == 4 ? 1 : quality == 0
                    ? Math.Min(1, Math.Sqrt(8000000.0 / ((double)original.PixelWidth * original.PixelHeight)))
                    : Math.Min(1, (quality == 1 ? 1600.0 : quality == 2 ? 2800.0 : 4000.0) / Math.Max(original.PixelWidth, original.PixelHeight));
                BitmapSource bitmap = original;
                string inputPath = Path.Combine(workspace, "source-" + quality + ".png");
                if (scale < .999999)
                {
                    bitmap = DtfCorrectionPixels.Capture(new TransformedBitmap(original, new ScaleTransform(scale, scale))).Bitmap();
                    scale = Math.Min((double)bitmap.PixelWidth / original.PixelWidth, (double)bitmap.PixelHeight / original.PixelHeight);
                }
                Save(bitmap, inputPath); cancellation.ThrowIfCancellationRequested();
                input = new PreviewInput { Quality = quality, Scale = scale, Path = inputPath, Bitmap = bitmap };
            }
            return input;
        }
        private static int PreviewTrim(PreviewInput input, BackgroundRemovalOptions options) =>
            Math.Max(0, Math.Min(8, (int)Math.Round(options.TrimPixels * input.Scale, MidpointRounding.AwayFromZero)));

        private static string PreviewMetrics(BitmapSource original, PreviewInput input, BackgroundRemovalOptions options)
        {
            string size = original.PixelWidth + " × " + original.PixelHeight + " px";
            return input.Scale >= .999999
                ? size + " · просмотр в исходном разрешении.\nСрез " + options.TrimPixels + " px."
                : size + " → " + input.Bitmap.PixelWidth + " × " + input.Bitmap.PixelHeight + " px.\nОбзор: срез " + options.TrimPixels + " px → " + PreviewTrim(input, options) + " px просмотра (округлено). Для точной оценки выберите «Оригинал».";
        }

        private static PreviewResult BuildPreview(PreviewInput input, string workspace, Color[] colors,
            BackgroundRemovalOptions options, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string output = Path.Combine(workspace, "preview-output.png");
            BackgroundRemovalProcessor.RemoveColor(input.Path, output, colors, options.Tolerance, PreviewTrim(input, options), cancellation);
            cancellation.ThrowIfCancellationRequested();
            return new PreviewResult { Bitmap = LoadDetached(output) };
        }

        private void ShowResultHint(string message)
        {
            _resultHint.Text = message; _resultHint.Visibility = Visibility.Visible;
        }

        private void ClearResult(string hint)
        {
            _after.Source = null; _after.Opacity = 1; _displayRevision = -1; _apply.IsEnabled = false;
            ShowResultHint(hint);
        }

        private void SelectBackgroundColor(Color color)
        {
            string hex = Hex(color);
            if (String.Equals(_hex.Text, hex, StringComparison.OrdinalIgnoreCase)) QueuePreview();
            else _hex.Text = hex;
        }
        private void PickFromImage(MouseButtonEventArgs e)
        {
            if (_closed || _applying || !_loaded || _original == null || Keyboard.IsKeyDown(Key.Space)) return;
            Point point = e.GetPosition(_before);
            if (_before.ActualWidth <= 0 || _before.ActualHeight <= 0 || point.X < 0 || point.Y < 0 ||
                point.X >= _before.ActualWidth || point.Y >= _before.ActualHeight) return;
            int x = Math.Max(0, Math.Min(_original.PixelWidth - 1, (int)(point.X * _original.PixelWidth / Math.Max(1, _before.ActualWidth))));
            int y = Math.Max(0, Math.Min(_original.PixelHeight - 1, (int)(point.Y * _original.PixelHeight / Math.Max(1, _before.ActualHeight))));
            byte[] pixel = new byte[4]; _original.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            if (pixel[3] == 0) { ShowMessage("В этой точке прозрачный пиксель. Выберите цветной участок фона.", true); return; }
            SelectBackgroundColor(Color.FromRgb(pixel[2], pixel[1], pixel[0])); e.Handled = true;
        }
        private void ChooseColor(bool display)
        {
            if (_closed || _applying) return;
            try
            {
                Color current = display ? _displayBackgroundColor : (TryColor(_hex.Text, out Color parsed) ? parsed : Colors.White);
                Color? color = WindowsColorPicker.Pick(new WindowInteropHelper(this).Handle, current);
                if (!color.HasValue || _closed) return;
                if (display) { _displayBackgroundColor = color.Value; _background.SelectedIndex = 3; UpdatePreviewBackground(); }
                else SelectBackgroundColor(color.Value);
            }
            catch (Exception error) { ShowMessage(error.GetBaseException().Message, true); }
        }
        private void UpdatePreviewBackground()
        {
            if (_beforeScroll == null || _afterScroll == null) return;
            Brush brush = _background.SelectedIndex == 0 ? Checkerboard() : new SolidColorBrush(
                _background.SelectedIndex == 1 ? Color.FromRgb(55, 55, 55) : _background.SelectedIndex == 2 ? Colors.White : _displayBackgroundColor);
            _beforeScroll.Background = _afterScroll.Background = brush;
            _backgroundColor.IsEnabled = _background.SelectedIndex == 3;
        }
        private void SyncScroll(ScrollViewer source, ScrollViewer target, ScrollChangedEventArgs change)
        {
            if (_syncingScroll || (change.HorizontalChange == 0 && change.VerticalChange == 0)) return;
            _syncingScroll = true;
            try { target.ScrollToHorizontalOffset(source.HorizontalOffset); target.ScrollToVerticalOffset(source.VerticalOffset); }
            finally { _syncingScroll = false; }
        }
        private void ShowMessage(string message, bool error)
        { _message.Text = message; DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, error ? DockerTheme.ErrorTextBrush : DockerTheme.TextBrush); }
        private Task OnUiAsync(Action action)
        { if (Dispatcher.CheckAccess()) { action(); return Task.CompletedTask; } return Dispatcher.InvokeAsync(action).Task; }
        private void Cleanup()
        {
            if (!_closed || _loading || _rendering || _applying) return;
            try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true); } catch { }
        }
        private static void DeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static BitmapSource LoadDetached(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                BitmapSource frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                return DtfCorrectionPixels.Capture(frame).Bitmap();
            }
        }
        private static void Save(BitmapSource bitmap, string path)
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream); }
        private static bool TryColor(string value, out Color color)
        {
            color = Colors.White; string hex = (value ?? "").Trim().TrimStart('#');
            if (hex.Length != 6 || !Int32.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true;
        }
        private static string Hex(Color color) => "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        private static TextBox Field(string value) => new TextBox { Text = value, FontSize = 12, MinHeight = 28, Padding = new Thickness(4, 1, 4, 1) };
        private static Slider Slider(double min, double max, double value) => new Slider { Minimum = min, Maximum = max, Value = value,
            TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 6) };
        private static ComboBox Choice(params string[] choices) => new ComboBox { ItemsSource = choices, SelectedIndex = 0, MinHeight = 28 };
        private static Button Button(string text, RoutedEventHandler click)
        { var button = new Button { Content = text, MinHeight = 30, Padding = new Thickness(7, 3, 7, 3) }; button.Click += click; return button; }
        private static TextBlock Text(string text, int size, bool bold = false) => new TextBlock { Text = text, FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
        private static TextBlock Note(string value)
        { var note = Text(value, 10); DockerTheme.UseTheme(note, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush); return note; }
        private static FrameworkElement Labeled(string title, FrameworkElement field)
        { var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 4) }; panel.Children.Add(Text(title, 11)); panel.Children.Add(field); return panel; }
        private static FrameworkElement Preview(string title, out Image image, out ScrollViewer scroll)
        {
            var panel = new Grid { Margin = new Thickness(3) }; panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition()); panel.Children.Add(Text(title, 12));
            image = new Image { Cursor = Cursors.Arrow };
            scroll = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Checkerboard() };
            var border = new Border { Child = scroll, BorderThickness = new Thickness(1) };
            DockerTheme.UseTheme(border, Border.BorderBrushProperty, DockerTheme.BorderBrush);
            Grid.SetRow(border, 1); panel.Children.Add(border); return panel;
        }
        private static Brush Checkerboard()
        {
            var group = new DrawingGroup(); group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            group.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            group.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
            var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
            brush.Freeze(); return brush;
        }
    }
}
