using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class PrintExportTab : UserControl
    {
        private readonly PrintExportSettings _settings;
        private readonly Action<string, bool> _status;
        private readonly StackPanel _panel;
        private readonly TextBlock[] _folders = new TextBlock[5];
        private readonly TextBlock _result;
        private readonly ComboBox _quality;
        private bool _busy, _refreshing;

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
                "Если выделения нет, экспортируется активная страница целиком, с её размером. Масштаб 1:1. " +
                "Работает с любыми документами CorelDRAW. Векторы сохраняются, текст выводится кривыми, растры сжимаются JPEG 95% и ограничиваются выбранным DPI.");
            _panel.Children.Add(Title("DTF и UV · только выделение"));
            AddJob(1, "DTF · красная", Color.FromRgb(183, 48, 56));
            AddJob(2, "DTF · синяя", Color.FromRgb(35, 100, 190));
            AddJob(3, "UV · зелёная", Color.FromRgb(34, 126, 72));
            AddJob(4, "UV · жёлтая", Color.FromRgb(242, 201, 76));
            DockerHelp.Add(_panel, "Четыре кнопки готовят PDF 1:1, каждая в свою папку. Красная и синяя — DTF, зелёная и жёлтая — UV. " +
                "Надпись UV выделена маленькой пунктирной рамкой только в интерфейсе. Без выделения задание не отправляется. " +
                "Исходное разрешение растров сохраняется, используется ZIP без потерь. Страница целиком не растрируется, белый фон не добавляется. " +
                "Исходные RGB/CMYK и прозрачность сохраняются; профиль материала, белый слой и зеркалирование настраиваются в RIP. " +
                "Папки можно выбрать здесь либо в «Настройки → Печать PDF, DTF и UV». PDF появляется только после окончания записи.");
            _result = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            _panel.Children.Add(_result);
            _settings.Changed += Refresh; Refresh();
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
            var button = new Button { Content = text, MinHeight = 34, Margin = new Thickness(0, 1, 0, 5) };
            if (color.HasValue) DockerTheme.Accent(button, color.Value, index == 4);
            else DockerTheme.Primary(button);
            AutomationProperties.SetName(button, index == 0 ? text : text + " · экспорт выделения в папку");
            button.ToolTip = index == 0 ? "PDF 1:1: выделение, а без него — активная страница" : "Отправить выделение в PDF 1:1 в папку этой кнопки";
            if (index >= 3)
            {
                Brush ink = index == 4 ? new SolidColorBrush(Color.FromRgb(37, 60, 76)) : Brushes.White;
                var badge = new Grid { Width = 30, Height = 21, Margin = new Thickness(0, 0, 6, 0) };
                badge.Children.Add(new System.Windows.Shapes.Rectangle { Stroke = ink, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 }, RadiusX = 2, RadiusY = 2 });
                badge.Children.Add(new TextBlock { Text = "UV", Foreground = ink, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(badge);
                content.Children.Add(new TextBlock { Text = index == 3 ? "Зелёная · отправить" : "Жёлтая · отправить",
                    Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
                button.Content = content;
            }
            button.Click += (_, __) => Export(index);
            _panel.Children.Add(button);
        }

        private void Refresh()
        {
            _refreshing = true;
            try
            {
                for (int i = 0; i < _folders.Length; i++)
                {
                    string path = _settings.Folder(i);
                    _folders[i].Text = String.IsNullOrWhiteSpace(path) ? "Папка не выбрана" : path;
                    _folders[i].ToolTip = path;
                }
                _quality.SelectedIndex = _settings.PrintDpi == 150 ? 1 : 0;
            }
            finally { _refreshing = false; }
        }

        private void Export(int index)
        {
            if (_busy) return;
            _busy = true; _panel.IsEnabled = false;
            try
            {
                string folder = PrintExportSettings.NormalizeFolder(_settings.Folder(index), true);
                _status("Готовлю PDF для печати…", false);
                var result = SportPrintExporter.Export(folder, _settings.PrintDpi == 150 ? 150 : 300, true, index != 0,
                    processName: index >= 3 ? "UV" : "DTF");
                string text = "PDF 1:1 · " + result.WidthMm.ToString("0.##") + "×" + result.HeightMm.ToString("0.##") +
                    " мм · " + (result.Bytes / 1048576.0).ToString("0.##") + " МБ\n" + result.Path;
                if (!String.IsNullOrEmpty(result.Warning)) text += "\n" + result.Warning;
                _result.Text = text; _status("PDF отправлен в папку: " + result.Path, !String.IsNullOrEmpty(result.Warning));
            }
            catch (Exception error) { Log.Error("Print PDF export failed.", error); _result.Text = error.Message; _status(error.Message, true); }
            finally { _panel.IsEnabled = true; _busy = false; }
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception error) { Log.Error("Print PDF UI failed.", error); _status(error.Message, true); }
        }
    }
}
