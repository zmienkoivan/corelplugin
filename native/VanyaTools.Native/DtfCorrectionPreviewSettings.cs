using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed partial class DtfCorrectionWindow
    {
        private sealed class PreviewOverview
        {
            internal int Quality;
            internal DtfCorrectionPixels Pixels;
            internal string Path;
        }

        private ComboBox _previewQuality, _previewBackground;
        private Button _previewBackgroundColor;
        private Color _customPreviewBackground = Color.FromRgb(65, 65, 65);
        private readonly Dictionary<int, PreviewOverview> _overviewCache = new Dictionary<int, PreviewOverview>();
        private CancellationTokenSource _qualityCancellation, _sourceCancellation;
        private Task _qualityTask;
        private int _qualityRevision, _qualityActive;
        private bool _qualityChanging, _loadingSource;

        private FrameworkElement CreatePreviewSettingsToolbar()
        {
            var row = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            row.Children.Add(new TextBlock { Text = "Обзор", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            _previewQuality = Combo(new[] { "1100 px", "2200 px", "3600 px" });
            _previewQuality.SelectedIndex = 1; _previewQuality.Width = 95;
            System.Windows.Automation.AutomationProperties.SetName(_previewQuality, "Качество обзора");
            _previewQuality.ToolTip = "Длинная сторона обзорного изображения. Точный фрагмент всегда берётся из исходника.";
            row.Children.Add(_previewQuality);
            row.Children.Add(new TextBlock { Text = "Фон", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 5, 0) });
            _previewBackground = Combo(new[] { "Шашки", "Тёмно-серый", "Свой цвет" });
            _previewBackground.Width = 115; row.Children.Add(_previewBackground);
            System.Windows.Automation.AutomationProperties.SetName(_previewBackground, "Фон предпросмотра");
            _previewBackground.ToolTip = "Фон только для просмотра прозрачности. В готовый PNG не добавляется.";
            _previewBackgroundColor = new Button { Width = 29, Height = 28, MinWidth = 0,
                Margin = new Thickness(4, 0, 0, 0), Background = new SolidColorBrush(_customPreviewBackground),
                ToolTip = "Выбрать свой цвет фона предпросмотра" };
            row.Children.Add(_previewBackgroundColor);
            System.Windows.Automation.AutomationProperties.SetName(_previewBackgroundColor, "Выбрать цвет фона предпросмотра");
            _previewBackground.SelectionChanged += (_, __) => ChangePreviewBackground();
            _previewBackgroundColor.Click += (_, __) =>
            {
                if (_closed || _applying) return;
                try
                {
                    Color? picked = WindowsColorPicker.Pick(new WindowInteropHelper(this).Handle, _customPreviewBackground);
                    if (!picked.HasValue) return;
                    _customPreviewBackground = picked.Value;
                    _previewBackgroundColor.Background = new SolidColorBrush(picked.Value);
                    _previewBackground.SelectedIndex = 2; ChangePreviewBackground();
                }
                catch (Exception error)
                {
                    Log.Error("Correction preview background picker failed.", error);
                    ShowMessage("Не удалось выбрать фон: " + error.GetBaseException().Message, true);
                }
            };
            _previewQuality.SelectionChanged += async (_, __) => await ChangePreviewQualityAsync();
            return row;
        }

        private int SelectedPreviewQuality() => _previewQuality.SelectedIndex == 0 ? 1100 :
            _previewQuality.SelectedIndex == 2 ? 3600 : 2200;

        private bool OverviewReadyForSelection() => _view.SelectedIndex != 0 ||
            (_overviewCache.TryGetValue(SelectedPreviewQuality(), out PreviewOverview overview) &&
                ReferenceEquals(_overviewPixels, overview.Pixels));

        private void UpdateCorrectionControlAvailability()
        {
            bool available = _settingsPanel.IsEnabled && !_applying && !_closed;
            _threshold.IsEnabled = _thresholdSlider.IsEnabled = available && _remove.IsChecked == true;
            bool outline = available && _outline.IsChecked == true;
            _radius.IsEnabled = _expansion.IsEnabled = _hex.IsEnabled = _swatch.IsEnabled = outline;
        }

        private void ChangePreviewBackground()
        {
            if (_beforeScroll == null || _afterScroll == null) return;
            Brush background = _previewBackground.SelectedIndex == 0 ? Checkerboard() :
                new SolidColorBrush(_previewBackground.SelectedIndex == 1 ? Color.FromRgb(48, 48, 48) : _customPreviewBackground);
            if (background.CanFreeze) background.Freeze();
            _beforeScroll.Background = background; _afterScroll.Background = background;
        }

        private static PreviewOverview BuildPreviewOverview(BitmapSource source, int quality,
            string workspace, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            double factor = Math.Min(1, (double)quality / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource overview = source;
            if (factor < 1)
            {
                var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor)); scaled.Freeze();
                int stride = checked(scaled.PixelWidth * 4);
                var pixels = new byte[checked(stride * scaled.PixelHeight)];
                Bgra(scaled).CopyPixels(pixels, stride, 0);
                // The overview represents the same physical dimensions as the source.
                overview = BitmapSource.Create(scaled.PixelWidth, scaled.PixelHeight,
                    source.DpiX * scaled.PixelWidth / source.PixelWidth,
                    source.DpiY * scaled.PixelHeight / source.PixelHeight,
                    PixelFormats.Bgra32, null, pixels, stride); overview.Freeze();
            }
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(workspace, "overview-" + quality + "-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Save(overview, path); cancellation.ThrowIfCancellationRequested();
                return new PreviewOverview { Quality = quality, Pixels = DtfCorrectionPixels.Capture(overview), Path = path };
            }
            catch { DeleteFile(path); throw; }
        }

        private void UsePreviewOverview(PreviewOverview overview)
        {
            if (!_zoomFit && _view.SelectedIndex == 0 && _overviewPixels != null)
                _zoomScale *= (double)_overviewPixels.Width / overview.Pixels.Width;
            _overviewCache[overview.Quality] = overview;
            _overviewPixels = overview.Pixels; _overview = overview.Pixels.Bitmap(); _overviewPath = overview.Path;
        }

        private async Task ChangePreviewQualityAsync()
        {
            if (!_loaded || _closed || _applying) return;
            int quality = SelectedPreviewQuality(), revision = ++_qualityRevision;
            _qualityCancellation?.Cancel();
            var cancellationSource = new CancellationTokenSource(); _qualityCancellation = cancellationSource;
            CancellationToken cancellation = cancellationSource.Token;
            _qualityActive++; _qualityChanging = true; _apply.IsEnabled = false; _after.Opacity = 0.45;
            _revision++; _previewCancellation?.Cancel(); _delay.Stop();
            DtfCorrectionPixels source = _originalPixels;
            string workspace = _workspace;
            Task previous = _qualityTask;
            string generatedPath = null; bool retained = false;
            try
            {
                ShowMessage("Готовлю обзор " + quality + " px…", false);
                PreviewOverview overview;
                if (!_overviewCache.TryGetValue(quality, out overview))
                {
                    var task = Task.Run(async () =>
                    {
                        // Never allocate several full source bitmaps in parallel.
                        if (previous != null) try { await previous.ConfigureAwait(false); } catch { }
                        cancellation.ThrowIfCancellationRequested();
                        return BuildPreviewOverview(source.Bitmap(), quality, workspace, cancellation);
                    }, cancellation);
                    _qualityTask = task;
                    overview = await task.ConfigureAwait(false); generatedPath = overview.Path;
                }
                cancellation.ThrowIfCancellationRequested();
                await OnUiAsync(() =>
                {
                    if (_closed || revision != _qualityRevision) return;
                    UsePreviewOverview(overview); retained = true; _qualityChanging = false; QueuePreview();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Log.Error("Correction overview quality failed; quality=" + quality + ".", error);
                await OnUiAsync(() =>
                {
                    if (!_closed && revision == _qualityRevision)
                        ShowMessage("Обзор не обновлён: " + error.GetBaseException().Message, true);
                });
            }
            finally
            {
                if (!retained) DeleteFile(generatedPath);
                await OnUiAsync(() =>
                {
                    _qualityActive--; cancellationSource.Dispose();
                    if (ReferenceEquals(_qualityCancellation, cancellationSource)) _qualityCancellation = null;
                    if (revision == _qualityRevision) _qualityChanging = false;
                    if (_closed) Cleanup();
                });
            }
        }
    }
}
