using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    internal sealed class AiPrintTab : UserControl
    {
        private readonly Func<string, string> _import;
        private readonly Action<string, bool> _status;
        private readonly Func<string> _captureSelection;
        private readonly ComboBox _operation;
        private readonly ComboBox _model;
        private readonly ComboBox _palette;
        private readonly PasswordBox _token;
        private readonly TextBox _prompt, _left, _top, _right, _bottom;
        private readonly Image _sourcePreview, _resultPreview;
        private readonly TextBlock _cost;
        private readonly StackPanel _activityPanel;
        private readonly TextBlock _activityText;
        private readonly ProgressBar _activityBar;
        private readonly Button _runEditButton, _runVectorButton;
        private readonly Button _cancelButton, _retryButton;
        private CancellationTokenSource _cancellation;
        private readonly DispatcherTimer _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private string _stage;
        private bool _busy;
        private string _lastModel, _lastOutput, _readyOutput;
        private Dictionary<string, object> _lastInput;
        private string _importWarning;
        private string _source, _result, _svg;
        private readonly CheckBox _smartRestore, _upscalePrint;
        private readonly TextBox _printTarget, _analysisText;
        private PrintRestorationPipeline _pipeline;

        private sealed class ModelItem
        {
            public string Id; public string Label; public decimal Cost;
            public ModelItem(string id, string label, decimal cost) { Id = id; Label = label; Cost = cost; }
            public override string ToString() { return Label; }
        }

        public AiPrintTab(Func<string, string> import, Action<string, bool> status, Func<string> captureSelection)
        {
            _import = import; _status = status; _captureSelection = captureSelection;
            var panel = new StackPanel { Margin = new Thickness(7) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
            panel.Children.Add(Label("AI-графика · Replicate", true));
            panel.Children.Add(Note("Операции для печатной графики: восстановление принта, удаление фона, стилизация, свободный промпт."));

            panel.Children.Add(Button("Баланс / Billing ↗", (_, __) => OpenBilling()));
            panel.Children.Add(Note("Выделите графику на холсте и запустите операцию. Результат автоматически появится на холсте; исходные объекты сохраняются."));

            var previews = new UniformGrid { Columns = 2 };
            previews.Children.Add(Preview("Исходник", out _sourcePreview));
            previews.Children.Add(Preview("Результат", out _resultPreview));
            panel.Children.Add(previews);

            panel.Children.Add(Label("Кадрировать область принта, %: L / T / R / B", false));
            var crop = new UniformGrid { Columns = 4 };
            _left = CropField(crop, "0"); _top = CropField(crop, "0");
            _right = CropField(crop, "100"); _bottom = CropField(crop, "100");
            panel.Children.Add(crop);
            panel.Children.Add(Note("Для заранее обрезанного artwork оставьте 0 / 0 / 100 / 100."));

            panel.Children.Add(Label("Заготовка операции", false));
            _operation = new ComboBox { FontSize = 11, Margin = new Thickness(0, 0, 0, 5) };
            foreach (string item in new[] { "Восстановить принт с одежды", "Удалить фон", "Чистая плашечная графика", "Винтажная шелкография", "Линогравюра", "Свой промпт" }) _operation.Items.Add(item);
            _operation.SelectedIndex = 0;
            _operation.SelectionChanged += (_, __) => { SetPrompt(); UpdateCost(); };
            panel.Children.Add(_operation);
            _smartRestore = new CheckBox { Content = "Восстановление в 3 этапа · экспериментальный режим", IsChecked = false, FontSize = 10, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(_smartRestore);
            _smartRestore.Checked += (_, __) => UpdateCost();
            _smartRestore.Unchecked += (_, __) => UpdateCost();
            panel.Children.Add(Note("Применяется только к восстановлению принта. Анализ: Gemini 2.5 Flash; рисунок: выбранная модель; фон: Bria. Анализ и удаление фона оплачиваются отдельно."));
            panel.Children.Add(Label("Какой принт восстановить", false));
            _printTarget = new TextBox { Text = "Основной принт на футболке. Не включать рукав и бирку.", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
            panel.Children.Add(_printTarget);
            _upscalePrint = new CheckBox { Content = "Апскейл 2× перед восстановлением · дополнительная оплата", FontSize = 10, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(_upscalePrint);
            _upscalePrint.Checked += (_, __) => UpdateCost();
            _upscalePrint.Unchecked += (_, __) => UpdateCost();
            panel.Children.Add(Note("Апскейл может изменить мелкие штрихи. По умолчанию отключён. При нескольких футболках укажите, например: принт на груди левой футболки."));
            panel.Children.Add(Label("Цветов для плашечной графики", false));
            _palette = new ComboBox { FontSize = 10, Margin = new Thickness(0, 0, 0, 4) };
            foreach (string item in new[] { "Авто · одноцветный в 1 цвет, иначе до 4", "1 цвет · плоский принт", "2 цвета", "4 цвета" }) _palette.Items.Add(item);
            _palette.SelectedIndex = 1;
            panel.Children.Add(_palette);
            panel.Children.Add(Note("PNG перед импортом готовится в A3 при 300 DPI; прозрачные края после вставки обрезает штатная функция «Обрезать растр». Плашечные цвета упрощаются, чтобы трассировка не строила градиенты."));
            _analysisText = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 10 };
            panel.Children.Add(new Expander { Header = "Что увидела модель анализа", Content = _analysisText });

            panel.Children.Add(Label("Модель и ориентировочная цена", false));
            _model = new ComboBox { FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
            _model.Items.Add(new ModelItem("black-forest-labs/flux-2-pro", "FLUX.2 Pro · около $0.045 за 1MP запуск", 0.045m));
            _model.Items.Add(new ModelItem("black-forest-labs/flux-kontext-max", "FLUX.1 Kontext Max · $0.08 / изображение", 0.08m));
            _model.Items.Add(new ModelItem("qwen/qwen-image-edit", "Qwen Image Edit · $0.03 / изображение", 0.03m));
            _model.SelectedIndex = 0;
            _model.SelectionChanged += (_, __) => UpdateCost();
            panel.Children.Add(_model);
            _cost = Note("");
            panel.Children.Add(_cost);

            panel.Children.Add(Label("Промпт (можно редактировать)", false));
            _prompt = new TextBox { MinHeight = 110, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 11, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(_prompt);

            panel.Children.Add(Label("Ключ Replicate · шифруется средствами Windows", false));
            _token = new PasswordBox { Margin = new Thickness(0, 0, 0, 4) };
            LoadToken();
            panel.Children.Add(_token);
            panel.Children.Add(Button("Сохранить ключ", (_, __) => SaveToken()));
            _runEditButton = Button("Запустить AI-операцию", async (_, __) => await RunEdit());
            panel.Children.Add(_runEditButton);
            _runVectorButton = Button("Векторизовать в SVG · Recraft · $0.01", async (_, __) => await RunVector());
            panel.Children.Add(_runVectorButton);
            _cancelButton = Button("Отменить операцию", (_, __) => CancelOperation());
            _cancelButton.Visibility = Visibility.Collapsed;
            panel.Children.Add(_cancelButton);
            _retryButton = Button("Повторить / продолжить операцию", async (_, __) => await RetryOperation());
            _retryButton.Visibility = Visibility.Collapsed;
            panel.Children.Add(_retryButton);
            _activityText = Note("Подготовка…");
            _activityBar = new ProgressBar { IsIndeterminate = true, Height = 7, Margin = new Thickness(0, 2, 0, 8) };
            _activityPanel = new StackPanel { Visibility = Visibility.Collapsed };
            _activityPanel.Children.Add(_activityText);
            _activityPanel.Children.Add(_activityBar);
            panel.Children.Add(_activityPanel);
            _elapsedTimer.Tick += (_, __) => _activityText.Text = _stage + " · " + (int)_elapsed.Elapsed.TotalSeconds + " с";
            panel.Children.Add(Note("AI-векторизация создаёт редактируемые контуры, но не восстанавливает исходный шрифт. Проверяйте надписи и мелкие детали."));
            SetPrompt(); UpdateCost();
            RestorePendingResult();
        }

        private bool EnsureSource()
        {
            var timer = Stopwatch.StartNew();
            try
            {
                _source = _captureSelection();
                BitmapSource captured = Bitmap(_source);
                _sourcePreview.Source = captured;
                _result = null; _svg = null;
                Log.Info("AI selection prepared in " + timer.ElapsedMilliseconds + " ms; image=" + captured.PixelWidth + "x" + captured.PixelHeight + ", bytes=" + new FileInfo(_source).Length + ".");
                Report("Выделение Corel скопировано, растрировано и подготовлено с прозрачностью.", false);
                return true;
            }
            catch (Exception ex) { Log.Error("AI selection preparation failed after " + timer.ElapsedMilliseconds + " ms.", ex); Report(ex.Message, true); return false; }
        }

        private void SetPrompt()
        {
            if (_prompt == null || _operation == null) return;
            string[] prompts = {
                "Extract the exact existing main print from the reference into a flat print artwork on pure white. " +
                "Copy the visible letter shapes and illustration exactly, preserving object count, relative sizes, spacing, slant and original ink colors. " +
                "Use one solid ink color for a single-color original. Completely remove the garment and its texture. " +
                "No redesign, no invented details, no outlines, no white sticker border, no gradients, no glow, no shadows, no checkerboard. " +
                "Output the print once, uniformly enlarged, with a small plain white margin. Exclude sleeve prints, labels and other shirts. " +
                "The reference image is authoritative; do not replace its lettering with a different font, correct spelling, change line breaks or guess unreadable characters.",
                "Удали фон. Сохрани исходный объект, все надписи, контуры, цвета, пропорции и мелкие детали. Не стилизуй и не перерисовывай объект. Прозрачный PNG.",
                "Сохрани рисунок и надписи. Сделай края чёткими, цвета чистыми плашечными, без градиентов, теней и текстуры. Не меняй текст, композицию и пропорции.",
                "Сделай винтажную шелкографию с ограниченной палитрой цветовых плашек. Сохрани исходные формы, композицию и все надписи посимвольно. Не добавляй элементов.",
                "Стилизуй изображение как линогравюру с чёткими контурами и малыми цветовыми плашками. Сохрани точное написание надписей, их переносы, композицию и пропорции. Не добавляй деталей."
            };
            if (_operation.SelectedIndex < prompts.Length) _prompt.Text = prompts[_operation.SelectedIndex];
            else if (_prompt.Text.Length == 0) _prompt.Text = "Опишите операцию и перечислите элементы, которые необходимо сохранить.";
        }

        private async Task RunEdit()
        {
            if (_busy) return;
            var model = _model.SelectedItem as ModelItem;
            bool removeBackground = _operation.SelectedIndex == 1;
            bool smart = _operation.SelectedIndex == 0 && _smartRestore.IsChecked == true;
            if (removeBackground) model = new ModelItem("bria/remove-background", "Bria Remove Background", 0.018m);
            if (model == null) { Report("Выберите модель.", true); return; }
            string key = CurrentToken();
            if (key.Length < 8) { Report("Введите ключ и сохраните его.", true); return; }
            if (!EnsureSource()) return;
            string costMessage = "Операция выбранной модели: примерно $" + model.Cost.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ".";
            if (smart) costMessage += "\nУдаление фона: примерно $0.018.\nАнализ Gemini — дополнительно, по числу токенов." + (_upscalePrint.IsChecked == true ? "\nАпскейл — дополнительно по тарифу Real-ESRGAN." : "") + "\nЭто несколько платных запросов, итоговая сумма будет выше цены одной генерации.";
            if (MessageBox.Show(costMessage + "\nПродолжить?", "Платный запрос Replicate", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            try
            {
                var operationTimer = Stopwatch.StartNew();
                Log.Info("AI edit started. Model=" + model.Id + ".");
                SetBusy(true, "Подготавливаю изображение…");
                Report("Подготавливаю изображение для Replicate…", false);
                // Read WPF controls and encode the crop on the UI thread.
                var prepareTimer = Stopwatch.StartNew();
                string data = CropData();
                Log.Info("AI input PNG prepared in " + prepareTimer.ElapsedMilliseconds + " ms; encoded chars=" + data.Length + ".");
                _pipeline = null;
                if (smart)
                {
                    _lastInput = null; _readyOutput = null; _analysisText.Text = "Анализ выполняется…";
                    _pipeline = new PrintRestorationPipeline(data, model.Id, _prompt.Text, _printTarget.Text, _upscalePrint.IsChecked == true);
                    await ExecutePipeline(key);
                ReportOutcome("Готово: три этапа завершены, результат вставлен. Проверьте текст и форму по исходнику.");
                    return;
                }
                var input = new Dictionary<string, object> { ["prompt"] = _prompt.Text, ["output_format"] = "png" };
                if (removeBackground) { input["image"] = data; input["preserve_alpha"] = true; input["content_moderation"] = false; }
                else if (model.Id.Contains("kontext")) { input["input_image"] = data; input["aspect_ratio"] = "match_input_image"; input["safety_tolerance"] = 2; }
                else if (model.Id.Contains("flux-2")) { input["input_images"] = new[] { data }; input["aspect_ratio"] = "match_input_image"; input["resolution"] = "1 MP"; }
                else { input["image"] = data; input["go_fast"] = true; input["output_quality"] = 95; }
                _result = Path.Combine(Path.GetTempPath(), "Vanya-AI-" + Guid.NewGuid().ToString("N") + ".png");
                string outputPath = _result;
                await ExecuteJob(model.Id, key, input, outputPath);
                Log.Info("AI edit succeeded in " + operationTimer.ElapsedMilliseconds + " ms; output bytes=" + new FileInfo(_result).Length + ".");
                ReportOutcome("Готово: результат вставлен на холст. Проверьте надписи и геометрию перед печатью.");
            }
            catch (OperationCanceledException) { Report("Операция остановлена. Готовый результат, если он получен, сохранён. Повтор доступен ниже.", false); }
            catch (WebException ex) { Log.Error("AI edit network request failed.", ex); Report("Не удалось связаться с Replicate. Выделение осталось в Corel; проверьте подключение и настройки прокси. Код: " + ex.Status + ". " + ex.Message + (ex.InnerException == null ? "" : " · " + ex.InnerException.Message), true); }
            catch (Exception ex) { Log.Error("AI edit failed.", ex); Report(ex.Message, true); }
            finally { SetBusy(false, null); }
        }

        private async Task RunVector()
        {
            if (_busy) return;
            if (!EnsureSource()) return;
            string file = File.Exists(_result) ? _result : _source;
            if (String.IsNullOrEmpty(file) || !File.Exists(file)) { Report("Не удалось подготовить изображение.", true); return; }
            string key = CurrentToken();
            if (key.Length < 8) { Report("Введите ключ и сохраните его.", true); return; }
            if (MessageBox.Show("Recraft Vectorize стоит около $0.01 за SVG. Продолжить?", "Платный запрос Replicate", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            try
            {
                var operationTimer = Stopwatch.StartNew();
                Log.Info("AI vectorization started. Model=recraft-ai/recraft-vectorize.");
                _pipeline = null;
                SetBusy(true, "Подготавливаю изображение для векторизации…");
                Report("Подготавливаю изображение для векторизации…", false);
                var prepareTimer = Stopwatch.StartNew();
                string data = await Task.Run(() => DataUri(Bitmap(file)));
                Log.Info("AI vector input PNG prepared in " + prepareTimer.ElapsedMilliseconds + " ms; encoded chars=" + data.Length + ".");
                _svg = Path.Combine(Path.GetTempPath(), "Vanya-AI-" + Guid.NewGuid().ToString("N") + ".svg");
                string outputPath = _svg;
                await ExecuteJob("recraft-ai/recraft-vectorize", key,
                    new Dictionary<string, object> { ["image"] = data }, outputPath);
                Log.Info("AI vectorization succeeded in " + operationTimer.ElapsedMilliseconds + " ms; output bytes=" + new FileInfo(_svg).Length + ".");
                ReportOutcome("Готово: векторный результат вставлен на холст.");
            }
            catch (OperationCanceledException) { Report("Операция остановлена. Повтор доступен ниже.", false); }
            catch (WebException ex) { Log.Error("AI vectorization network request failed.", ex); Report("Не удалось связаться с Replicate. Проверьте подключение и настройки прокси. Код: " + ex.Status + ". " + ex.Message + (ex.InnerException == null ? "" : " · " + ex.InnerException.Message), true); }
            catch (Exception ex) { Log.Error("AI vectorization failed.", ex); Report(ex.Message, true); }
            finally { SetBusy(false, null); }
        }

        private void UpdateProgress(string stage)
        {
            string message;
            switch (stage)
            {
                case "sending": message = "Отправляю изображение модели…"; break;
                case "processing": message = "Модель обрабатывает изображение. Это может занять несколько минут…"; break;
                case "downloading": message = "Модель готова. Загружаю результат…"; break;
                case "cancelling": message = "Ожидаю подтверждение отмены от Replicate…"; break;
                default: message = stage; break;
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_busy) return;
                _stage = _cancellation != null && _cancellation.IsCancellationRequested
                    ? "Ожидаю подтверждение отмены…" : message;
                _activityText.Text = _stage;
                Report(_stage, false);
            }));
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            _activityPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _runEditButton.IsEnabled = !busy;
            _runVectorButton.IsEnabled = !busy;
            _cancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _cancelButton.IsEnabled = busy;
            _retryButton.IsEnabled = !busy;
            _retryButton.Visibility = !busy && (_pipeline != null || _lastInput != null || File.Exists(_readyOutput)) ? Visibility.Visible : Visibility.Collapsed;
            _retryButton.Content = File.Exists(_readyOutput) ? "Повторить вставку · без оплаты" : "Повторить / продолжить операцию";
            foreach (Control control in new Control[] { _operation, _model, _prompt, _left, _top, _right, _bottom, _token, _smartRestore, _upscalePrint, _printTarget, _palette }) control.IsEnabled = !busy;
            if (busy)
            {
                _cancellation = new CancellationTokenSource();
                _elapsed.Restart(); _elapsedTimer.Start();
                _stage = message; _activityText.Text = message;
            }
            else
            {
                _elapsedTimer.Stop();
                if (_cancellation != null) { _cancellation.Dispose(); _cancellation = null; }
            }
        }

        private async Task ExecuteJob(string model, string key, Dictionary<string, object> input, string path)
        {
            _lastModel = model; _lastInput = input; _lastOutput = path; _readyOutput = null;
            CancellationToken cancel = _cancellation.Token;
            await Task.Run(() => ReplicateWorkerClient.Run(model, key, input, path, UpdateProgress, cancel));
            await CompleteResult(path, cancel);
        }

        private async Task ExecutePipeline(string key)
        {
            CancellationToken cancel = _cancellation.Token;
            string path = await _pipeline.Run(key, cancel, UpdateProgress, text => _analysisText.Text = text);
            await CompleteResult(path, cancel);
        }

        private async Task CompleteResult(string path, CancellationToken cancel)
        {
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                Report("Подготавливаю A3 · 300 DPI · палитру для печати…", false);
                string printPath = Path.Combine(Path.GetTempPath(), "Vanya-A3-" + Guid.NewGuid().ToString("N") + ".png");
                int palette = _palette.SelectedIndex == 1 ? 1 : _palette.SelectedIndex == 2 ? 2 : _palette.SelectedIndex == 3 ? 4 : 0;
                await Task.Run(() => PrintOutputProcessor.Prepare(path, printPath, palette));
                cancel.ThrowIfCancellationRequested();
                path = printPath;
                Log.Info("Print output prepared for A3 at 300 DPI with " + (_palette.SelectedIndex == 0 ? "automatic" : palette.ToString()) + " spot colors.");
            }
            _readyOutput = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), path);
            }
            catch (Exception ex) { Log.Error("Could not persist pending import.", ex); }
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                _resultPreview.Source = Bitmap(path);
            cancel.ThrowIfCancellationRequested();
            InsertReadyResult();
        }

        private void InsertReadyResult()
        {
            try { _importWarning = _import(_readyOutput); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Файл готов, но вставка не удалась. Нажмите «Повторить вставку» — без новой оплаты. " + ex.GetBaseException().Message, ex);
            }
            _readyOutput = null; _lastInput = null; _pipeline = null;
            try { if (File.Exists(PendingPath())) File.Delete(PendingPath()); } catch { }
        }

        private void CancelOperation()
        {
            if (!_busy || _cancellation == null) return;
            _cancellation.Cancel();
            _cancelButton.IsEnabled = false;
            _stage = "Ожидаю подтверждение отмены от Replicate…";
            Report(_stage, false);
        }

        private async Task RetryOperation()
        {
            if (_busy) return;
            bool local = File.Exists(_readyOutput);
            if (!local && _lastInput == null && _pipeline == null) return;
            string key = CurrentToken();
            if (!local && key.Length < 8) { Report("Введите ключ Replicate.", true); return; }
            if (!local && MessageBox.Show("Продолжить предыдущий запрос? Если он отменён или завершился ошибкой, будет создан новый платный запрос с тем же изображением и настройками.",
                "Повтор операции", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            SetBusy(true, local ? "Вставляю готовый результат…" : "Продолжаю предыдущую операцию…");
            try
            {
                if (local) InsertReadyResult();
                else if (_pipeline != null) await ExecutePipeline(key);
                else await ExecuteJob(_lastModel, key, _lastInput, _lastOutput);
                ReportOutcome("Готово: результат вставлен на холст.");
            }
            catch (OperationCanceledException) { Report("Операция остановлена. Повтор доступен ниже.", false); }
            catch (Exception ex) { Log.Error("AI retry failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static string PendingPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "pending-ai-result.txt"); }

        private void RestorePendingResult()
        {
            try
            {
                if (!File.Exists(PendingPath())) return;
                string path = File.ReadAllText(PendingPath()).Trim();
                if (!File.Exists(path)) return;
                _readyOutput = path;
                if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) _resultPreview.Source = Bitmap(path);
                SetBusy(false, null);
            }
            catch (Exception ex) { Log.Error("Pending AI result restore failed.", ex); }
        }

        private void ReportOutcome(string success)
        {
            Report(String.IsNullOrEmpty(_importWarning) ? success : _importWarning, !String.IsNullOrEmpty(_importWarning));
            _importWarning = null;
        }

        private string CropData()
        {
            BitmapSource image = Bitmap(_source);
            double l = P(_left.Text, 0), t = P(_top.Text, 0), r = P(_right.Text, 100), b = P(_bottom.Text, 100);
            if (l < 0 || t < 0 || r > 100 || b > 100 || r <= l || b <= t) throw new InvalidOperationException("Некорректные границы кадрирования.");
            int x = (int)(image.PixelWidth * l / 100), y = (int)(image.PixelHeight * t / 100);
            int w = Math.Min(image.PixelWidth - x, Math.Max(1, (int)(image.PixelWidth * (r - l) / 100)));
            int h = Math.Min(image.PixelHeight - y, Math.Max(1, (int)(image.PixelHeight * (b - t) / 100)));
            return DataUri(new CroppedBitmap(image, new Int32Rect(x, y, w, h)));
        }

        internal static string DataUri(BitmapSource image)
        {
            BitmapSource current = image;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(current));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    if (ms.Length <= 950000)
                        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }

                if (attempt == 4) break;
                double scale = Math.Min(0.8, 2048.0 / Math.Max(current.PixelWidth, current.PixelHeight));
                if (scale >= 1.0) scale = 0.75;
                current = new TransformedBitmap(current, new ScaleTransform(scale, scale));
            }
            throw new InvalidOperationException("PNG больше лимита передачи даже после уменьшения. Выделите только нужную область принта.");
        }

        internal static BitmapSource Bitmap(string path) { using (var s = File.OpenRead(path)) { var b = BitmapFrame.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); b.Freeze(); return b; } }

        private void OpenBilling()
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://replicate.com/account/billing", UseShellExecute = true });
        }

        private static string TokenPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "replicate-token.bin"); }
        private string CurrentToken()
        {
            if (!String.IsNullOrWhiteSpace(_token.Password)) return _token.Password.Trim();
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenPath()), null, DataProtectionScope.CurrentUser)); } catch { return ""; }
        }
        private void LoadToken()
        {
            try { _token.Password = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenPath()), null, DataProtectionScope.CurrentUser)); } catch { }
        }
        private void SaveToken()
        {
            try
            {
                if (String.IsNullOrWhiteSpace(_token.Password)) { Report("Введите ключ.", true); return; }
                string path = TokenPath(); Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(_token.Password), null, DataProtectionScope.CurrentUser));
                Report("Ключ сохранён в зашифрованном виде для этого пользователя Windows.", false);
            }
            catch (Exception ex) { Report(ex.Message, true); }
        }

        private void UpdateCost()
        {
            if (_model == null || _cost == null) return;
            var m = _model.SelectedItem as ModelItem;
            if (m == null) { _cost.Text = ""; return; }
            if (_operation.SelectedIndex == 1) { _cost.Text = "Удаление фона Bria: ориентировочно $0.018."; return; }
            _cost.Text = "Рисунок: ориентировочно $" + m.Cost.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ".";
            if (_operation.SelectedIndex == 0 && _smartRestore.IsChecked == true)
                _cost.Text += " Фон: +$0.018. Анализ Gemini: дополнительно по токенам." +
                    (_upscalePrint.IsChecked == true ? " Апскейл: дополнительно по тарифу Real-ESRGAN." : "");
        }
        private void Report(string message, bool error) { _status(message, error); }
        private static double P(string s, double fallback) { double v; return Double.TryParse((s ?? "").Replace(",", "."), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback; }
        private static TextBlock Label(string text, bool heading) { return new TextBlock { Text = text, FontSize = heading ? 13 : 11, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) }; }
        private static TextBlock Note(string text) { return new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 5) }; }
        private static Button Button(string text, RoutedEventHandler handler) { var b = new Button { Content = text, FontSize = 11, MinHeight = 24, Margin = new Thickness(0, 0, 0, 4) }; b.Click += handler; return b; }
        private static Border Preview(string title, out Image image)
        {
            image = new Image { Height = 130, Stretch = Stretch.Uniform };
            var stack = new StackPanel(); stack.Children.Add(Label(title, false));
            stack.Children.Add(new Border { Height = 136, BorderThickness = new Thickness(1), BorderBrush = Brushes.LightGray, Child = image });
            return new Border { Margin = new Thickness(2), Child = stack };
        }
        private static TextBox CropField(Panel parent, string value) { var t = new TextBox { Text = value, FontSize = 10, Height = 22, TextAlignment = TextAlignment.Center, Margin = new Thickness(1) }; parent.Children.Add(t); return t; }
    }
}




