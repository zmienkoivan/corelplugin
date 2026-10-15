using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal sealed class SportLayoutTab : UserControl
    {
        private sealed class Draft
        {
            public List<SportLayoutTemplate> Templates { get; set; }
            public List<SportLayoutPlayer> Players { get; set; }
            public string PatternPath { get; set; }
            public string OutputFolder { get; set; }
            public string FontName { get; set; }
            public bool FrontNameOnRight { get; set; }
            public bool WhiteText { get; set; }
            public bool KeepGuides { get; set; }
            public bool RepeatPattern { get; set; }
            public string TileWidth { get; set; }
        }

        private static readonly string DraftPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "sport-layout-draft.json");

        private readonly Action<string, bool> _status;
        private readonly ObservableCollection<SportLayoutTemplate> _templates =
            new ObservableCollection<SportLayoutTemplate>();
        private readonly ObservableCollection<SportLayoutPlayer> _players =
            new ObservableCollection<SportLayoutPlayer>();
        private readonly DataGrid _templateGrid, _playerGrid;
        private readonly TextBox _pattern, _output, _font;
        private readonly ComboBox _nameSide, _textColor;
        private readonly ComboBox _patternMode;
        private readonly TextBox _tileWidth;
        private readonly CheckBox _keepGuides;
        private readonly TextBlock _count, _progress;
        private readonly Button _generate;
        private bool _loading;

        public SportLayoutTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(7) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel };
            panel.Children.Add(Title("Раскладка спортформы"));
            panel.Children.Add(Note("Каждая строка создаёт отдельный CDR для каждого экземпляра. " +
                "Исходные лекала остаются без изменений."));

            panel.Children.Add(Title("1. Лекала по размерам"));
            _templateGrid = new DataGrid { Height = 132, AutoGenerateColumns = false,
                CanUserAddRows = false, CanUserDeleteRows = false, FontSize = 12,
                Margin = new Thickness(0, 3, 0, 4), ItemsSource = _templates };
            _templateGrid.Columns.Add(Column("Размер", "Size", 80));
            _templateGrid.Columns.Add(Column("Масштаб %", "ScalePercent", 85));
            _templateGrid.Columns.Add(Column("Файл CDR", "Path", 290));
            panel.Children.Add(_templateGrid);
            var templateButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            templateButtons.Children.Add(Button("Добавить шаблон CDR", AddTemplate));
            templateButtons.Children.Add(Button("Сделать S и XL из М", AddApproximateSizes));
            templateButtons.Children.Add(Button("Убрать выбранный", (_, __) =>
            {
                if (_templateGrid.SelectedItem is SportLayoutTemplate item) _templates.Remove(item);
                SaveDraft();
            }));
            panel.Children.Add(templateButtons);
            panel.Children.Add(Note("Для размера М: перед, спина, верхний левый рукав и нижний правый. " +
                "Временные S и XL масштабируются целиком; 95% и 110% можно изменить. " +
                "Позже укажите отдельные файлы CDR и масштаб 100%."));

            panel.Children.Add(Title("2. Игроки"));
            panel.Children.Add(Note("Столбцы: размер, имя, номер, количество. Можно вставить строки из Excel."));
            _playerGrid = new DataGrid { Height = 224, AutoGenerateColumns = false,
                CanUserAddRows = false, CanUserDeleteRows = false, FontSize = 12,
                Margin = new Thickness(0, 3, 0, 4), ItemsSource = _players };
            _playerGrid.Columns.Add(Column("Размер", "Size", 70));
            _playerGrid.Columns.Add(Column("Имя", "Name", 185));
            _playerGrid.Columns.Add(Column("№", "Number", 60));
            _playerGrid.Columns.Add(Column("Шт.", "Quantity", 55));
            panel.Children.Add(_playerGrid);
            var playerButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
            playerButtons.Children.Add(Button("Добавить строку", (_, __) =>
            {
                _players.Add(new SportLayoutPlayer { Size = "М", Quantity = 1 });
                _playerGrid.SelectedIndex = _players.Count - 1;
                SaveDraft();
            }));
            playerButtons.Children.Add(Button("Вставить из Excel", PastePlayers));
            playerButtons.Children.Add(Button("Убрать строку", (_, __) =>
            {
                if (_playerGrid.SelectedItem is SportLayoutPlayer item) _players.Remove(item);
                SaveDraft();
            }));
            panel.Children.Add(playerButtons);
            _count = Note("");
            panel.Children.Add(_count);

            panel.Children.Add(Title("3. Рисунок и надписи"));
            panel.Children.Add(Label("Рисунок для заливки деталей (необязательно)"));
            _pattern = Field("");
            panel.Children.Add(PathRow(_pattern, "Выбрать рисунок", BrowsePattern));
            panel.Children.Add(Button("Взять выделенный рисунок из Corel", CapturePattern));
            var patternOptions = new WrapPanel { Margin = new Thickness(0, 2, 0, 5) };
            patternOptions.Children.Add(Label("Способ заливки"));
            _patternMode = new ComboBox { Width = 167, Height = 27, FontSize = 11,
                Margin = new Thickness(5, 0, 10, 0) };
            _patternMode.Items.Add("Повторять паттерн");
            _patternMode.Items.Add("Покрыть деталь");
            _patternMode.SelectedIndex = 0;
            patternOptions.Children.Add(_patternMode);
            patternOptions.Children.Add(Label("Ширина плитки, мм"));
            _tileWidth = Field("100");
            _tileWidth.Width = 58;
            _tileWidth.Margin = new Thickness(5, 0, 0, 0);
            patternOptions.Children.Add(_tileWidth);
            panel.Children.Add(patternOptions);
            panel.Children.Add(Note("«Повторять» создаёт редактируемую заливку Corel. " +
                "«Покрыть» сохраняет пропорции рисунка и обрезает его по лекалу."));
            panel.Children.Add(Label("Папка готовых CDR"));
            _output = Field("");
            panel.Children.Add(PathRow(_output, "Выбрать папку", BrowseOutput));
            panel.Children.Add(Label("Шрифт имени и номера"));
            _font = Field("Arial");
            panel.Children.Add(_font);

            var settings = new WrapPanel { Margin = new Thickness(0, 7, 0, 6) };
            settings.Children.Add(Label("Имя спереди"));
            _nameSide = new ComboBox { Width = 110, Height = 27, FontSize = 12,
                Margin = new Thickness(4, 0, 12, 0) };
            _nameSide.Items.Add("Слева");
            _nameSide.Items.Add("Справа");
            _nameSide.SelectedIndex = 0;
            settings.Children.Add(_nameSide);
            settings.Children.Add(Label("Цвет надписей"));
            _textColor = new ComboBox { Width = 92, Height = 27, FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0) };
            _textColor.Items.Add("Чёрный");
            _textColor.Items.Add("Белый");
            _textColor.SelectedIndex = 0;
            settings.Children.Add(_textColor);
            panel.Children.Add(settings);
            _keepGuides = new CheckBox { Content = "Оставить красные рамки в готовом CDR",
                FontSize = 11, Margin = new Thickness(0, 0, 0, 8), IsChecked = false };
            panel.Children.Add(_keepGuides);
            panel.Children.Add(Note("Свободная зона переда и зоны рукавов сохраняются как именованные " +
                "фреймы для дополнительной графики. Подписи каждой детали находятся в нижнем припуске."));

            _generate = DockerTheme.Primary(Button("Создать CDR для всех строк", Generate));
            _generate.Margin = new Thickness(0, 7, 0, 4);
            panel.Children.Add(_generate);
            _progress = Note("");
            panel.Children.Add(_progress);
            panel.Children.Add(Button("Открыть папку готовых CDR", OpenOutput));

            _templates.CollectionChanged += (_, __) => SaveDraft();
            _players.CollectionChanged += (_, __) => { UpdateCount(); SaveDraft(); };
            _templateGrid.CellEditEnding += (_, __) => SaveSoon();
            _playerGrid.CellEditEnding += (_, __) => SaveSoon();
            _pattern.TextChanged += (_, __) => SaveDraft();
            _output.TextChanged += (_, __) => SaveDraft();
            _font.TextChanged += (_, __) => SaveDraft();
            _tileWidth.TextChanged += (_, __) => SaveDraft();
            _patternMode.SelectionChanged += (_, __) => SaveDraft();
            _nameSide.SelectionChanged += (_, __) => SaveDraft();
            _textColor.SelectionChanged += (_, __) => SaveDraft();
            _keepGuides.Checked += (_, __) => SaveDraft();
            _keepGuides.Unchecked += (_, __) => SaveDraft();
            LoadDraft();
            UpdateCount();
        }

        private void AddTemplate(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "CorelDRAW (*.cdr)|*.cdr",
                CheckFileExists = true, Multiselect = true };
            if (dialog.ShowDialog() != true) return;
            foreach (string path in dialog.FileNames)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string size = name.Split(new[] { ' ', '_', '-' },
                    StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "М";
                if (_templates.Any(x => String.Equals(x.Path, path,
                    StringComparison.OrdinalIgnoreCase) &&
                    SportLayoutService.NormalizeSize(x.Size) ==
                        SportLayoutService.NormalizeSize(size))) continue;
                var existing = _templates.FirstOrDefault(x =>
                    SportLayoutService.NormalizeSize(x.Size) ==
                        SportLayoutService.NormalizeSize(size));
                if (existing != null)
                {
                    existing.Path = path;
                    existing.ScalePercent = 100;
                    _templateGrid.Items.Refresh();
                }
                else _templates.Add(new SportLayoutTemplate {
                    Size = size, Path = path, ScalePercent = 100 });
            }
            SaveDraft();
        }

        private void AddApproximateSizes(object sender, RoutedEventArgs e)
        {
            var medium = _templates.FirstOrDefault(item =>
                SportLayoutService.NormalizeSize(item.Size) == "M");
            if (medium == null || String.IsNullOrWhiteSpace(medium.Path))
            {
                _status("Сначала добавьте шаблон размера М.", true);
                return;
            }
            if (!_templates.Any(item => SportLayoutService.NormalizeSize(item.Size) == "S"))
                _templates.Add(new SportLayoutTemplate { Size = "S", Path = medium.Path,
                    ScalePercent = 95 });
            if (!_templates.Any(item => SportLayoutService.NormalizeSize(item.Size) == "XL"))
                _templates.Add(new SportLayoutTemplate { Size = "XL", Path = medium.Path,
                    ScalePercent = 110 });
            SaveDraft();
            _status("S и XL временно построены из М. Проверьте масштаб перед печатью.", false);
        }

        private void PastePlayers(object sender, RoutedEventArgs e)
        {
            try
            {
                string text = Clipboard.GetText();
                if (String.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("В буфере обмена нет таблицы.");
                var parsed = new List<SportLayoutPlayer>();
                foreach (string line in text.Split(new[] { "\r\n", "\n" },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] cells = line.Trim().Split('\t');
                    if (cells.Length < 2) cells = line.Trim().Split(';');
                    if (cells.Length < 2)
                        throw new InvalidOperationException("Нужны столбцы: размер, имя, номер, количество.");
                    if (cells[0].Trim().Equals("Размер", StringComparison.CurrentCultureIgnoreCase))
                        continue;
                    int quantity = 1;
                    if (cells.Length > 3 && !String.IsNullOrWhiteSpace(cells[3]) &&
                        (!Int32.TryParse(cells[3].Trim(), out quantity) || quantity < 1 || quantity > 100))
                        throw new InvalidOperationException("Неверное количество в строке: " + line);
                    parsed.Add(new SportLayoutPlayer { Size = cells[0].Trim(),
                        Name = cells[1].Trim(), Number = cells.Length > 2 ? cells[2].Trim() : "",
                        Quantity = quantity });
                }
                foreach (var row in parsed) _players.Add(row);
                _status("Добавлено игроков: " + parsed.Count + ".", false);
                SaveDraft();
            }
            catch (Exception error) { _status(error.Message, true); }
        }

        private void BrowsePattern(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter =
                "Изображения (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|Все файлы (*.*)|*.*",
                CheckFileExists = true };
            if (dialog.ShowDialog() == true) _pattern.Text = dialog.FileName;
        }

        private void CapturePattern(object sender, RoutedEventArgs e)
        {
            string temporary = null;
            try
            {
                temporary = AiSelectionCapture.Capture(4096, 16000000);
                string folder = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "sport-patterns");
                Directory.CreateDirectory(folder);
                string destination = Path.Combine(folder, "pattern-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                File.Move(temporary, destination);
                temporary = null;
                _pattern.Text = destination;
                _status("Выделенный рисунок сохранён для раскладки.", false);
            }
            catch (Exception error)
            {
                Log.Error("Could not capture selected sport pattern.", error);
                _status(error.Message, true);
            }
            finally
            {
                try { if (temporary != null) File.Delete(temporary); } catch { }
            }
        }

        private void BrowseOutput(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = WindowsFolderPicker.Pick(IntPtr.Zero, _output.Text,
                    "Папка для готовых лекал");
                if (!String.IsNullOrWhiteSpace(path)) _output.Text = path;
            }
            catch (Exception error) { _status(error.Message, true); }
        }

        private async void Generate(object sender, RoutedEventArgs e)
        {
            if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
                !_templateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_templateGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                _status("Закончите редактирование таблицы и проверьте значения.", true);
                return;
            }
            SaveDraft();
            _generate.IsEnabled = false;
            var created = new List<string>();
            try
            {
                var players = _players.ToList();
                var templates = _templates.ToList();
                if (players.Count == 0)
                    throw new InvalidOperationException("Добавьте строки с игроками.");
                if (players.Any(row => row.Quantity < 1 || row.Quantity > 100))
                    throw new InvalidOperationException("Количество в каждой строке: от 1 до 100.");
                long totalLong = players.Sum(row => (long)row.Quantity);
                if (totalLong > 300)
                    throw new InvalidOperationException("За один запуск можно подготовить не более 300 комплектов.");
                int total = (int)totalLong;
                var options = new SportLayoutOptions {
                    PatternPath = _pattern.Text.Trim(), OutputFolder = _output.Text.Trim(),
                    FontName = _font.Text.Trim(), FrontNameOnRight = _nameSide.SelectedIndex == 1,
                    WhiteText = _textColor.SelectedIndex == 1,
                    KeepGuides = _keepGuides.IsChecked == true,
                    RepeatPattern = _patternMode.SelectedIndex == 0,
                    TileWidthMm = _patternMode.SelectedIndex == 0 &&
                        !String.IsNullOrWhiteSpace(_pattern.Text)
                        ? ParseTileWidth(_tileWidth.Text) : 100
                };
                foreach (var row in players)
                for (int copy = 0; copy < row.Quantity; copy++)
                {
                    _progress.Text = "Готовлю " + (created.Count + 1) + " из " + total +
                        ": " + row.Size + " · " + row.Name;
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    var single = new SportLayoutPlayer { Size = row.Size, Name = row.Name,
                        Number = row.Number, Quantity = 1 };
                    var files = new SportLayoutService().Generate(new[] { single },
                        templates, options, null);
                    created.AddRange(files);
                }
                _progress.Text = "Готово: " + created.Count + " CDR в " + options.OutputFolder;
                _status("Подготовлено комплектов: " + created.Count + ".", false);
            }
            catch (Exception error)
            {
                Log.Error("Sport layout generation failed.", error);
                _progress.Text = "Создано " + created.Count + " CDR до ошибки.";
                _status(error.Message, true);
            }
            finally { _generate.IsEnabled = true; }
        }

        private void OpenOutput(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(_output.Text.Trim()))
                    throw new DirectoryNotFoundException("Папка готовых CDR не найдена.");
                Process.Start(new ProcessStartInfo { FileName = _output.Text.Trim(), UseShellExecute = true });
            }
            catch (Exception error) { _status(error.Message, true); }
        }

        private void UpdateCount()
        {
            _count.Text = _players.Count + " строк · " + _players.Sum(row => Math.Max(0L, (long)row.Quantity)) +
                " комплектов";
        }

        private void SaveSoon()
        {
            Dispatcher.BeginInvoke(new Action(() => { UpdateCount(); SaveDraft(); }),
                DispatcherPriority.Background);
        }

        private void SaveDraft()
        {
            if (_loading || _pattern == null || _output == null || _font == null ||
                _nameSide == null || _textColor == null || _keepGuides == null ||
                _patternMode == null || _tileWidth == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DraftPath));
                var draft = new Draft {
                    Templates = _templates.ToList(), Players = _players.ToList(),
                    PatternPath = _pattern.Text, OutputFolder = _output.Text,
                    FontName = _font.Text, FrontNameOnRight = _nameSide.SelectedIndex == 1,
                    WhiteText = _textColor.SelectedIndex == 1,
                    KeepGuides = _keepGuides.IsChecked == true,
                    RepeatPattern = _patternMode.SelectedIndex == 0,
                    TileWidth = _tileWidth.Text
                };
                File.WriteAllText(DraftPath, new JavaScriptSerializer().Serialize(draft),
                    new UTF8Encoding(false));
            }
            catch (Exception error) { Log.Error("Could not save sport layout draft.", error); }
        }

        private void LoadDraft()
        {
            _loading = true;
            try
            {
                if (File.Exists(DraftPath))
                {
                    var draft = new JavaScriptSerializer().Deserialize<Draft>(
                        File.ReadAllText(DraftPath, Encoding.UTF8));
                    if (draft != null)
                    {
                        foreach (var row in draft.Templates ?? new List<SportLayoutTemplate>())
                            _templates.Add(row);
                        foreach (var row in draft.Players ?? new List<SportLayoutPlayer>())
                            _players.Add(row);
                        _pattern.Text = draft.PatternPath ?? "";
                        _output.Text = draft.OutputFolder ?? "";
                        _font.Text = draft.FontName ?? "Arial";
                        _nameSide.SelectedIndex = draft.FrontNameOnRight ? 1 : 0;
                        _textColor.SelectedIndex = draft.WhiteText ? 1 : 0;
                        _keepGuides.IsChecked = draft.KeepGuides;
                        _patternMode.SelectedIndex = draft.RepeatPattern ? 0 : 1;
                        _tileWidth.Text = draft.TileWidth ?? "100";
                    }
                }
                if (_templates.Count == 0)
                {
                    string sample = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.DesktopDirectory), "спорт М.cdr");
                    if (File.Exists(sample))
                    {
                        _templates.Add(new SportLayoutTemplate { Size = "М", Path = sample,
                            ScalePercent = 100 });
                        _templates.Add(new SportLayoutTemplate { Size = "S", Path = sample,
                            ScalePercent = 95 });
                        _templates.Add(new SportLayoutTemplate { Size = "XL", Path = sample,
                            ScalePercent = 110 });
                    }
                }
                if (_players.Count == 0)
                    _players.Add(new SportLayoutPlayer { Size = "М", Quantity = 1 });
            }
            catch (Exception error) { Log.Error("Could not load sport layout draft.", error); }
            finally { _loading = false; }
        }

        private static DataGridTextColumn Column(string header, string path, double width)
        {
            return new DataGridTextColumn { Header = header,
                Binding = new Binding(path) { Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                Width = new DataGridLength(width) };
        }

        private static double ParseTileWidth(string text)
        {
            if (Double.TryParse((text ?? "").Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double width) &&
                width >= 5 && width <= 1000) return width;
            throw new InvalidOperationException("Ширина плитки паттерна: от 5 до 1000 мм.");
        }

        private static UIElement PathRow(TextBox field, string label, RoutedEventHandler click)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(field);
            var button = Button(label, click);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
            return grid;
        }

        private static Button Button(string label, RoutedEventHandler click)
        {
            var button = new Button { Content = label, MinHeight = 27, FontSize = 11,
                Margin = new Thickness(0, 0, 4, 3), Padding = new Thickness(8, 3, 8, 3) };
            button.Click += click;
            return button;
        }

        private static TextBox Field(string value)
        {
            return new TextBox { Text = value, FontSize = 12, MinHeight = 27,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0) };
        }

        private static TextBlock Title(string value)
        {
            return new TextBlock { Text = value, FontSize = 13,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
        }

        private static TextBlock Label(string value)
        {
            return new TextBlock { Text = value, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 2) };
        }

        private static TextBlock Note(string value)
        {
            return new TextBlock { Text = value, FontSize = 11,
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 5) };
        }
    }
}
