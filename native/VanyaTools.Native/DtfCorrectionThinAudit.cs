using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VanyaTools.Native
{
    internal sealed partial class DtfCorrectionWindow
    {
        private CheckBox _thinEnabled, _thinWhite;
        private ComboBox _thinPreset;
        private Slider _thinMinimum, _thinSafe, _thinDot, _thinAlpha;
        private StackPanel _thinFields;
        private Button _thinMask;
        private TextBlock _thinNote;
        private bool _syncThin, _insertingThinMask;
        private Action<byte[], int, int> _insertThinMask;
        private CancellationTokenSource _thinMaskCancellation;

        private void CreateThinAuditControls(Panel panel, bool startEnabled, Action<byte[], int, int> insertMask)
        {
            _insertThinMask = insertMask;
            var header = new Grid { Margin = new Thickness(0, 5, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _thinEnabled = Check("Тонкие элементы", startEnabled); _thinEnabled.FontSize = 11; header.Children.Add(_thinEnabled);
            _thinPreset = Combo(new[] { "ДТФ", "УФ-ДТФ" }); _thinPreset.Width = 91; Grid.SetColumn(_thinPreset, 1); header.Children.Add(_thinPreset); panel.Children.Add(header);
            _thinFields = new StackPanel { Margin = new Thickness(3, 2, 3, 2) };
            _thinMinimum = ThinField(_thinFields, "Минимум линии, мм", .1, 1.5, .6, .05);
            _thinSafe = ThinField(_thinFields, "Безопасно от, мм", .1, 2, .8, .05);
            _thinDot = ThinField(_thinFields, "Точка от, мм", .1, 2, 1, .05);
            _thinAlpha = ThinField(_thinFields, "Порог alpha", 1, 255, 128, 1);
            _thinWhite = Check("Белый считать фоном", false); _thinWhite.FontSize = 11; _thinFields.Children.Add(_thinWhite);
            panel.Children.Add(new Expander { Header = "Пороги тонких элементов", FontSize = 11, IsExpanded = startEnabled, Content = _thinFields });
            var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            var ruler = Button("Линейка 0,1–1 мм", (_, __) => {
                try { ThinElementReference.Insert(_thinPreset.SelectedIndex == 1, ThinOptions()); ShowMessage("Векторная линейка вставлена под выделением.", false); }
                catch (Exception error) { ShowMessage(error.GetBaseException().Message, true); }
            });
            ruler.FontSize = 10; actions.Children.Add(ruler);
            _thinMask = Button("Маску на холст", async (_, __) => await InsertThinMaskAsync()); _thinMask.FontSize = 10;
            _thinMask.ToolTip = "Маска исходника на отдельном непечатаемом слое. Цветная подсветка не входит в готовый результат коррекции.";
            actions.Children.Add(_thinMask); panel.Children.Add(actions);
            _thinNote = Text("Красный — ниже минимума; жёлтый — между порогами; зелёный — около безопасного. Пересчёт автоматический.", 10); panel.Children.Add(_thinNote);
            _thinPreset.SelectionChanged += (_, __) => {
                _syncThin = true; var options = ThinAuditOptions.Preset(_thinPreset.SelectedIndex == 1);
                _thinMinimum.Value = options.MinimumMm; _thinSafe.Value = options.SafeMm; _thinDot.Value = options.DotMm; _syncThin = false; QueuePreview();
            };
            foreach (var slider in new[] { _thinMinimum, _thinSafe, _thinDot, _thinAlpha }) slider.ValueChanged += (sender, __) => {
                if (_syncThin) return; _syncThin = true;
                if (_thinMinimum.Value > _thinSafe.Value)
                { if (ReferenceEquals(sender, _thinSafe)) _thinMinimum.Value = _thinSafe.Value; else _thinSafe.Value = _thinMinimum.Value; }
                if (_thinDot.Value < _thinMinimum.Value) _thinDot.Value = _thinMinimum.Value;
                _syncThin = false; QueuePreview();
            };
            _thinEnabled.Checked += (_, __) => QueuePreview(); _thinEnabled.Unchecked += (_, __) => QueuePreview();
            _thinWhite.Checked += (_, __) => QueuePreview(); _thinWhite.Unchecked += (_, __) => QueuePreview();
        }
        private static Slider ThinField(Panel panel, string caption, double minimum, double maximum, double initial, double tick)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
            var value = new TextBlock { Width = 34, TextAlignment = TextAlignment.Right, FontSize = 11 }; DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = caption, FontSize = 11 }); panel.Children.Add(row);
            var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = initial, TickFrequency = tick, IsSnapToTickEnabled = true };
            Action show = () => value.Text = slider.Value.ToString("0.##"); show(); slider.ValueChanged += (_, __) => show(); panel.Children.Add(slider); return slider;
        }
        private ThinAuditOptions ThinOptions()
        {
            var options = new ThinAuditOptions { MinimumMm = _thinMinimum.Value, SafeMm = _thinSafe.Value,
                DotMm = _thinDot.Value, Alpha = (int)_thinAlpha.Value, IgnoreWhite = _thinWhite.IsChecked == true };
            options.Validate(); return options;
        }
        private void UpdateThinAvailability()
        {
            if (_thinEnabled == null) return;
            bool available = _loaded && !_applying && !_closed && !_insertingThinMask;
            _thinFields.IsEnabled = available && _thinEnabled.IsChecked == true;
            _thinMask.IsEnabled = available && _insertThinMask != null;
        }
        private static ThinAuditResult AnalyzeThinPreview(DtfCorrectionPixels pixels, ThinAuditOptions options, bool fragment,
            CancellationToken cancellation, out string note)
        {
            note = ""; if (options == null) return null;
            if ((long)pixels.Width * pixels.Height > ThinElementAudit.MaxPixels ||
                Math.Max(25.4 / pixels.DpiX, 25.4 / pixels.DpiY) > options.MinimumMm / 4)
            { note = fragment ? "У исходника не хватает разрешения для точной проверки этой толщины. Используйте векторную линейку для сравнения." :
                "На обзоре не хватает разрешения для тонких элементов. Увеличьте участок: точная подсветка появится автоматически. «1:1» — масштаб исходника."; return null; }
            var result = pixels.AnalyzeThickness(options, cancellation);
            if (fragment)
            {
                // Do not flag artificial cut edges of the displayed fragment.
                int edges = options.CutEdges | (options.ClipX ? 3 : 0) | (options.ClipY ? 12 : 0);
                int gx = (int)Math.Ceiling(Math.Max(options.DotMm, options.SafeMm + .2) * pixels.DpiX / 25.4);
                int gy = (int)Math.Ceiling(Math.Max(options.DotMm, options.SafeMm + .2) * pixels.DpiY / 25.4);
                for (int y = 0; y < pixels.Height; y++)
                {
                    if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                    for (int x = 0; x < pixels.Width; x++) if (((edges & 1) != 0 && x < gx) || ((edges & 2) != 0 && x >= pixels.Width - gx) ||
                        ((edges & 4) != 0 && y < gy) || ((edges & 8) != 0 && y >= pixels.Height - gy))
                        result.Overlay[(y * pixels.Width + x) * 4 + 3] = 0;
                }
            }
            note = "Точки ниже минимума: " + result.SmallDots + ". Оценка ±1–2 пикселя." +
                (fragment && (options.CutEdges != 0 || options.ClipX || options.ClipY) ? " Искусственные края участка исключены из подсветки." : "");
            return result;
        }
        private async Task InsertThinMaskAsync()
        {
            if (!_loaded || _closed || _applying || _qualityChanging || _qualityActive > 0 || _insertingThinMask || _insertThinMask == null) return;
            _insertingThinMask = true; _settingsPanel.IsEnabled = false; _apply.IsEnabled = false; UpdateThinAvailability();
            _revision++; _previewCancellation?.Cancel(); CancelDetail(false); _delay.Stop();
            _thinMaskCancellation = new CancellationTokenSource(); var cancellation = _thinMaskCancellation;
            try
            {
                var options = ThinOptions(); var pixels = (long)_originalPixels.Width * _originalPixels.Height <= ThinElementAudit.MaxPixels ? _originalPixels : _overviewPixels;
                if (_previewTask != null) try { await _previewTask; } catch { }
                if (_detailTask != null) try { await _detailTask; } catch { }
                cancellation.Token.ThrowIfCancellationRequested();
                ShowMessage("Считаю маску исходника для холста…", false);
                var result = await Task.Run(() => pixels.AnalyzeThickness(options, cancellation.Token));
                if (!_closed) { _insertThinMask(result.Overlay, pixels.Width, pixels.Height); ShowMessage("Маска исходника добавлена на заблокированный непечатаемый слой.", false); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_closed) ShowMessage(error.GetBaseException().Message, true); }
            finally { cancellation.Dispose(); if (ReferenceEquals(_thinMaskCancellation, cancellation)) _thinMaskCancellation = null;
                _insertingThinMask = false; if (_closed) Cleanup(); else { _settingsPanel.IsEnabled = true; UpdateCorrectionControlAvailability(); QueuePreview(); } }
        }
    }
}
