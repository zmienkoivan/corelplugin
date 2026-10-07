using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class ColorProofTab : UserControl
    {
        private readonly Action<string, bool> _status;
        private readonly ColorProofService _service = new ColorProofService();
        private readonly TextBlock _referenceLabel;
        private readonly Border _preview;
        private readonly ComboBox _model;
        private readonly ComboBox _horizontal;
        private readonly ComboBox _vertical;
        private readonly ComboBox _cells;
        private readonly TextBox _step;
        private readonly TextBox _square;
        private ColorProofReference _reference;

        public ColorProofTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(8) };
            Content = panel;

            panel.Children.Add(Title("Цветопроба"));
            panel.Children.Add(Note("Эталон: заливка объекта или цвет из палитры CorelDRAW."));

            var sourceButtons = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            sourceButtons.ColumnDefinitions.Add(new ColumnDefinition());
            sourceButtons.ColumnDefinitions.Add(new ColumnDefinition());
            var fromObject = Button("Из выделения", (_, __) => ReadSelection());
            fromObject.Margin = new Thickness(0, 0, 4, 0);
            sourceButtons.Children.Add(fromObject);
            var fromPalette = Button("Выбрать в палитре", (_, __) => PickPalette());
            Grid.SetColumn(fromPalette, 1);
            sourceButtons.Children.Add(fromPalette);
            panel.Children.Add(sourceButtons);

            var source = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            source.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            source.ColumnDefinitions.Add(new ColumnDefinition());
            _preview = new Border { Width = 22, Height = 22, BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1), Background = Brushes.White };
            source.Children.Add(_preview);
            _referenceLabel = new TextBlock { Text = "Цвет не выбран", FontSize = 11,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_referenceLabel, 1);
            source.Children.Add(_referenceLabel);
            panel.Children.Add(source);

            _model = new ComboBox { Height = 27, FontSize = 11 };
            _model.Items.Add("CMYK");
            _model.Items.Add("RGB");
            _model.SelectedIndex = 0;
            panel.Children.Add(Row("Модель вариантов", _model));

            _horizontal = new ComboBox { Height = 27, FontSize = 11 };
            _vertical = new ComboBox { Height = 27, FontSize = 11 };
            panel.Children.Add(Row("По горизонтали ±", _horizontal));
            panel.Children.Add(Row("По вертикали ±", _vertical));
            _model.SelectionChanged += (_, __) => UpdateChannels();
            UpdateChannels();

            _step = Field("5");
            panel.Children.Add(Row("Шаг канала, % / 0–255", _step));
            _cells = new ComboBox { Height = 27, FontSize = 11 };
            _cells.Items.Add("3 × 3");
            _cells.Items.Add("5 × 5");
            _cells.Items.Add("7 × 7");
            _cells.SelectedIndex = 1;
            panel.Children.Add(Row("Сетка", _cells));
            _square = Field("24");
            panel.Children.Add(Row("Плашка, мм", _square));

            var create = DockerTheme.Primary(Button("Создать на странице", (_, __) => Create()));
            create.Margin = new Thickness(0, 7, 0, 0);
            panel.Children.Add(create);
            panel.Children.Add(Note("В центре — значение эталона. Плашечный эталон сохраняется отдельно; варианты — триадные."));
        }

        private void ReadSelection()
        {
            try { SetReference(_service.FromSelection()); }
            catch (Exception ex) { Fail("Color proof source selection failed.", ex); }
        }

        private void PickPalette()
        {
            try
            {
                ColorProofReference selected = _service.FromPalette();
                if (selected != null) SetReference(selected);
            }
            catch (Exception ex) { Fail("Color proof palette selection failed.", ex); }
        }

        private void SetReference(ColorProofReference reference)
        {
            _reference = reference;
            int[] rgb = _service.Components(reference, false);
            _preview.Background = new SolidColorBrush(Color.FromRgb(
                (byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));
            _referenceLabel.Text = (reference.IsSpot ? "Плашечный · " : "") + reference.Name +
                " · " + String.Join(", ", _service.Components(reference, _model.SelectedIndex == 0));
            _status("Эталонный цвет выбран.", false);
        }

        private void UpdateChannels()
        {
            if (_horizontal == null || _vertical == null) return;
            int horizontal = _horizontal.SelectedIndex;
            int vertical = _vertical.SelectedIndex;
            string[] names = _model.SelectedIndex == 0
                ? new[] { "C · голубой", "M · пурпурный", "Y · жёлтый", "K · чёрный" }
                : new[] { "R · красный", "G · зелёный", "B · синий" };
            _horizontal.Items.Clear();
            _vertical.Items.Clear();
            foreach (string name in names) { _horizontal.Items.Add(name); _vertical.Items.Add(name); }
            _horizontal.SelectedIndex = horizontal >= 0 && horizontal < names.Length ? horizontal : 0;
            _vertical.SelectedIndex = vertical >= 0 && vertical < names.Length ? vertical : 1;
            if (_horizontal.SelectedIndex == _vertical.SelectedIndex) _vertical.SelectedIndex = 1;
            if (_reference != null) SetReference(_reference);
        }

        private void Create()
        {
            try
            {
                if (_reference == null) SetReference(_service.FromSelection());
                bool cmyk = _model.SelectedIndex == 0;
                int step;
                if (!Int32.TryParse(_step.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out step))
                    throw new InvalidOperationException("Введите целый шаг изменения цвета.");
                double square;
                if (!Double.TryParse(_square.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out square) &&
                    !Double.TryParse(_square.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out square))
                    throw new InvalidOperationException("Введите размер плашки в мм.");
                int cells = (_cells.SelectedIndex + 1) * 2 + 1;
                _service.Create(_reference, new ColorProofOptions {
                    Cmyk = cmyk, HorizontalChannel = _horizontal.SelectedIndex,
                    VerticalChannel = _vertical.SelectedIndex, Step = step,
                    Cells = cells, SquareMm = square });
                _status("Цветопроба " + cells + " × " + cells + " создана на странице.", false);
            }
            catch (Exception ex) { Fail("Color proof creation failed.", ex); }
        }

        private void Fail(string context, Exception ex)
        {
            Log.Error(context, ex);
            _status(ex.Message, true);
        }

        private static Grid Row(string label, UIElement control)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private static TextBox Field(string value)
        {
            return new TextBox { Text = value, Height = 26, FontSize = 11,
                TextAlignment = TextAlignment.Center };
        }

        private static Button Button(string label, RoutedEventHandler click)
        {
            var button = new Button { Content = label, MinHeight = 29,
                FontSize = 11, Padding = new Thickness(5, 1, 5, 1) };
            button.Click += click;
            return button;
        }

        private static TextBlock Title(string text)
        {
            return new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4) };
        }

        private static TextBlock Note(string text)
        {
            return new TextBlock { Text = text, FontSize = 10,
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8) };
        }
    }
}
