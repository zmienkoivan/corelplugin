using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class DtfCorrectionOptions
    {
        internal bool RemoveSemiTransparent, SmoothStairs, Outline, WhitenNearWhite;
        internal int Threshold = 130;
        internal int NearWhiteTolerance = 6;
        internal int StairsScale = 1;
        internal double RadiusMm = 2 * 25.4 / 400, ExpansionMm = 0.175;
        internal Color Color = Colors.White;
        internal DtfCorrectionOptions Copy() => (DtfCorrectionOptions)MemberwiseClone();
    }

    internal sealed class DtfCorrectionResult
    {
        internal string OutputPath;
        internal Tuple<double, double> OffsetMm = Tuple.Create(0.0, 0.0);
    }

    /// <summary>All processing is local. Only the caller captures and imports Corel artwork.</summary>
    internal sealed partial class DtfCorrectionWindow : Window
    {
        private readonly string _sourcePath;
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "Vanya-Correction-" + Guid.NewGuid().ToString("N"));
        private readonly Func<string, string, DtfCorrectionOptions, CancellationToken, DtfCorrectionResult> _processor;
        private readonly CheckBox _remove, _stairs, _outline, _highlight, _highlightWhite, _whiten;
        private readonly TextBox _threshold, _radius, _expansion, _hex;
        private readonly Slider _thresholdSlider, _nearWhiteTolerance;
        private readonly ComboBox _units, _view, _marker;
        private readonly TextBlock _message, _metrics, _nearWhiteValue;
        private readonly Button _apply, _cancel, _swatch, _nearWhiteSwatch;
        private Color _nearWhiteMarker = Color.FromRgb(255, 0, 168);
        private readonly Image _before, _after;
        private readonly StackPanel _settingsPanel;
        private readonly ScrollViewer _beforeScroll, _afterScroll;
        private readonly DispatcherTimer _delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        private BitmapSource _overview;
        private DtfCorrectionPixels _originalPixels, _overviewPixels;
        private string _overviewPath;
        private CancellationTokenSource _previewCancellation, _applyCancellation;
        private Task _previewTask;
        private bool _syncing, _syncingScroll, _loaded, _closed, _rendering, _applying, _retryPreview;
        private int _revision, _previousUnit;
        private int _displayView = -1;
        private double _cropX = 0.5, _cropY = 0.5;
        internal DtfCorrectionResult Result { get; private set; }
        internal DtfCorrectionOptions Options { get; private set; }

        internal DtfCorrectionWindow(string sourcePath, DtfCorrectionOptions initial,
            DtfOutlineSettings presets,
            Func<string, string, DtfCorrectionOptions, CancellationToken, DtfCorrectionResult> processor)
        {
            _sourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            Options = (initial ?? new DtfCorrectionOptions()).Copy();
            presets = presets ?? new DtfOutlineSettings();
            Title = "Коррекция и обводка · Vanya Tools";
            Width = Math.Min(1220, SystemParameters.WorkArea.Width - 40);
            Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
            MinWidth = 860; MinHeight = 570; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);
            DockerTheme.UseTheme(this, Control.BackgroundProperty, DockerTheme.WindowBackground);
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(268) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            Content = root;

            var controls = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; _settingsPanel = controls;
            root.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            controls.Children.Add(Text("Коррекция", 16, true));
            controls.Children.Add(Text("Настройки меняют предпросмотр. В Corel вставляется только готовый результат.", 11));
            controls.Children.Add(Text("Экспресс-аудит", 12, true));
            var alphaAudit = new Grid();
            alphaAudit.ColumnDefinitions.Add(new ColumnDefinition());
            alphaAudit.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _highlight = Check("Полупрозрачные", false); _highlight.FontSize = 11;
            _highlight.ToolTip = "Подсветить полупрозрачные пиксели только в предпросмотре";
            alphaAudit.Children.Add(_highlight);
            _marker = Combo(new[] { "Зелёный", "Красный" }); _marker.Width = 85;
            _marker.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(_marker, 1); alphaAudit.Children.Add(_marker);
            controls.Children.Add(alphaAudit);
            var whiteActions = new Grid();
            whiteActions.ColumnDefinitions.Add(new ColumnDefinition());
            whiteActions.ColumnDefinitions.Add(new ColumnDefinition());
            whiteActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _highlightWhite = Check("Почти белое", false); _highlightWhite.FontSize = 11;
            _whiten = Check("Подбелить", Options.WhitenNearWhite); _whiten.FontSize = 11;
            _highlightWhite.ToolTip = "Подсветить почти белые пиксели в пределах допуска. Точный белый не подсвечивается.";
            _whiten.ToolTip = "Исправить почти белые пиксели на чистый белый. Цвета вне допуска и прозрачность сохраняются.";
            whiteActions.Children.Add(_highlightWhite); Grid.SetColumn(_whiten, 1); whiteActions.Children.Add(_whiten);
            _nearWhiteSwatch = new Button { Width = 26, Height = 24, Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(_nearWhiteMarker), ToolTip = "Цвет подсветки почти белого · 50%" };
            _nearWhiteSwatch.Padding = new Thickness(1); _nearWhiteSwatch.Content = SwatchContent(_nearWhiteSwatch);
            Grid.SetColumn(_nearWhiteSwatch, 2); whiteActions.Children.Add(_nearWhiteSwatch);
            controls.Children.Add(whiteActions);
            var whiteToleranceRow = new Grid { Margin = new Thickness(0, 1, 0, 2) };
            whiteToleranceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            whiteToleranceRow.ColumnDefinitions.Add(new ColumnDefinition());
            whiteToleranceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            whiteToleranceRow.Children.Add(new TextBlock { Text = "Допуск", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _nearWhiteTolerance = new Slider { Minimum = 0, Maximum = 64,
                Value = Math.Max(0, Math.Min(64, Options.NearWhiteTolerance)),
                IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            _nearWhiteTolerance.ToolTip = "Допуск каждого канала RGB от белого. Например, 6 — значения 249–254; точный белый не меняется.";
            System.Windows.Automation.AutomationProperties.SetName(_nearWhiteTolerance, "Допуск почти белого");
            Grid.SetColumn(_nearWhiteTolerance, 1); whiteToleranceRow.Children.Add(_nearWhiteTolerance);
            _nearWhiteValue = new TextBlock { Text = ((int)_nearWhiteTolerance.Value).ToString(), FontSize = 11,
                MinWidth = 20, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0) };
            Grid.SetColumn(_nearWhiteValue, 2); whiteToleranceRow.Children.Add(_nearWhiteValue);
            controls.Children.Add(whiteToleranceRow);
            controls.Children.Add(Text("Подсветка — только для просмотра. «Подбелить» исправляет почти белое на чистый белый, сохраняя прозрачность.", 10));
            _nearWhiteSwatch.Click += (_, __) =>
            {
                if (_applying || _closed) return;
                try
                {
                    Color? picked = WindowsColorPicker.Pick(new WindowInteropHelper(this).Handle, _nearWhiteMarker);
                    if (!picked.HasValue) return;
                    _nearWhiteMarker = picked.Value; _nearWhiteSwatch.Background = new SolidColorBrush(picked.Value); QueuePreview();
                }
                catch (Exception error) { ShowMessage(error.GetBaseException().Message, true); }
            };

            controls.Children.Add(new Separator { Margin = new Thickness(0, 9, 0, 8) });
            _remove = Check("Убрать полупрозрачность", Options.RemoveSemiTransparent);
            controls.Children.Add(_remove);
            _threshold = Field(Options.Threshold.ToString());
            controls.Children.Add(Labeled("Порог · 0–255", _threshold));
            _thresholdSlider = new Slider { Minimum = 0, Maximum = 255, Value = Options.Threshold,
                IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 0, 0, 6) };
            controls.Children.Add(_thresholdSlider);
            _stairs = Check("Сгладить лесенку", Options.SmoothStairs); controls.Children.Add(_stairs);
            _stairs.ToolTip = "Сглаживает край и цветовые границы. Порог применяется только при включённом удалении полупрозрачности.";

            controls.Children.Add(new Separator { Margin = new Thickness(0, 9, 0, 8) });
            _outline = Check("Обводка и сглаживание края", Options.Outline); controls.Children.Add(_outline);
            var presetRow = new UniformGrid { Columns = 3, Margin = new Thickness(0, 3, 0, 6) };
            presetRow.Children.Add(Preset(false, presets.ThinMm)); presetRow.Children.Add(Preset(true, presets.ThickMm));
            _units = Combo(new[] { "мм", "px" }); presetRow.Children.Add(_units); controls.Children.Add(presetRow);
            _radius = Field(DtfOutlineUnits.Format(Options.RadiusMm));
            _expansion = Field(DtfOutlineUnits.Format(Options.ExpansionMm));
            var outlineValues = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            outlineValues.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            outlineValues.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
            outlineValues.ColumnDefinitions.Add(new ColumnDefinition());
            var radiusField = Labeled("Сглаж.", _radius); radiusField.Margin = new Thickness(0, 2, 5, 0);
            radiusField.ToolTip = "Сглаживание края в выбранных единицах"; outlineValues.Children.Add(radiusField);
            var expansionField = Labeled("Обводка +", _expansion); expansionField.Margin = new Thickness(0, 2, 5, 0);
            Grid.SetColumn(expansionField, 1); outlineValues.Children.Add(expansionField);
            var colors = new Grid();
            colors.ColumnDefinitions.Add(new ColumnDefinition());
            colors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _hex = Field(Hex(Options.Color)); _hex.FontSize = 10; _hex.MinWidth = 57; colors.Children.Add(_hex);
            _swatch = new Button { Width = 24, Height = 28, Margin = new Thickness(3, 0, 3, 0),
                ToolTip = "Выбрать цвет обводки", Background = new SolidColorBrush(Options.Color) };
            _swatch.Padding = new Thickness(1); _swatch.Content = SwatchContent(_swatch);
            Grid.SetColumn(_swatch, 1); colors.Children.Add(_swatch);
            var pipette = Button("⌖", async (_, __) =>
            {
                if (_applying || _closed) return;
                try { Color? picked = await ScreenColorPicker.PickAsync(); if (picked.HasValue && !_closed) _hex.Text = Hex(picked.Value); }
                catch (Exception error) { ShowMessage(error.GetBaseException().Message, true); }
            });
            pipette.Width = 24; pipette.MinHeight = 28; pipette.FontSize = 16; pipette.Padding = new Thickness(0);
            pipette.ToolTip = "Пипетка с экрана";
            System.Windows.Automation.AutomationProperties.SetName(pipette, "Пипетка с экрана");
            Grid.SetColumn(pipette, 2); colors.Children.Add(pipette);
            var colorField = Labeled("Цвет", colors); colorField.Margin = new Thickness(0, 2, 0, 0);
            Grid.SetColumn(colorField, 2); outlineValues.Children.Add(colorField); controls.Children.Add(outlineValues);
            pipette.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsChecked") { Source = _outline });
            _swatch.Click += (_, __) =>
            {
                try
                {
                    Color current; if (!TryColor(_hex.Text, out current)) current = Colors.White;
                    Color? picked = WindowsColorPicker.Pick(new WindowInteropHelper(this).Handle, current);
                    if (picked.HasValue) _hex.Text = Hex(picked.Value);
                }
                catch (Exception error) { ShowMessage(error.GetBaseException().Message, true); }
            };
            _metrics = Text("Загружаю исходник…", 10); controls.Children.Add(_metrics);

            var previews = new Grid(); Grid.SetColumn(previews, 1); root.Children.Add(previews);
            previews.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            previews.RowDefinitions.Add(new RowDefinition());
            previews.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var previewToolbar = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            previewToolbar.Children.Add(Text("Исходник и результат", 15, true));
            _view = Combo(new[] { "Весь рисунок", "Точный фрагмент" });
            previewToolbar.Children.Add(CreateZoomToolbar());
            previewToolbar.Children.Add(CreatePreviewSettingsToolbar()); previews.Children.Add(previewToolbar);
            var pair = new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition());
            pair.Children.Add(Preview("Исходник", out _before, out _beforeScroll));
            var after = Preview("Результат", out _after, out _afterScroll); Grid.SetColumn(after, 1); pair.Children.Add(after);
            _beforeScroll.ScrollChanged += (_, e) => SyncScroll(_beforeScroll, _afterScroll, e);
            _afterScroll.ScrollChanged += (_, e) => SyncScroll(_afterScroll, _beforeScroll, e);
            ConfigurePreviewNavigation(_beforeScroll, _before);
            ConfigurePreviewNavigation(_afterScroll, _after);
            Grid.SetRow(pair, 1); previews.Children.Add(pair);
            var hint = Text("Колесо — масштаб; средняя/правая кнопка или пробел + перетаскивание — перемещение. Клик на обзор — точный фрагмент края.", 11);
            Grid.SetRow(hint, 2); previews.Children.Add(hint);
            _before.MouseLeftButtonDown += (_, e) =>
            {
                if (!_loaded || _applying || _view.SelectedIndex != 0 || _before.Source == null ||
                    _panScroll != null || Keyboard.IsKeyDown(Key.Space) || !_zoomFit) return;
                var point = e.GetPosition(_before);
                double scale = Math.Min(_before.ActualWidth / _overview.PixelWidth, _before.ActualHeight / _overview.PixelHeight);
                double width = _overview.PixelWidth * scale, height = _overview.PixelHeight * scale;
                double x = (point.X - (_before.ActualWidth - width) / 2) / width;
                double y = (point.Y - (_before.ActualHeight - height) / 2) / height;
                if (x < 0 || x > 1 || y < 0 || y > 1) return;
                _cropX = x; _cropY = y; _view.SelectedIndex = 1;
            };

            var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2); root.Children.Add(footer);
            _message = Text("Загружаю выделение…", 11); footer.Children.Add(_message);
            _apply = DockerTheme.Primary(Button("Применить в Corel", async (_, __) => await ApplyAsync()));
            _apply.IsEnabled = false; _apply.MinWidth = 142; _apply.Margin = new Thickness(10, 0, 5, 0);
            Grid.SetColumn(_apply, 1); footer.Children.Add(_apply);
            _cancel = Button("Закрыть", (_, __) => { if (_applying) _applyCancellation?.Cancel(); else Close(); });
            _cancel.MinWidth = 86; Grid.SetColumn(_cancel, 2); footer.Children.Add(_cancel);

            _threshold.TextChanged += (_, __) =>
            {
                if (_syncing) return;
                if (Int32.TryParse(_threshold.Text, out int value) && value >= 0 && value <= 255)
                { _syncing = true; _thresholdSlider.Value = value; _syncing = false; }
                QueuePreview();
            };
            _thresholdSlider.ValueChanged += (_, __) =>
            {
                if (_syncing) return; _syncing = true; _threshold.Text = ((int)_thresholdSlider.Value).ToString(); _syncing = false; QueuePreview();
            };
            foreach (var toggle in new[] { _remove, _stairs, _outline, _highlight, _highlightWhite, _whiten })
            { toggle.Checked += (_, __) => QueuePreview(); toggle.Unchecked += (_, __) => QueuePreview(); }
            _nearWhiteTolerance.ValueChanged += (_, __) =>
            { _nearWhiteValue.Text = ((int)_nearWhiteTolerance.Value).ToString(); QueuePreview(); };
            _radius.TextChanged += (_, __) => QueuePreview(); _expansion.TextChanged += (_, __) => QueuePreview();
            _hex.TextChanged += (_, __) =>
            { if (TryColor(_hex.Text, out Color color)) _swatch.Background = new SolidColorBrush(color); QueuePreview(); };
            _marker.SelectionChanged += (_, __) => QueuePreview();
            _view.SelectionChanged += (_, __) => QueuePreview();
            _units.SelectionChanged += (_, __) => SwitchUnits();
            _delay.Tick += async (_, __) => { _delay.Stop(); await PreviewAsync(); };
            Loaded += async (_, __) => await LoadAsync();
            Closed += (_, __) =>
            {
                _closed = true; _delay.Stop(); _previewCancellation?.Cancel(); _applyCancellation?.Cancel();
                _qualityCancellation?.Cancel(); _sourceCancellation?.Cancel(); Cleanup();
            };
            SetControlsEnabled(false);
        }

        private async Task LoadAsync()
        {
            if (_loaded || _closed || _loadingSource) return;
            _loadingSource = true;
            var cancellationSource = new CancellationTokenSource(); _sourceCancellation = cancellationSource;
            CancellationToken cancellation = cancellationSource.Token;
            int quality = SelectedPreviewQuality();
            try
            {
                Directory.CreateDirectory(_workspace);
                var loaded = await Task.Run(() =>
                {
                    BitmapSource image = Load(_sourcePath);
                    cancellation.ThrowIfCancellationRequested();
                    PreviewOverview overview = BuildPreviewOverview(image, quality, _workspace, cancellation);
                    var original = DtfCorrectionPixels.Capture(image); cancellation.ThrowIfCancellationRequested();
                    return Tuple.Create(original, overview);
                }, cancellation).ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (_closed) { Cleanup(); return; }
                    _originalPixels = loaded.Item1; UsePreviewOverview(loaded.Item2);
                    _loaded = true; _before.Source = _overview; SetControlsEnabled(true); QueuePreview();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Log.Error("Correction source load failed.", error);
                await OnUiAsync(() => { if (!_closed) ShowMessage("Не удалось загрузить исходник: " + error.GetBaseException().Message, true); });
            }
            finally
            {
                await OnUiAsync(() =>
                {
                    _loadingSource = false; cancellationSource.Dispose();
                    if (ReferenceEquals(_sourceCancellation, cancellationSource)) _sourceCancellation = null;
                    if (_closed) Cleanup();
                });
            }
        }

        private void SwitchUnits()
        {
            if (_syncing || !_loaded || _units.SelectedIndex == _previousUnit) return;
            _syncing = true;
            if (DtfOutlineUnits.TryNumber(_radius.Text, out double radius) && DtfOutlineUnits.TryNumber(_expansion.Text, out double expansion))
            {
                double factor = _units.SelectedIndex == 0 ? 25.4 / _originalPixels.DpiX : _originalPixels.DpiX / 25.4;
                _radius.Text = DtfOutlineUnits.Format(radius * factor); _expansion.Text = DtfOutlineUnits.Format(expansion * factor);
                _previousUnit = _units.SelectedIndex;
            }
            else _units.SelectedIndex = _previousUnit;
            _syncing = false; QueuePreview();
        }

        private bool ReadOptions(out DtfCorrectionOptions options)
        {
            options = null;
            if (!_loaded) return false;
            bool remove = _remove.IsChecked == true, outline = _outline.IsChecked == true;
            bool validThreshold = Int32.TryParse(_threshold.Text, out int threshold) && threshold >= 0 && threshold <= 255;
            if (!validThreshold && remove) { ShowMessage("Порог должен быть от 0 до 255.", true); return false; }
            if (!validThreshold) threshold = 130;
            bool mm = _units.SelectedIndex == 0;
            bool validRadius = DtfOutlineUnits.TryNumber(_radius.Text, out double radius);
            bool validExpansion = DtfOutlineUnits.TryNumber(_expansion.Text, out double expansion);
            if ((!validRadius || !validExpansion) && outline)
            { ShowMessage("Сглаживание и обводка: неотрицательные числа.", true); return false; }
            double factor = mm ? 1 : 25.4 / _originalPixels.DpiX;
            radius = validRadius ? radius * factor : Options.RadiusMm;
            expansion = validExpansion ? expansion * factor : Options.ExpansionMm;
            if (!TryColor(_hex.Text, out Color color))
            {
                if (outline) { ShowMessage("Цвет обводки: #RRGGBB.", true); return false; }
                color = Options.Color;
            }
            options = new DtfCorrectionOptions { Threshold = threshold, RadiusMm = radius, ExpansionMm = expansion,
                RemoveSemiTransparent = remove, SmoothStairs = _stairs.IsChecked == true,
                Outline = outline, Color = color, WhitenNearWhite = _whiten.IsChecked == true,
                NearWhiteTolerance = (int)_nearWhiteTolerance.Value };
            long pixels = (long)_originalPixels.Width * _originalPixels.Height;
            options.StairsScale = pixels <= 2000000 ? 4 : pixels <= 3500000 ? 3 : pixels <= 8000000 ? 2 : 1;
            try
            {
                double dpiX = _originalPixels.DpiX * (options.SmoothStairs ? options.StairsScale : 1);
                double dpiY = _originalPixels.DpiY * (options.SmoothStairs ? options.StairsScale : 1);
                int rx = DtfOutlineUnits.Pixels(radius, true, dpiX, DtfOutlineUnits.MaxRadiusPixels, "Сглаживание");
                int ry = DtfOutlineUnits.Pixels(radius, true, dpiY, DtfOutlineUnits.MaxRadiusPixels, "Сглаживание");
                int ex = DtfOutlineUnits.Pixels(expansion, true, dpiX, DtfOutlineUnits.MaxExpansionPixels, "Обводка");
                int ey = DtfOutlineUnits.Pixels(expansion, true, dpiY, DtfOutlineUnits.MaxExpansionPixels, "Обводка");
                _metrics.Text = "Сглаживание " + rx + (rx == ry ? "" : "×" + ry) + " px · обводка +" + ex +
                    (ex == ey ? "" : "×" + ey) + " px\n" + DtfOutlineUnits.Format(_originalPixels.DpiX) + " DPI · " +
                    _originalPixels.Width + " × " + _originalPixels.Height + " px";
            }
            catch (Exception error)
            { if (options.Outline) { ShowMessage(error.Message, true); return false; } }
            return true;
        }

        private void QueuePreview()
        {
            if (_syncing || !_loaded || _closed || _applying) return;
            UpdateCorrectionControlAvailability();
            _apply.IsEnabled = false; _after.Opacity = 0.45;
            _revision++; _previewCancellation?.Cancel(); _delay.Stop();
            if (!_qualityChanging && OverviewReadyForSelection()) _delay.Start();
        }

        private async Task PreviewAsync()
        {
            if (_closed || !_loaded || _applying || _qualityChanging || !OverviewReadyForSelection()) return;
            if (_rendering) { _retryPreview = true; return; }
            if (!ReadOptions(out DtfCorrectionOptions options)) return;
            int revision = _revision, view = _view.SelectedIndex;
            double cropX = _cropX, cropY = _cropY;
            bool highlight = _highlight.IsChecked == true, red = _marker.SelectedIndex == 1;
            bool highlightWhite = _highlightWhite.IsChecked == true;
            byte whiteRed = _nearWhiteMarker.R, whiteGreen = _nearWhiteMarker.G, whiteBlue = _nearWhiteMarker.B;
            // Capture only managed data on the UI thread. A BitmapFrame can
            // retain a decoder graph even when its top-level object is frozen.
            DtfCorrectionPixels originalPixels = _originalPixels, overviewPixels = _overviewPixels;
            string overviewPath = _overviewPath, workspace = _workspace;
            var processor = _processor;
            _previewCancellation = new CancellationTokenSource();
            var cancellationSource = _previewCancellation; CancellationToken cancellation = cancellationSource.Token;
            _rendering = true; _retryPreview = false; ShowMessage("Пересчитываю предпросмотр…", false);
            string input = null, output = Path.Combine(_workspace, "preview-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var task = Task.Run(() =>
                {
                    Int32Rect focus = new Int32Rect();
                    BitmapSource original = view == 0 ? overviewPixels.Bitmap() : Fragment(originalPixels, cropX, cropY, options, cancellation, out focus);
                    if (view == 0) focus = new Int32Rect(0, 0, original.PixelWidth, original.PixelHeight);
                    if (!options.SmoothStairs && !options.Outline)
                    {
                        // The common audit/threshold/whiten path stays in memory.
                        // Even a tolerance-only change used to encode/decode PNGs.
                        var before = view == 0 ? overviewPixels : DtfCorrectionPixels.Capture(original).Crop(focus, cancellation);
                        var after = before.Correct(options.RemoveSemiTransparent, options.Threshold,
                            options.WhitenNearWhite, options.NearWhiteTolerance, cancellation);
                        return Tuple.Create(
                            before.Audit(highlight, red, highlightWhite, options.NearWhiteTolerance, whiteRed, whiteGreen, whiteBlue, cancellation),
                            after.Audit(highlight, red, highlightWhite, options.NearWhiteTolerance, whiteRed, whiteGreen, whiteBlue, cancellation));
                    }
                    input = view == 0 ? overviewPath : Path.Combine(workspace, "fragment-" + Guid.NewGuid().ToString("N") + ".png");
                    if (view != 0) Save(original, input);
                    cancellation.ThrowIfCancellationRequested();
                    DtfCorrectionResult processed = processor(input, output, options, cancellation);
                    BitmapSource result = Load(output);
                    var aligned = Align(original, result, processed.OffsetMm, focus, cancellation);
                    original = aligned.Item1; result = aligned.Item2;
                    if (highlightWhite)
                    {
                        original = HighlightNearWhite(original, options.NearWhiteTolerance, whiteRed, whiteGreen, whiteBlue, cancellation);
                        result = HighlightNearWhite(result, options.NearWhiteTolerance, whiteRed, whiteGreen, whiteBlue, cancellation);
                    }
                    if (highlight) { original = Highlight(original, red, cancellation); result = Highlight(result, red, cancellation); }
                    cancellation.ThrowIfCancellationRequested();
                    return Tuple.Create(DtfCorrectionPixels.Capture(original), DtfCorrectionPixels.Capture(result));
                }, cancellation);
                _previewTask = task;
                var images = await task.ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                if (_closed || revision != _revision || _applying || _qualityChanging || !OverviewReadyForSelection()) return;
                _before.Source = images.Item1.Bitmap(); _after.Source = images.Item2.Bitmap(); _after.Opacity = 1; SetPreviewScale(view);
                if (_displayView != view)
                {
                    _displayView = view;
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_closed || revision != _revision) return;
                        foreach (var scroll in new[] { _beforeScroll, _afterScroll })
                        {
                            scroll.ScrollToHorizontalOffset(view == 0 ? 0 : Math.Max(0, (scroll.ExtentWidth - scroll.ViewportWidth) / 2));
                            scroll.ScrollToVerticalOffset(view == 0 ? 0 : Math.Max(0, (scroll.ExtentHeight - scroll.ViewportHeight) / 2));
                        }
                    }), DispatcherPriority.Loaded);
                }
                Options = options; _apply.IsEnabled = true;
                ShowMessage(view == 0 ? "Обзор уменьшен. Кнопка «1:1» открывает точный фрагмент." :
                    "Точный фрагмент исходного растра. Полная обработка — по кнопке «Применить».", false);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Log.Error("Correction preview failed; view=" + view + ", revision=" + revision + ".", error);
                await OnUiAsync(() => { if (!_closed && revision == _revision) ShowMessage("Предпросмотр не обновлён: " + error.GetBaseException().Message, true); });
            }
            finally
            {
                if (input != overviewPath) DeleteFile(input); DeleteFile(output);
                await OnUiAsync(() =>
                {
                _rendering = false; cancellationSource.Dispose();
                if (ReferenceEquals(_previewCancellation, cancellationSource)) _previewCancellation = null;
                if (_closed && !_applying) Cleanup();
                else if (!_applying && !_qualityChanging && OverviewReadyForSelection() &&
                    (_retryPreview || revision != _revision)) { _delay.Stop(); _delay.Start(); }
                });
            }
        }

        private async Task ApplyAsync()
        {
            if (_applying || !_loaded || _qualityChanging || !OverviewReadyForSelection() ||
                !ReadOptions(out DtfCorrectionOptions options)) return;
            _applying = true; _delay.Stop(); _previewCancellation?.Cancel(); _apply.IsEnabled = false;
            SetControlsEnabled(false); _cancel.Content = "Отменить";
            _applyCancellation = new CancellationTokenSource();
            var cancellationSource = _applyCancellation; CancellationToken cancellation = cancellationSource.Token;
            string output = Path.Combine(Path.GetTempPath(), "Vanya-Correction-Result-" + Guid.NewGuid().ToString("N") + ".png");
            string sourcePath = _sourcePath;
            var processor = _processor;
            bool keepOutput = false;
            try
            {
                ShowMessage("Обрабатываю исходник в полном разрешении…", false);
                if (_previewTask != null) { try { await _previewTask.ConfigureAwait(false); } catch { /* A stale preview must not prevent a fresh full render. */ } }
                cancellation.ThrowIfCancellationRequested();
                var result = await Task.Run(() => processor(sourcePath, output, options, cancellation), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                await OnUiAsync(() =>
                {
                    if (_closed) return;
                    result.OutputPath = output; Result = result; Options = options; keepOutput = true; DialogResult = true;
                });
            }
            catch (OperationCanceledException) { await OnUiAsync(() => { if (!_closed) ShowMessage("Обработка отменена. Настройки сохранены в этом окне.", false); }); }
            catch (Exception error)
            {
                Log.Error("Correction full render failed.", error);
                await OnUiAsync(() => { if (!_closed) ShowMessage("Не удалось обработать: " + error.GetBaseException().Message, true); });
            }
            finally
            {
                if (!keepOutput) DeleteFile(output);
                await OnUiAsync(() =>
                {
                _applying = false; cancellationSource.Dispose();
                if (ReferenceEquals(_applyCancellation, cancellationSource)) _applyCancellation = null;
                if (_closed) Cleanup();
                else { SetControlsEnabled(true); _cancel.Content = "Закрыть"; QueuePreview(); }
                });
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            _settingsPanel.IsEnabled = enabled;
            foreach (var control in new Control[] { _remove, _stairs, _outline, _threshold, _thresholdSlider,
                _radius, _expansion, _hex, _units, _view, _marker, _highlight, _swatch,
                _highlightWhite, _whiten, _nearWhiteTolerance, _nearWhiteSwatch,
                _previewQuality, _previewBackground, _previewBackgroundColor }) control.IsEnabled = enabled;
            if (enabled) UpdateCorrectionControlAvailability();
        }

        private void SetPreviewScale(int view)
        {
            if (_zoomView != view)
            {
                _zoomView = view; _zoomFit = view == 0;
                _zoomScale = view <= 1 ? 1 : view == 2 ? 2 : 4;
            }
            ApplyPreviewZoom();
        }

        private void SyncScroll(ScrollViewer source, ScrollViewer target, ScrollChangedEventArgs change)
        {
            if (_syncingScroll || (change.HorizontalChange == 0 && change.VerticalChange == 0)) return;
            _syncingScroll = true;
            try { target.ScrollToHorizontalOffset(source.HorizontalOffset); target.ScrollToVerticalOffset(source.VerticalOffset); }
            finally { _syncingScroll = false; }
        }

        private static BitmapSource Fragment(DtfCorrectionPixels source, double centerX, double centerY,
            DtfCorrectionOptions options, CancellationToken cancellation, out Int32Rect focus)
        {
            int width = Math.Min(560, source.Width), height = Math.Min(560, source.Height);
            int x = Math.Max(0, Math.Min(source.Width - width, (int)(source.Width * centerX) - width / 2));
            int y = Math.Max(0, Math.Min(source.Height - height, (int)(source.Height * centerY) - height / 2));
            // Process a guard band around the visible fragment. Its cut edges
            // must not become artificial contours inside the visible preview.
            int guardX = options.Outline ? (int)Math.Ceiling((options.RadiusMm + options.ExpansionMm) * source.DpiX / 25.4) + 4 : 4;
            int guardY = options.Outline ? (int)Math.Ceiling((options.RadiusMm + options.ExpansionMm) * source.DpiY / 25.4) + 4 : 4;
            int left = Math.Max(0, x - guardX), top = Math.Max(0, y - guardY);
            int right = Math.Min(source.Width, x + width + guardX), bottom = Math.Min(source.Height, y + height + guardY);
            focus = new Int32Rect(x - left, y - top, width, height);
            return source.Crop(new Int32Rect(left, top, right - left, bottom - top), cancellation).Bitmap();
        }

        private static Tuple<BitmapSource, BitmapSource> Align(BitmapSource original, BitmapSource result,
            Tuple<double, double> offsetMm, Int32Rect focus, CancellationToken cancellation)
        {
            // Crop and outside expansion change the result's origin. Rebuild a
            // common physical canvas so changing a value never shifts the view.
            double sx = result.DpiX / original.DpiX, sy = result.DpiY / original.DpiY;
            int originalWidth = Math.Max(1, (int)Math.Round(original.PixelWidth * sx));
            int originalHeight = Math.Max(1, (int)Math.Round(original.PixelHeight * sy));
            if (originalWidth != original.PixelWidth || originalHeight != original.PixelHeight)
            {
                var scaled = new TransformedBitmap(original, new ScaleTransform(
                    (double)originalWidth / original.PixelWidth, (double)originalHeight / original.PixelHeight));
                scaled.Freeze(); original = scaled;
            }
            int x = -(int)Math.Round((offsetMm?.Item1 ?? 0) * result.DpiX / 25.4);
            int y = -(int)Math.Round((offsetMm?.Item2 ?? 0) * result.DpiY / 25.4);
            int left = (int)Math.Round(focus.X * sx), top = (int)Math.Round(focus.Y * sy);
            int right = (int)Math.Round((focus.X + focus.Width) * sx);
            int bottom = (int)Math.Round((focus.Y + focus.Height) * sy);
            if (focus.X == 0) left = Math.Min(left, x);
            if (focus.Y == 0) top = Math.Min(top, y);
            if (focus.X + focus.Width >= originalWidth / sx) right = Math.Max(right, x + result.PixelWidth);
            if (focus.Y + focus.Height >= originalHeight / sy) bottom = Math.Max(bottom, y + result.PixelHeight);
            int width = Math.Max(1, right - left), height = Math.Max(1, bottom - top);
            BitmapSource before = Canvas(original, -left, -top, width, height, result.DpiX, result.DpiY, cancellation);
            BitmapSource after = Canvas(result, x - left, y - top, width, height, result.DpiX, result.DpiY, cancellation);
            return Tuple.Create(before, after);
        }

        private static BitmapSource Canvas(BitmapSource image, int x, int y, int width, int height,
            double dpiX, double dpiY, CancellationToken cancellation)
        {
            int stride = checked(width * 4); byte[] pixels = new byte[checked(stride * height)];
            int left = Math.Max(0, x), top = Math.Max(0, y);
            int right = Math.Min(width, x + image.PixelWidth), bottom = Math.Min(height, y + image.PixelHeight);
            cancellation.ThrowIfCancellationRequested();
            if (right > left && bottom > top)
                Bgra(image).CopyPixels(new Int32Rect(left - x, top - y, right - left, bottom - top),
                    pixels, stride, checked(top * stride + left * 4));
            var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze(); return bitmap;
        }

        private static BitmapSource Highlight(BitmapSource source, bool red, CancellationToken cancellation)
        {
            BitmapSource bgra = Bgra(source); int stride = checked(bgra.PixelWidth * 4);
            var pixels = new byte[checked(stride * bgra.PixelHeight)]; bgra.CopyPixels(pixels, stride, 0);
            for (int index = 0; index < pixels.Length; index += 4)
            {
                if ((index & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                if (pixels[index + 3] == 0 || pixels[index + 3] == 255) continue;
                pixels[index] = 30; pixels[index + 1] = red ? (byte)35 : (byte)245;
                pixels[index + 2] = red ? (byte)255 : (byte)20; pixels[index + 3] = 255;
            }
            var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze(); return result;
        }

        private static BitmapSource HighlightNearWhite(BitmapSource source, int tolerance,
            byte red, byte green, byte blue, CancellationToken cancellation)
        {
            BitmapSource bgra = Bgra(source); int stride = checked(bgra.PixelWidth * 4);
            var pixels = new byte[checked(stride * bgra.PixelHeight)]; bgra.CopyPixels(pixels, stride, 0);
            var mask = DtfNearWhitePixels.CreateMask(pixels, bgra.PixelWidth, bgra.PixelHeight, tolerance, cancellation);
            byte[] marked = DtfNearWhitePixels.Highlight(pixels, mask, red, green, blue,
                DtfNearWhitePixels.DefaultOpacity, cancellation);
            var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY,
                PixelFormats.Bgra32, null, marked, stride);
            result.Freeze(); return result;
        }

        private static BitmapSource Load(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                bitmap.Freeze(); return bitmap;
            }
        }
        private static BitmapSource Bgra(BitmapSource source)
        {
            if (source.Format == PixelFormats.Bgra32) return source;
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); converted.Freeze(); return converted;
        }
        private static void Save(BitmapSource image, string path)
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(path)) encoder.Save(stream); }
        private void Cleanup()
        {
            if (_loadingSource || _rendering || _applying || _qualityActive > 0) return;
            try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true); } catch { }
        }
        private static void DeleteFile(string path) { if (path != null) try { File.Delete(path); } catch { } }
        private void ShowMessage(string message, bool error)
        { _message.Text = message; DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, error ? DockerTheme.ErrorTextBrush : DockerTheme.TextBrush); }
        private Task OnUiAsync(Action action)
        {
            if (Dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
            return Dispatcher.InvokeAsync(action).Task;
        }
        private static bool TryColor(string value, out Color color)
        {
            color = Colors.White; string hex = (value ?? "").Trim().TrimStart('#');
            if (hex.Length != 6 || !Int32.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int rgb)) return false;
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true;
        }
        private static string Hex(Color color) => "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        private static FrameworkElement SwatchContent(Button button)
        {
            // The shared button chrome uses theme brushes. Keep the actual color
            // inside its content so both light and dark themes display the sample.
            var sample = new Border { Width = 14, Height = 16, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
            sample.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { Source = button });
            return sample;
        }
        private Button Preset(bool thick, double millimeters)
        {
            var shape = new System.Windows.Shapes.Path { Data = Geometry.Parse("M5,4 C8,0 18,0 21,6 L23,14 C24,20 18,24 13,22 L4,20 C0,18 2,9 5,4 Z"),
                StrokeThickness = thick ? 3 : 1, Fill = Brushes.Transparent,
                Width = 22, Height = 22, Stretch = Stretch.Uniform };
            DockerTheme.UseTheme(shape, System.Windows.Shapes.Shape.StrokeProperty, DockerTheme.MutedTextBrush);
            var button = new Button { Content = shape, Height = 30, Margin = new Thickness(0, 0, 4, 0),
                ToolTip = (thick ? "Толстая +" : "Тонкая +") + DtfOutlineUnits.Format(millimeters) + " мм" };
            button.Click += (_, __) =>
            {
                if (_applying || !_loaded) return;
                _expansion.Text = DtfOutlineUnits.Format(_units.SelectedIndex == 0 ? millimeters : millimeters * _originalPixels.DpiX / 25.4);
                _outline.IsChecked = true;
            };
            return button;
        }
        private static CheckBox Check(string text, bool value) => new CheckBox { Content = text, IsChecked = value,
            FontSize = 12, Margin = new Thickness(0, 6, 0, 6) };
        private static TextBox Field(string text) => new TextBox { Text = text, FontSize = 12, MinHeight = 28,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 1, 4, 1) };
        private static ComboBox Combo(string[] choices) => new ComboBox { ItemsSource = choices, SelectedIndex = 0,
            MinHeight = 28, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center };
        private static Button Button(string text, RoutedEventHandler handler)
        { var button = new Button { Content = text, FontSize = 12, MinHeight = 30, Padding = new Thickness(8, 3, 8, 3) }; button.Click += handler; return button; }
        private static TextBlock Text(string text, int size, bool bold = false) => new TextBlock { Text = text, FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
        private static FrameworkElement Labeled(string title, FrameworkElement field)
        { var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) }; panel.Children.Add(Text(title, 11)); panel.Children.Add(field); return panel; }
        private static FrameworkElement Preview(string title, out Image image, out ScrollViewer scroll)
        {
            var panel = new Grid { Margin = new Thickness(3) }; panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition()); panel.Children.Add(Text(title, 12));
            image = new Image { Stretch = Stretch.Uniform, Cursor = Cursors.Cross };
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
