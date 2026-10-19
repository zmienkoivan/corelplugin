using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class DtfNoiseTab : UserControl
    {
        private readonly Func<string, string, string> _import;
        private readonly Action<string, bool> _status;
        private readonly TextBox _amount;
        private readonly Slider _slider;
        private readonly ComboBox _sampleSize;
        private readonly CheckBox _protect;
        private readonly Button _load, _apply, _compare, _pattern, _retry, _cancel;
        private readonly Image _before, _after;
        private readonly TextBlock _progress;
        private readonly DispatcherTimer _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        private CancellationTokenSource _cancellation;
        private DtfNoiseProcessor.Sample _sample;
        private string _source, _ready;
        private int _seed = 1729;
        private bool _busy, _syncing;
        private static readonly string PendingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "pending-dtf-noise-result.txt");

        internal DtfNoiseTab(Func<string, string, string> import, Action<string, bool> status)
        {
            _import = import; _status = status;
            var panel = new StackPanel { Margin = new Thickness(7) };
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text = "Эксперимент · шум для ДТФ", FontWeight = FontWeights.SemiBold, FontSize = 13 });
            DockerHelp.Add(panel, "Это визуальная проба, а не исправление подачи и стыков проходов принтера. " +
                "Монохромный шум добавляет одинаковую случайную прибавку в RGB-каналы; сила — максимальное отклонение в процентах от диапазона канала. " +
                "Изображение может стать зернистее и изменить оттенок при ограничении каналов. Начните с 0,5% и сравните на реальной печати. " +
                "Прозрачность, разрешение и физический размер результата сохраняются; исходник остаётся на холсте.");
            _load = MakeButton("Загрузить / обновить выделение", async (_, __) => await LoadSource()); panel.Children.Add(_load);
            _amount = new TextBox { Text = "0,5", MinHeight = 25, VerticalContentAlignment = VerticalAlignment.Center };
            panel.Children.Add(DockerLayout.FieldRow("Сила шума, %", _amount, 65));
            _slider = new Slider { Minimum = 0, Maximum = 5, Value = 0.5, TickFrequency = 0.25, Margin = new Thickness(0, 3, 0, 3) };
            panel.Children.Add(_slider);
            _protect = new CheckBox { Content = "Не менять чистый белый и чистый чёрный", IsChecked = true, FontSize = 11,
                Margin = new Thickness(0, 2, 0, 4) }; panel.Children.Add(_protect);
            _pattern = MakeButton("Другой рисунок шума", (_, __) => { _seed = unchecked(_seed + 7919); QueuePreview(); }); panel.Children.Add(_pattern);
            var previews = new UniformGrid { Columns = 2 };
            previews.Children.Add(Preview("Исходный фрагмент", out _before)); previews.Children.Add(Preview("С шумом", out _after)); panel.Children.Add(previews);
            DockerHelp.Add(panel, "Предпросмотр показывает центральный фрагмент в пикселях исходника. " +
                "Используется тот же рисунок шума, что и при полной обработке. " +
                "Кнопка сравнения создаёт восемь квадратов с уровнями 0 / 0,5 / 1 / 1,5 / 2 / 3 / 4 / 5%. " +
                "Размер каждого квадрата — 5×5 или 10×10 см; на листе четыре столбца и два ряда. " +
                "Берётся центральный фрагмент 1:1. Если исходник меньше квадрата, фрагмент увеличивается с сохранением пропорций. " +
                "Шум накладывается после увеличения, с исходным DPI. " +
                "Напечатайте их с одинаковыми параметрами CADlink и PrintExp. Эффект зависит от вашего принтера и RIP.");
            _apply = DockerTheme.Primary(MakeButton("Создать копию с шумом", async (_, __) => await Generate(false))); panel.Children.Add(_apply);
            _sampleSize = new ComboBox { ItemsSource = new[] { "5×5 см", "10×10 см" }, SelectedIndex = 0, MinHeight = 27 };
            panel.Children.Add(DockerLayout.FieldRow("Квадраты тестового листа", _sampleSize, 105));
            _compare = MakeButton("Тестовый лист шума · 0–5%", async (_, __) => await Generate(true)); panel.Children.Add(_compare);
            _retry = MakeButton("Повторить вставку готового результата", (_, __) => InsertReady()); panel.Children.Add(_retry);
            _cancel = MakeButton("Отменить", (_, __) => _cancellation?.Cancel()); panel.Children.Add(_cancel);
            _progress = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) }; panel.Children.Add(_progress);
            _amount.TextChanged += (_, __) => {
                if (!_syncing && TryAmount(out double value)) { _syncing = true; _slider.Value = value; _syncing = false; }
                QueuePreview(); };
            _slider.ValueChanged += (_, __) => {
                if (_syncing) return; _syncing = true; _amount.Text = DtfOutlineUnits.Format(_slider.Value); _syncing = false; QueuePreview(); };
            _protect.Checked += (_, __) => QueuePreview(); _protect.Unchecked += (_, __) => QueuePreview();
            _previewTimer.Tick += (_, __) => { _previewTimer.Stop(); UpdatePreview(); };
            Unloaded += (_, __) => _previewTimer.Stop();
            Loaded += (_, __) => QueuePreview();
            try { if (File.Exists(PendingPath)) { string path = File.ReadAllText(PendingPath).Trim(); if (File.Exists(path)) _ready = path; } }
            catch (Exception error) { Log.Error("Could not restore DTF noise result.", error); }
            SetBusy(false);
            if (_ready != null) Report("Результат уже готов. Нажмите «Повторить вставку».", false);
        }

        private bool TryAmount(out double amount) => DtfOutlineUnits.TryNumber(_amount.Text, out amount) && amount <= 5;
        private void QueuePreview() { if (!_busy && _sample != null) _previewTimer.Start(); }
        private void UpdatePreview()
        {
            if (_busy || _sample == null) return;
            if (!TryAmount(out double amount)) { Report("Сила шума: от 0 до 5%.", true); return; }
            try { _after.Source = DtfNoiseProcessor.Render(_sample.Image, amount, _seed, _protect.IsChecked == true,
                CancellationToken.None, _sample.X, _sample.Y); }
            catch (Exception error) { Log.Error("DTF noise preview failed.", error); Report(error.Message, true); }
        }

        private async Task<bool> LoadSource()
        {
            if (_busy) return false;
            if (_ready != null) { Report("Сначала вставьте готовый результат.", true); return false; }
            SetBusy(true);
            string captured = null;
            try
            {
                Report("Подготавливаю выделение…", false); await Dispatcher.Yield(DispatcherPriority.Background);
                captured = AiSelectionCapture.Capture();
                var sample = await Task.Run(() => DtfNoiseProcessor.Preview(captured));
                string previous = _source; _source = captured; captured = null; _sample = sample; _before.Source = sample.Image;
                if (previous != null) try { File.Delete(previous); } catch { }
                Report("Фрагмент загружен. Сравните шум с исходником.", false); return true;
            }
            catch (Exception error) { Log.Error("DTF noise capture failed.", error); Report(error.Message, true); return false; }
            finally { if (captured != null) try { File.Delete(captured); } catch { } SetBusy(false); QueuePreview(); }
        }

        private async Task Generate(bool comparison)
        {
            if (_busy) return;
            if (_ready != null) { Report("Сначала вставьте готовый результат.", true); return; }
            double amount = 0;
            if (!comparison && !TryAmount(out amount)) { Report("Сила шума: от 0 до 5%.", true); return; }
            if ((_source == null || !File.Exists(_source)) && !await LoadSource()) return;
            int seed = _seed; bool protect = _protect.IsChecked == true;
            int sampleSizeMm = _sampleSize.SelectedIndex == 1 ? 100 : 50;
            _cancellation = new CancellationTokenSource(); SetBusy(true);
            try
            {
                Report(comparison ? "Готовлю образцы шума…" : "Добавляю шум…", false);
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Dtf-Noise-" + (comparison ? "Samples-" : "") + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => { if (comparison) DtfNoiseProcessor.Comparison(_source, output, seed, protect, sampleSizeMm, cancellation);
                    else DtfNoiseProcessor.Apply(_source, output, amount, seed, protect, cancellation); }, cancellation);
                cancellation.ThrowIfCancellationRequested(); _ready = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)); File.WriteAllText(PendingPath, output);
                InsertReady();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception error) { Log.Error("DTF noise processing failed.", error); Report(error.GetBaseException().Message, true); }
            finally { _cancellation.Dispose(); _cancellation = null; SetBusy(false); }
        }

        private void InsertReady()
        {
            if (_ready == null) return;
            try
            {
                string warning = _import(_ready, Path.GetFileName(_ready).Contains("Samples-") ? "Эксперимент · образцы шума" : "Эксперимент · шум ДТФ");
                _ready = null; try { File.Delete(PendingPath); } catch { }
                Report(warning ?? "Готово. Копия вставлена рядом с исходником.", warning != null);
            }
            catch (Exception error) { Log.Error("DTF noise import failed.", error); Report("Результат готов, но не вставлен: " + error.GetBaseException().Message, true); }
            if (!_busy) SetBusy(false);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _load.IsEnabled = _apply.IsEnabled = _compare.IsEnabled = _pattern.IsEnabled = _amount.IsEnabled =
                _slider.IsEnabled = _protect.IsEnabled = _sampleSize.IsEnabled = !busy;
            _retry.IsEnabled = !busy; _retry.Visibility = _ready != null ? Visibility.Visible : Visibility.Collapsed;
            _cancel.Visibility = busy && _cancellation != null ? Visibility.Visible : Visibility.Collapsed;
        }
        private void Report(string text, bool error) { _progress.Text = text; _status(text, error); }
        private static Button MakeButton(string text, RoutedEventHandler action)
        { var button = new Button { Content = text, Margin = new Thickness(0, 2, 0, 2) }; button.Click += action; return button; }
        private static FrameworkElement Preview(string text, out Image image)
        {
            var panel = new StackPanel { Margin = new Thickness(2) }; panel.Children.Add(new TextBlock { Text = text, FontSize = 10 });
            image = new Image { Height = 180, Stretch = Stretch.Uniform };
            panel.Children.Add(new Border { Child = image, Background = Brushes.WhiteSmoke, BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1) }); return panel;
        }
    }
}
