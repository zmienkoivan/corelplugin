using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class PatternFillerWindow : Window
    {
        private readonly BitmapSource _reference;
        private readonly string _token;
        private readonly PatternReferenceKind _referenceSource;
        private readonly ComboBox _analysisModel;
        private readonly TextBox _theme, _style;
        private TextBox[] _elements;
        private readonly ComboBox _count;
        private readonly CheckBox _small;
        private readonly StackPanel _elementRows = new StackPanel();
        private readonly StackPanel _fields;
        private readonly Button _analyze, _generate, _cancel, _review, _openSheet;
        private readonly TextBlock _message;
        private readonly Stopwatch _elapsed = new Stopwatch();
        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private CancellationTokenSource _request;
        private bool _closed;
        private string _stage, _timingKey;

        internal BitmapSource[] Result { get; private set; }
        internal PatternFillerResult Generation { get; private set; }
        internal string AnalysisModel => (_analysisModel.SelectedItem as PatternFillerBrief.AnalysisChoice)?.Id ?? PatternFillerBrief.AnalysisModel;
        internal PatternFillerPlan Plan => new PatternFillerPlan
        {
            Theme = _theme.Text.Trim(), Style = _style.Text.Trim(), Source = _referenceSource,
            Elements = Array.ConvertAll(_elements, input => input.Text.Trim()), SmallFillers = _small.IsChecked == true
        };

        internal PatternFillerWindow(BitmapSource reference, string token, PatternFillerPlan initial, string analysisModel = null, PatternFillerResult generation = null)
        {
            _reference = reference; _token = token; Generation = generation;
            initial = initial ?? new PatternFillerPlan();
            _referenceSource = initial.Source;
            Title = "ИИ · рисунки паттерна · Vanya Tools";
            Width = Math.Min(880, SystemParameters.WorkArea.Width - 28);
            Height = Math.Min(740, SystemParameters.WorkArea.Height - 28);
            MinWidth = 570; MinHeight = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);
            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            var footer = new StackPanel();
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            _message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Margin = new Thickness(0, 8, 0, 8) };
            footer.Children.Add(_message);
            var buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _analyze = Button("Подобрать комплект · ИИ", async (_, __) => await Execute(true));
            _analyze.ToolTip = "Отдельный платный анализ превью через выбранную модель в Replicate. Предложит тему, стиль и выбранное количество разных рисунков. Повторный анализ заменит эти поля.";
            _generate = DockerTheme.Primary(Button("Создать рисунки", async (_, __) => await Execute(false)));
            _generate.ToolTip = "Отдельный платный запрос Nano Banana 2. Использует показанное превью и отредактированный комплект.";
            _cancel = Button("Закрыть", (_, __) => CancelOrClose());
            _cancel.MinWidth = 80;
            Grid.SetColumn(_generate, 1); Grid.SetColumn(_cancel, 2);
            buttons.Children.Add(_analyze); buttons.Children.Add(_generate); buttons.Children.Add(_cancel);
            footer.Children.Add(buttons);

            var preview = new StackPanel { Width = 190, Margin = new Thickness(14, 0, 0, 0) };
            DockPanel.SetDock(preview, Dock.Right); root.Children.Add(preview);
            preview.Children.Add(new TextBlock { Text = PatternFillerBrief.ReferenceLabel(_referenceSource), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8) });
            var image = new Image { Source = reference, Stretch = Stretch.Uniform, MaxHeight = 240,
                ToolTip = "Это уменьшенное превью будет отправлено только после подтверждения каждого запроса." };
            var frame = new Border { Child = image, Padding = new Thickness(4), BorderThickness = new Thickness(1) };
            DockerTheme.UseTheme(frame, Border.BorderBrushProperty, DockerTheme.BorderBrush);
            DockerTheme.UseTheme(frame, Border.BackgroundProperty, DockerTheme.SurfaceBackground);
            preview.Children.Add(frame);
            preview.Children.Add(new TextBlock { Text = "Анализатор", Margin = new Thickness(0, 12, 0, 4) });
            _analysisModel = new ComboBox { ItemsSource = PatternFillerBrief.AnalysisModels, MinHeight = 30,
                SelectedIndex = Math.Max(0, Array.FindIndex(PatternFillerBrief.AnalysisModels, item => item.Id == analysisModel)),
                ToolTip = "Модель для следующего анализа. Смена не отправляет превью. Каждый запрос требует подтверждения." };
            preview.Children.Add(_analysisModel);
            preview.Children.Add(new TextBlock { Text = "Рисунков в наборе", Margin = new Thickness(0, 12, 0, 4) });
            _count = new ComboBox { ItemsSource = new[] { 4, 5, 6, 7, 8, 9, 10 }, MinHeight = 30,
                SelectedItem = Math.Max(4, Math.Min(10, initial.Elements.Length)) };
            preview.Children.Add(_count);
            _small = new CheckBox { Content = "Мелкие заполнители", IsChecked = initial.SmallFillers,
                Margin = new Thickness(0, 9, 0, 9),
                ToolTip = "Выключено — самостоятельные рисунки основного паттерна. Включено — небольшие акценты для пустот." };
            preview.Children.Add(_small);
            _review = Button("Готовый результат…", (_, __) => Review());
            _review.IsEnabled = Generation != null;
            _review.ToolTip = "Повторно посмотреть последний готовый лист без генерации и оплаты.";
            preview.Children.Add(_review);
            _openSheet = Button("Открыть готовый лист…", (_, __) => OpenSheet());
            _openSheet.ToolTip = "Разделить сохранённый PNG-лист локально, без отправки в ИИ и оплаты. Сначала укажите число рисунков в листе; у старых листов их четыре.";
            preview.Children.Add(_openSheet);

            _fields = new StackPanel { Margin = new Thickness(0, 0, 1, 0) };
            root.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            _theme = Field("Тема", initial.Theme, 600, false);
            _style = Field("Стиль и палитра", initial.Style, 1800, true);
            _style.Height = 95;
            _fields.Children.Add(new TextBlock { Text = "Комплект · можно изменить", FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 6) });
            _fields.Children.Add(_elementRows);
            BuildElements(initial.Elements.Length, initial.Elements);
            _count.SelectionChanged += (_, __) => BuildElements((int)_count.SelectedItem,
                Array.ConvertAll(_elements, input => input.Text));


            Report(String.IsNullOrWhiteSpace(initial.Elements[0])
                ? "Подберите 4–10 рисунков по показанному источнику или укажите их вручную."
                : "Комплект можно редактировать. Анализ не запускается автоматически.", false);
            _clock.Tick += (_, __) => _message.Text = OperationTiming.Display(_stage, _elapsed.Elapsed, _timingKey);
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CancelOrClose(); } };
            Closing += (_, __) => { _closed = true; _clock.Stop(); _request?.Cancel(); };
        }

        private void BuildElements(int count, string[] values)
        {
            _elementRows.Children.Clear();
            _elements = new TextBox[count];
            for (int i = 0; i < count; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = (i + 1).ToString(), Width = 24,
                    VerticalAlignment = VerticalAlignment.Center });
                var input = new TextBox { Text = i < values.Length ? values[i] : "", MaxLength = 240,
                    MinHeight = 29, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Center };
                row.Children.Add(input); _elementRows.Children.Add(row); _elements[i] = input;
            }
        }

        private TextBox Field(string label, string value, int limit, bool multiline, bool inline = false)
        {
            var title = new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 3) };
            var input = new TextBox { Text = value ?? "", MaxLength = limit, MinHeight = 29,
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, VerticalContentAlignment = VerticalAlignment.Center,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
            if (inline)
            {
                title.Margin = new Thickness(0, 0, 7, 0); title.VerticalAlignment = VerticalAlignment.Center;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(title); row.Children.Add(input); _fields.Children.Add(row);
            }
            else { _fields.Children.Add(title); _fields.Children.Add(input); }
            return input;
        }

        private async Task Execute(bool analysis)
        {
            if (_request != null || _closed) return;
            PatternFillerPlan plan = null;
            try
            {
                plan = Plan;
                if (!analysis) plan.Validate();
                string model = AnalysisModel;
                string question = analysis
                    ? "Отправить показанное превью («" + PatternFillerBrief.ReferenceLabel(_referenceSource) + "») в " + PatternFillerBrief.FindAnalysisModel(model).Name + " через Replicate для подбора " + plan.Elements.Length + " рисунков по теме и стилю? Это отдельный платный запрос."
                    : "Отправить показанное превью («" + PatternFillerBrief.ReferenceLabel(_referenceSource) + "») и комплект из " + plan.Elements.Length + " рисунков в Nano Banana 2 через Replicate? Один платный запрос, лист " + PatternFillerBrief.Resolution(plan.Elements.Length) + ". Для 5–10 рисунков используется 4K: запрос может стоить дороже и выполняться дольше.";
                if (MessageBox.Show(this, question, analysis ? "Анализ источника стиля" : "Генерация рисунков",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

                using (var request = new CancellationTokenSource())
                {
                    _request = request;
                    SetBusy(true, analysis);
                    if (analysis)
                    {
                        var proposed = await Task.Run(() => PatternFillerAi.Analyze(_reference, _token, model, request.Token, plan.Elements.Length, plan.SmallFillers, plan.Source), request.Token);
                        if (_closed) return;
                        _theme.Text = proposed.Theme; _style.Text = proposed.Style;
                        for (int i = 0; i < _elements.Length; i++) _elements[i].Text = proposed.Elements[i];
                    }
                    else
                    {
                        var result = await Task.Run(() => PatternFillerAi.Generate(_reference, _token, plan, request.Token), request.Token);
                        if (_closed) return;
                        Generation = result;
                        _review.IsEnabled = true;
                    }
                    OperationTiming.Record(_timingKey, _elapsed.Elapsed);
                }
                _request = null; _clock.Stop(); _elapsed.Stop();
                if (analysis) Report("Комплект предложен. Проверьте элементы и нажмите «Создать рисунки».", false);
                else
                {
                    Report("Лист готов · выделено " + Generation.Count + " из " + Generation.Cells.Length + ". Проверьте фигуры перед добавлением.", false);
                    Review();
                }
            }
            catch (OperationCanceledException)
            { if (!_closed) Report("Запрос отменён. Поля сохранены.", false); }
            catch (Exception error)
            {
                Log.Error(analysis ? "Pattern set analysis failed." : "Pattern filler generation failed.", error);
                if (!_closed)
                {
                    Report(PatternFillerBrief.FailureMessage(error, analysis), true);
                    _message.ToolTip = error.GetBaseException().Message;
                }
            }
            finally
            {
                _request = null; _clock.Stop(); _elapsed.Stop();
                if (!_closed) SetBusy(false, analysis);
            }
        }

        private void SetBusy(bool busy, bool analysis)
        {
            _fields.IsEnabled = _analysisModel.IsEnabled = _analyze.IsEnabled = _generate.IsEnabled = _openSheet.IsEnabled = !busy;
            _count.IsEnabled = _small.IsEnabled = !busy;
            _review.IsEnabled = !busy && Generation != null;
            _cancel.Content = busy ? "Отменить" : "Закрыть";
            _cancel.IsEnabled = true;
            if (!busy) return;
            _stage = analysis ? "Анализирую источник стиля…" : "Создаю рисунки…";
            _timingKey = analysis ? "pattern:analysis:" + AnalysisModel : "pattern:illustrations:google/nano-banana-2:" + PatternFillerBrief.Resolution(_elements.Length);
            _elapsed.Restart(); _clock.Start();
            Report(OperationTiming.Display(_stage, _elapsed.Elapsed, _timingKey), false);
        }

        private void OpenSheet()
        {
            if (_request != null || _closed) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Готовый лист ИИ|*.png", Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                Generation = PatternFillerExtraction.Extract(AiPrintTab.Bitmap(dialog.FileName), Plan, CancellationToken.None);
                _review.IsEnabled = true;
                Report("Лист открыт локально · выделено " + Generation.Count + " из " + Generation.Cells.Length + ".", false);
                Review();
            }
            catch (Exception error) { Report(error.GetBaseException().Message, true); }
        }

        private void Review()
        {
            if (_request != null || Generation == null || _closed) return;
            var review = new PatternFillerReviewWindow(_reference, Generation) { Owner = this };
            if (review.ShowDialog() == true) { Result = review.Selected; DialogResult = true; }
        }

        private void CancelOrClose()
        {
            if (_request == null) { Close(); return; }
            _request.Cancel(); _cancel.IsEnabled = false;
        }

        private void Report(string text, bool error)
        {
            _message.Text = text;
            _message.ToolTip = null;
            DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, error ? DockerTheme.ErrorTextBrush : DockerTheme.MutedTextBrush);
        }

        private static Button Button(string label, RoutedEventHandler click)
        {
            var button = new Button { Content = label, MinHeight = 34, Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
            button.Click += click; return button;
        }
    }
}
