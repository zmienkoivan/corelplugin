using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class GenerativeFillTab : UserControl
    {
        private const string Model = "black-forest-labs/flux-fill-pro";
        private readonly Func<string> _token;
        private readonly Func<string, string, GenerativeFillCapture.Prepared, string> _import;
        private readonly Action<string, bool> _status;
        private readonly TextBox _prompt;
        private readonly Image _imagePreview, _maskPreview, _resultPreview;
        private readonly TextBlock _progress;
        private readonly Button _generate, _retry, _cancel;
        private readonly ProgressBar _bar;
        private CancellationTokenSource _cancellation;
        private GenerativeFillCapture.Prepared _prepared;
        private Dictionary<string, object> _input;
        private string _rawOutput, _readyOutput;
        private string _importWarning;
        private bool _busy;

        public GenerativeFillTab(Func<string> token,
            Func<string, string, GenerativeFillCapture.Prepared, string> import,
            Action<string, bool> status)
        {
            _token = token; _import = import; _status = status;
            var panel = new StackPanel { Margin = new Thickness(6) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel };
            panel.Children.Add(Note("Выделите фотографию и нарисованный поверх неё прямоугольник. Прямоугольник задаёт область замены и может выходить за край фото."));
            panel.Children.Add(Note("Для расширения фото выделите новую область и немного края исходника. " +
                "Для восстановления руки захватите её видимую часть и место, где она должна продолжиться. " +
                "Заливка продолжает исходное фото по вашему описанию."));
            panel.Children.Add(Note("Что нужно сделать в выделенной области:"));
            _prompt = new TextBox { MinHeight = 64, MaxHeight = 105, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = LoadPrompt() };
            _prompt.LostFocus += (_, __) => SavePrompt();
            panel.Children.Add(_prompt);
            panel.Children.Add(Note("Модель FLUX Fill Pro · ориентировочно $0.05 за результат. Исходное фото останется на холсте."));
            var previews = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 5) };
            previews.Children.Add(Preview("Фото", out _imagePreview));
            previews.Children.Add(Preview("Маска", out _maskPreview));
            previews.Children.Add(Preview("Результат", out _resultPreview));
            panel.Children.Add(previews);
            _generate = DockerTheme.Primary(Button("Дорисовать выделенную область", async (_, __) => await Generate()));
            panel.Children.Add(_generate);
            var actions = new UniformGrid { Columns = 2 };
            _retry = Button("Повторить / вставить", async (_, __) => await Retry());
            _cancel = Button("Отменить", (_, __) => Cancel());
            actions.Children.Add(_retry); actions.Children.Add(_cancel);
            panel.Children.Add(actions);
            _progress = Note(""); panel.Children.Add(_progress);
            _bar = new ProgressBar { Height = 7, IsIndeterminate = true,
                Visibility = Visibility.Collapsed };
            panel.Children.Add(_bar);
            RestorePending();
            SetBusy(false, null);
        }

        private static Button Button(string label, RoutedEventHandler handler)
        {
            var button = new Button { Content = label, Margin = new Thickness(1, 2, 1, 2) };
            button.Click += handler; return button;
        }

        private static TextBlock Note(string text)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                FontSize = 10, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 3) };
        }

        private static FrameworkElement Preview(string label, out Image image)
        {
            var panel = new StackPanel { Margin = new Thickness(1) };
            panel.Children.Add(new TextBlock { Text = label, FontSize = 10 });
            image = new Image { Width = 110, Height = 110, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new Border { Child = image, Background = Brushes.WhiteSmoke,
                BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) });
            return panel;
        }

        private async Task Generate()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат кнопкой «Повторить / вставить».", true); return; }
            string prompt = _prompt.Text.Trim();
            if (prompt.Length == 0) { Report("Опишите, что должно появиться в области правки.", true); return; }
            SavePrompt();
            string key = _token();
            if (key.Length < 8) { Report("Введите ключ Replicate в настройках.", true); return; }
            if (MessageBox.Show("Платный запрос Replicate · ориентировочно $0.05. Продолжить?",
                "Генеративное заполнение", MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes) return;

            SetBusy(true, "Подготавливаю фото и маску…");
            try
            {
                _importWarning = null;
                _prepared = GenerativeFillCapture.Capture();
                _imagePreview.Source = LoadPreview(_prepared.ImagePath);
                _maskPreview.Source = LoadPreview(_prepared.MaskPath);
                _resultPreview.Source = null;
                _input = new Dictionary<string, object>
                {
                    ["prompt"] = BuildPhotoEditPrompt(prompt, _prepared.ExtensionDirections),
                    // DataUri creates upload copies. The network worker removes its
                    // upload files after success; keep the originals for compositing.
                    ["image"] = AiPrintTab.DataUri(AiPrintTab.Bitmap(_prepared.ImagePath), 2048, 4000000),
                    ["mask"] = AiPrintTab.DataUri(AiPrintTab.Bitmap(_prepared.MaskPath), 2048, 4000000),
                    ["output_format"] = "png",
                    ["outpaint"] = "None", // Keep the custom mask authoritative.
                    ["steps"] = 50,
                    ["guidance"] = 30,
                    ["prompt_upsampling"] = false
                };
                _rawOutput = Path.Combine(Path.GetTempPath(),
                    "Vanya-Fill-Model-" + Guid.NewGuid().ToString("N") + ".png");
                await Execute(key);
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Запрос отменён.", false); }
            catch (Exception ex) { Log.Error("Generative fill failed.", ex);
                Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static string BuildPhotoEditPrompt(string request, string extensionDirections)
        {
            string operation = String.IsNullOrWhiteSpace(extensionDirections)
                ? "A seamless local retouch of the same original photograph. Reconstruct or replace the masked content as requested, using the visible surroundings as reference."
                : "A seamless continuation of the same original photograph beyond its " +
                    extensionDirections + " edge(s). The new area continues the existing scene and any cropped subjects naturally.";
            return "One continuous full-bleed photograph. The supplied photograph is the visual reference for all new content. " +
                operation + " Match the existing camera viewpoint, perspective, scale, lighting, color, texture and depth of field. " +
                "Preserve the identity, pose, clothing and proportions of visible people; reconstructed body parts connect naturally to them. " +
                "Edit only the white mask. Preserve the unmasked photograph. " +
                "Do not add text, borders, graphic panels, interface elements, logos or decorative layouts unless the requested edit explicitly calls for them. " +
                "Requested edit or content (follow this description):\n" + request;
        }

        private async Task Execute(string key)
        {
            CancellationToken cancellation = _cancellation.Token;
            Progress("Отправляю фото и маску…");
            await Task.Run(() => ReplicateWorkerClient.Run(Model, key, _input,
                _rawOutput, OnProgress, cancellation), cancellation);
            cancellation.ThrowIfCancellationRequested();
            Progress("Сохраняю исходник вне области правки…");
            string result = Path.Combine(Path.GetTempPath(),
                "Vanya-Fill-Ready-" + Guid.NewGuid().ToString("N") + ".png");
            GenerativeFillCapture.PreserveOutsideMask(_prepared.ImagePath,
                _prepared.MaskPath, _rawOutput, result);
            _readyOutput = result;
            SavePending();
            _resultPreview.Source = LoadPreview(result);
            cancellation.ThrowIfCancellationRequested();
            InsertReady();
        }

        private void InsertReady()
        {
            if (_prepared == null || String.IsNullOrEmpty(_readyOutput) || !File.Exists(_readyOutput))
                throw new InvalidOperationException("Готовый результат не найден.");
            try
            {
                Progress("Вставляю результат в CorelDRAW…");
                string warning = _import(_readyOutput, "Генеративное заполнение", _prepared);
                _importWarning = warning;
                _readyOutput = null; _input = null;
                try { if (File.Exists(PendingPath())) File.Delete(PendingPath()); } catch { }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Результат готов, но вставка не удалась. " +
                    "Нажмите «Повторить / вставить» без новой оплаты. " + ex.GetBaseException().Message, ex);
            }
        }

        private async Task Retry()
        {
            if (_busy) return;
            bool ready = !String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput);
            if (!ready && (_input == null || _prepared == null || String.IsNullOrEmpty(_rawOutput))) return;
            string key = ready ? "" : _token();
            if (!ready && key.Length < 8) { Report("Введите ключ Replicate в настройках.", true); return; }
            if (!ready && MessageBox.Show("Продолжить запрос? Если он не был отправлен, может начаться новый платный запуск.",
                "Генеративное заполнение", MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            SetBusy(true, ready ? "Вставляю готовый результат…" : "Продолжаю запрос…");
            try
            {
                _importWarning = null;
                if (ready) InsertReady();
                else await Execute(key);
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Запрос отменён.", false); }
            catch (Exception ex) { Log.Error("Generative fill retry failed.", ex);
                Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private void Cancel()
        {
            if (!_busy || _cancellation == null) return;
            _cancellation.Cancel(); _cancel.IsEnabled = false;
            Progress("Отменяю запрос…");
        }

        private void OnProgress(string value)
        {
            string message = value == "sending" ? "Отправляю фото и маску…" :
                value == "processing" ? "Нейросеть дорисовывает область…" :
                value == "downloading" ? "Загружаю результат…" :
                value == "cancelling" ? "Отменяю запрос…" : value;
            Dispatcher.BeginInvoke(new Action(() => { if (_busy) Progress(message); }));
        }

        private void Progress(string message) { _progress.Text = message; _status(message, false); }
        private void Report(string message, bool error) { _progress.Text = message; _status(message, error); }
        private void ReportOutcome()
        {
            Report(String.IsNullOrEmpty(_importWarning)
                ? "Готово: результат вставлен рядом с фото." : _importWarning,
                !String.IsNullOrEmpty(_importWarning));
            _importWarning = null;
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            if (busy) _cancellation = new CancellationTokenSource();
            else if (_cancellation != null) { _cancellation.Dispose(); _cancellation = null; }
            _generate.IsEnabled = _prompt.IsEnabled = !busy;
            _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _cancel.IsEnabled = busy;
            _retry.Visibility = !busy && ((!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput)) ||
                _input != null) ? Visibility.Visible : Visibility.Collapsed;
            _bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!String.IsNullOrEmpty(message)) Progress(message);
        }

        private static BitmapSource LoadPreview(string path)
        {
            var image = new BitmapImage();
            image.BeginInit(); image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 360;
            image.EndInit(); image.Freeze();
            return image;
        }

        private static string PendingPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "pending-generative-fill.json");
        }

        private static string PromptPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "generative-fill-prompt.txt");
        }

        private static string LoadPrompt()
        {
            try { return File.Exists(PromptPath()) ? File.ReadAllText(PromptPath()) : ""; }
            catch (Exception ex) { Log.Error("Could not load generative fill prompt.", ex); return ""; }
        }

        private void SavePrompt()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PromptPath()));
                File.WriteAllText(PromptPath(), _prompt.Text);
            }
            catch (Exception ex) { Log.Error("Could not save generative fill prompt.", ex); }
        }

        private void SavePending()
        {
            var pending = new Dictionary<string, object>
            {
                ["output"] = _readyOutput,
                ["left"] = _prepared.LeftMm,
                ["top"] = _prepared.TopMm,
                ["width"] = _prepared.WidthMm,
                ["height"] = _prepared.HeightMm
            };
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
            File.WriteAllText(PendingPath(), new JavaScriptSerializer().Serialize(pending));
        }

        private void RestorePending()
        {
            try
            {
                if (!File.Exists(PendingPath())) return;
                var pending = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(PendingPath()));
                if (pending == null || !pending.ContainsKey("output")) return;
                string output = Convert.ToString(pending["output"]);
                if (!File.Exists(output)) return;
                _readyOutput = output;
                _prepared = new GenerativeFillCapture.Prepared
                {
                    LeftMm = Convert.ToDouble(pending["left"]),
                    TopMm = Convert.ToDouble(pending["top"]),
                    WidthMm = Convert.ToDouble(pending["width"]),
                    HeightMm = Convert.ToDouble(pending["height"])
                };
                _resultPreview.Source = LoadPreview(_readyOutput);
            }
            catch (Exception ex) { Log.Error("Generative fill pending result restore failed.", ex); }
        }
    }
}
