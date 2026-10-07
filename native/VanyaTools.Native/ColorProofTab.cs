using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private readonly ComboBox _layout;
        private readonly ComboBox _horizontal;
        private readonly ComboBox _vertical;
        private readonly Grid _horizontalRow;
        private readonly Grid _verticalRow;
        private readonly Grid _stepRow;
        private readonly Grid _cellsRow;
        private readonly StackPanel _combinationPanel;
        private readonly Grid[] _rangeRows = new Grid[4];
        private readonly TextBlock[] _rangeLabels = new TextBlock[4];
        private readonly TextBox[] _ranges = new TextBox[4];
        private readonly TextBlock _sampleCount;
        private readonly ComboBox _cells;
        private readonly TextBox _step;
        private readonly TextBox _square;
        private readonly TextBlock _layoutNote;
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

            _layout = new ComboBox { Height = 27, FontSize = 11 };
            _layout.Items.Add("Все комбинации");
            _layout.Items.Add("Все каналы");
            _layout.Items.Add("Два канала");
            _layout.SelectedIndex = 0;
            panel.Children.Add(Row("Режим", _layout));

            _horizontal = new ComboBox { Height = 27, FontSize = 11 };
            _vertical = new ComboBox { Height = 27, FontSize = 11 };
            _horizontalRow = Row("По горизонтали ±", _horizontal);
            _verticalRow = Row("По вертикали ±", _vertical);
            panel.Children.Add(_horizontalRow);
            panel.Children.Add(_verticalRow);
            UpdateChannels();

            _combinationPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            for (int i = 0; i < _ranges.Length; i++)
            {
                _ranges[i] = Field("");
                _rangeRows[i] = RangeRow(i);
                _combinationPanel.Children.Add(_rangeRows[i]);
                _ranges[i].TextChanged += (_, __) => UpdateSampleCount();
            }
            var presets = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            presets.ColumnDefinitions.Add(new ColumnDefinition());
            presets.ColumnDefinitions.Add(new ColumnDefinition());
            presets.ColumnDefinitions.Add(new ColumnDefinition());
            var blue = Button("Синий CMYK", (_, __) => BluePreset(true));
            blue.Margin = new Thickness(0, 0, 3, 0);
            presets.Children.Add(blue);
            var blueRgb = Button("Синий RGB", (_, __) => BluePreset(false));
            blueRgb.Margin = new Thickness(0, 0, 3, 0);
            Grid.SetColumn(blueRgb, 1);
            presets.Children.Add(blueRgb);
            var near = Button("Вокруг эталона", (_, __) => AroundReference());
            Grid.SetColumn(near, 2);
            presets.Children.Add(near);
            _combinationPanel.Children.Add(presets);
            _sampleCount = Note("");
            _combinationPanel.Children.Add(_sampleCount);
            panel.Children.Add(_combinationPanel);

            _step = Field("5");
            _stepRow = Row("Шаг канала, % / 0–255", _step);
            panel.Children.Add(_stepRow);
            _cells = new ComboBox { Height = 27, FontSize = 11 };
            _cells.SelectedIndex = 1;
            _cellsRow = Row("Сетка", _cells);
            panel.Children.Add(_cellsRow);
            _square = Field("24");
            panel.Children.Add(Row("Плашка, мм", _square));

            _model.SelectionChanged += (_, __) => { UpdateChannels(); ApplyDefaultRanges(); };
            _layout.SelectionChanged += (_, __) => UpdateProofLayout();
            ApplyDefaultRanges();
            UpdateProofLayout();

            var create = DockerTheme.Primary(Button("Создать на странице", (_, __) => Create()));
            create.Margin = new Thickness(0, 7, 0, 0);
            panel.Children.Add(create);
            _layoutNote = Note("");
            panel.Children.Add(_layoutNote);
            UpdateProofLayout();
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

        private void UpdateProofLayout()
        {
            if (_horizontalRow == null || _verticalRow == null || _cells == null) return;
            bool combinations = _layout.SelectedIndex == 0;
            bool all = _layout.SelectedIndex == 1;
            _horizontalRow.Visibility = combinations || all ? Visibility.Collapsed : Visibility.Visible;
            _verticalRow.Visibility = combinations || all ? Visibility.Collapsed : Visibility.Visible;
            _combinationPanel.Visibility = combinations ? Visibility.Visible : Visibility.Collapsed;
            _stepRow.Visibility = combinations ? Visibility.Collapsed : Visibility.Visible;
            _cellsRow.Visibility = combinations ? Visibility.Collapsed : Visibility.Visible;
            int selected = _cells.SelectedIndex;
            _cells.Items.Clear();
            foreach (int count in new[] { 3, 5, 7 })
                _cells.Items.Add(all ? count + " вариантов" : count + " × " + count);
            _cells.SelectedIndex = selected >= 0 && selected < 3 ? selected : 1;
            if (_layoutNote != null)
                _layoutNote.Text = combinations
                    ? "Все указанные сочетания. Для каждого Y/K или B создаётся отдельная страница нужного размера."
                    : all
                    ? "Отдельная строка для каждого канала. Средняя плашка — эталон; остальные каналы не меняются."
                    : "Сетка отклонений по двум каналам. Средняя плашка — эталон.";
        }

        private Grid RangeRow(int channel)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            _rangeLabels[channel] = new TextBlock { FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(_rangeLabels[channel]);
            Grid.SetColumn(_ranges[channel], 1);
            row.Children.Add(_ranges[channel]);
            return row;
        }

        private void ApplyDefaultRanges()
        {
            bool cmyk = _model.SelectedIndex == 0;
            string[] names = cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
            string[] defaults = cmyk
                ? new[] { "70,80,90,100", "40,50,60,70,80,90,100", "0,5,10", "0,5" }
                : new[] { "0,20,40", "0,30,60,90", "220,240,255" };
            for (int i = 0; i < _ranges.Length; i++)
            {
                _rangeRows[i].Visibility = i < names.Length ? Visibility.Visible : Visibility.Collapsed;
                _rangeLabels[i].Text = i < names.Length ? names[i] + ", значения" : "";
                _ranges[i].Text = i < defaults.Length ? defaults[i] : "";
            }
            UpdateSampleCount();
        }

        private void BluePreset(bool cmyk)
        {
            try
            {
                _model.SelectedIndex = cmyk ? 0 : 1;
                SetReference(cmyk ? _service.BlueStartingColor() : _service.BlueRgbStartingColor());
                string[] blue = cmyk
                    ? new[] { "70,80,90,100", "40,50,60,70,80,90,100", "0,5,10", "0,5" }
                    : new[] { "0,20,40,60", "0,30,60,90,120", "220,240,255" };
                for (int i = 0; i < blue.Length; i++) _ranges[i].Text = blue[i];
                _status(cmyk ? "Синий CMYK: 168 плашек на 6 страницах."
                    : "Синий RGB: 60 плашек на 3 страницах.", false);
            }
            catch (Exception ex) { Fail("Blue color proof preset failed.", ex); }
        }

        private void AroundReference()
        {
            try
            {
                if (_reference == null) SetReference(_service.FromSelection());
                bool cmyk = _model.SelectedIndex == 0;
                int[] values = _service.Components(_reference, cmyk);
                int limit = cmyk ? 100 : 255;
                int step = cmyk ? 5 : 15;
                for (int channel = 0; channel < values.Length; channel++)
                {
                    int radius = channel < 2 ? 2 : 1;
                    _ranges[channel].Text = String.Join(",", Enumerable.Range(-radius, radius * 2 + 1)
                        .Select(offset => Math.Max(0, Math.Min(limit, values[channel] + offset * step)))
                        .Distinct());
                }
                UpdateSampleCount();
            }
            catch (Exception ex) { Fail("Reference color proof preset failed.", ex); }
        }

        private void UpdateSampleCount()
        {
            if (_sampleCount == null) return;
            try
            {
                int[][] levels = ReadLevels();
                int planes = levels.Skip(2).Aggregate(1, (count, channel) => count * channel.Length);
                int swatches = planes * levels[0].Length * levels[1].Length;
                _sampleCount.Text = swatches + " плашек · " + planes + " стр. Печатайте с одним профилем.";
            }
            catch { _sampleCount.Text = "Укажите значения через запятую, например 70,80,90,100."; }
        }

        private int[][] ReadLevels()
        {
            int channels = _model.SelectedIndex == 0 ? 4 : 3;
            int max = channels == 4 ? 100 : 255;
            var result = new int[channels][];
            for (int i = 0; i < channels; i++)
            {
                string[] parts = _ranges[i].Text.Split(new[] { ',', ';', ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Length > 9)
                    throw new InvalidOperationException("Укажите от 1 до 9 значений для каждого канала.");
                var values = new List<int>();
                foreach (string part in parts)
                {
                    int value;
                    if (!Int32.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
                        value < 0 || value > max)
                        throw new InvalidOperationException("Значения каналов должны быть от 0 до " + max + ".");
                    if (!values.Contains(value)) values.Add(value);
                }
                result[i] = values.ToArray();
            }
            return result;
        }

        private void Create()
        {
            try
            {
                if (_reference == null) SetReference(_service.FromSelection());
                bool cmyk = _model.SelectedIndex == 0;
                double square;
                if (!Double.TryParse(_square.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out square) &&
                    !Double.TryParse(_square.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out square))
                    throw new InvalidOperationException("Введите размер плашки в мм.");
                if (_layout.SelectedIndex == 0)
                {
                    int pages = _service.CreateCombinations(_reference, cmyk, ReadLevels(), square);
                    _status("Цветопроба комбинаций создана: " + pages + " страниц.", false);
                    return;
                }
                int step;
                if (!Int32.TryParse(_step.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out step))
                    throw new InvalidOperationException("Введите целый шаг изменения цвета.");
                int cells = (_cells.SelectedIndex + 1) * 2 + 1;
                bool allChannels = _layout.SelectedIndex == 1;
                _service.Create(_reference, new ColorProofOptions {
                    Cmyk = cmyk, AllChannels = allChannels,
                    HorizontalChannel = _horizontal.SelectedIndex,
                    VerticalChannel = _vertical.SelectedIndex, Step = step,
                    Cells = cells, SquareMm = square });
                _status(allChannels
                    ? "Создано " + (cmyk ? 4 : 3) + " таблицы по " + cells + " плашек."
                    : "Цветопроба " + cells + " × " + cells + " создана на странице.", false);
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
