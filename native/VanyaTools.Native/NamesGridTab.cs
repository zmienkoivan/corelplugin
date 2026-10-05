using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal sealed class NamesGridTab : UserControl
    {
        private readonly Action<string, bool> _status;
        private readonly TextBox _names;
        private readonly ComboBox _font;
        private readonly TextBox _size;
        private readonly ComboBox _unit;
        private readonly TextBox _maxWidth;
        private readonly TextBox _columns;
        private readonly TextBox _horizontalGap;
        private readonly TextBox _verticalGap;
        private readonly CheckBox _center;
        private readonly TextBlock _count;
        private bool _sizeIsMm;

        public NamesGridTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(10) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };

            panel.Children.Add(new TextBlock { Text = "Фамилии для печати", FontSize = 14,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
            panel.Children.Add(Note("Одна строка — одна надпись. Текст останется редактируемым."));

            var inputHeader = new Grid { Margin = new Thickness(0, 4, 0, 3) };
            inputHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _count = new TextBlock { Text = "0 надписей", VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DimGray, FontSize = 11 };
            inputHeader.Children.Add(_count);
            var loadButton = Button("Загрузить TXT", LoadFile);
            Grid.SetColumn(loadButton, 1);
            inputHeader.Children.Add(loadButton);
            panel.Children.Add(inputHeader);

            _names = new TextBox { AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.NoWrap,
                MinHeight = 170, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 9) };
            _names.TextChanged += (_, __) => _count.Text = CountLines(_names.Text) + " надписей";
            panel.Children.Add(_names);

            panel.Children.Add(Label("Шрифт"));
            _font = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8), MaxDropDownHeight = 230 };
            foreach (string family in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
                _font.Items.Add(family);
            _font.Text = "Arial";
            panel.Children.Add(_font);

            var sizeRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            sizeRow.Children.Add(Label("Размер шрифта"));
            _size = Field("36");
            Grid.SetColumn(_size, 1);
            sizeRow.Children.Add(_size);
            _unit = new ComboBox { FontSize = 11, Margin = new Thickness(3, 0, 0, 0), Height = 25 };
            _unit.Items.Add("pt");
            _unit.Items.Add("мм");
            _unit.SelectedIndex = 0;
            _unit.SelectionChanged += ChangeUnit;
            Grid.SetColumn(_unit, 2);
            sizeRow.Children.Add(_unit);
            panel.Children.Add(sizeRow);

            _maxWidth = AddNumberRow(panel, "Макс. ширина надписи, мм", "85");
            _columns = AddNumberRow(panel, "Колонок", "2");
            _horizontalGap = AddNumberRow(panel, "Между колонками, мм", "10");
            _verticalGap = AddNumberRow(panel, "Между строками, мм", "8");
            _center = new CheckBox { Content = "По центру ячеек", IsChecked = true, FontSize = 11,
                Margin = new Thickness(0, 3, 0, 9) };
            panel.Children.Add(_center);
            panel.Children.Add(Button("Создать сетку в CorelDRAW", CreateGrid));
            panel.Children.Add(Note("Сетка начнётся в 10 мм от верхнего левого края страницы. Пустые строки пропускаются."));
        }

        private void LoadFile(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Текст (*.txt)|*.txt|Все файлы (*.*)|*.*",
                CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > 1024 * 1024)
                    throw new InvalidOperationException("Файл больше 1 МБ. Загрузите список имён меньшего размера.");
                try
                {
                    using (var reader = new StreamReader(dialog.FileName, new UTF8Encoding(false, true), true))
                        _names.Text = reader.ReadToEnd();
                }
                catch (DecoderFallbackException)
                {
                    _names.Text = File.ReadAllText(dialog.FileName, Encoding.GetEncoding(1251));
                }
                _status("Список загружен: " + CountLines(_names.Text) + " строк.", false);
            }
            catch (Exception ex)
            {
                Log.Error("Names file loading failed.", ex);
                _status(ex.Message, true);
            }
        }

        private void ChangeUnit(object sender, SelectionChangedEventArgs e)
        {
            bool toMm = _unit.SelectedIndex == 1;
            if (toMm == _sizeIsMm) return;
            double value;
            if (TryNumber(_size.Text, out value) && value > 0)
                _size.Text = (toMm ? value * 25.4 / 72.0 : value * 72.0 / 25.4)
                    .ToString("0.###", CultureInfo.CurrentCulture);
            _sizeIsMm = toMm;
        }

        private void CreateGrid(object sender, RoutedEventArgs e)
        {
            try
            {
                List<string> names = ParseNames(_names.Text);
                if (names.Count == 0)
                    throw new InvalidOperationException("Вставьте список: одно имя на строку.");
                if (names.Count > 2000)
                    throw new InvalidOperationException("За один раз можно создать не более 2000 надписей.");
                if (names.Any(name => name.Length > 200))
                    throw new InvalidOperationException("Одна из строк длиннее 200 символов.");

                string font = _font.Text.Trim();
                if (font.Length == 0)
                    throw new InvalidOperationException("Выберите шрифт.");
                double fontSize = Positive(_size.Text, "Размер шрифта");
                double fontSizePt = _sizeIsMm ? fontSize * 72.0 / 25.4 : fontSize;
                if (fontSizePt > 1000)
                    throw new InvalidOperationException("Размер шрифта должен быть не больше 1000 pt.");
                double maxWidth = Positive(_maxWidth.Text, "Максимальная ширина");
                if (maxWidth > 2000)
                    throw new InvalidOperationException("Максимальная ширина должна быть не больше 2000 мм.");
                int columns;
                if (!Int32.TryParse(_columns.Text.Trim(), out columns) || columns < 1 || columns > 20)
                    throw new InvalidOperationException("Укажите от 1 до 20 колонок.");
                var options = new NamesGridOptions {
                    FontName = font, FontSizePt = fontSizePt, MaxWidthMm = maxWidth,
                    Columns = Math.Min(columns, names.Count),
                    HorizontalGapMm = NonNegative(_horizontalGap.Text, "Расстояние между колонками"),
                    VerticalGapMm = NonNegative(_verticalGap.Text, "Расстояние между строками"),
                    CenterInCell = _center.IsChecked == true
                };
                var result = new NamesGridService().Create(names, options);
                _status("Создано: " + result.Count + ". Уменьшено по ширине: " + result.ReducedCount +
                    ". Сетка: " + result.WidthMm.ToString("0.#") + " × " +
                    result.HeightMm.ToString("0.#") + " мм.", false);
            }
            catch (Exception ex)
            {
                Log.Error("Names grid creation failed.", ex);
                _status(ex.Message, true);
            }
        }

        private static List<string> ParseNames(string text)
        {
            return text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Select(line => String.Join(" ", line.Trim().Split(
                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)))
                .Where(line => line.Length != 0).ToList();
        }

        private static int CountLines(string text) { return ParseNames(text).Count; }

        private static bool TryNumber(string text, out double result)
        {
            return Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
                   Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static double Positive(string text, string label)
        {
            double value;
            if (!TryNumber(text, out value) || Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0)
                throw new InvalidOperationException(label + ": укажите положительное число.");
            return value;
        }

        private static double NonNegative(string text, string label)
        {
            double value;
            if (!TryNumber(text, out value) || Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 || value > 2000)
                throw new InvalidOperationException(label + ": укажите число от 0 до 2000 мм.");
            return value;
        }

        private static TextBox AddNumberRow(Panel panel, string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.Children.Add(Label(label));
            var field = Field(value);
            Grid.SetColumn(field, 1);
            row.Children.Add(field);
            panel.Children.Add(row);
            return field;
        }

        private static TextBlock Label(string text) { return new TextBlock { Text = text, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }; }
        private static TextBlock Note(string text) { return new TextBlock { Text = text, FontSize = 10,
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6) }; }
        private static TextBox Field(string text) { return new TextBox { Text = text, FontSize = 11,
            Height = 25, TextAlignment = TextAlignment.Center, Margin = new Thickness(3, 0, 0, 0) }; }
        private static Button Button(string text, RoutedEventHandler action)
        {
            var button = new Button { Content = text, FontSize = 11, MinHeight = 26,
                Padding = new Thickness(5, 1, 5, 1) };
            button.Click += action;
            return button;
        }
    }
}
