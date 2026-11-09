using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed class PatternFillTab : UserControl
    {
        private readonly PatternFillService _service = new PatternFillService();
        private readonly Action<string, bool> _status;
        private readonly TextBlock _motifs, _hero, _message;
        private readonly Button _forgetHero;
        private PatternEditorSettings _settings = new PatternEditorSettings();

        internal PatternFillTab(Action<string, bool> status)
        {
            _status = status;
            var panel = DockerLayout.ToolGroup("Паттерн по контуру",
                "Запомните фоновые объекты. При необходимости отдельно выберите главный элемент или группу. " +
                "Затем выделите замкнутый контур детали и откройте настройки. " +
                "Для готовых фактур достаточно контура. Узор создаётся отдельным PowerClip по кнопке «Применить в Corel».");
            panel.Margin = new Thickness(8);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(Button("Запомнить элементы узора", (_, __) => Run(() =>
            {
                _motifs.Text = "Элементов в наборе: " + _service.Remember();
                Report("Набор сохранён.", false);
            })));
            _motifs = Note("Для рядов и случайной раскладки");
            panel.Children.Add(_motifs);

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
                _settings.UseHero = true; _settings.HeroSize = 0;
                _hero.Text = "Главный элемент выбран";
                _forgetHero.Visibility = Visibility.Visible;
                Report("Теперь выделите контур детали.", false);
            })), _forgetHero));
            _hero = Note("По желанию, отдельно от фонового узора");
            panel.Children.Add(_hero);
            panel.Children.Add(DockerTheme.Primary(Button("Настроить паттерн…", (_, __) => Run(Edit))));
            _message = Note("");
            panel.Children.Add(_message);
        }

        private void Edit()
        {
            PatternEditorSession session;
            using (DockerUiWatchdog.Trace(Dispatcher, "Pattern preview capture"))
                session = _service.OpenEditor();
            var window = new PatternFillWindow(session, _settings);
            var owner = PresentationSource.FromVisual(this) as HwndSource;
            if (owner != null) new WindowInteropHelper(window).Owner = owner.Handle;
            if (window.ShowDialog() != true) return;
            _settings = window.Settings;
            using (DockerUiWatchdog.Trace(Dispatcher, "Pattern insertion"))
                Report("Паттерн готов · объектов: " + _service.Apply(session, window.Draft), false);
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
