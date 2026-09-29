using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class SimpleAiTab : UserControl
    {
        private readonly Func<string> _capture;
        private readonly Func<string> _token;
        private readonly Func<string, string> _import;
        private readonly Action<string, bool> _status;
        private readonly ComboBox _model;
        private readonly TextBox _prompt;
        private readonly Image _sourcePreview, _resultPreview;
        private readonly TextBlock _progress;
        private readonly ProgressBar _progressBar;
        private readonly Button _removeButton, _upscaleButton, _editButton, _cancelButton, _retryButton;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private CancellationTokenSource _cancellation;
        private bool _busy;
        private string _stage, _lastModel, _lastOutput, _readyOutput;
        private string _importWarning;
        private Dictionary<string, object> _lastInput;

        private sealed class ModelChoice
        {
            public readonly string Id, Name;
            public readonly decimal EstimatedCost;
            public ModelChoice(string id, string name, decimal estimatedCost)
            { Id = id; Name = name; EstimatedCost = estimatedCost; }
            public override string ToString() { return Name; }
        }

        private sealed class StyleChoice
        {
            public readonly string Name, Asset, Prompt;
            public StyleChoice(string name, string asset, string prompt)
            { Name = name; Asset = asset; Prompt = prompt; }
        }

        public SimpleAiTab(Func<string> capture, Func<string> token,
            Func<string, string> import, Action<string, bool> status)
        {
            _capture = capture; _token = token; _import = import; _status = status;
            var panel = new StackPanel { Margin = new Thickness(7) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };

            panel.Children.Add(Label("Простые AI-инструменты", true));
            panel.Children.Add(Note("Выделите объект или группу в CorelDRAW. Результат появится на холсте."));
            var quick = new UniformGrid { Columns = 2, Margin = new Thickness(0, 3, 0, 5) };
            _removeButton = Button("Удалить фон", async (_, __) => await Run("background"));
            _upscaleButton = Button("Апскейл ×2", async (_, __) => await Run("upscale"));
            quick.Children.Add(_removeButton); quick.Children.Add(_upscaleButton);
            panel.Children.Add(quick);

            var previews = new UniformGrid { Columns = 2 };
            previews.Children.Add(Preview("Выделение", out _sourcePreview));
            previews.Children.Add(Preview("Результат", out _resultPreview));
            panel.Children.Add(previews);

            panel.Children.Add(Label("Стилизация", true));
            var styles = new UniformGrid { Columns = 2 };
            foreach (var style in new[]
            {
                new StyleChoice("Аниме", "anime", "Transform the selected image into polished Japanese anime illustration with clean expressive linework and cel shading."),
                new StyleChoice("Наивная иллюстрация", "naive", "Transform the selected image into a playful handmade naive illustration with simple shapes, uneven ink outlines and flat warm colors."),
                new StyleChoice("Пиксар-персонаж", "animated3d", "Transform the selected subject into a friendly, expressive 3D animated feature-film character with rounded forms and soft studio lighting."),
                new StyleChoice("Акварель", "watercolor", "Transform the selected image into a delicate hand-painted watercolor illustration with soft pigment washes and natural paper texture."),
                new StyleChoice("Комикс", "comic", "Transform the selected image into modern comic-book art with bold ink outlines, halftone shadows and vivid flat color blocks.")
            }) styles.Children.Add(StyleCard(style));
            panel.Children.Add(styles);
            panel.Children.Add(Label("Модель", false));
            _model = new ComboBox { FontSize = 11, Margin = new Thickness(0, 0, 0, 5) };
            _model.Items.Add(new ModelChoice("black-forest-labs/flux-2-pro", "FLUX.2 Pro", 0.14m));
            _model.Items.Add(new ModelChoice("black-forest-labs/flux-kontext-max", "FLUX.1 Kontext Max", 0.08m));
            _model.Items.Add(new ModelChoice("qwen/qwen-image-edit", "Qwen Image Edit", 0.03m));
            _model.SelectedIndex = 0;
            panel.Children.Add(_model);
            panel.Children.Add(Label("Промпт", false));
            _prompt = new TextBox { MinHeight = 75, MaxHeight = 160, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 11 };
            panel.Children.Add(_prompt);
            panel.Children.Add(Note("Выберите стиль или напишите свой промпт. Исходник сохранится."));
            _editButton = Button("Применить стиль / промпт", async (_, __) => await Run("edit"));
            panel.Children.Add(_editButton);

            _cancelButton = Button("Отменить", (_, __) => Cancel());
            _cancelButton.Visibility = Visibility.Collapsed;
            panel.Children.Add(_cancelButton);
            _retryButton = Button("Повторить", async (_, __) => await Retry());
            _retryButton.Visibility = Visibility.Collapsed;
            panel.Children.Add(_retryButton);
            _progress = Note("");
            _progressBar = new ProgressBar { Height = 7, IsIndeterminate = true,
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 5) };
            panel.Children.Add(_progress); panel.Children.Add(_progressBar);
            _timer.Tick += (_, __) => _progress.Text = _stage + " · " + (int)_elapsed.Elapsed.TotalSeconds + " с";
            RestorePending();
        }

        private Border StyleCard(StyleChoice style)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Image { Source = Thumbnail(style.Asset), Width = 90, Height = 90,
                Stretch = Stretch.UniformToFill, Margin = new Thickness(2) });
            content.Children.Add(new TextBlock { Text = style.Name, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, FontSize = 10, MaxWidth = 130 });
            var choice = new RadioButton { GroupName = "VanyaSimpleAiStyle", Content = content,
                HorizontalAlignment = HorizontalAlignment.Center };
            var border = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                Margin = new Thickness(2), Padding = new Thickness(3), Child = choice };
            choice.Checked += (_, __) => { _prompt.Text = style.Prompt +
                " Preserve the main subject, pose and composition of the input image. Do not add text.";
                border.BorderBrush = Brushes.SteelBlue; border.BorderThickness = new Thickness(2); };
            choice.Unchecked += (_, __) => { border.BorderBrush = Brushes.LightGray;
                border.BorderThickness = new Thickness(1); };
            return border;
        }

        private static BitmapSource Thumbnail(string asset)
        {
            string resource = "VanyaTools.Native.Assets.styles." + asset + ".png";
            using (Stream stream = typeof(SimpleAiTab).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null) throw new InvalidDataException("Не найдена миниатюра стиля: " + asset);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image.Freeze();
                return image;
            }
        }

        private async Task Run(string action)
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат кнопкой «Повторить вставку».", true); return; }
            string key = _token();
            if (key.Length < 8) { Report("Укажите ключ Replicate во вкладке «AI-графика».", true); return; }
            var selectedModel = _model.SelectedItem as ModelChoice;
            if (selectedModel == null) { Report("Выберите модель.", true); return; }
            string prompt = _prompt.Text.Trim();
            if (action == "edit" && prompt.Length == 0) { Report("Выберите стиль или введите промпт.", true); return; }
            string model = action == "background" ? "bria/remove-background" :
                action == "upscale" ? "nightmareai/real-esrgan" : selectedModel.Id;
            decimal cost = action == "background" ? 0.018m : action == "upscale" ? 0.002m : selectedModel.EstimatedCost;
            if (MessageBox.Show("Платный запрос Replicate · ориентировочно $" +
                cost.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                ". Продолжить?", "Vanya Tools", MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            SetBusy(true, "Подготавливаю выделение…");
            try
            {
                _importWarning = null;
                // Corel COM capture must run on the UI thread. It duplicates the selection,
                // rasterizes the temporary copy, then restores the original objects.
                string source = _capture();
                _sourcePreview.Source = AiPrintTab.Bitmap(source);
                _resultPreview.Source = null;
                string image = action == "background"
                    ? AiPrintTab.DataUri(AiPrintTab.Bitmap(source), 4096, 16000000)
                    : AiPrintTab.DataUri(AiPrintTab.Bitmap(source), 2048,
                        action == "upscale" ? 1900000 : 4000000);
                Dictionary<string, object> input;
                if (action == "background")
                    input = new Dictionary<string, object> { ["image"] = image,
                        ["preserve_alpha"] = true, ["content_moderation"] = false };
                else if (action == "upscale")
                    input = new Dictionary<string, object> { ["image"] = image,
                        ["scale"] = 2, ["face_enhance"] = false };
                else input = PrintRestorationPipeline.ImageInput(model, image, prompt);
                _lastModel = model; _lastInput = input;
                _lastOutput = Path.Combine(Path.GetTempPath(), "Vanya-Simple-AI-" + Guid.NewGuid().ToString("N") + ".png");
                await Execute(key);
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Simple AI operation failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private async Task Execute(string key)
        {
            CancellationToken cancellation = _cancellation.Token;
            SetStage("Отправляю изображение…");
            await Task.Run(() => ReplicateWorkerClient.Run(_lastModel, key, _lastInput,
                _lastOutput, UpdateProgress, cancellation));
            cancellation.ThrowIfCancellationRequested();
            SetStage("Подготавливаю результат…");
            string png = Path.Combine(Path.GetTempPath(), "Vanya-Simple-Ready-" + Guid.NewGuid().ToString("N") + ".png");
            await Task.Run(() => SaveAsPng(_lastOutput, png), cancellation);
            _readyOutput = png;
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
            File.WriteAllText(PendingPath(), png);
            _resultPreview.Source = AiPrintTab.Bitmap(png);
            cancellation.ThrowIfCancellationRequested();
            InsertReady();
        }

        private static void SaveAsPng(string source, string destination)
        {
            BitmapSource image = AiPrintTab.Bitmap(source);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(destination)) encoder.Save(stream);
        }

        private void InsertReady()
        {
            try
            {
                string warning = _import(_readyOutput);
                _importWarning = warning;
                _readyOutput = null; _lastInput = null;
                try { if (File.Exists(PendingPath())) File.Delete(PendingPath()); } catch { }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Результат готов, но вставка не удалась. " +
                    "Нажмите «Повторить вставку» без новой оплаты. " + ex.GetBaseException().Message, ex);
            }
        }

        private async Task Retry()
        {
            if (_busy) return;
            bool ready = !String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput);
            if (!ready && (_lastInput == null || String.IsNullOrEmpty(_lastOutput))) return;
            string key = ready ? "" : _token();
            if (!ready && key.Length < 8) { Report("Укажите ключ Replicate.", true); return; }
            if (!ready && MessageBox.Show("Продолжить запрос? При необходимости будет создан новый платный запуск.",
                "Vanya Tools", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            SetBusy(true, ready ? "Вставляю результат…" : "Продолжаю запрос…");
            try
            {
                _importWarning = null;
                if (ready) InsertReady();
                else await Execute(key);
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Simple AI retry failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private void Cancel()
        {
            if (!_busy || _cancellation == null) return;
            _cancellation.Cancel();
            _cancelButton.IsEnabled = false;
            SetStage("Отменяю запрос…");
        }

        private void UpdateProgress(string progress)
        {
            string message = progress == "sending" ? "Отправляю изображение…" :
                progress == "processing" ? "Модель обрабатывает изображение…" :
                progress == "downloading" ? "Загружаю результат…" :
                progress == "cancelling" ? "Отменяю запрос…" : progress;
            Dispatcher.BeginInvoke(new Action(() => { if (_busy) SetStage(message); }));
        }

        private void SetStage(string message) { _stage = message; _progress.Text = message; _status(message, false); }

        private void SetBusy(bool busy, string stage)
        {
            _busy = busy;
            _removeButton.IsEnabled = _upscaleButton.IsEnabled = _editButton.IsEnabled = !busy;
            _model.IsEnabled = _prompt.IsEnabled = !busy;
            _cancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _cancelButton.IsEnabled = busy;
            _retryButton.Visibility = !busy && ((!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput)) ||
                _lastInput != null) ? Visibility.Visible : Visibility.Collapsed;
            _retryButton.Content = !String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput)
                ? "Повторить вставку · бесплатно" : "Повторить запрос";
            _progressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                _cancellation = new CancellationTokenSource();
                _elapsed.Restart(); _timer.Start(); SetStage(stage);
            }
            else
            {
                _timer.Stop(); _elapsed.Stop();
                if (_cancellation != null) { _cancellation.Dispose(); _cancellation = null; }
            }
        }

        private static string PendingPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "pending-simple-ai-result.txt");
        }

        private void RestorePending()
        {
            try
            {
                if (!File.Exists(PendingPath())) return;
                string path = File.ReadAllText(PendingPath()).Trim();
                if (!File.Exists(path)) return;
                _readyOutput = path;
                _resultPreview.Source = AiPrintTab.Bitmap(path);
                SetBusy(false, null);
            }
            catch (Exception ex) { Log.Error("Simple AI pending result restore failed.", ex); }
        }

        private void Report(string message, bool error) { _status(message, error); _progress.Text = message; }
        private void ReportOutcome()
        {
            Report(String.IsNullOrEmpty(_importWarning) ? "Готово: результат вставлен на холст." : _importWarning,
                !String.IsNullOrEmpty(_importWarning));
            _importWarning = null;
        }
        private static TextBlock Label(string text, bool heading) { return new TextBlock { Text = text,
            FontSize = heading ? 13 : 11, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) }; }
        private static TextBlock Note(string text) { return new TextBlock { Text = text, FontSize = 10,
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 5) }; }
        private static Button Button(string text, RoutedEventHandler handler) { var button = new Button
            { Content = text, FontSize = 11, MinHeight = 28, Margin = new Thickness(2) };
            button.Click += handler; return button; }
        private static Border Preview(string title, out Image image)
        {
            image = new Image { Height = 115, Stretch = Stretch.Uniform };
            var stack = new StackPanel(); stack.Children.Add(Label(title, false));
            stack.Children.Add(new Border { Height = 120, BorderThickness = new Thickness(1),
                BorderBrush = Brushes.LightGray, Child = image });
            return new Border { Margin = new Thickness(2), Child = stack };
        }
    }
}
