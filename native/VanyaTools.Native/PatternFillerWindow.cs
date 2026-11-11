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
        private readonly ComboBox _analysisModel;
        private readonly TextBox _theme, _style;
        private readonly TextBox[] _elements = new TextBox[4];
        private readonly StackPanel _fields;
        private readonly Button _analyze, _generate, _cancel;
        private readonly TextBlock _message;
        private readonly Stopwatch _elapsed = new Stopwatch();
        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private CancellationTokenSource _request;
        private bool _closed;
        private string _stage, _timingKey;

        internal BitmapSource[] Result { get; private set; }
        internal string AnalysisModel => (_analysisModel.SelectedItem as PatternFillerBrief.AnalysisChoice)?.Id ?? PatternFillerBrief.AnalysisModel;
        internal PatternFillerPlan Plan => new PatternFillerPlan
        {
            Theme = _theme.Text.Trim(), Style = _style.Text.Trim(),
            Elements = Array.ConvertAll(_elements, input => input.Text.Trim())
        };

        internal PatternFillerWindow(BitmapSource reference, string token, PatternFillerPlan initial, string analysisModel = null)
        {
            _reference = reference; _token = token;
            initial = initial ?? new PatternFillerPlan();
            Title = "ИИ-заполнители · Vanya Tools";
            Width = Math.Min(760, SystemParameters.WorkArea.Width - 28);
            Height = Math.Min(620, SystemParameters.WorkArea.Height - 28);
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
            _analyze.ToolTip = "Отдельный платный анализ превью через выбранную модель в Replicate. Предложит тему, стиль и четыре разных элемента. Повторный анализ заменит эти поля.";
            _generate = DockerTheme.Primary(Button("Создать мини-фигуры", async (_, __) => await Execute(false)));
            _generate.ToolTip = "Отдельный платный запрос Nano Banana 2. Использует показанное превью и отредактированный комплект.";
            _cancel = Button("Закрыть", (_, __) => CancelOrClose());
            _cancel.MinWidth = 80;
            Grid.SetColumn(_generate, 1); Grid.SetColumn(_cancel, 2);
            buttons.Children.Add(_analyze); buttons.Children.Add(_generate); buttons.Children.Add(_cancel);
            footer.Children.Add(buttons);

            var preview = new StackPanel { Width = 190, Margin = new Thickness(14, 0, 0, 0) };
            DockPanel.SetDock(preview, Dock.Right); root.Children.Add(preview);
            preview.Children.Add(new TextBlock { Text = "Набор для анализа", FontWeight = FontWeights.SemiBold,
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

            _fields = new StackPanel { Margin = new Thickness(0, 0, 1, 0) };
            root.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            _theme = Field("Тема", initial.Theme, 600, false);
            _style = Field("Стиль и палитра", initial.Style, 1800, true);
            _style.Height = 95;
            _fields.Children.Add(new TextBlock { Text = "Комплект · можно изменить", FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 6) });
            for (int i = 0; i < _elements.Length; i++)
                _elements[i] = Field((i + 1).ToString(), initial.Elements[i], 240, false, true);

            Report(String.IsNullOrWhiteSpace(initial.Elements[0])
                ? "Подберите комплект по набору или укажите четыре элемента вручную."
                : "Комплект можно редактировать. Анализ не запускается автоматически.", false);
            _clock.Tick += (_, __) => _message.Text = OperationTiming.Display(_stage, _elapsed.Elapsed, _timingKey);
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CancelOrClose(); } };
            Closing += (_, __) => { _closed = true; _clock.Stop(); _request?.Cancel(); };
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
                if (!analysis) { plan = Plan; plan.Validate(); }
                string model = AnalysisModel;
                string question = analysis
                    ? "Отправить показанное превью набора в " + PatternFillerBrief.FindAnalysisModel(model).Name + " через Replicate для подбора темы, стиля и комплекта? Это отдельный платный запрос."
                    : "Отправить показанное превью и выбранный комплект в Nano Banana 2 через Replicate? Это отдельный платный запрос генерации.";
                if (MessageBox.Show(this, question, analysis ? "Анализ набора" : "Генерация мини-фигур",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

                using (var request = new CancellationTokenSource())
                {
                    _request = request;
                    SetBusy(true, analysis);
                    if (analysis)
                    {
                        var proposed = await Task.Run(() => PatternFillerAi.Analyze(_reference, _token, model, request.Token), request.Token);
                        if (_closed) return;
                        _theme.Text = proposed.Theme; _style.Text = proposed.Style;
                        for (int i = 0; i < 4; i++) _elements[i].Text = proposed.Elements[i];
                    }
                    else
                    {
                        var result = await Task.Run(() => PatternFillerAi.Generate(_reference, _token, plan, request.Token), request.Token);
                        if (_closed) return;
                        Result = result;
                    }
                    OperationTiming.Record(_timingKey, _elapsed.Elapsed);
                }
                _request = null; _clock.Stop(); _elapsed.Stop();
                if (analysis) Report("Комплект предложен. Проверьте элементы и нажмите «Создать мини-фигуры».", false);
                else DialogResult = true;
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
            _fields.IsEnabled = _analysisModel.IsEnabled = _analyze.IsEnabled = _generate.IsEnabled = !busy;
            _cancel.Content = busy ? "Отменить" : "Закрыть";
            _cancel.IsEnabled = true;
            if (!busy) return;
            _stage = analysis ? "Анализирую набор…" : "Создаю мини-фигуры…";
            _timingKey = analysis ? "pattern:analysis:" + AnalysisModel : "pattern:fillers:google/nano-banana-2";
            _elapsed.Restart(); _clock.Start();
            Report(OperationTiming.Display(_stage, _elapsed.Elapsed, _timingKey), false);
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
