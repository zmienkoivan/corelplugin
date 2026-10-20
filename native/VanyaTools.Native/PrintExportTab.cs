using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class PrintExportTab : UserControl
    {
        private readonly PrintExportSettings _settings;
        private readonly Action<string, bool> _status;
        private readonly StackPanel _panel;
        private readonly StackPanel _quickPanel;
        private readonly List<KeyValuePair<int, Button>> _jobs = new List<KeyValuePair<int, Button>>();
        private readonly TextBlock[] _folders = new TextBlock[5];
        private readonly TextBlock _result;
        private readonly ComboBox _quality;
        private readonly DispatcherTimer _selectionWatch = new DispatcherTimer(DispatcherPriority.Background) {
            Interval = TimeSpan.FromMilliseconds(500) };
        private bool _busy, _refreshing, _tabLoaded, _quickLoaded;
        internal FrameworkElement QuickButtons => _quickPanel;

        internal PrintExportTab(PrintExportSettings settings, Action<string, bool> status)
        {
            _settings = settings; _status = status;
            _panel = new StackPanel { Margin = new Thickness(6) };
            Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _panel.Children.Add(Title("Печать PDF · выделение или страница"));
            _quality = new ComboBox { ItemsSource = new[] { "300 DPI · детально", "150 DPI · компактно" },
                SelectedIndex = settings.PrintDpi == 150 ? 1 : 0, MinHeight = 27 };
            _quality.SelectionChanged += (_, __) => {
                if (!_refreshing) Safe(() => _settings.SetQuality(_quality.SelectedIndex == 1 ? 150 : 300));
            };
            _panel.Children.Add(_quality);
            AddJob(0, "Экспортировать PDF для печати", null);
            DockerHelp.Add(_panel, "Если есть выделение, PDF включает только его и получает размер по его габаритам, включая обводки. " +
                "Если выделения нет, перед экспортом активной страницы целиком появится подтверждение «Да / Нет». Масштаб 1:1. " +
                "Работает с любыми документами CorelDRAW. Векторы сохраняются, текст выводится кривыми, растры сжимаются JPEG 95% и ограничиваются выбранным DPI.");
            _quickPanel = new StackPanel { Margin = new Thickness(0, 3, 0, 5) };
            var quick = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 5 };
            quick.Children.Add(CreateJobButton(1, "DTF · красная", Color.FromRgb(183, 48, 56), true));
            quick.Children.Add(CreateJobButton(2, "DTF · синяя", Color.FromRgb(35, 100, 190), true));
            quick.Children.Add(CreateJobButton(3, "UV · зелёная", Color.FromRgb(34, 126, 72), true));
            quick.Children.Add(CreateJobButton(4, "UV · жёлтая", Color.FromRgb(242, 201, 76), true));
            quick.Children.Add(CreateJobButton(0, "Сублимация", Color.FromRgb(88, 101, 116), true));
            _quickPanel.Children.Add(quick);
            DockerHelp.Add(_quickPanel, "Отправка файлов в очередь печати. " +
                "DTF/UV — выделение, SUB — выделение или активный лист. Папки очередей задаются в настройках.");
            _result = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            _panel.Children.Add(_result);
            _settings.Changed += Refresh; Refresh();
            _selectionWatch.Tick += (_, __) => UpdateAvailability();
            Loaded += (_, __) => { _tabLoaded = true; UpdateSelectionWatch(); };
            Unloaded += (_, __) => { _tabLoaded = false; UpdateSelectionWatch(); };
            IsVisibleChanged += (_, __) => UpdateSelectionWatch();
            _quickPanel.Loaded += (_, __) => { _quickLoaded = true; UpdateSelectionWatch(); };
            _quickPanel.Unloaded += (_, __) => { _quickLoaded = false; UpdateSelectionWatch(); };
            _quickPanel.IsVisibleChanged += (_, __) => UpdateSelectionWatch();
        }

        private static TextBlock Title(string text) => new TextBlock { Text = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 5) };

        private void AddJob(int index, string text, Color? color)
        {
            var folder = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            _folders[index] = folder;
            var browse = new Button { Content = "Папка…", Margin = new Thickness(0, 2, 0, 2) };
            browse.Click += (_, __) => Safe(() => {
                var source = PresentationSource.FromVisual(this) as HwndSource;
                string path = WindowsFolderPicker.Pick(source == null ? IntPtr.Zero : source.Handle, _settings.Folder(index), text);
                if (!String.IsNullOrWhiteSpace(path)) _settings.SetFolder(index, path);
            });
            _panel.Children.Add(DockerLayout.Trailing(folder, browse));
            _panel.Children.Add(CreateJobButton(index, text, color));
        }

        private Button CreateJobButton(int index, string text, Color? color, bool compact = false)
        {
            var button = new Button { Content = text,
                MinHeight = 34, IsEnabled = false, Margin = compact ? new Thickness(1) : new Thickness(0, 1, 0, 5) };
            if (compact)
            {
                button.Padding = new Thickness(3, 2, 3, 2);
                button.Content = new TextBlock { Text = index == 0 ? "SUB" : index >= 3 ? "UV" : "DTF",
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap };
            }
            ToolTipService.SetShowOnDisabled(button, true);
            if (color.HasValue) DockerTheme.Accent(button, color.Value, index == 4, compact);
            else if (!compact) DockerTheme.Primary(button);
            AutomationProperties.SetName(button, index == 0 ? text : text + " · экспорт выделения в папку");
            button.ToolTip = index == 0 ? "PDF 1:1: выделение, а без него — активная страница" : "Отправить выделение в PDF 1:1 в папку этой кнопки";
            if (index == 4)
            {
                var badge = new Grid { Width = 30, Height = 21, HorizontalAlignment = HorizontalAlignment.Center };
                var frame = new System.Windows.Shapes.Rectangle { StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 }, RadiusX = 2, RadiusY = 2 };
                frame.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = button });
                badge.Children.Add(frame);
                var label = new TextBlock { Text = "UV", FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                label.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = button });
                badge.Children.Add(label);
                button.Content = badge;
            }
            button.Click += (_, __) => Export(index);
            _jobs.Add(new KeyValuePair<int, Button>(index, button));
            return button;
        }

        private void Refresh()
        {
            _refreshing = true;
            try
            {
                for (int i = 0; i < _folders.Length; i++)
                {
                    if (_folders[i] == null) continue;
                    string path = _settings.Folder(i);
                    _folders[i].Text = String.IsNullOrWhiteSpace(path) ? "Папка не выбрана" : path;
                    _folders[i].ToolTip = path;
                }
                _quality.SelectedIndex = _settings.PrintDpi == 150 ? 1 : 0;
                foreach (var job in _jobs)
                {
                    string[] names = { "Сублимация / печать PDF", "DTF · красная", "DTF · синяя", "UV · зелёная", "UV · жёлтая" };
                    string path = _settings.Folder(job.Key);
                    job.Value.ToolTip = new TextBlock { Text = names[job.Key] + "\n" +
                        (job.Key == 0 ? "PDF 1:1: выделение или активная страница после подтверждения" :
                            "PDF 1:1: только выделение. Выделите объекты в Corel, чтобы включить кнопку.") + "\n" +
                        (String.IsNullOrWhiteSpace(path) ? "Папка не выбрана. Откройте настройки печати." : path) + "\n" +
                        _settings.IccFor(job.Key).Description,
                        MaxWidth = 380, TextWrapping = TextWrapping.Wrap };
                }
            }
            finally { _refreshing = false; }
        }

        private void UpdateSelectionWatch()
        {
            if ((_tabLoaded && IsVisible) || (_quickLoaded && _quickPanel.IsVisible))
            {
                UpdateAvailability(); _selectionWatch.Start();
            }
            else _selectionWatch.Stop();
        }

        private void UpdateAvailability()
        {
            if (_busy) return;
            bool documentOpen = false, selected = false;
            try
            {
                dynamic app = CorelApp.Get();
                if (app.ActiveDocument != null)
                {
                    documentOpen = true;
                    dynamic range = app.ActiveSelectionRange;
                    selected = range != null && Convert.ToInt32(range.Count) > 0;
                }
            }
            catch { documentOpen = selected = false; /* Corel can reject calls while a native dialog is open. */ }
            foreach (var job in _jobs)
            {
                bool enabled = documentOpen && (job.Key == 0 || selected);
                if (job.Value.IsEnabled != enabled) job.Value.IsEnabled = enabled;
            }
        }

        private async void Export(int index)
        {
            if (_busy) return;
            _busy = true; _panel.IsEnabled = false; _quickPanel.IsEnabled = false;
            try
            {
                string folder = PrintExportSettings.NormalizeFolder(_settings.Folder(index), true);
                _status("Готовлю PDF для печати…", false);
                var result = await PrintJobExporter.Export(folder, _settings.PrintDpi == 150 ? 150 : 300, index,
                    _settings.IccFor(index), question => MessageBox.Show(question,
                        "Сублимация · печать активного листа", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No) == MessageBoxResult.Yes);
                string text = "PDF 1:1 · " + result.WidthMm.ToString("0.##") + "×" + result.HeightMm.ToString("0.##") +
                    " мм · " + (result.Bytes / 1048576.0).ToString("0.##") + " МБ\n" + result.Path;
                if (!String.IsNullOrEmpty(result.Warning)) text += "\n" + result.Warning;
                _result.Text = text; _status("PDF отправлен в папку: " + result.Path, !String.IsNullOrEmpty(result.Warning));
            }
            catch (OperationCanceledException) { _result.Text = "Печать активного листа отменена."; _status(_result.Text, false); }
            catch (Exception error) { Log.Error("Print PDF export failed.", error); _result.Text = error.Message; _status(error.Message, true); }
            finally { _panel.IsEnabled = true; _quickPanel.IsEnabled = true; _busy = false; UpdateAvailability(); }
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception error) { Log.Error("Print PDF UI failed.", error); _status(error.Message, true); }
        }
    }
}
