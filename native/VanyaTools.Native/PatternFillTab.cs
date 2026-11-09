using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class PatternFillTab : UserControl
    {
        private readonly PatternFillService _service = new PatternFillService();
        private readonly Action<string, bool> _status;
        private readonly ComboBox _mode;
        private readonly TextBox _size, _gap, _allowance, _minScale, _maxScale, _angle, _variation;
        private readonly TextBlock _remembered, _message;
        private readonly Border _colorSwatch;
        private Color _cellColor = Color.FromRgb(58, 78, 67);
        private int _seed;

        internal PatternFillTab(Action<string, bool> status)
        {
            _status = status;
            var panel = DockerLayout.ToolGroup("Паттерн по контуру",
                "Сначала выделите объекты узора и запомните их. Затем выделите один замкнутый контур детали. " +
                "«Крокодиловая кожа» и «Галька» сами создают векторные ячейки. " +
                "Паттерн создаётся в отдельном PowerClip поверх детали. Припуск расширяет контур наружу. " +
                "Элементы могут пересекать линию шва и обрезаются только по внешней границе. " +
                "«Другой вариант» пересобирает последний паттерн, если он выделен. Для нового припуска выберите исходный контур.");
            panel.Margin = new Thickness(8);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _mode = new ComboBox { ItemsSource = new[] { "Ряды", "Вразбежку", "Случайно", "Крокодиловая кожа", "Галька" },
                SelectedIndex = 0, MinHeight = 27 };
            panel.Children.Add(DockerLayout.FieldRow("Раскладка", _mode, 0));

            var capture = new StackPanel();
            capture.Children.Add(Button("1. Запомнить элементы", (_, __) => Run(() =>
            {
                int count = _service.Remember();
                _remembered.Text = "Элементов в наборе: " + count;
                Report("Выделите контур детали и создайте паттерн.", false);
            })));
            _remembered = Note("Выделите объекты узора на холсте.");
            capture.Children.Add(_remembered);
            panel.Children.Add(capture);
            panel.Children.Add(Note("2. Выделите замкнутый контур детали"));

            _allowance = Field("0");
            _allowance.ToolTip = "Расширяет копию контура наружу в миллиметрах.";
            panel.Children.Add(DockerLayout.FieldRow("Припуск на шов, мм", _allowance, 78));

            _size = Field("30");
            _size.ToolTip = "Длинная сторона элемента с учётом поворота или средний размер ячейки фактуры.";
            _gap = Field("3");
            panel.Children.Add(Pair(DockerLayout.LabeledField("Размер, мм", _size),
                DockerLayout.LabeledField("Зазор, мм", _gap)));

            _angle = Field("0");
            panel.Children.Add(DockerLayout.FieldRow("Поворот, °", _angle, 78));
            var variations = new StackPanel();
            _minScale = Field("100"); _maxScale = Field("100");
            variations.Children.Add(Pair(DockerLayout.LabeledField("Размер от, %", _minScale),
                DockerLayout.LabeledField("Размер до, %", _maxScale)));
            _variation = Field("0");
            _variation.ToolTip = "Случайное отклонение от заданного угла в обе стороны.";
            variations.Children.Add(DockerLayout.FieldRow("Разброс поворота, ±°", _variation, 78));
            panel.Children.Add(variations);

            _colorSwatch = new Border { Width = 24, Height = 17, Background = new SolidColorBrush(_cellColor) };
            var pick = Button("", (_, __) => Run(ChooseColor));
            pick.Content = _colorSwatch;
            pick.ToolTip = "Выбрать цвет ячеек фактуры";
            var color = DockerLayout.FieldRow("Цвет ячеек", pick, 78);
            panel.Children.Add(color);
            void ModeChanged()
            {
                bool texture = _mode.SelectedIndex >= (int)PatternFillMode.Crocodile;
                capture.Visibility = variations.Visibility = texture ? Visibility.Collapsed : Visibility.Visible;
                color.Visibility = texture ? Visibility.Visible : Visibility.Collapsed;
            }
            _mode.SelectionChanged += (_, __) => ModeChanged();
            ModeChanged();

            panel.Children.Add(Pair(DockerTheme.Primary(Button("Создать паттерн", (_, __) => Run(() => Fill(_seed)))),
                Button("Другой вариант", (_, __) => Run(() => Fill(unchecked(_seed + 7919))))));
            _message = Note("");
            panel.Children.Add(_message);
        }

        private void Fill(int seed)
        {
            var options = new PatternFillOptions
            {
                Mode = (PatternFillMode)_mode.SelectedIndex, SizeMm = Number(_size, "размер"),
                GapMm = Number(_gap, "зазор"), Angle = Number(_angle, "поворот"), Seed = seed
            };
            if (!options.Texture)
            {
                options.MinScale = Number(_minScale, "размер от") / 100;
                options.MaxScale = Number(_maxScale, "размер до") / 100;
                options.AngleVariation = Number(_variation, "разброс поворота");
            }
            int count = _service.Fill(options, Number(_allowance, "припуск на шов"), _cellColor);
            _seed = seed;
            Report("Паттерн готов · элементов: " + count, false);
        }

        private void ChooseColor()
        {
            var owner = PresentationSource.FromVisual(this) as HwndSource;
            Color? picked = WindowsColorPicker.Pick(owner == null ? IntPtr.Zero : owner.Handle, _cellColor);
            if (!picked.HasValue) return;
            _cellColor = picked.Value;
            _colorSwatch.Background = new SolidColorBrush(_cellColor);
        }

        private void Run(Action action)
        {
            try
            {
                using (DockerUiWatchdog.Trace(Dispatcher, "PatternFill")) action();
            }
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

        private static double Number(TextBox field, string name)
        {
            if (Double.TryParse(field.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value)) return value;
            throw new ArgumentException("Введите число в поле «" + name + "».");
        }

        private static UniformGrid Pair(UIElement left, UIElement right)
        {
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 5) };
            grid.Children.Add(left); grid.Children.Add(right); return grid;
        }

        private static TextBlock Note(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
            FontSize = 11, Margin = new Thickness(0, 3, 0, 5) };
        private static TextBox Field(string value) => new TextBox { Text = value, MinHeight = 26,
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) };
        private static Button Button(string title, RoutedEventHandler click)
        {
            var button = new Button { Content = title, MinHeight = 28, Margin = new Thickness(1), FontSize = 11 };
            button.Click += click; return button;
        }
    }
}
