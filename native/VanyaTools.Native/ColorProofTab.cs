using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class ColorProofTab : UserControl
    {
        private readonly Action<string, bool> _status;
        private readonly ColorProofService _service = new ColorProofService();
        private readonly TextBlock _referenceLabel, _sampleCount, _layoutNote, _artworkLabel;
        private readonly Border _preview;
        private readonly ComboBox _model, _mode, _layout, _horizontal, _vertical, _sampleShape;
        private readonly Grid _layoutRow, _horizontalRow, _verticalRow, _stepRow;
        private readonly StackPanel _artworkPanel;
        private readonly StackPanel _combinationPanel, _visualPanel;
        private readonly Grid[] _rangeRows = new Grid[4];
        private readonly TextBlock[] _rangeLabels = new TextBlock[4];
        private readonly TextBox[] _ranges = new TextBox[4];
        private readonly TextBox _step, _hueStep, _lightStep, _saturationStep, _sheetWidth;
        private ColorProofReference _reference;
        private ColorProofArtwork _artwork;
        private static readonly string WidthPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "color-proof-width.txt");

        public ColorProofTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(8) };
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(Title("Цветопроба относительно эталона"));
            panel.Children.Add(Note("Выберите заливку объекта или цвет из палитры. Варианты всегда рассчитываются от этого цвета."));
            var sources = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            sources.ColumnDefinitions.Add(new ColumnDefinition()); sources.ColumnDefinitions.Add(new ColumnDefinition());
            var fromObject = Button("Из выделения", (_, __) => ReadSelection()); fromObject.Margin = new Thickness(0, 0, 4, 0);
            var fromPalette = Button("Выбрать в палитре", (_, __) => PickPalette()); Grid.SetColumn(fromPalette, 1);
            sources.Children.Add(fromObject); sources.Children.Add(fromPalette); panel.Children.Add(sources);
            var source = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            source.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); source.ColumnDefinitions.Add(new ColumnDefinition());
            _preview = new Border { Width = 22, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White };
            _referenceLabel = Note("Цвет не выбран"); Grid.SetColumn(_referenceLabel, 1);
            source.Children.Add(_preview); source.Children.Add(_referenceLabel); panel.Children.Add(source);

            _sampleShape = Combo("Квадраты", "Покрасить логотип");
            panel.Children.Add(Row("Образец", _sampleShape));
            _artworkPanel = new StackPanel();
            _artworkPanel.Children.Add(Button("Взять логотип из выделения", (_, __) => CaptureArtwork()));
            _artworkLabel = Note("Логотип не выбран."); _artworkPanel.Children.Add(_artworkLabel);
            _artworkPanel.Children.Add(Note("Выделите одноцветный векторный логотип, текст или группу. " +
                "Каждая копия сохраняет размер 1:1; окрашиваются все её заливки и обводки. " +
                "Параметры цвета печатаются под копией."));
            panel.Children.Add(_artworkPanel);
            _sheetWidth = Field("570");
            try { if (File.Exists(WidthPath)) _sheetWidth.Text = File.ReadAllText(WidthPath).Trim(); }
            catch (Exception ex) { Log.Error("Could not read color proof sheet width.", ex); }
            panel.Children.Add(Row("Ширина листа, мм", _sheetWidth));
            panel.Children.Add(Note("Квадраты: 21 ячейка на всю ширину между полями по 8 мм; эталон в 11-й. " +
                "Логотип сохраняет размер 1:1: в ряду помещается максимально возможное нечётное число копий, " +
                "по центру — эталон. Остальные варианты продолжаются ниже."));

            _mode = Combo("Наглядный", "Каналы CMYK/RGB");
            panel.Children.Add(Row("Поиск", _mode));
            _model = Combo("CMYK", "RGB"); panel.Children.Add(Row("Цветовая модель", _model));
            _visualPanel = new StackPanel();
            _lightStep = Field("1"); _hueStep = Field("1"); _saturationStep = Field("1");
            _visualPanel.Children.Add(Row("Шаг светлоты, %", _lightStep));
            _visualPanel.Children.Add(Row("Шаг оттенка, °", _hueStep));
            _visualPanel.Children.Add(Row("Шаг насыщенности, %", _saturationStep));
            _visualPanel.Children.Add(Note("Три ряда: темнее/светлее, сдвиг оттенка, менее/более насыщенный цвет. " +
                "В каждом ряду меняется одно свойство эталона. Поиск идёт в HSL на основе RGB; " +
                "для CMYK результат преобразует Corel. Можно задать дробный шаг от 0,1 — например 0,5. " +
                "Для точной работы с красками выберите режим каналов."));
            panel.Children.Add(_visualPanel);

            _layout = Combo("Все сочетания отклонений", "По одному каналу", "Сетка по двум каналам");
            _layout.SelectedIndex = 1;
            _layoutRow = Row("Варианты каналов", _layout); panel.Children.Add(_layoutRow);
            _horizontal = Combo(); _vertical = Combo();
            _horizontalRow = Row("По горизонтали ±", _horizontal); _verticalRow = Row("По вертикали ±", _vertical);
            panel.Children.Add(_horizontalRow); panel.Children.Add(_verticalRow);
            _combinationPanel = new StackPanel();
            for (int i = 0; i < 4; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
                _rangeLabels[i] = Note(""); _ranges[i] = Field(""); Grid.SetColumn(_ranges[i], 1);
                row.Children.Add(_rangeLabels[i]); row.Children.Add(_ranges[i]); _rangeRows[i] = row;
                _combinationPanel.Children.Add(row);
                _ranges[i].TextChanged += (_, __) => UpdateSampleCount();
            }
            _combinationPanel.Children.Add(Note("Введите отклонения от эталона через пробел: −5 0 +5. " +
                "Например, при C=40 это даст C=35,40,45. Нулевое отклонение включается всегда. " +
                "В каждом ряду эталон по центру; сочетания распределяются по сторонам по первому " +
                "отличающемуся каналу, ближайшие — рядом с эталоном."));
            _sampleCount = Note(""); _combinationPanel.Children.Add(_sampleCount); panel.Children.Add(_combinationPanel);
            _step = Field("1"); _stepRow = Row("Шаг канала, % / 0–255", _step); panel.Children.Add(_stepRow);
            var create = DockerTheme.Primary(Button("Создать отдельный лист цветопробы", (_, __) => Create()));
            create.Margin = new Thickness(0, 7, 0, 5); panel.Children.Add(create);
            _layoutNote = Note(""); panel.Children.Add(_layoutNote);
            _model.SelectionChanged += (_, __) => { UpdateChannels(); DefaultOffsets(); };
            _mode.SelectionChanged += (_, __) => UpdateForm();
            _layout.SelectionChanged += (_, __) => UpdateForm();
            _sampleShape.SelectionChanged += (_, __) => UpdateForm();
            UpdateChannels(); DefaultOffsets(); UpdateForm();
        }

        private void CaptureArtwork()
        {
            try
            {
                ColorProofArtwork artwork = ColorProofArtwork.Capture();
                _artwork = artwork;
                SetReference(artwork.Reference);
                _artworkLabel.Text = "Образец: " + artwork.WidthMm.ToString("0.##") + " × " +
                    artwork.HeightMm.ToString("0.##") + " мм · 1:1";
                _sampleShape.SelectedIndex = 1;
                _status("Логотип выбран. Будут созданы окрашенные копии в исходном размере.", false);
            }
            catch (Exception ex) { Fail("Could not capture color proof artwork.", ex); }
        }

        private void ReadSelection()
        {
            try { SetReference(_service.FromSelection()); }
            catch (Exception ex) { Fail("Color proof source selection failed.", ex); }
        }

        private void PickPalette()
        {
            try { ColorProofReference selected = _service.FromPalette(); if (selected != null) SetReference(selected); }
            catch (Exception ex) { Fail("Color proof palette selection failed.", ex); }
        }

        private void SetReference(ColorProofReference reference)
        {
            _reference = reference;
            UpdateReferenceLabel(); UpdateSampleCount();
            _status("Эталонный цвет выбран. Все варианты будут рассчитаны относительно него.", false);
        }

        private void UpdateReferenceLabel()
        {
            if (_reference == null) return;
            int[] rgb = _service.Components(_reference, false);
            _preview.Background = new SolidColorBrush(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));
            string[] names = _model.SelectedIndex == 0 ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
            int[] values = _service.Components(_reference, _model.SelectedIndex == 0);
            _referenceLabel.Text = (_reference.IsSpot ? "Плашечный · " : "") + _reference.Name + " · " +
                String.Join(" ", values.Select((value, i) => names[i] + ":" + value));
        }

        private void UpdateChannels()
        {
            string[] names = _model.SelectedIndex == 0 ? new[] { "C · голубой", "M · пурпурный", "Y · жёлтый", "K · чёрный" }
                : new[] { "R · красный", "G · зелёный", "B · синий" };
            int x = _horizontal.SelectedIndex, y = _vertical.SelectedIndex;
            _horizontal.Items.Clear(); _vertical.Items.Clear();
            foreach (string name in names) { _horizontal.Items.Add(name); _vertical.Items.Add(name); }
            _horizontal.SelectedIndex = x >= 0 && x < names.Length ? x : 0;
            _vertical.SelectedIndex = y >= 0 && y < names.Length && y != _horizontal.SelectedIndex ? y :
                (_horizontal.SelectedIndex == 0 ? 1 : 0);
            UpdateReferenceLabel();
        }

        private void DefaultOffsets()
        {
            bool cmyk = _model.SelectedIndex == 0;
            string[] names = cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
            for (int i = 0; i < 4; i++)
            {
                _rangeRows[i].Visibility = i < names.Length ? Visibility.Visible : Visibility.Collapsed;
                _rangeLabels[i].Text = i < names.Length ? names[i] + ", отклонения" : "";
                _ranges[i].Text = i < names.Length ? "-1 0 +1" : "";
            }
            _step.Text = "1";
            UpdateSampleCount();
        }

        private void UpdateForm()
        {
            bool artwork = _sampleShape.SelectedIndex == 1;
            _artworkPanel.Visibility = artwork ? Visibility.Visible : Visibility.Collapsed;
            bool visual = _mode.SelectedIndex == 0, combinations = !visual && _layout.SelectedIndex == 0;
            bool two = !visual && _layout.SelectedIndex == 2;
            _visualPanel.Visibility = visual ? Visibility.Visible : Visibility.Collapsed;
            _layoutRow.Visibility = visual ? Visibility.Collapsed : Visibility.Visible;
            _horizontalRow.Visibility = _verticalRow.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
            _combinationPanel.Visibility = combinations ? Visibility.Visible : Visibility.Collapsed;
            _stepRow.Visibility = visual || combinations ? Visibility.Collapsed : Visibility.Visible;
            _layoutNote.Text = "21 позиция: 10 шагов влево, эталон, 10 шагов вправо. " +
                "На границах диапазона и при повторе округлённого цвета остаётся пустое место; эталон не сдвигается. " +
                "CMYK/RGB меняются целыми значениями, минимальный шаг — 1. " +
                "Под каждым образцом цвет указан одной строкой. В режиме двух каналов эталон находится " +
                "в ряду с нулевым изменением второго канала.";
        }

        private int[][] ReadLevels()
        {
            if (_reference == null) throw new InvalidOperationException("Выберите эталон, чтобы рассчитать варианты.");
            bool cmyk = _model.SelectedIndex == 0;
            int[] center = _service.Components(_reference, cmyk); int limit = cmyk ? 100 : 255;
            var levels = new int[center.Length][];
            for (int i = 0; i < center.Length; i++)
            {
                string[] parts = _ranges[i].Text.Replace('−', '-').Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Length > 9) throw new InvalidOperationException("Укажите от 1 до 9 отклонений для каждого канала.");
                var offsets = new HashSet<int> { 0 };
                foreach (string part in parts)
                {
                    if (!Int32.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset) || offset < -limit || offset > limit)
                        throw new InvalidOperationException("Отклонения канала: от −" + limit + " до +" + limit + ". Например: -5 0 +5.");
                    offsets.Add(offset);
                }
                if (offsets.Count > 9) throw new InvalidOperationException("Максимум 9 отклонений с учётом нуля.");
                int channel = i;
                levels[i] = offsets.Select(offset => center[channel] + offset)
                    .Where(value => value >= 0 && value <= limit).Distinct().OrderBy(value => value).ToArray();
            }
            return levels;
        }

        private void UpdateSampleCount()
        {
            if (_sampleCount == null) return;
            if (_reference == null) { _sampleCount.Text = "Выберите эталон для расчёта числа плашек."; return; }
            try
            {
                int swatches = ReadLevels().Aggregate(1, (count, values) => count * values.Length);
                _sampleCount.Text = swatches + " уникальных сочетаний · " +
                    (swatches > 500 ? "максимум 500; сократите отклонения." : "эталон включён.");
            }
            catch (Exception ex) { _sampleCount.Text = ex.Message; }
        }

        private void Create()
        {
            try
            {
                if (_sampleShape.SelectedIndex == 1 && _artwork == null)
                {
                    CaptureArtwork();
                    if (_artwork == null) return;
                }
                if (_reference == null) SetReference(_service.FromSelection());
                bool cmyk = _model.SelectedIndex == 0;
                var sheet = new ColorProofSheetOptions { WidthMm = Number(_sheetWidth, 100, 4000, "Ширина листа, мм"),
                    Artwork = _sampleShape.SelectedIndex == 1 ? _artwork : null };
                try { Directory.CreateDirectory(Path.GetDirectoryName(WidthPath));
                    File.WriteAllText(WidthPath, sheet.WidthMm.ToString(CultureInfo.InvariantCulture)); }
                catch (Exception ex) { Log.Error("Could not save color proof sheet width.", ex); }
                int count;
                if (_mode.SelectedIndex == 0)
                    count = _service.CreateVisual(_reference, cmyk,
                        Number(_hueStep, .1, 30, "Шаг оттенка, °"), Number(_lightStep, .1, 20, "Шаг светлоты, %"),
                        Number(_saturationStep, .1, 30, "Шаг насыщенности, %"), sheet);
                else if (_layout.SelectedIndex == 0)
                    count = _service.CreateCombinations(_reference, cmyk, ReadLevels(), sheet);
                else
                {
                    if (!Int32.TryParse(_step.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int step))
                        throw new InvalidOperationException("Введите целый шаг изменения канала.");
                    count = _service.Create(_reference, new ColorProofOptions { Cmyk = cmyk,
                        AllChannels = _layout.SelectedIndex == 1, HorizontalChannel = _horizontal.SelectedIndex,
                        VerticalChannel = _vertical.SelectedIndex, Step = step }, sheet);
                }
                _status("Создан лист шириной " + sheet.WidthMm + " мм: образцов " + count +
                    (sheet.Artwork == null ? "." : ", логотипы 1:1."), false);
            }
            catch (Exception ex) { Fail("Color proof creation failed.", ex); }
        }

        private static double Number(TextBox field, double min, double max, string label)
        {
            if (Double.TryParse(field.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= min && value <= max) return value;
            throw new InvalidOperationException(label + ": от " + min + " до " + max + ".");
        }

        private void Fail(string context, Exception ex) { Log.Error(context, ex); _status(ex.Message, true); }
        private static ComboBox Combo(params string[] items)
        {
            var combo = new ComboBox { MinHeight = 27, FontSize = 11 };
            foreach (string item in items) combo.Items.Add(item);
            if (items.Length > 0) combo.SelectedIndex = 0; return combo;
        }
        private static Grid Row(string label, UIElement control)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(control, 1); row.Children.Add(control); return row;
        }
        private static TextBox Field(string value) => new TextBox { Text = value, MinHeight = 26, FontSize = 11, TextAlignment = TextAlignment.Center };
        private static Button Button(string label, RoutedEventHandler click)
        {
            var button = new Button { Content = label, MinHeight = 29, FontSize = 11, Padding = new Thickness(5, 1, 5, 1) };
            button.Click += click; return button;
        }
        private static TextBlock Title(string text) => new TextBlock { Text = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        private static TextBlock Note(string text) => new TextBlock { Text = text, FontSize = 10,
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) };
    }
}
