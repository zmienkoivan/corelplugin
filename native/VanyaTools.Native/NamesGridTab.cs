using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        private readonly CheckBox _twoLines;
        private readonly TextBlock _count;
        private readonly TextBox _preview;
        private bool _sizeIsMm;
        private static readonly Regex NumberPrefix = new Regex(
            @"^\s*(?:\d{1,4}[.)]|\(\d{1,4}\))\s*", RegexOptions.Compiled);
        private static readonly Regex WidthNote = new Regex(
            @"\s*\(\s*(?:укоротить|сократить|уменьшить)\s+до\s+(?<width>\d+(?:[.,]\d+)?)\s*(?:мм)?\s*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex UnrecognizedWidthNote = new Regex(
            @"\([^)]*(?:укорот|сократ|уменьш|\d+\s*мм)[^)]*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public NamesGridTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(6) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel };

            panel.Children.Add(Note("Одна строка — одна надпись. Номера и пометки «укоротить до 110» удаляются."));

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
                MinHeight = 125, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 9) };
            panel.Children.Add(_names);
            _preview = new TextBox { IsReadOnly = true, FontSize = 11, MinHeight = 72, MaxHeight = 150,
                TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new Expander { Header = "Что будет напечатано", Content = _preview,
                Margin = new Thickness(0, 0, 0, 8) });
            _names.TextChanged += (_, __) => UpdatePreview();

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

            _maxWidth = AddNumberRow(panel, "Макс. ширина надписи, мм", "150");
            _columns = AddNumberRow(panel, "Колонок", "2");
            _horizontalGap = AddNumberRow(panel, "Между колонками, мм", "10");
            _verticalGap = AddNumberRow(panel, "Между строками, мм", "8");
            _twoLines = new CheckBox { Content = "Фамилия и имя в две строки", IsChecked = false,
                FontSize = 11, Margin = new Thickness(0, 3, 0, 3) };
            _twoLines.Checked += (_, __) => UpdatePreview();
            _twoLines.Unchecked += (_, __) => UpdatePreview();
            panel.Children.Add(_twoLines);
            _center = new CheckBox { Content = "По центру ячеек", IsChecked = true, FontSize = 11,
                Margin = new Thickness(0, 3, 0, 9) };
            panel.Children.Add(_center);
            panel.Children.Add(DockerTheme.Primary(Button("Создать сетку в CorelDRAW", CreateGrid)));
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
                _status("Список загружен: " + ParseNames(_names.Text).Count + " строк.", false);
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
                List<NamesGridEntry> names = ParseNames(_names.Text);
                if (names.Count == 0)
                    throw new InvalidOperationException("Вставьте список: одно имя на строку.");
                if (names.Count > 2000)
                    throw new InvalidOperationException("За один раз можно создать не более 2000 надписей.");
                if (names.Any(name => name.Text.Length > 200))
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
                    CenterInCell = _center.IsChecked == true,
                    TwoLines = _twoLines.IsChecked == true
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

        private void UpdatePreview()
        {
            try
            {
                List<NamesGridEntry> names = ParseNames(_names.Text);
                int customWidths = names.Count(name => name.MaxWidthMm.HasValue);
                _count.Text = names.Count + " надписей" +
                    (customWidths > 0 ? " · " + customWidths + " пометок ширины" : "");
                bool twoLines = _twoLines != null && _twoLines.IsChecked == true;
                _preview.Text = String.Join(twoLines ? Environment.NewLine + Environment.NewLine : Environment.NewLine,
                    names.Select(name => name.PrintText(twoLines).Replace("\r", Environment.NewLine)));
            }
            catch (InvalidOperationException ex)
            {
                _count.Text = "Проверьте список";
                _preview.Text = ex.Message;
            }
        }

        private static List<NamesGridEntry> ParseNames(string text)
        {
            var entries = new List<NamesGridEntry>();
            string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                line = NumberPrefix.Replace(line, "", 1).Trim();
                double? width = null;
                Match note = WidthNote.Match(line);
                if (note.Success)
                {
                    double value;
                    if (!Double.TryParse(note.Groups["width"].Value.Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value) || value <= 0 || value > 2000)
                        throw new InvalidOperationException("Строка " + (i + 1) + ": неверная ширина в пометке.");
                    width = value;
                    line = line.Substring(0, note.Index).TrimEnd();
                }
                else if (UnrecognizedWidthNote.IsMatch(line))
                    throw new InvalidOperationException("Строка " + (i + 1) + ": не удалось прочитать пометку ширины.");

                line = String.Join(" ", line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                if (line.Length == 0)
                    throw new InvalidOperationException("Строка " + (i + 1) + ": после номера не осталось имени.");
                entries.Add(new NamesGridEntry { Text = line, MaxWidthMm = width });
            }
            return entries;
        }

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
