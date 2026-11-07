using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed partial class BackgroundRemovalWindow
    {
        private readonly CheckBox _showWandSelection = new CheckBox { Content = "Показать выделение", IsChecked = true,
            Margin = new Thickness(0, 2, 0, 4) };
        private readonly Image _wandOverlay = new Image { IsHitTestVisible = false, Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private TextBlock _resolutionInfo;
        private Border _resolutionBadge;
        private Button _normalizeResolution;
        private string _workingSource;
        private double _inputDpiX, _inputDpiY;
        private bool _normalizing, _normalizedSourceReady, _preserveViewOnReload;
        private CancellationTokenSource _normalizationCancellation;

        private void AddWandOverlay()
        {
            ((Grid)_beforeScroll.Content).Children.Add(_wandOverlay);
            _showWandSelection.Checked += (_, __) => _wandOverlay.Visibility = Visibility.Visible;
            _showWandSelection.Unchecked += (_, __) => _wandOverlay.Visibility = Visibility.Collapsed;
        }

        private void PositionWandOverlay()
        {
            if (_before.Source == null) return;
            _wandOverlay.Width = _before.Width; _wandOverlay.Height = _before.Height;
            _wandOverlay.Margin = _before.Margin;
            RenderOptions.SetBitmapScalingMode(_wandOverlay, BitmapScalingMode.NearestNeighbor);
        }

        private static BitmapSource WandSelection(byte[] selected, int width, int height,
            int previewWidth, int previewHeight, CancellationToken cancellation)
        {
            byte[] pixels = selected;
            if (width != previewWidth || height != previewHeight)
            {
                pixels = new byte[checked(previewWidth * previewHeight)];
                for (int y = 0; y < previewHeight; y++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int sy = (int)((y + .5) * height / previewHeight);
                    for (int x = 0; x < previewWidth; x++)
                        pixels[y * previewWidth + x] = selected[sy * width + (int)((x + .5) * width / previewWidth)];
                }
            }
            var palette = new BitmapPalette(new[] { Colors.Transparent, Color.FromArgb(125, 20, 220, 225) });
            var overlay = BitmapSource.Create(previewWidth, previewHeight, 96, 96, PixelFormats.Indexed8, palette, pixels, previewWidth);
            overlay.Freeze();
            return overlay;
        }

        private void CreateResolutionControls()
        {
            _resolutionInfo = Text("Определяю DPI…", 11);
            _resolutionInfo.Foreground = Brushes.Black; _resolutionInfo.Margin = new Thickness(0);
            _resolutionBadge = new Border { Child = _resolutionInfo, Padding = new Thickness(6, 4, 6, 4),
                CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 3, 0, 3), Background = Brushes.LightGray };
            _settings.Children.Add(_resolutionBadge);
            _normalizeResolution = Button("Нормализовать до 300 DPI", async (_, __) => await NormalizeResolutionAsync());
            _normalizeResolution.ToolTip = "Повысить DPI рабочей копии с сохранением размера в миллиметрах.";
            _normalizeResolution.IsEnabled = false;
            _settings.Children.Add(_normalizeResolution);
        }

        private void UpdateResolutionControls()
        {
            var image = _original;
            _resolutionInfo.Text = DtfCorrectionWindow.FormatResolution(image.DpiX, image.DpiY) + " DPI · " +
                image.PixelWidth + " × " + image.PixelHeight + " px";
            _resolutionBadge.Background = new SolidColorBrush(DtfCorrectionWindow.ResolutionColor(Math.Min(image.DpiX, image.DpiY)));
            bool normalized = _workingSource != _sourcePath;
            _normalizeResolution.Content = normalized ? "Вернуть исходное разрешение" : "Нормализовать до 300 DPI";
            _normalizeResolution.IsEnabled = normalized || Math.Min(_inputDpiX, _inputDpiY) < 299.99;
        }

        private async Task NormalizeResolutionAsync()
        {
            if (!_loaded || _normalizing || _applying || _closed) return;
            bool normalize = _workingSource == _sourcePath;
            if (normalize && Math.Min(_inputDpiX, _inputDpiY) >= 299.99) return;
            var original = _original;
            bool prepare = normalize && !_normalizedSourceReady;
            string path = normalize ? Path.Combine(_workspace, "normalized-300.png") : _sourcePath;
            _normalizing = true; _settings.IsEnabled = false; _apply.IsEnabled = false;
            ++_revision; _delay.Stop(); _previewCancellation?.Cancel();
            var source = new CancellationTokenSource(); _normalizationCancellation = source;
            var token = source.Token;
            bool changed = false;
            ShowMessage(normalize ? "Поднимаю разрешение до 300 DPI…" : "Возвращаю исходное разрешение…", false);
            try
            {
                var image = await Task.Run(() =>
                {
                    if (prepare)
                        SimpleAiTab.ProcessCorrection(_sourcePath, path, new DtfCorrectionOptions { Resample300 = true }, token);
                    token.ThrowIfCancellationRequested();
                    var working = LoadDetached(path);
                    if (prepare && (!DtfCorrectionWindow.ResolutionSizeMatches(original.PixelWidth, original.DpiX, working.PixelWidth, working.DpiX) ||
                        !DtfCorrectionWindow.ResolutionSizeMatches(original.PixelHeight, original.DpiY, working.PixelHeight, working.DpiY)))
                        throw new InvalidOperationException("Ресемпл не сохранил ожидаемый размер и DPI.");
                    return working;
                }, token);
                if (_closed) return;
                _original = image; _workingSource = path; _previewInput = null;
                if (normalize) _normalizedSourceReady = true;
                _preserveViewOnReload = true;
                ClearResult("Подготавливаю предпросмотр…");
                UpdateResolutionControls();
                changed = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_closed) ShowMessage("DPI не изменён. " + error.GetBaseException().Message, true); }
            finally
            {
                _normalizing = false; _normalizationCancellation = null; source.Dispose();
                if (_closed) Cleanup();
                else
                {
                    _settings.IsEnabled = true;
                    if (changed) QueuePreview(false);
                    else
                    {
                        // Restore the last valid frame when preparation failed.
                        if (_displayRevision == _revision - 1) { _displayRevision = _revision; _apply.IsEnabled = true; }
                        else if (_previewRequested) { _delay.Stop(); _delay.Start(); }
                    }
                }
            }
        }
    }
}
