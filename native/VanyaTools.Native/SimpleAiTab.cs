using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class SimpleAiTab : UserControl
    {
        private readonly Func<string> _capture;
        private readonly Func<string> _token;
        private readonly Func<string, string> _import;
        private readonly Func<string, string> _importDtf;
        private readonly Action<string, bool> _status;
        private readonly bool _styleOnly;
        private readonly ComboBox _model;
        private readonly TextBox _prompt;
        private readonly TextBox _backgroundTolerance, _whiteCutPixels, _backgroundHex;
        private readonly Border _backgroundSwatch;
        private readonly TextBox _alphaThreshold;
        private readonly TextBox _edgeRadius, _edgeExpansion, _underlayHex;
        private readonly Border _underlaySwatch;
        private readonly TextBlock _edgeUnits;
        private readonly Image _sourcePreview, _resultPreview;
        private readonly TextBlock _progress;
        private readonly ProgressBar _progressBar;
        private readonly Button _removeButton, _removeWhiteButton, _backgroundPipetteButton, _upscaleButton,
            _alphaButton, _smoothButton, _stairButton, _pipetteButton,
            _editButton, _cancelButton, _retryButton;
        private readonly List<RadioButton> _styleButtons = new List<RadioButton>();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private CancellationTokenSource _cancellation;
        private bool _busy;
        private string _stage, _lastModel, _lastOutput, _lastSource, _readyOutput;
        private string _importWarning;
        private Dictionary<string, object> _lastInput;
        private StyleChoice _selectedStyle;

        private const string TiledUpscaler = "xinntao/realesrgan:1b976a4d456ed9e4d1a846597b7614e79eadad3032e9124fa63859db0fd59b56";

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
            Func<string, string> import, Action<string, bool> status, bool styleOnly,
            Func<string, string> importDtf = null)
        {
            _capture = capture; _token = token; _import = import;
            _importDtf = importDtf ?? import; _status = status;
            _styleOnly = styleOnly;
            var panel = new StackPanel { Margin = new Thickness(5) };
            Content = panel;

            if (styleOnly) panel.Children.Add(Label("Стилизация", true));
            panel.Children.Add(Note("Выделите объект или группу."));
            _removeButton = Button("Удалить фон · AI", async (_, __) => await Run("background"));
            _upscaleButton = Button("Апскейл ×2", async (_, __) => await Run("upscale"));
            var whiteRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            _removeWhiteButton = Button("Удалить фон", async (_, __) => await RemoveWhiteBackground());
            whiteRow.Children.Add(_removeWhiteButton);
            _backgroundHex = new TextBox { Text = "#FFFFFF", Width = 72, FontSize = 11,
                Margin = new Thickness(5, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center };
            whiteRow.Children.Add(_backgroundHex);
            _backgroundSwatch = new Border { Width = 18, Height = 18, Background = Brushes.White,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(2) };
            whiteRow.Children.Add(_backgroundSwatch);
            _backgroundPipetteButton = Button("Пипетка", (_, __) => PickBackgroundFromCanvas());
            _backgroundPipetteButton.ToolTip = "Выберите цвет на холсте CorelDRAW. Esc — отмена.";
            whiteRow.Children.Add(_backgroundPipetteButton);
            _backgroundHex.TextChanged += (_, __) => UpdateBackgroundSwatch();
            whiteRow.Children.Add(new TextBlock { Text = "Допуск", VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10, Margin = new Thickness(5, 0, 3, 0) });
            _backgroundTolerance = new TextBox { Text = "10", Width = 34, FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center };
            whiteRow.Children.Add(_backgroundTolerance);
            whiteRow.Children.Add(new TextBlock { Text = "Подрезать, px", VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10, Margin = new Thickness(8, 0, 3, 0) });
            _whiteCutPixels = new TextBox { Text = "0", Width = 34, FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center };
            _whiteCutPixels.ToolTip = "Сдвинуть край прозрачности внутрь на указанное число пикселей.";
            whiteRow.Children.Add(_whiteCutPixels);
            if (!styleOnly)
            {
                panel.Children.Add(whiteRow);
                var aiRow = new UniformGrid { Columns = 2 };
                aiRow.Children.Add(_removeButton);
                aiRow.Children.Add(_upscaleButton);
                panel.Children.Add(aiRow);
                panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 2) });
                panel.Children.Add(Label("ДТФ", true));
            }
            var alphaRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            _alphaButton = Button("Убрать полупрозрачность", async (_, __) => await RemoveSemiTransparent());
            alphaRow.Children.Add(_alphaButton);
            alphaRow.Children.Add(new TextBlock { Text = "Порог:", VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10, Margin = new Thickness(5, 0, 3, 0) });
            _alphaThreshold = new TextBox { Text = "130", Width = 40, FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center };
            alphaRow.Children.Add(_alphaThreshold);
            if (!styleOnly) panel.Children.Add(alphaRow);

            _stairButton = Button("Сгладить лесенку", async (_, __) => await SmoothStairs());
            _stairButton.ToolTip = "Уточняет край в более плотной сетке; прозрачность остаётся только 0 или 100%.";
            if (!styleOnly) panel.Children.Add(_stairButton);

            var edgeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 1) };
            _smoothButton = Button("Обводка", async (_, __) => await SmoothEdge());
            edgeRow.Children.Add(_smoothButton);
            edgeRow.Children.Add(new TextBlock { Text = "Радиус px", VerticalAlignment = VerticalAlignment.Center, FontSize = 10 });
            _edgeRadius = new TextBox { Text = "2", Width = 30, FontSize = 11, Margin = new Thickness(3, 0, 6, 0) };
            edgeRow.Children.Add(_edgeRadius);
            edgeRow.Children.Add(new TextBlock { Text = "Обводка px", VerticalAlignment = VerticalAlignment.Center, FontSize = 10 });
            _edgeExpansion = new TextBox { Text = "0", Width = 30, FontSize = 11, Margin = new Thickness(3, 0, 6, 0) };
            edgeRow.Children.Add(_edgeExpansion);
            edgeRow.Children.Add(new TextBlock { Text = "Цвет", VerticalAlignment = VerticalAlignment.Center, FontSize = 10 });
            _underlayHex = new TextBox { Text = "#FFFFFF", Width = 72, FontSize = 11,
                Margin = new Thickness(5, 0, 2, 0) };
            edgeRow.Children.Add(_underlayHex);
            _underlaySwatch = new Border { Width = 18, Height = 18, Background = Brushes.White,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(2) };
            edgeRow.Children.Add(_underlaySwatch);
            _pipetteButton = Button("Пипетка с экрана", async (_, __) => await BeginColorPick());
            edgeRow.Children.Add(_pipetteButton);
            _underlayHex.TextChanged += (_, __) => UpdateUnderlaySwatch();
            if (!styleOnly) panel.Children.Add(edgeRow);
            _edgeUnits = Note("");
            if (!styleOnly) panel.Children.Add(_edgeUnits);
            _edgeRadius.TextChanged += (_, __) => UpdateEdgeUnits();
            _edgeExpansion.TextChanged += (_, __) => UpdateEdgeUnits();
            edgeRow.MouseEnter += (_, __) => UpdateEdgeUnits();
            UpdateEdgeUnits();

            var previews = new UniformGrid { Columns = 2 };
            previews.Children.Add(Preview("Выделение", out _sourcePreview));
            previews.Children.Add(Preview("Результат", out _resultPreview));
            panel.Children.Add(previews);

            if (styleOnly) panel.Children.Add(Label("Пресеты", true));
            var styles = new UniformGrid { Columns = 3 };
            foreach (var style in new[]
            {
                new StyleChoice("Аниме", "anime", "Transform the selected image into polished Japanese anime illustration with clean expressive linework and cel shading."),
                new StyleChoice("Наивная иллюстрация", "naive", "Transform the selected image into a playful handmade naive illustration with simple shapes, uneven ink outlines and flat warm colors."),
                new StyleChoice("Пиксар-персонаж", "animated3d", "Transform the selected subject into a friendly, expressive 3D animated feature-film character with rounded forms and soft studio lighting."),
                new StyleChoice("Акварель", "watercolor", "Transform the selected image into a delicate hand-painted watercolor illustration with soft pigment washes and natural paper texture."),
                new StyleChoice("Комикс", "comic", "Transform the selected image into modern comic-book art with bold ink outlines, halftone shadows and vivid flat color blocks."),
                new StyleChoice("Городская иллюстрация", "street-illustration", "Render the selected subject as a playful hand-painted editorial street illustration: irregular thick black ink outlines, flat warm peach and cream areas, vivid cobalt blue and orange accents, simplified expressive figures and subtle painted-paper texture. Preserve the source subject and composition.")
            }) styles.Children.Add(StyleCard(style));
            if (styleOnly)
            {
                panel.Children.Add(styles);
                panel.Children.Add(Button("Свой промпт без образца стиля", (_, __) => ClearStyle()));
                panel.Children.Add(Label("Модель", false));
            }
            _model = new ComboBox { FontSize = 11, Margin = new Thickness(0, 0, 0, 5) };
            _model.Items.Add(new ModelChoice("black-forest-labs/flux-2-pro", "FLUX.2 Pro", 0.14m));
            _model.Items.Add(new ModelChoice("black-forest-labs/flux-kontext-max", "FLUX.1 Kontext Max", 0.08m));
            _model.Items.Add(new ModelChoice("qwen/qwen-image-edit", "Qwen Image Edit", 0.03m));
            _model.SelectedIndex = 0;
            if (styleOnly) panel.Children.Add(_model);
            if (styleOnly) panel.Children.Add(Label("Промпт", false));
            _prompt = new TextBox { MinHeight = 55, MaxHeight = 78, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, FontSize = 11 };
            if (styleOnly)
            {
                panel.Children.Add(_prompt);
            }
            _editButton = DockerTheme.Primary(Button("Применить стиль / промпт", async (_, __) => await Run("edit")));
            if (styleOnly) panel.Children.Add(_editButton);

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
            content.Children.Add(new Image { Source = Thumbnail(style.Asset), Width = 72, Height = 72,
                Stretch = Stretch.UniformToFill, Margin = new Thickness(1) });
            content.Children.Add(new TextBlock { Text = style.Name, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, FontSize = 10, MaxWidth = 100 });
            var choice = new RadioButton { GroupName = "VanyaSimpleAiStyle", Content = content,
                HorizontalAlignment = HorizontalAlignment.Center };
            _styleButtons.Add(choice);
            var border = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                Margin = new Thickness(1), Padding = new Thickness(2), Child = choice };
            choice.Checked += (_, __) => { _selectedStyle = style;
                _prompt.Text = style.Prompt +
                " Preserve the main subject, pose and composition of the input image. Do not invent text.";
                border.BorderBrush = Brushes.SteelBlue; border.BorderThickness = new Thickness(2); };
            choice.Unchecked += (_, __) => { border.BorderBrush = Brushes.LightGray;
                border.BorderThickness = new Thickness(1); };
            return border;
        }

        private void ClearStyle()
        {
            foreach (var choice in _styleButtons) choice.IsChecked = false;
            _selectedStyle = null;
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
            var selectedModel = _model.SelectedItem as ModelChoice;
            if (selectedModel == null) { Report("Выберите модель.", true); return; }
            string prompt = _prompt.Text.Trim();
            if (action == "edit" && prompt.Length == 0) { Report("Выберите стиль или введите промпт.", true); return; }
            string model = action == "background" ? "bria/remove-background" :
                action == "upscale" ? TiledUpscaler : selectedModel.Id;
            string key = _token();
            if (key.Length < 8) { Report("Укажите ключ Replicate во вкладке «Настройки».", true); return; }
            decimal cost = action == "background" ? 0.018m : action == "upscale" ? 0.006m : selectedModel.EstimatedCost;
            string price = "Платный запрос Replicate · ориентировочно $" +
                cost.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ".";
            if (MessageBox.Show(price + " Продолжить?", "Vanya Tools", MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            SetBusy(true, "Подготавливаю выделение…");
            try
            {
                _importWarning = null;
                // Corel COM capture must run on the UI thread. It duplicates the selection,
                // rasterizes the temporary copy, then restores the original objects.
                Log.Info("Simple AI capture starting: " + action);
                string source = action == "background" ? AiSelectionCapture.Capture() : _capture();
                Log.Info("Simple AI capture finished: " + action);
                _lastSource = action == "background" ? source : null;
                _sourcePreview.Source = PreviewBitmap(source);
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
                    input = new Dictionary<string, object> { ["img"] = image,
                        ["scale"] = 2, ["tile"] = 200, ["version"] = "General - v3", ["face_enhance"] = false };
                else input = PrintRestorationPipeline.ImageInput(model, image, prompt);
                _lastModel = model; _lastInput = input;
                _lastOutput = Path.Combine(Path.GetTempPath(), "Vanya-Simple-AI-" + Guid.NewGuid().ToString("N") + ".png");
                await Execute(key);
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Simple AI operation failed.", ex); Report(ex.Message, true); }
            finally { SetBusy(false, null); }
        }

        private async Task RemoveWhiteBackground()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            int tolerance, cutPixels;
            Color backgroundColor;
            if (!TryHexColor(_backgroundHex.Text, out backgroundColor))
            { Report("Цвет фона: #RRGGBB.", true); return; }
            if (!Int32.TryParse(_backgroundTolerance.Text, out tolerance) || tolerance < 0 || tolerance > 80)
            { Report("Допуск цвета: от 0 до 80.", true); return; }
            if (!Int32.TryParse(_whiteCutPixels.Text, out cutPixels) || cutPixels < 0 || cutPixels > 8)
            { Report("Подрезка края: от 0 до 8 пикселей.", true); return; }
            SetBusy(true, "Удаляю выбранный фон…");
            try
            {
                _importWarning = null;
                Color[] backgroundVariants = BackgroundVariants(backgroundColor);
                string source = AiSelectionCapture.Capture();
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Background-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => BackgroundRemovalProcessor.RemoveColor(
                    source, output, backgroundVariants, tolerance, cutPixels, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                SetStage("Вставляю результат в Corel…");
                InsertReady();
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Local background removal failed.", ex);
                Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private async Task RemoveSemiTransparent()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            int threshold;
            if (!Int32.TryParse(_alphaThreshold.Text, out threshold) || threshold < 0 || threshold > 255)
            { Report("Порог должен быть от 0 до 255.", true); return; }
            SetBusy(true, "Удаляю полупрозрачные пиксели…");
            try
            {
                _importWarning = null;
                // Local processing can retain the full capture resolution; the AI
                // actions above limit pixels to each remote model's practical range.
                string source = AiSelectionCapture.Capture();
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Alpha-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => ThresholdAlpha(source, output, threshold, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                InsertReady();
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Alpha threshold failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static void ThresholdAlpha(string source, string output, int threshold, CancellationToken cancellation)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            var bgra = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int stride = checked(bgra.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
            bgra.CopyPixels(pixels, stride, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if ((i & 0x3fffff) == 0) cancellation.ThrowIfCancellationRequested();
                bool keep = pixels[i + 3] > 0 && pixels[i + 3] >= threshold;
                pixels[i + 3] = keep ? (byte)255 : (byte)0;
                if (!keep) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
            }
            var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight,
                input.DpiX, input.DpiY, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(output)) encoder.Save(stream);
        }

        private async Task SmoothStairs()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            int threshold;
            if (!Int32.TryParse(_alphaThreshold.Text, out threshold) || threshold < 0 || threshold > 255)
            { Report("Порог должен быть от 0 до 255.", true); return; }
            SetBusy(true, "Сглаживаю ступеньки края…");
            try
            {
                _importWarning = null;
                Log.Info("Opaque stair smoothing capture starting.");
                string source = AiSelectionCapture.Capture();
                Log.Info("Opaque stair smoothing capture finished.");
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Stairs-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => SmoothOpaqueStairs(source, output, threshold, cancellation), cancellation);
                Log.Info("Opaque stair smoothing PNG finished.");
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                SetStage("Вставляю результат в Corel…");
                InsertReady();
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Opaque stair smoothing failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static void SmoothOpaqueStairs(string source, string output, int threshold,
            CancellationToken cancellation)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            var bgra = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            int length = checked(width * height), stride = checked(width * 4);
            // Keep the output under about 16 MP when enlarging. Large print
            // rasters are smoothed at their existing resolution to avoid a
            // second full-size RGBA buffer inside the Corel process.
            double scale = length <= 4000000 ? 2.0 : length <= 8000000 ? 1.4 : 1.0;
            if (width > 16000 || height > 16000) scale = 1.0;
            int outWidth = checked((int)Math.Round(width * scale));
            int outHeight = checked((int)Math.Round(height * scale));
            Log.Info("Opaque stair smoothing: " + width + "x" + height +
                " -> " + outWidth + "x" + outHeight + ".");
            byte[] pixels = new byte[checked(stride * height)];
            byte[] mask = new byte[length], work = new byte[length];
            bgra.CopyPixels(pixels, stride, 0);
            for (int p = 0; p < length; p++)
            {
                if ((p & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                mask[p] = pixels[p * 4 + 3] >= threshold && pixels[p * 4 + 3] > 0
                    ? (byte)255 : (byte)0;
            }
            GaussianHorizontal(mask, work, width, height, 1, cancellation);
            GaussianVertical(work, mask, width, height, 1, cancellation);
            // Preserve original alpha separately while changing the blurred mask.
            for (int p = 0; p < length; p++)
            {
                int original = pixels[p * 4 + 3] >= threshold && pixels[p * 4 + 3] > 0 ? 255 : 0;
                work[p] = (byte)original;
                mask[p] = (byte)(scale == 1.0
                    ? (mask[p] * 75 + original * 25 + 50) / 100
                    : (mask[p] * 45 + original * 55 + 50) / 100);
            }
            if (scale == 1.0)
            {
                for (int y = 0; y < height; y++)
                {
                    if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                    for (int x = 0; x < width; x++)
                    {
                        int p = y * width + x, i = p * 4;
                        if (mask[p] < 128)
                        {
                            if (work[p] != 0)
                            {
                                int neighbors = 0;
                                for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = x + dx, ny = y + dy;
                                    if ((dx != 0 || dy != 0) && nx >= 0 && nx < width &&
                                        ny >= 0 && ny < height && work[ny * width + nx] != 0)
                                        neighbors++;
                                }
                                if (neighbors <= 2) { pixels[i + 3] = 255; continue; }
                            }
                            pixels[i + 3] = 0;
                            continue;
                        }
                        if (work[p] != 0) { pixels[i + 3] = 255; continue; }
                        int neighbor = -1;
                        for (int dy = -1; dy <= 1 && neighbor < 0; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                                work[ny * width + nx] != 0)
                            { neighbor = (ny * width + nx) * 4; break; }
                        }
                        if (neighbor < 0) { pixels[i + 3] = 0; continue; }
                        pixels[i] = pixels[neighbor];
                        pixels[i + 1] = pixels[neighbor + 1];
                        pixels[i + 2] = pixels[neighbor + 2];
                        pixels[i + 3] = 255;
                    }
                }
                for (int p = 0; p < length; p++)
                {
                    if ((p & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                    if (pixels[p * 4 + 3] == 0)
                        pixels[p * 4] = pixels[p * 4 + 1] = pixels[p * 4 + 2] = 0;
                }
                Log.Info("Opaque stair smoothing pixels ready; saving PNG.");
                SaveBinaryResult(output, pixels, width, height,
                    input.DpiX, input.DpiY, cancellation);
                return;
            }
            byte[] resultPixels = new byte[checked(outWidth * outHeight * 4)];
            double scaleX = (double)outWidth / width, scaleY = (double)outHeight / height;
            for (int y = 0; y < outHeight; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                double sy = (y + 0.5) / scaleY - 0.5;
                int y0 = Math.Max(0, Math.Min(height - 1, (int)Math.Floor(sy)));
                int y1 = Math.Min(height - 1, y0 + 1);
                double fy = Math.Max(0, Math.Min(1, sy - y0));
                for (int x = 0; x < outWidth; x++)
                {
                    double sx = (x + 0.5) / scaleX - 0.5;
                    int x0 = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(sx)));
                    int x1 = Math.Min(width - 1, x0 + 1);
                    double fx = Math.Max(0, Math.Min(1, sx - x0));
                    int p00 = y0 * width + x0, p10 = y0 * width + x1;
                    int p01 = y1 * width + x0, p11 = y1 * width + x1;
                    double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy);
                    double w01 = (1 - fx) * fy, w11 = fx * fy;
                    double coverage = mask[p00] * w00 + mask[p10] * w10 +
                        mask[p01] * w01 + mask[p11] * w11;
                    int destination = (y * outWidth + x) * 4;
                    if (coverage < 128) continue;
                    // Sample color from the kept artwork, not from translucent
                    // fringe pixels that may contain a matte or white halo.
                    double c00 = work[p00] != 0 ? w00 : 0;
                    double c10 = work[p10] != 0 ? w10 : 0;
                    double c01 = work[p01] != 0 ? w01 : 0;
                    double c11 = work[p11] != 0 ? w11 : 0;
                    double total = c00 + c10 + c01 + c11;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double color = total > 0 ?
                            (pixels[p00 * 4 + channel] * c00 + pixels[p10 * 4 + channel] * c10 +
                             pixels[p01 * 4 + channel] * c01 + pixels[p11 * 4 + channel] * c11) / total :
                            pixels[p00 * 4 + channel];
                        resultPixels[destination + channel] = (byte)Math.Max(0, Math.Min(255, Math.Round(color)));
                    }
                    resultPixels[destination + 3] = 255;
                }
            }
            Log.Info("Opaque stair smoothing pixels ready; saving PNG.");
            SaveBinaryResult(output, resultPixels, outWidth, outHeight,
                input.DpiX * scaleX, input.DpiY * scaleY, cancellation);
        }

        private void UpdateEdgeUnits()
        {
            if (_edgeUnits == null || _busy) return;
            int radius, expansion;
            if (!Int32.TryParse(_edgeRadius.Text, out radius) ||
                !Int32.TryParse(_edgeExpansion.Text, out expansion))
            { _edgeUnits.Text = "Введите радиус и обводку в пикселях."; return; }
            try
            {
                int dpi = AiSelectionCapture.EstimateDefaultCaptureDpi();
                double mmPerPixel = 25.4 / dpi;
                _edgeUnits.Text = String.Format(CultureInfo.CurrentCulture,
                    "Радиус ≈ {0:0.00} мм · обводка ≈ {1:0.00} мм · {2} DPI",
                    radius * mmPerPixel, expansion * mmPerPixel, dpi);
            }
            catch { _edgeUnits.Text = "Выделите объект для расчёта мм."; }
        }

        private async Task SmoothEdge()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            int threshold, radius, expansion;
            if (!Int32.TryParse(_alphaThreshold.Text, out threshold) || threshold < 0 || threshold > 255 ||
                !Int32.TryParse(_edgeRadius.Text, out radius) || radius < 0 || radius > 12 ||
                !Int32.TryParse(_edgeExpansion.Text, out expansion) || expansion < 0 || expansion > 20)
            { Report("Порог: 0–255, радиус: 0–12, расширение: 0–20 пикселей.", true); return; }
            Color fill;
            if (!TryUnderlayColor(out fill)) { Report("Введите цвет подложки в формате #RRGGBB.", true); return; }
            SetBusy(true, "Сглаживаю край…");
            try
            {
                _importWarning = null;
                string source = AiSelectionCapture.Capture();
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Edge-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => SmoothBinaryEdge(source, output, threshold, radius,
                    expansion, fill, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                SetStage("Вставляю результат в Corel…");
                InsertReady();
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Edge smoothing failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static void SmoothBinaryEdge(string source, string output, int threshold,
            int radius, int expansion, Color fill, CancellationToken cancellation)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            var bgra = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            int stride = checked(width * 4), length = checked(width * height);
            byte[] pixels = new byte[checked(stride * height)];
            byte[] mask = new byte[length];
            byte[] work = new byte[length];
            bgra.CopyPixels(pixels, stride, 0);
            for (int p = 0; p < length; p++)
                mask[p] = pixels[p * 4 + 3] > 0 && pixels[p * 4 + 3] >= threshold ? (byte)255 : (byte)0;
            if (expansion > 0)
            {
                DilateHorizontal(mask, work, width, height, expansion, cancellation);
                DilateVertical(work, mask, width, height, expansion, cancellation);
            }
            if (radius > 0)
            {
                GaussianHorizontal(mask, work, width, height, radius, cancellation);
                GaussianVertical(work, mask, width, height, radius, cancellation);
            }
            for (int p = 0; p < length; p++)
            {
                if ((p & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                int i = p * 4;
                // Blur changes only the outline. The exported alpha remains binary.
                if (mask[p] < 128)
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                }
                else if (pixels[i + 3] > 0 && pixels[i + 3] >= threshold)
                    pixels[i + 3] = 255;
                else
                {
                    pixels[i] = fill.B; pixels[i + 1] = fill.G; pixels[i + 2] = fill.R;
                    pixels[i + 3] = 255;
                }
            }
            SaveBinaryResult(output, pixels, width, height, input.DpiX, input.DpiY, cancellation);
        }

        // Crop on the worker thread, before Corel imports the result. The old
        // post-import Corel bitmap scan blocked the docker on large prints.
        private static void SaveBinaryResult(string output, byte[] pixels, int width,
            int height, double dpiX, double dpiY, CancellationToken cancellation)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                if ((y & 127) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    if (pixels[row + x * 4 + 3] == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            var bitmap = BitmapSource.Create(width, height, dpiX, dpiY,
                PixelFormats.Bgra32, null, pixels, checked(width * 4));
            BitmapSource result = bitmap;
            if (maxX >= 0)
            {
                const int padding = 2;
                int left = Math.Max(0, minX - padding), top = Math.Max(0, minY - padding);
                int right = Math.Min(width - 1, maxX + padding);
                int bottom = Math.Min(height - 1, maxY + padding);
                if (left != 0 || top != 0 || right != width - 1 || bottom != height - 1)
                    result = new CroppedBitmap(bitmap,
                        new Int32Rect(left, top, right - left + 1, bottom - top + 1));
            }
            cancellation.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(output)) encoder.Save(stream);
        }

        private static void DilateHorizontal(byte[] source, byte[] target, int width,
            int height, int radius, CancellationToken cancellation)
        {
            for (int y = 0; y < height; y++)
            {
                if ((y & 127) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width, count = 0;
                for (int x = 0; x <= Math.Min(radius, width - 1); x++)
                    if (source[row + x] != 0) count++;
                for (int x = 0; x < width; x++)
                {
                    target[row + x] = count > 0 ? (byte)255 : (byte)0;
                    if (x - radius >= 0 && source[row + x - radius] != 0) count--;
                    if (x + radius + 1 < width && source[row + x + radius + 1] != 0) count++;
                }
            }
        }

        private static void DilateVertical(byte[] source, byte[] target, int width,
            int height, int radius, CancellationToken cancellation)
        {
            for (int x = 0; x < width; x++)
            {
                if ((x & 127) == 0) cancellation.ThrowIfCancellationRequested();
                int count = 0;
                for (int y = 0; y <= Math.Min(radius, height - 1); y++)
                    if (source[y * width + x] != 0) count++;
                for (int y = 0; y < height; y++)
                {
                    target[y * width + x] = count > 0 ? (byte)255 : (byte)0;
                    if (y - radius >= 0 && source[(y - radius) * width + x] != 0) count--;
                    if (y + radius + 1 < height && source[(y + radius + 1) * width + x] != 0) count++;
                }
            }
        }

        private static double[] GaussianWeights(int radius)
        {
            var weights = new double[radius * 2 + 1];
            double sigma = Math.Max(0.7, radius / 1.5), total = 0;
            for (int i = -radius; i <= radius; i++)
            {
                double value = Math.Exp(-(i * i) / (2 * sigma * sigma));
                weights[i + radius] = value; total += value;
            }
            for (int i = 0; i < weights.Length; i++) weights[i] /= total;
            return weights;
        }

        private static void GaussianHorizontal(byte[] source, byte[] target, int width,
            int height, int radius, CancellationToken cancellation)
        {
            double[] weights = GaussianWeights(radius);
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    double value = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sample = x + k;
                        if (sample >= 0 && sample < width) value += source[row + sample] * weights[k + radius];
                    }
                    target[row + x] = (byte)Math.Min(255, Math.Round(value));
                }
            }
        }

        private static void GaussianVertical(byte[] source, byte[] target, int width,
            int height, int radius, CancellationToken cancellation)
        {
            double[] weights = GaussianWeights(radius);
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    double value = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sample = y + k;
                        if (sample >= 0 && sample < height) value += source[sample * width + x] * weights[k + radius];
                    }
                    target[row + x] = (byte)Math.Min(255, Math.Round(value));
                }
            }
        }

        private bool TryUnderlayColor(out Color color)
        {
            return TryHexColor(_underlayHex.Text, out color);
        }

        private static bool TryHexColor(string value, out Color color)
        {
            color = Colors.White;
            string hex = value.Trim().TrimStart('#');
            if (hex.Length != 6) return false;
            int rgbValue;
            if (!Int32.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgbValue)) return false;
            color = Color.FromRgb((byte)(rgbValue >> 16), (byte)(rgbValue >> 8), (byte)rgbValue);
            return true;
        }

        private void UpdateUnderlaySwatch()
        {
            Color color;
            _underlaySwatch.Background = TryUnderlayColor(out color) ?
                new SolidColorBrush(color) : Brushes.Transparent;
        }

        private void UpdateBackgroundSwatch()
        {
            Color color;
            _backgroundSwatch.Background = TryHexColor(_backgroundHex.Text, out color) ?
                new SolidColorBrush(color) : Brushes.Transparent;
        }

        private void PickBackgroundFromCanvas()
        {
            if (_busy) return;
            try
            {
                dynamic app = CorelApp.Get();
                dynamic doc = app.ActiveDocument;
                if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
                Report("Кликните по цвету на холсте CorelDRAW. Esc — отмена.", false);
                double x = 0, y = 0;
                int shift = 0;
                int result = Convert.ToInt32(doc.GetUserClick(ref x, ref y, ref shift,
                    30, false, 326)); // cdrCursorEyeDrop
                if (result != 0) { Report("Выбор цвета отменён.", false); return; }
                dynamic sampled = doc.SampleColorAtPoint(x, y, 99); // cdrColorMixed: retain RGB/CMYK source
                if (sampled == null) throw new InvalidOperationException("Цвет в этой точке не найден.");
                dynamic rgb = app.CreateColor();
                rgb.CopyAssign(sampled);
                rgb.ConvertToRGB(); // Corel applies the document color profile to CMYK colors.
                _backgroundHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                    (int)rgb.RGBRed, (int)rgb.RGBGreen, (int)rgb.RGBBlue);
                Report("Цвет фона: " + _backgroundHex.Text, false);
            }
            catch (Exception ex) { Log.Error("Corel canvas color pick failed.", ex);
                Report("Не удалось взять цвет с холста: " + ex.GetBaseException().Message, true); }
        }

        private static Color[] BackgroundVariants(Color chosen)
        {
            int min = Math.Min(chosen.R, Math.Min(chosen.G, chosen.B));
            int max = Math.Max(chosen.R, Math.Max(chosen.G, chosen.B));
            bool white = min >= 230 && max - min <= 15;
            bool black = max <= 60 && max - min <= 15;
            if (!white && !black) return new[] { chosen };
            try
            {
                dynamic app = CorelApp.Get();
                dynamic cmyk = app.CreateCMYKColor(0, 0, 0, white ? 0 : 100);
                cmyk.ConvertToRGB();
                Color converted = Color.FromRgb((byte)(int)cmyk.RGBRed,
                    (byte)(int)cmyk.RGBGreen, (byte)(int)cmyk.RGBBlue);
                return new[] { chosen, white ? Colors.White : Colors.Black, converted };
            }
            catch (Exception ex)
            {
                Log.Error("CMYK neutral conversion failed; using RGB neutral.", ex);
                return new[] { chosen, white ? Colors.White : Colors.Black };
            }
        }

        private async Task BeginColorPick()
        {
            if (_busy) return;
            try
            {
                Report("Кликните по цвету на экране. Esc — отмена.", false);
                Color? picked = await ScreenColorPicker.PickAsync();
                if (!picked.HasValue) { Report("Выбор цвета отменён.", false); return; }
                Color color = picked.Value;
                _underlayHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                    color.R, color.G, color.B);
                Report("Цвет подложки: " + _underlayHex.Text, false);
            }
            catch (Exception ex) { Report(ex.GetBaseException().Message, true); }
        }

        private async Task Execute(string key)
        {
            CancellationToken cancellation = _cancellation.Token;
            SetStage("Отправляю изображение…");
            await Task.Run(() => ReplicateWorkerClient.Run(_lastModel, key, _lastInput,
                _lastOutput, UpdateProgress, cancellation));
            cancellation.ThrowIfCancellationRequested();
            SetStage("Подготавливаю результат…");
            bool background = _lastModel == "bria/remove-background" &&
                !String.IsNullOrEmpty(_lastSource) && File.Exists(_lastSource);
            string png = Path.Combine(Path.GetTempPath(),
                (background ? "Vanya-Background-Ready-" : "Vanya-Simple-Ready-") +
                Guid.NewGuid().ToString("N") + ".png");
            if (background)
            {
                SetStage("Совмещаю маску с исходником…");
                Log.Info("AI background mask application starting.");
                await Task.Run(() => BackgroundRemovalProcessor.ApplyModelMask(
                    _lastSource, _lastOutput, png, cancellation), cancellation);
                Log.Info("AI background mask application finished.");
            }
            else
                await Task.Run(() => SaveAsPng(_lastOutput, png), cancellation);
            _readyOutput = png;
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
            File.WriteAllText(PendingPath(), png);
            _resultPreview.Source = PreviewBitmap(png);
            cancellation.ThrowIfCancellationRequested();
            SetStage("Вставляю результат в Corel…");
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
                string name = Path.GetFileName(_readyOutput);
                bool alreadyCropped = name.StartsWith("Vanya-Edge-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Vanya-Stairs-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Vanya-Background-Ready-", StringComparison.OrdinalIgnoreCase);
                Log.Info("Simple AI import starting: " + name +
                    (alreadyCropped ? " (cropped PNG)" : ""));
                string warning = (alreadyCropped ? _importDtf : _import)(_readyOutput);
                Log.Info("Simple AI import finished: " + name);
                _importWarning = warning;
                _readyOutput = null; _lastInput = null; _lastSource = null;
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
            catch (Exception ex) { Log.Error("Simple AI retry failed.", ex); Report(ex.Message, true); }
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
            _removeButton.IsEnabled = _removeWhiteButton.IsEnabled = _backgroundPipetteButton.IsEnabled = _upscaleButton.IsEnabled = _alphaButton.IsEnabled =
                _smoothButton.IsEnabled = _stairButton.IsEnabled = _pipetteButton.IsEnabled =
                _editButton.IsEnabled = !busy;
            _model.IsEnabled = _prompt.IsEnabled = _backgroundHex.IsEnabled = _backgroundTolerance.IsEnabled = _whiteCutPixels.IsEnabled = _alphaThreshold.IsEnabled =
                _edgeRadius.IsEnabled = _edgeExpansion.IsEnabled = _underlayHex.IsEnabled = !busy;
            foreach (var choice in _styleButtons) choice.IsEnabled = !busy;
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

        private string PendingPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", _styleOnly ? "pending-simple-style-result.txt" : "pending-simple-ai-result.txt");
        }

        private void RestorePending()
        {
            try
            {
                if (!File.Exists(PendingPath())) return;
                string path = File.ReadAllText(PendingPath()).Trim();
                if (!File.Exists(path)) return;
                _readyOutput = path;
                _resultPreview.Source = PreviewBitmap(path);
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
            FontSize = heading ? 12 : 10, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) }; }
        private static TextBlock Note(string text) { return new TextBlock { Text = text, FontSize = 10,
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 2) }; }
        private static BitmapSource PreviewBitmap(string path)
        {
            int width, height;
            using (var stream = File.OpenRead(path))
            {
                BitmapFrame frame = BitmapFrame.Create(stream,
                    BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
                width = frame.PixelWidth; height = frame.PixelHeight;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (width >= height) image.DecodePixelWidth = 256;
            else image.DecodePixelHeight = 256;
            image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        private static Button Button(string text, RoutedEventHandler handler) { var button = new Button
            { Content = text, FontSize = 10, MinHeight = 24, Margin = new Thickness(1),
              Padding = new Thickness(3, 1, 3, 1) };
            button.Click += handler; return button; }
        private static Border Preview(string title, out Image image)
        {
            image = new Image { Height = 72, Stretch = Stretch.Uniform };
            var stack = new StackPanel(); stack.Children.Add(Label(title, false));
            stack.Children.Add(new Border { Height = 76, BorderThickness = new Thickness(1),
                BorderBrush = Brushes.LightGray, Child = image });
            return new Border { Margin = new Thickness(2), Child = stack };
        }
    }
}
