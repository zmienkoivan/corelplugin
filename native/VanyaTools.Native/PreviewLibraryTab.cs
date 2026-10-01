using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class PreviewLibraryTab : UserControl
    {
        private readonly Action<string, bool> _status;
        private readonly TextBox _server, _link;
        private readonly PasswordBox _key;
        private readonly StackPanel _items;
        private readonly Expander _settings;
        private readonly Button _publish, _refresh, _cancel;
        private readonly TextBlock _activity, _size;
        private readonly ProgressBar _progress;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private CancellationTokenSource _cancellation;
        private bool _busy;
        private string _stage;

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "preview-publisher.bin");

        public PreviewLibraryTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(8) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
            panel.Children.Add(new TextBlock { Text = "Превью принта", FontSize = 14,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 6) });
            panel.Children.Add(Note("Выделите принт. Ссылка действует 7 дней."));
            _publish = Button("Опубликовать выделение", async (_, __) => await Publish());
            panel.Children.Add(_publish);
            _cancel = Button("Отменить", (_, __) => _cancellation?.Cancel());
            _cancel.Visibility = Visibility.Collapsed;
            panel.Children.Add(_cancel);
            _size = Note("Размер сохраняется. Большие макеты: 150 или 72 DPI.");
            panel.Children.Add(_size);
            _activity = Note("");
            panel.Children.Add(_activity);
            _progress = new ProgressBar { Height = 6, IsIndeterminate = true,
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 5) };
            panel.Children.Add(_progress);
            _timer.Tick += (_, __) => _activity.Text = _stage + " · " + _elapsed.Elapsed.ToString(@"m\:ss");
            _link = new TextBox { IsReadOnly = true, FontSize = 11,
                Margin = new Thickness(0, 1, 0, 4) };
            panel.Children.Add(_link);
            panel.Children.Add(Button("Копировать ссылку", (_, __) => Copy(_link.Text)));

            var header = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            header.Children.Add(new TextBlock { Text = "Опубликованные", FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            _refresh = Button("Обновить список", async (_, __) => await Refresh());
            header.Children.Add(_refresh);
            panel.Children.Add(header);
            _items = new StackPanel();
            panel.Children.Add(_items);

            var settingsPanel = new StackPanel { Margin = new Thickness(4) };
            _settings = new Expander { Header = "Подключение", IsExpanded = false,
                Margin = new Thickness(0, 7, 0, 0), Content = settingsPanel };
            panel.Children.Add(_settings);
            settingsPanel.Children.Add(Note("Настраивается один раз. Ключ сохраняется в Windows в зашифрованном виде."));
            settingsPanel.Children.Add(Note("Адрес сервера"));
            _server = new TextBox { Text = "https://evpmerch.com:9443", FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4) };
            settingsPanel.Children.Add(_server);
            settingsPanel.Children.Add(Note("Ключ публикации"));
            _key = new PasswordBox { FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
            settingsPanel.Children.Add(_key);
            settingsPanel.Children.Add(Button("Сохранить подключение", (_, __) => SaveSettings()));
            LoadSettings();
            Loaded += async (_, __) => await Refresh();
        }

        private async Task Publish()
        {
            if (_busy) return;
            if (!Credentials()) return;
            int dpi;
            long pixels;
            double widthMm, heightMm;
            long pixels300;
            try { pixels300 = AiSelectionCapture.EstimatePublishPixels(300, out widthMm, out heightMm); }
            catch (Exception error) { _status(error.Message, true); return; }
            dpi = pixels300 <= 70000000 && FitsSide(widthMm, heightMm, 300) ? 300
                : PixelCount(widthMm, heightMm, 150) <= 70000000 &&
                  FitsSide(widthMm, heightMm, 150) ? 150 : 72;
            pixels = PixelCount(widthMm, heightMm, dpi);
            _size.Text = String.Format(CultureInfo.CurrentCulture,
                "{0:0} × {1:0} мм · публикация {2} DPI ({3:0.0} Мп).\n" +
                "Без сжатия: 300 / 150 / 72 DPI — {4} / {5} / {6} МБ.",
                widthMm, heightMm, dpi, pixels / 1000000.0,
                RawSize(widthMm, heightMm, 300), RawSize(widthMm, heightMm, 150),
                RawSize(widthMm, heightMm, 72));
            if (pixels > 70000000 || !FitsSide(widthMm, heightMm, dpi))
            { _status("Макет слишком велик даже при 72 DPI. Уменьшите выделение.", true); return; }
            string title = "Принт";
            try
            {
                dynamic doc = CorelApp.Get().ActiveDocument;
                string name = Convert.ToString(doc.Name);
                if (!String.IsNullOrWhiteSpace(name)) title = name;
            }
            catch { }
            title += " · " + DateTime.Now.ToString("dd.MM HH:mm", CultureInfo.CurrentCulture);
            string options = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                ["title"] = title, ["watermark"] = "evpmerch.com", ["opacity"] = 0.32,
                ["days"] = 7, ["dpi"] = dpi
            });
            string source = null;
            _cancellation = new CancellationTokenSource();
            SetBusy(true, "Подготовка выделения");
            _link.Text = "";
            try
            {
                source = AiSelectionCapture.CaptureForPublish(dpi);
                _cancellation.Token.ThrowIfCancellationRequested();
                long uploadBytes = new FileInfo(source).Length;
                if (uploadBytes > 350L * 1024 * 1024)
                    throw new InvalidOperationException("PNG больше 350 МБ. Уменьшите выделение.");
                _stage = "Отправка изображения";
                string captured = source, server = _server.Text.Trim(), key = _key.Password;
                string link = await Task.Run(() => PreviewPublishWorkerClient.Run(server, key,
                    captured, options, stage => Dispatcher.BeginInvoke(new Action(() =>
                    { _stage = stage == "processing" ? "Создание превью" : "Отправка изображения"; })),
                    _cancellation.Token));
                _link.Text = link;
                SaveSettings(false);
                _status("Превью опубликовано. Ссылка готова.", false);
                await LoadList();
            }
            catch (OperationCanceledException) { _status("Публикация отменена.", false); }
            catch (Exception error) { Log.Error("Preview publish failed", error); _status(error.Message, true); }
            finally
            {
                SetBusy(false, null);
                _cancellation.Dispose(); _cancellation = null;
                if (source != null) { try { File.Delete(source); File.Delete(source + ".cancel"); } catch { } }
            }
        }

        private async Task Refresh()
        {
            if (_busy || !Credentials(false)) return;
            SetBusy(true, "Обновление списка");
            try { await LoadList(); }
            catch (Exception error) { _status(error.Message, true); }
            finally { SetBusy(false, null); }
        }

        private async Task LoadList()
        {
            string server = _server.Text.Trim(), key = _key.Password;
            var previews = await Task.Run(() => PreviewAdminWorkerClient.List(server, key));
            _items.Children.Clear();
            if (previews.Count == 0) { _items.Children.Add(Note("Пока нет опубликованных превью.")); return; }
            foreach (var item in previews)
            {
                string id = Value(item, "id"), link = Value(item, "link");
                string title = Value(item, "title"), expires = Value(item, "expiresAt");
                string dpiText = Value(item, "dpi"), bytesText = Value(item, "sizeBytes");
                DateTime date;
                string until = DateTime.TryParse(expires, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out date) ? date.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : expires;
                var row = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
                row.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap,
                    FontSize = 11, FontWeight = FontWeights.SemiBold });
                long sizeBytes;
                string size = Int64.TryParse(bytesText, out sizeBytes) && sizeBytes >= 0
                    ? FormatFileSize(sizeBytes) : "вес неизвестен";
                row.Children.Add(Note("Готовый PNG: " + size + " · " + dpiText + " DPI · до " + until));
                var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
                actions.Children.Add(Button("Ссылка", (_, __) => Copy(link)));
                actions.Children.Add(Button("Открыть", (_, __) => Open(link)));
                actions.Children.Add(Button("Удалить", async (_, __) => await Delete(id, title)));
                row.Children.Add(actions);
                _items.Children.Add(new Border { Child = row, BorderBrush = Brushes.LightGray,
                    BorderThickness = new Thickness(0, 0, 0, 1) });
            }
        }

        private async Task Delete(string id, string title)
        {
            if (_busy) return;
            if (MessageBox.Show("Удалить превью «" + title + "»? Ссылка сразу перестанет работать.",
                "Vanya Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SetBusy(true, "Удаление превью");
            try
            {
                string server = _server.Text.Trim(), key = _key.Password;
                await Task.Run(() => PreviewAdminWorkerClient.Delete(server, key, id));
                if (_link.Text.Contains("/p/" + id)) _link.Text = "";
                await LoadList();
                _status("Превью удалено.", false);
            }
            catch (Exception error) { _status(error.Message, true); }
            finally { SetBusy(false, null); }
        }

        private bool Credentials(bool report = true)
        {
            if (!String.IsNullOrWhiteSpace(_server.Text) && !String.IsNullOrWhiteSpace(_key.Password)) return true;
            _settings.IsExpanded = true;
            if (report) _status("Укажите адрес и ключ публикации в блоке «Подключение».", true);
            return false;
        }

        private void SaveSettings(bool report = true)
        {
            if (!Credentials(report)) return;
            try
            {
                string json = new JavaScriptSerializer().Serialize(new Dictionary<string, string>
                    { ["server"] = _server.Text.Trim(), ["key"] = _key.Password });
                byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null,
                    DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllBytes(SettingsPath, encrypted);
                if (report) _status("Подключение сохранено.", false);
            }
            catch (Exception error) { _status("Не удалось сохранить подключение: " + error.Message, true); }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) { _settings.IsExpanded = true; return; }
                string json = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    File.ReadAllBytes(SettingsPath), null, DataProtectionScope.CurrentUser));
                var values = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
                if (values.TryGetValue("server", out string server)) _server.Text = server;
                if (values.TryGetValue("key", out string key)) _key.Password = key;
            }
            catch { _settings.IsExpanded = true; }
        }

        private void SetBusy(bool busy, string stage)
        {
            _busy = busy;
            _publish.IsEnabled = _refresh.IsEnabled = !busy;
            _cancel.Visibility = busy && _cancellation != null ? Visibility.Visible : Visibility.Collapsed;
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) { _stage = stage; _elapsed.Restart(); _timer.Start(); }
            else { _timer.Stop(); _elapsed.Stop(); _activity.Text = ""; }
        }

        private static string Value(Dictionary<string, object> item, string name) =>
            item.ContainsKey(name) ? Convert.ToString(item[name]) : "";
        private static long PixelCount(double widthMm, double heightMm, int dpi) =>
            checked((long)Math.Ceiling(widthMm * dpi / 25.4) *
                (long)Math.Ceiling(heightMm * dpi / 25.4));
        private static bool FitsSide(double widthMm, double heightMm, int dpi) =>
            Math.Ceiling(widthMm * dpi / 25.4) <= 20000 &&
            Math.Ceiling(heightMm * dpi / 25.4) <= 20000;
        private static string RawSize(double widthMm, double heightMm, int dpi) =>
            FormatMegabytes(PixelCount(widthMm, heightMm, dpi) * 4);
        private static string FormatMegabytes(long bytes) =>
            (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture);
        private static string FormatFileSize(long bytes) => bytes < 1048576
            ? (bytes / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " КБ"
            : FormatMegabytes(bytes) + " МБ";
        private void Copy(string link)
        {
            if (String.IsNullOrWhiteSpace(link)) { _status("Ссылка недоступна.", true); return; }
            Clipboard.SetText(link);
            _status("Ссылка скопирована.", false);
        }
        private void Open(string link)
        {
            if (String.IsNullOrWhiteSpace(link)) return;
            try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
            catch (Exception error) { _status(error.Message, true); }
        }
        private static TextBlock Note(string text) => new TextBlock { Text = text, FontSize = 11,
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 5) };
        private static Button Button(string text, RoutedEventHandler handler)
        {
            var button = new Button { Content = text, MinHeight = 28, Margin = new Thickness(1, 1, 1, 3) };
            button.Click += handler;
            return button;
        }
    }
}
