using System;
using System.Collections.Generic;
using System.Globalization;
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
        private sealed class ModelChoice
        {
            internal readonly string Id, Label, Price;
            internal ModelChoice(string id, string label, string price)
            { Id = id; Label = label; Price = price; }
            public override string ToString() { return Label; }
        }
        private readonly Func<string> _token;
        private readonly Func<string, string, GenerativeFillCapture.Prepared, string> _import;
        private readonly Action<string, bool> _status;
        private readonly TextBox _prompt;
        private readonly ComboBox _model;
        private readonly TextBlock _modelNote;
        private readonly Image _imagePreview, _maskPreview, _resultPreview;
        private readonly TextBlock _progress;
        private readonly Button _generate, _retry, _cancel;
        private readonly ProgressBar _bar;
        private CancellationTokenSource _cancellation;
        private GenerativeFillCapture.Prepared _prepared;
        private Dictionary<string, object> _input;
        private string _rawOutput, _readyOutput;
        private string _requestModel;
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
            panel.Children.Add(Note("Выделите фотографию и один прямоугольник. Для расширения нарисуйте рамку желаемого кадра за краями фото — она может охватывать весь исходник. Фото сохранится, дорисуются новые края."));
            panel.Children.Add(Note("Прямоугольник целиком внутри фото задаёт область замены: например, для удаления предмета или исправления руки. " +
                "В расширении сохраняется исходник с небольшой полосой совмещения по краю."));
            panel.Children.Add(Note("Достаточно короткой команды: «дорисуй сверху», «убери бутылку», «восстанови руку». " +
                "Модель использует фотографию и выделенную область как ориентир."));
            _model = new ComboBox { Margin = new Thickness(1, 3, 1, 3) };
            _model.Items.Add(new ModelChoice("google/nano-banana-2", "Nano Banana 2 · 2K", "$0.101"));
            _model.Items.Add(new ModelChoice("google/nano-banana-pro", "Nano Banana Pro · 2K", "$0.15"));
            _model.SelectedIndex = LoadModelIndex();
            panel.Children.Add(_model);
            _modelNote = Note(""); panel.Children.Add(_modelNote);
            _model.SelectionChanged += (_, __) => { UpdateModelNote(); SaveModel(); };
            UpdateModelNote();
            panel.Children.Add(Note("Что нужно сделать в выделенной области:"));
            _prompt = new TextBox { MinHeight = 64, MaxHeight = 105, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = LoadPrompt() };
            _prompt.LostFocus += (_, __) => SavePrompt();
            panel.Children.Add(_prompt);
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
            var model = (ModelChoice)_model.SelectedItem;
            if (MessageBox.Show(model.Label + " · платный запрос Replicate · ориентировочно " + model.Price + ". Продолжить?",
                "Генеративное заполнение", MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes) return;

            SetBusy(true, "Подготавливаю фото и маску…");
            try
            {
                _importWarning = null;
                _input = null; _requestModel = null; _rawOutput = null; _prepared = null;
                _prepared = GenerativeFillCapture.Capture();
                _requestModel = model.Id;
                _imagePreview.Source = LoadPreview(_prepared.ImagePath);
                _maskPreview.Source = LoadPreview(_prepared.MaskPath);
                _resultPreview.Source = null;
                _input = new Dictionary<string, object>
                {
                    ["prompt"] = BuildPhotoEditPrompt(prompt, _prepared),
                    // DataUri creates upload copies. The network worker removes its
                    // upload files after success; keep the originals for compositing.
                    ["image_input"] = new[]
                    {
                        AiPrintTab.DataUri(AiPrintTab.Bitmap(_prepared.ModelImagePath), 4096, 16000000)
                    },
                    ["output_format"] = "png",
                    ["aspect_ratio"] = _prepared.ModelAspectRatio,
                    ["resolution"] = "2K"
                };
                // Keep an explicitly selected model throughout retries. Never
                // silently switch provider or submit an automatic second job.
                if (_requestModel == "google/nano-banana-pro") _input["allow_fallback_model"] = false;
                Log.Info("Generative fill request: model=" + _requestModel + ", aspect=" +
                    _prepared.ModelAspectRatio + ", resolution=2K");
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

        private static string BuildPhotoEditPrompt(string request, GenerativeFillCapture.Prepared prepared)
        {
            Rect photo = prepared.PhotoBoundsPixels;
            string bounds = String.Format(CultureInfo.InvariantCulture, "({0:0}, {1:0}) to ({2:0}, {3:0})",
                photo.Left, photo.Top, photo.Right, photo.Bottom);
            string extension = String.IsNullOrWhiteSpace(prepared.ExtensionDirections)
                ? "Perform the requested local edit only in the tinted region. Use the visible details under the tint as context. "
                : "This is OUTPAINTING of one existing photograph toward the " + prepared.ExtensionDirections +
                    " edge(s). The existing photo occupies pixels " + bounds + " from the top-left of the canvas. " +
                    "It is an anchored fragment of the final shot, already at the final scale and position. " +
                    "Continue the visible scene directly across its edges into ALL missing areas. " +
                    "Connect cropped objects and body parts to their visible portions at the boundary. " +
                    "Existing heads stay in their original positions; extend only the missing parts of those same people. " +
                    "Do not fit a new complete photo into the missing area, repeat the scene, zoom out, shrink or move the original. ";
            return "Edit this ONE spatially marked photograph. User request: " + request + "\n" +
                "Bright magenta is an editing annotation: solid magenta marks missing pixels, " +
                "a translucent magenta tint marks the adjoining editable area over the original. " +
                "Remove these annotations by replacing them with photographic content. " + extension +
                "Keep all unmarked content fixed. Match perspective, lighting, colors and texture across every join. " +
                "Preserve people's identity, clothing, pose, scale and positions. " +
                "Return ONE seamless finished photograph on the same " + prepared.ModelWidth + "x" + prepared.ModelHeight +
                " canvas. The working frame is the top-left " + prepared.WidthPixels + "x" + prepared.HeightPixels +
                " pixels; any neutral-gray padding outside it is technical padding, not part of the scene. " +
                "Add text or graphic design only if the user explicitly requests it.";
        }

        private async Task Execute(string key)
        {
            CancellationToken cancellation = _cancellation.Token;
            Progress("Отправляю кадр для правки…");
            if (!File.Exists(_rawOutput) || new FileInfo(_rawOutput).Length == 0)
                await Task.Run(() => ReplicateWorkerClient.Run(_requestModel, key, _input,
                    _rawOutput, OnProgress, cancellation), cancellation);
            cancellation.ThrowIfCancellationRequested();
            Progress("Сохраняю исходник вне области правки…");
            string result = Path.Combine(Path.GetTempPath(),
                "Vanya-Fill-Ready-" + Guid.NewGuid().ToString("N") + ".png");
            GenerativeFillCapture.PreserveOutsideMask(_prepared.ImagePath,
                _prepared.MaskPath, _rawOutput, result, _prepared.ModelWidth, _prepared.ModelHeight);
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
                _readyOutput = null; _input = null; _requestModel = null;
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
            if (!ready && (_input == null || _prepared == null || String.IsNullOrEmpty(_rawOutput) ||
                String.IsNullOrEmpty(_requestModel))) return;
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
            string message = value == "sending" ? "Отправляю кадр для правки…" :
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
            _generate.IsEnabled = _prompt.IsEnabled = _model.IsEnabled = !busy;
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

        private static string ModelPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "generative-fill-model.txt");
        }

        private static int LoadModelIndex()
        {
            try { return File.Exists(ModelPath()) && File.ReadAllText(ModelPath()).Trim() == "google/nano-banana-pro" ? 1 : 0; }
            catch (Exception ex) { Log.Error("Could not load generative fill model.", ex); return 0; }
        }

        private void UpdateModelNote()
        {
            var model = (ModelChoice)_model.SelectedItem;
            _modelNote.Text = "Ориентировочно " + model.Price + " за результат 2K · Replicate. " +
                "Пиксели вне области правки сохраняются из исходника.";
        }

        private void SaveModel()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ModelPath()));
                File.WriteAllText(ModelPath(), ((ModelChoice)_model.SelectedItem).Id);
            }
            catch (Exception ex) { Log.Error("Could not save generative fill model.", ex); }
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
