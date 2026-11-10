using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class PatternFillTab : UserControl
    {
        private readonly PatternFillService _service = new PatternFillService();
        private readonly Action<string, bool> _status;
        private readonly Func<string> _token;
        private readonly Button _generateFillers;
        private readonly ComboBox _fillerSet;
        private const string FillerTimingKey = "pattern:fillers:google/nano-banana-2";
        private const string FillerStage = "Готовлю мини-заполнители по стилю набора…";
        private readonly Stopwatch _fillerElapsed = new Stopwatch();
        private readonly DispatcherTimer _fillerClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private CancellationTokenSource _fillerRequest;
        private BitmapSource[] _readyFillers;
        private object _fillerDocument;
        private readonly TextBlock _motifs, _hero, _message;
        private readonly ListBox _motifList;
        private readonly Button _forgetHero, _removeMotifs;
        private PatternEditorSettings _settings = new PatternEditorSettings();
        private PatternEditorSettings _bottleSettings = new PatternEditorSettings
        {
            Layout = new PatternFillOptions { Mode = PatternFillMode.Staggered }
        };

        internal PatternFillTab(Action<string, bool> status, Func<string> token)
        {
            _status = status; _token = token;
            var panel = DockerLayout.ToolGroup("Паттерн по контуру",
                "Добавьте фоновые фигуры из выделения. При необходимости отдельно выберите главный элемент или группу. " +
                "Для паттерна по детали выделите замкнутый контур. Развёртка бутылки строится по размерам. " +
                "«Галька» и «Крокодиловая кожа» распределяют ваши фигуры по ячейкам.");
            panel.Margin = new Thickness(8);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(Button("Добавить выделение в набор", (_, __) => Run(() =>
            {
                int count = _service.AddMotifs();
                RefreshMotifs();
                Report(count == 0 ? "Выделенные фигуры уже в наборе." : "Добавлено фигур · " + count, false);
            })));
            _motifs = Note("");
            panel.Children.Add(_motifs);
            _motifList = new ListBox { SelectionMode = SelectionMode.Extended,
                ItemTemplate = MotifTemplate(), MaxHeight = 160 };
            panel.Children.Add(_motifList);
            _removeMotifs = Button("Удалить выбранные из набора", (_, __) =>
            {
                var ids = _motifList.SelectedItems.Cast<PatternSource>().Select(source => source.Id).ToArray();
                _service.RemoveMotifs(ids);
                RefreshMotifs();
            });
            panel.Children.Add(_removeMotifs);
            _motifList.SelectionChanged += (_, __) => _removeMotifs.IsEnabled = _motifList.SelectedItems.Count > 0;
            _fillerSet = new ComboBox { MinHeight = 27, SelectedIndex = 0, Margin = new Thickness(1, 4, 1, 0),
                ItemsSource = new[] { "Звезда, шарик, квадрат, ромб", "Кольцо, треугольник, шестигранник, капсула", "Искра, капля, полумесяц, точки" },
                ToolTip = "Форма мини-фигур. ИИ берёт из рисунков только палитру, обводку и фактуру." };
            panel.Children.Add(_fillerSet);
            _generateFillers = Button("ИИ · мини-заполнители…", async (_, __) => await GenerateFillers());
            _generateFillers.ToolTip = "До четырёх миниатюр по стилю набора через Nano Banana 2.";
            panel.Children.Add(_generateFillers);
            RefreshMotifs();

            _forgetHero = Button("×", (_, __) =>
            {
                _service.ForgetHero();
                _hero.Text = "По желанию, отдельно от фонового узора";
                _forgetHero.Visibility = Visibility.Collapsed;
            });
            _forgetHero.ToolTip = "Сбросить главный элемент";
            _forgetHero.MinWidth = 30;
            _forgetHero.Visibility = Visibility.Collapsed;
            panel.Children.Add(DockerLayout.Trailing(Button("Запомнить главный элемент", (_, __) => Run(() =>
            {
                _service.RememberHero();
                _settings.UseHero = _bottleSettings.UseHero = true;
                _settings.HeroSize = _bottleSettings.HeroSize = 0;
                _hero.Text = "Главный элемент выбран";
                _forgetHero.Visibility = Visibility.Visible;
                Report("Теперь выделите контур детали.", false);
            })), _forgetHero));
            _hero = Note("По желанию, отдельно от фонового узора");
            panel.Children.Add(_hero);
            panel.Children.Add(DockerTheme.Primary(Button("Настроить паттерн…", (_, __) => Run(() => Edit(false)))));
            panel.Children.Add(Button("Паттерн для бутылки…", (_, __) => Run(() => Edit(true))));
            _message = Note("");
            panel.Children.Add(_message);
            _fillerClock.Tick += (_, __) => _message.Text = OperationTiming.Display(FillerStage, _fillerElapsed.Elapsed, FillerTimingKey);
        }

        private void RefreshMotifs()
        {
            var sources = _service.Motifs.ToArray();
            _motifs.Text = "Фоновые фигуры · " + sources.Length;
            _motifList.ItemsSource = sources;
            _motifList.Visibility = _removeMotifs.Visibility = sources.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _removeMotifs.IsEnabled = false;
        }

        private static DataTemplate MotifTemplate()
        {
            var row = new FrameworkElementFactory(typeof(DockPanel));
            var preview = new FrameworkElementFactory(typeof(Image));
            preview.SetValue(Image.WidthProperty, 44d);
            preview.SetValue(Image.HeightProperty, 36d);
            preview.SetValue(Image.MarginProperty, new Thickness(0, 1, 5, 1));
            preview.SetBinding(Image.SourceProperty, new Binding(nameof(PatternSource.Preview)));
            var imageStyle = new Style(typeof(Image));
            var hidden = new DataTrigger { Binding = new Binding(nameof(PatternSource.Preview)), Value = null };
            hidden.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
            imageStyle.Triggers.Add(hidden);
            preview.SetValue(StyleProperty, imageStyle);
            row.AppendChild(preview);
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(PatternSource.Label)));
            row.AppendChild(label);
            return new DataTemplate { VisualTree = row };
        }

        private async Task GenerateFillers()
        {
            if (_fillerRequest != null) { _fillerRequest.Cancel(); return; }
            try
            {
                if (_readyFillers == null)
                {
                    string token = _token();
                    if (String.IsNullOrEmpty(token) || token.Length < 8)
                        throw new InvalidOperationException("Введите ключ Replicate в настройках.");
                    int set = Math.Max(0, _fillerSet.SelectedIndex);
                    var reference = _service.FillerReference(out _fillerDocument);
                    var parent = Window.GetWindow(this);
                    const string question = "Отправить уменьшенные превью рисунков из набора в Nano Banana 2 через Replicate? Это один платный запрос.";
                    var choice = parent == null
                        ? MessageBox.Show(question, "Мини-заполнители", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                        : MessageBox.Show(parent, question, "Мини-заполнители", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (choice != MessageBoxResult.Yes) return;

                    using (var request = new CancellationTokenSource())
                    {
                        _fillerRequest = request;
                        _generateFillers.Content = "Отменить генерацию";
                        _fillerSet.IsEnabled = false;
                        _fillerElapsed.Restart();
                        _fillerClock.Start();
                        Report(OperationTiming.Display(FillerStage, _fillerElapsed.Elapsed, FillerTimingKey), false);
                        _readyFillers = await Task.Run(() => PatternFillerAi.Generate(reference, token, set, request.Token), request.Token);
                        OperationTiming.Record(FillerTimingKey, _fillerElapsed.Elapsed);
                    }
                }
                int count = _service.AddFillers(_fillerDocument, _readyFillers);
                _readyFillers = null; _fillerDocument = null;
                RefreshMotifs();
                Report("Мини-заполнители добавлены · " + count, false);
            }
            catch (OperationCanceledException) { Report("Генерация мини-заполнителей отменена.", false); }
            catch (Exception error)
            {
                Log.Error("Pattern filler generation or capture failed.", error);
                Report(error.GetBaseException().Message, true);
            }
            finally
            {
                _fillerClock.Stop();
                _fillerElapsed.Stop();
                _fillerSet.IsEnabled = true;
                _fillerRequest = null;
                if (_readyFillers == null) _fillerDocument = null;
                _generateFillers.Content = _readyFillers == null ? "ИИ · мини-заполнители…" : "Добавить готовые мини-фигуры";
            }
        }

        private void Edit(bool bottle)
        {
            PatternEditorSession session;
            using (DockerUiWatchdog.Trace(Dispatcher, "Pattern preview capture"))
                session = bottle ? _service.OpenBottleEditor() : _service.OpenEditor();
            var window = new PatternFillWindow(session, bottle ? _bottleSettings : _settings);
            var owner = PresentationSource.FromVisual(this) as HwndSource;
            if (owner != null) new WindowInteropHelper(window).Owner = owner.Handle;
            if (window.ShowDialog() != true) return;
            using (DockerUiWatchdog.Trace(Dispatcher, "Pattern insertion"))
                Report("Паттерн готов · объектов: " + _service.Apply(session, window.Draft), false);
            if (bottle) _bottleSettings = window.Settings;
            else _settings = window.Settings;
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                Log.Error("Pattern fill failed.", error);
                Report(error.Message, true);
            }
        }

        private void Report(string message, bool error)
        {
            _message.Text = message;
            DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, error ? DockerTheme.ErrorTextBrush : DockerTheme.MutedTextBrush);
            _status(message, error);
        }

        private static TextBlock Note(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
            FontSize = 11, Margin = new Thickness(0, 3, 0, 5) };

        private static Button Button(string title, RoutedEventHandler click)
        {
            var button = new Button { Content = title, MinHeight = 28, Margin = new Thickness(1), FontSize = 11 };
            button.Click += click;
            return button;
        }
    }
}
