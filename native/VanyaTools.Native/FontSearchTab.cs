using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class FontSearchTab : UserControl
    {
        private const string WhatTheFontUrl = "https://www.myfonts.com/pages/whatthefont";
        private readonly Action<string, bool> _status;
        private readonly TextBox _path;
        private readonly Image _preview;
        private readonly Button _prepare, _open, _copy;
        private bool _busy;
        private string _samplePath;

        internal FontSearchTab(FrameworkElement legacyAnalysis, Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(7) };
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text = "Поиск шрифта · WhatTheFont", FontSize = 13, FontWeight = FontWeights.SemiBold });
            DockerHelp.Add(panel, "Выделите одну строку букв или слово в Corel. " +
                "Подготовьте PNG: прозрачные буквы получат контрастный фон. " +
                "Откройте WhatTheFont и загрузите этот PNG на сайте. Путь можно вставить в окно выбора файла. " +
                "Сервис ищет совпадения по своей базе; старый ИИ-анализ ниже даёт лишь предположения.");
            _prepare = MakeButton("Подготовить PNG из выделения", async (_, __) => await Prepare()); panel.Children.Add(_prepare);
            _preview = new Image { Height = 150, Stretch = Stretch.Uniform, Margin = new Thickness(0, 3, 0, 3) }; panel.Children.Add(_preview);
            _path = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 10,
                Margin = new Thickness(0, 2, 0, 3) }; panel.Children.Add(_path);
            _copy = MakeButton("Скопировать путь PNG", (_, __) => CopyPath()); panel.Children.Add(_copy);
            _open = DockerTheme.Primary(MakeButton("Открыть WhatTheFont ↗", async (_, __) => {
                if (_samplePath == null || !File.Exists(_samplePath)) { if (!await Prepare()) return; }
                try
                {
                    CopyPath(); Process.Start(new ProcessStartInfo { FileName = WhatTheFontUrl, UseShellExecute = true });
                    _status("В WhatTheFont загрузите подготовленный PNG; путь показан в докере.", false);
                }
                catch (Exception error) { Log.Error("Could not open WhatTheFont.", error); _status(error.Message, true); }
            })); panel.Children.Add(_open);
            panel.Children.Add(new Expander { Header = "Прежний ИИ-анализ · эксперимент", Content = legacyAnalysis,
                IsExpanded = false, Margin = new Thickness(0, 7, 0, 0) });
        }

        private async Task<bool> Prepare()
        {
            if (_busy) return false;
            _busy = true; _prepare.IsEnabled = _open.IsEnabled = _copy.IsEnabled = false;
            string captured = null;
            try
            {
                _status("Подготавливаю буквы для поиска…", false); await Dispatcher.Yield(DispatcherPriority.Background);
                captured = AiSelectionCapture.Capture(2048, 2000000);
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "font-samples");
                Directory.CreateDirectory(folder);
                string sample = Path.Combine(folder, "Font-" + Guid.NewGuid().ToString("N") + ".png");
                BitmapSource image = await Task.Run(() => {
                    BitmapSource reference = AiPrintTab.FontReference(AiPrintTab.Bitmap(captured));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(reference));
                    using (Stream stream = File.Create(sample)) encoder.Save(stream); return reference; });
                _samplePath = sample; _path.Text = sample; _preview.Source = image;
                _status("PNG букв готов. Откройте WhatTheFont и загрузите этот файл.", false); return true;
            }
            catch (Exception error) { Log.Error("Font sample preparation failed.", error); _status(error.GetBaseException().Message, true); return false; }
            finally
            {
                if (captured != null) try { File.Delete(captured); } catch { }
                _busy = false; _prepare.IsEnabled = _open.IsEnabled = _copy.IsEnabled = true;
            }
        }

        private void CopyPath()
        {
            if (_samplePath == null || !File.Exists(_samplePath)) { _status("Сначала подготовьте PNG букв.", true); return; }
            try { Clipboard.SetText(_samplePath); _status("Путь PNG скопирован. Вставьте его в окно выбора файла на сайте.", false); }
            catch (Exception error) { Log.Error("Could not copy font sample path.", error); _status("Путь виден в поле — его можно скопировать вручную.", true); }
        }
        private static Button MakeButton(string text, RoutedEventHandler handler)
        { var button = new Button { Content = text, Margin = new Thickness(0, 2, 0, 2) }; button.Click += handler; return button; }
    }
}
