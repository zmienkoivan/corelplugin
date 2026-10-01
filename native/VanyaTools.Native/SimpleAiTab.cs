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
        private readonly Action<string, bool> _status;
        private readonly ComboBox _model;
        private readonly TextBox _prompt;
        private readonly TextBox _alphaThreshold;
        private readonly TextBox _edgeRadius, _edgeExpansion, _underlayHex;
        private readonly Border _underlaySwatch;
        private readonly TextBlock _edgeUnits;
        private readonly Image _sourcePreview, _resultPreview;
        private readonly TextBlock _progress;
        private readonly ProgressBar _progressBar;
        private readonly Button _removeButton, _upscaleButton, _alphaButton, _smoothButton, _pipetteButton,
            _editButton, _cancelButton, _retryButton;
        private readonly List<RadioButton> _styleButtons = new List<RadioButton>();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private CancellationTokenSource _cancellation;
        private bool _busy;
        private string _stage, _lastModel, _lastOutput, _readyOutput;
        private string _importWarning;
        private Dictionary<string, object> _lastInput;
        private StyleChoice _selectedStyle;
        private bool _samplingColor;

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
            var alphaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            _alphaButton = Button("Убрать полупрозрачные пиксели", async (_, __) => await RemoveSemiTransparent());
            alphaRow.Children.Add(_alphaButton);
            alphaRow.Children.Add(new TextBlock { Text = "Порог:", VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10, Margin = new Thickness(6, 0, 3, 0) });
            _alphaThreshold = new TextBox { Text = "150", Width = 40, FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center };
            alphaRow.Children.Add(_alphaThreshold);
            panel.Children.Add(alphaRow);

            var edgeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
            _smoothButton = Button("Сгладить край", async (_, __) => await SmoothEdge());
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
            _pipetteButton = Button("Пипетка", (_, __) => BeginColorPick());
            edgeRow.Children.Add(_pipetteButton);
            _underlayHex.TextChanged += (_, __) => UpdateUnderlaySwatch();
            panel.Children.Add(edgeRow);
            _edgeUnits = Note("");
            panel.Children.Add(_edgeUnits);
            _edgeRadius.TextChanged += (_, __) => UpdateEdgeUnits();
            _edgeExpansion.TextChanged += (_, __) => UpdateEdgeUnits();
            edgeRow.MouseEnter += (_, __) => UpdateEdgeUnits();
            UpdateEdgeUnits();

            var previews = new UniformGrid { Columns = 2 };
            previews.Children.Add(Preview("Выделение", out _sourcePreview));
            previews.Children.Add(Preview("Результат", out _resultPreview));
            _sourcePreview.MouseLeftButtonDown += SampleUnderlayColor;
            panel.Children.Add(previews);

            panel.Children.Add(Label("Стилизация", true));
            var styles = new UniformGrid { Columns = 2 };
            foreach (var style in new[]
            {
                new StyleChoice("Аниме", "anime", "Transform the selected image into polished Japanese anime illustration with clean expressive linework and cel shading."),
                new StyleChoice("Наивная иллюстрация", "naive", "Transform the selected image into a playful handmade naive illustration with simple shapes, uneven ink outlines and flat warm colors."),
                new StyleChoice("Пиксар-персонаж", "animated3d", "Transform the selected subject into a friendly, expressive 3D animated feature-film character with rounded forms and soft studio lighting."),
                new StyleChoice("Акварель", "watercolor", "Transform the selected image into a delicate hand-painted watercolor illustration with soft pigment washes and natural paper texture."),
                new StyleChoice("Комикс", "comic", "Transform the selected image into modern comic-book art with bold ink outlines, halftone shadows and vivid flat color blocks."),
                new StyleChoice("Городская иллюстрация", "street-illustration", "Render the selected subject as a playful hand-painted editorial street illustration: irregular thick black ink outlines, flat warm peach and cream areas, vivid cobalt blue and orange accents, simplified expressive figures and subtle painted-paper texture. Preserve the source subject and composition.")
            }) styles.Children.Add(StyleCard(style));
            panel.Children.Add(styles);
            panel.Children.Add(Button("Свой промпт без образца стиля", (_, __) => ClearStyle()));
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
            _styleButtons.Add(choice);
            var border = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                Margin = new Thickness(2), Padding = new Thickness(3), Child = choice };
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
            if (key.Length < 8) { Report("Укажите ключ Replicate во вкладке «AI-графика».", true); return; }
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
                _sourcePreview.Source = AiPrintTab.Bitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Alpha-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => ThresholdAlpha(source, output, threshold, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = AiPrintTab.Bitmap(output);
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

        private void UpdateEdgeUnits()
        {
            if (_edgeUnits == null) return;
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
                _sourcePreview.Source = AiPrintTab.Bitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Edge-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => SmoothBinaryEdge(source, output, threshold, radius,
                    expansion, fill, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = AiPrintTab.Bitmap(output);
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
            var result = BitmapSource.Create(width, height, input.DpiX, input.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
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
            color = Colors.White;
            string hex = _underlayHex.Text.Trim().TrimStart('#');
            if (hex.Length != 6) return false;
            int value;
            if (!Int32.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }

        private void UpdateUnderlaySwatch()
        {
            Color color;
            _underlaySwatch.Background = TryUnderlayColor(out color) ?
                new SolidColorBrush(color) : Brushes.Transparent;
        }

        private void BeginColorPick()
        {
            if (_busy) return;
            try
            {
                _sourcePreview.Source = AiPrintTab.Bitmap(AiSelectionCapture.Capture(2048, 4000000));
                _samplingColor = true;
                Report("Нажмите на нужный цвет в превью выделения.", false);
            }
            catch (Exception ex) { Report(ex.GetBaseException().Message, true); }
        }

        private void SampleUnderlayColor(object sender, MouseButtonEventArgs e)
        {
            if (!_samplingColor || _busy) return;
            var image = _sourcePreview.Source as BitmapSource;
            if (image == null) return;
            Point point = e.GetPosition(_sourcePreview);
            double scale = Math.Min(_sourcePreview.ActualWidth / image.PixelWidth,
                _sourcePreview.ActualHeight / image.PixelHeight);
            if (scale <= 0) return;
            double left = (_sourcePreview.ActualWidth - image.PixelWidth * scale) / 2;
            double top = (_sourcePreview.ActualHeight - image.PixelHeight * scale) / 2;
            int x = (int)Math.Floor((point.X - left) / scale);
            int y = (int)Math.Floor((point.Y - top) / scale);
            if (x < 0 || y < 0 || x >= image.PixelWidth || y >= image.PixelHeight) return;
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            byte[] pixel = new byte[4];
            bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            if (pixel[3] == 0) { Report("Выберите непрозрачный пиксель.", true); return; }
            _underlayHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                pixel[2], pixel[1], pixel[0]);
            _samplingColor = false;
            Report("Цвет подложки: " + _underlayHex.Text, false);
            e.Handled = true;
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
            _removeButton.IsEnabled = _upscaleButton.IsEnabled = _alphaButton.IsEnabled =
                _smoothButton.IsEnabled = _pipetteButton.IsEnabled =
                _editButton.IsEnabled = !busy;
            _model.IsEnabled = _prompt.IsEnabled = _alphaThreshold.IsEnabled =
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
