using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class PatternFillTab : UserControl
    {
        private readonly PatternFillService _service = new PatternFillService();
        private readonly Action<string, bool> _status;
        private readonly Func<string> _token;
        private readonly Button _generateFillers;
        private readonly ComboBox _fillerSet;
        private PatternFillerPlan _fillerPlan;
        private string _fillerPlanKey;
        private PatternFillerResult _fillerGeneration;
        private string _fillerAnalysisModel = PatternFillerBrief.AnalysisModel;
        private BitmapSource[] _readyFillers;
        private bool _readySmallFillers;
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
                "Добавьте фоновые фигуры из выделения. Для генерации по стилю сначала запомните главный принт или группу. " +
                "Для паттерна по детали выделите замкнутый контур. Развёртка бутылки строится по размерам. " +
                "«Галька» и «Крокодиловая кожа» распределяют ваши фигуры по ячейкам.");
            panel.Margin = new Thickness(8);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _forgetHero = Button("×", (_, __) =>
            {
                _service.ForgetHero();
                _hero.Text = "Главный принт — источник стиля ИИ";
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
                Report("Теперь можно создать набор по стилю принта или выделить контур детали.", false);
            })), _forgetHero));
            _hero = Note("Главный принт — источник стиля ИИ");
            panel.Children.Add(_hero);
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
                ItemsSource = new[] { "По главному принту · анализ ИИ", "Звезда, шарик, квадрат, ромб", "Кольцо, треугольник, шестигранник, капсула", "Искра, капля, полумесяц, точки" },
                ToolTip = "Подбор по теме и стилю главного принта или готовые формы. Комплект можно изменить в окне перед генерацией." };
            panel.Children.Add(_fillerSet);
            _generateFillers = Button("ИИ · набор рисунков…", (_, __) => GenerateFillers());
            _generateFillers.ToolTip = "Создать 4–10 рисунков по главному принту через Nano Banana 2. Каждый запрос требует подтверждения.";
            panel.Children.Add(_generateFillers);
            RefreshMotifs();

            panel.Children.Add(DockerTheme.Primary(Button("Настроить паттерн…", (_, __) => Run(() => Edit(false)))));
            panel.Children.Add(Button("Паттерн для бутылки…", (_, __) => Run(() => Edit(true))));
            _message = Note("");
            panel.Children.Add(_message);
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

        private void GenerateFillers()
        {
            try
            {
                if (_readyFillers == null)
                {
                    string token = _token();
                    if (String.IsNullOrEmpty(token) || token.Length < 8)
                        throw new InvalidOperationException("Введите ключ Replicate в настройках.");
                    var reference = _service.FillerReference(out _fillerDocument);
                    string key = PatternFillerAi.ReferenceKey(reference) + ":" + _fillerSet.SelectedIndex;
                    var initial = _fillerPlan != null && key == _fillerPlanKey
                        ? _fillerPlan.Copy() : PatternFillerBrief.Preset(_fillerSet.SelectedIndex - 1);
                    var window = new PatternFillerWindow(reference, token, initial, _fillerAnalysisModel,
                        key == _fillerPlanKey ? _fillerGeneration : null);
                    var owner = PresentationSource.FromVisual(this) as HwndSource;
                    if (owner != null) new WindowInteropHelper(window).Owner = owner.Handle;
                    bool? accepted;
                    try { accepted = window.ShowDialog(); }
                    finally
                    {
                        _fillerPlan = window.Plan.Copy(); _fillerPlanKey = key;
                        _fillerAnalysisModel = window.AnalysisModel;
                        _fillerGeneration = window.Generation;
                    }
                    if (accepted != true) return;
                    _readyFillers = window.Result;
                    _readySmallFillers = window.Generation.SmallFillers;
                }
                int count = _service.AddFillers(_fillerDocument, _readyFillers, _readySmallFillers);
                _readyFillers = null; _fillerDocument = null;
                RefreshMotifs();
                Report("Рисунки добавлены · " + count, false);
            }
            catch (Exception error)
            {
                Log.Error("Pattern filler generation or capture failed.", error);
                Report(error.GetBaseException().Message, true);
            }
            finally
            {
                if (_readyFillers == null) _fillerDocument = null;
                _generateFillers.Content = _readyFillers == null ? "ИИ · набор рисунков…" : "Добавить готовые рисунки";
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
