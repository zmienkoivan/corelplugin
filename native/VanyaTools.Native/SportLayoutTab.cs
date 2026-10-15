using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace VanyaTools.Native
{
    internal sealed class SportLayoutTab : UserControl
    {
        private sealed class Draft
        {
            public List<SportLayoutTemplate> Templates { get; set; }
            public List<SportLayoutPlayer> Players { get; set; }
            public string PatternPath { get; set; }
            public string FrontArtworkPath { get; set; }
            public string BackArtworkPath { get; set; }
            public string LeftSleeveArtworkPath { get; set; }
            public string RightSleeveArtworkPath { get; set; }
            public string SheetWidth { get; set; }
            public string Gap { get; set; }
            public string FontName { get; set; }
            public bool FrontNameOnRight { get; set; }
            public bool WhiteText { get; set; }
            public SportTextStyle NameStyle { get; set; }
            public SportTextStyle NumberStyle { get; set; }
            public bool KeepGuides { get; set; }
        }

        private sealed class TextStyleEditor
        {
            public readonly TextBox Font = Field("Arial");
            public readonly TextBox Points = Field("72");
            public readonly CheckBox Bold = new CheckBox { Content = "Жирный", FontSize = 11 };
            public readonly CheckBox Italic = new CheckBox { Content = "Курсив", FontSize = 11 };
            public readonly TextBox Fill = Field("#000000");
            public readonly TextBox Outline = Field("#000000");
            public readonly TextBox OutlineWidth = Field("0");

            public TextStyleEditor(Panel parent, string title, string defaultPoints,
                Action changed)
            {
                Points.Text = defaultPoints;
                parent.Children.Add(Title(title));
                var fontRow = new WrapPanel();
                fontRow.Children.Add(Label("Шрифт"));
                Font.Width = 168;
                fontRow.Children.Add(Font);
                fontRow.Children.Add(Button("Выбрать…", (_, __) => ChooseFont()));
                fontRow.Children.Add(Label("Макс. пт"));
                Points.Width = 56;
                fontRow.Children.Add(Points);
                parent.Children.Add(fontRow);
                var faceRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
                Bold.Margin = Italic.Margin = new Thickness(4, 4, 12, 4);
                faceRow.Children.Add(Bold);
                faceRow.Children.Add(Italic);
                parent.Children.Add(faceRow);
                var colorRow = new WrapPanel();
                colorRow.Children.Add(Label("Цвет"));
                Fill.Width = 88;
                colorRow.Children.Add(Fill);
                colorRow.Children.Add(Button("●", (_, __) => ChooseColor(Fill)));
                colorRow.Children.Add(Label("Обводка"));
                Outline.Width = 88;
                colorRow.Children.Add(Outline);
                colorRow.Children.Add(Button("●", (_, __) => ChooseColor(Outline)));
                parent.Children.Add(colorRow);
                var widthRow = new WrapPanel();
                widthRow.Children.Add(Label("Ширина обводки, мм (0 — нет)"));
                OutlineWidth.Width = 60;
                widthRow.Children.Add(OutlineWidth);
                parent.Children.Add(widthRow);
                foreach (TextBox field in new[] { Font, Points, Fill, Outline, OutlineWidth })
                    field.TextChanged += (_, __) => changed();
                Bold.Checked += (_, __) => changed();
                Bold.Unchecked += (_, __) => changed();
                Italic.Checked += (_, __) => changed();
                Italic.Unchecked += (_, __) => changed();
            }

            private void ChooseFont()
            {
                using (var dialog = new Forms.FontDialog { ShowColor = false, ShowEffects = true })
                {
                    try
                    {
                        dialog.Font = new Drawing.Font(Font.Text,
                            float.Parse(Points.Text.Replace(',', '.'), CultureInfo.InvariantCulture),
                            (Bold.IsChecked == true ? Drawing.FontStyle.Bold : 0) |
                            (Italic.IsChecked == true ? Drawing.FontStyle.Italic : 0));
                    }
                    catch { }
                    if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                    Font.Text = dialog.Font.FontFamily.Name;
                    Points.Text = dialog.Font.SizeInPoints.ToString("0.#", CultureInfo.InvariantCulture);
                    Bold.IsChecked = dialog.Font.Bold;
                    Italic.IsChecked = dialog.Font.Italic;
                }
            }

            private static void ChooseColor(TextBox target)
            {
                using (var dialog = new Forms.ColorDialog { FullOpen = true })
                {
                    try { dialog.Color = Drawing.ColorTranslator.FromHtml(target.Text); }
                    catch { }
                    if (dialog.ShowDialog() == Forms.DialogResult.OK)
                        target.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
                }
            }

            public SportTextStyle Read(string label)
            {
                string fill = NormalizeColor(Fill.Text, label + ": цвет");
                string outline = NormalizeColor(Outline.Text, label + ": обводка");
                if (String.IsNullOrWhiteSpace(Font.Text))
                    throw new InvalidOperationException(label + ": выберите шрифт.");
                return new SportTextStyle { FontName = Font.Text.Trim(),
                    MaxPoints = (float)ParseRange(Points.Text, 1, 500, label + ": размер, пт"),
                    Bold = Bold.IsChecked == true, Italic = Italic.IsChecked == true,
                    FillHex = fill, OutlineHex = outline,
                    OutlineWidthMm = ParseRange(OutlineWidth.Text, 0, 10,
                        label + ": ширина обводки, мм") };
            }

            public void Load(SportTextStyle style)
            {
                if (style == null) return;
                Font.Text = style.FontName ?? "Arial";
                Points.Text = style.MaxPoints > 0
                    ? style.MaxPoints.ToString("0.#", CultureInfo.InvariantCulture) : "72";
                Bold.IsChecked = style.Bold;
                Italic.IsChecked = style.Italic;
                Fill.Text = style.FillHex ?? "#000000";
                Outline.Text = style.OutlineHex ?? "#000000";
                OutlineWidth.Text = style.OutlineWidthMm.ToString("0.##",
                    CultureInfo.InvariantCulture);
            }

            private static string NormalizeColor(string value, string label)
            {
                string hex = (value ?? "").Trim().TrimStart('#');
                if (hex.Length != 6 || !Int32.TryParse(hex, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out _))
                    throw new InvalidOperationException(label + " — укажите #RRGGBB.");
                return "#" + hex.ToUpperInvariant();
            }
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
        private readonly TextBox _frontArt, _backArt, _leftSleeveArt, _rightSleeveArt;
        private readonly TextBox _sheetWidth, _gap;
        private readonly ComboBox _nameSide;
        private readonly TextStyleEditor _nameStyle, _numberStyle;
        private readonly CheckBox _keepGuides;
        private readonly TextBlock _count, _progress;
        private readonly Button _prepare, _generate;
        private bool _loading, _busy;

        public SportLayoutTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(7) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel };
            panel.Children.Add(Title("Раскладка спортформы"));
            panel.Children.Add(Note("Сначала подготовьте страницу-макет и поправьте рисунки " +
                "под видимыми контурами. Затем создайте комплекты и раскладку."));

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

            panel.Children.Add(Title("2. Рисунки для деталей"));
            panel.Children.Add(Note("Выберите файл или выделите готовый рисунок в Corel и нажмите " +
                "«Взять выделение» для нужной детали. На странице-макете рисунок " +
                "лежит под контуром; выступающие края обрежутся при генерации. " +
                "Выделение снимается в PNG 300 DPI; исходные объекты не меняются."));
            _frontArt = ArtworkField(panel, "Перед", "переда");
            _backArt = ArtworkField(panel, "Спина", "спины");
            _leftSleeveArt = ArtworkField(panel, "Левый рукав (верхний на шаблоне)", "левого рукава");
            _rightSleeveArt = ArtworkField(panel, "Правый рукав (нижний, необязательно)", "правого рукава");
            panel.Children.Add(Note("Если рисунок правого рукава не выбран, берётся рисунок левого."));
            _prepare = DockerTheme.Primary(Button("Этап 1 · Подготовить макет", Prepare));
            panel.Children.Add(_prepare);
            panel.Children.Add(Note("Кнопка создаёт отдельные страницы-макеты для всех размеров таблицы. " +
                "На каждой странице можно двигать и масштабировать рисунки, контуры и красные зоны " +
                "имени и номера. Повторное нажатие создаст новые макеты, а прежние сохранит отдельно. " +
                "Не переименовывайте страницу-макет и именованные объекты."));
            panel.Children.Add(Note("Можно нарисовать новый прямоугольник на странице-макете, " +
                "выделить его и назначить боксом нужной надписи. Старый бокс этой надписи заменится."));
            var zoneButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            zoneButtons.Children.Add(Button("Бокс груди слева", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_FRONT_RIGHT", "имени на груди слева")));
            zoneButtons.Children.Add(Button("Бокс груди справа", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_FRONT_LEFT", "имени на груди справа")));
            zoneButtons.Children.Add(Button("Бокс имени на спине", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_BACK", "имени на спине")));
            zoneButtons.Children.Add(Button("Бокс номера на спине", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_BACK_NUMBER", "номера на спине")));
            panel.Children.Add(zoneButtons);

            panel.Children.Add(Title("3. Игроки и допечатка"));
            panel.Children.Add(Note("Столбцы: размер, имя, номер, количество. Можно вставить строки из Excel. " +
                "Кнопка ↗ в строке создаёт одну выбранную деталь для допечатки."));
            _playerGrid = new DataGrid { Height = 224, AutoGenerateColumns = false,
                CanUserAddRows = false, CanUserDeleteRows = false, FontSize = 12,
                Margin = new Thickness(0, 3, 0, 4), ItemsSource = _players };
            _playerGrid.Columns.Add(Column("Размер", "Size", 70));
            _playerGrid.Columns.Add(Column("Имя", "Name", 185));
            _playerGrid.Columns.Add(Column("№", "Number", 60));
            _playerGrid.Columns.Add(Column("Шт.", "Quantity", 55));
            _playerGrid.Columns.Add(PartColumn("Перед", SportLayoutPart.Front));
            _playerGrid.Columns.Add(PartColumn("Спина", SportLayoutPart.Back));
            _playerGrid.Columns.Add(PartColumn("Лев. рук.", SportLayoutPart.LeftSleeve));
            _playerGrid.Columns.Add(PartColumn("Прав. рук.", SportLayoutPart.RightSleeve));
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

            panel.Children.Add(Title("4. Полотно и надписи"));
            var roll = new WrapPanel { Margin = new Thickness(0, 3, 0, 6) };
            roll.Children.Add(Label("Ширина, мм"));
            _sheetWidth = Field("1500");
            _sheetWidth.Width = 72;
            roll.Children.Add(_sheetWidth);
            roll.Children.Add(Label("Между деталями, мм"));
            _gap = Field("10");
            _gap.Width = 60;
            roll.Children.Add(_gap);
            panel.Children.Add(roll);
            panel.Children.Add(Note("Автораскладка уплотняет габариты деталей без поворота рисунка; " +
                "длина страницы определяется автоматически. Для этапа 2 берутся текущие " +
                "положения рисунков и зон со страницы-макета; после замены исходного файла " +
                "рисунка подготовьте макет заново."));
            _nameStyle = new TextStyleEditor(panel, "Имя спереди и на спине", "72", SaveDraft);
            _numberStyle = new TextStyleEditor(panel, "Номер на спине", "150", SaveDraft);
            panel.Children.Add(Note("Максимальный размер текста ограничен указанным кеглем и нарисованным " +
                "боксом. Текст пропорционально уменьшается по ширине или высоте — по той границе, " +
                "которая достигнута первой. Боксы можно двигать и менять на странице каждого размера."));

            var settings = new WrapPanel { Margin = new Thickness(0, 7, 0, 6) };
            settings.Children.Add(Label("Имя спереди (сторона футболки)"));
            _nameSide = new ComboBox { Width = 110, Height = 27, FontSize = 12,
                Margin = new Thickness(4, 0, 12, 0) };
            _nameSide.Items.Add("Слева");
            _nameSide.Items.Add("Справа");
            _nameSide.SelectedIndex = 0;
            settings.Children.Add(_nameSide);
            panel.Children.Add(settings);
            _keepGuides = new CheckBox { Content = "Оставить красные рамки в готовом CDR",
                FontSize = 11, Margin = new Thickness(0, 0, 0, 8), IsChecked = false };
            panel.Children.Add(_keepGuides);
            panel.Children.Add(Note("Свободная зона переда и зоны рукавов сохраняются как именованные " +
                "фреймы для дополнительной графики. Подписи каждой детали находятся в нижнем припуске."));

            _generate = DockerTheme.Primary(Button("Этап 2 · Создать комплекты и раскладку", Generate));
            _generate.Margin = new Thickness(0, 7, 0, 4);
            panel.Children.Add(_generate);
            _progress = Note("");
            panel.Children.Add(_progress);

            _templates.CollectionChanged += (_, __) => SaveDraft();
            _players.CollectionChanged += (_, __) => { UpdateCount(); SaveDraft(); };
            _templateGrid.CellEditEnding += (_, __) => SaveSoon();
            _playerGrid.CellEditEnding += (_, __) => SaveSoon();
            _frontArt.TextChanged += (_, __) => SaveDraft();
            _backArt.TextChanged += (_, __) => SaveDraft();
            _leftSleeveArt.TextChanged += (_, __) => SaveDraft();
            _rightSleeveArt.TextChanged += (_, __) => SaveDraft();
            _sheetWidth.TextChanged += (_, __) => SaveDraft();
            _gap.TextChanged += (_, __) => SaveDraft();
            _nameSide.SelectionChanged += (_, __) => SaveDraft();
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

        private TextBox ArtworkField(Panel panel, string title, string partName)
        {
            panel.Children.Add(Label(title));
            TextBox field = Field("");
            panel.Children.Add(PathRow(field, "Выбрать файл", (_, __) => BrowseArtwork(field)));
            panel.Children.Add(Button("Взять выделение из Corel", (_, __) =>
                CaptureArtwork(field, partName)));
            return field;
        }

        private static void BrowseArtwork(TextBox field)
        {
            var dialog = new OpenFileDialog { Filter =
                "Изображения (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|Все файлы (*.*)|*.*",
                CheckFileExists = true };
            if (dialog.ShowDialog() == true) field.Text = dialog.FileName;
        }

        private void CaptureArtwork(TextBox field, string partName)
        {
            string temporary = null;
            try
            {
                temporary = AiSelectionCapture.CaptureForPublish(300);
                string folder = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "sport-patterns");
                Directory.CreateDirectory(folder);
                string destination = Path.Combine(folder, "artwork-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                File.Move(temporary, destination);
                temporary = null;
                field.Text = destination;
                _status("Рисунок " + partName + " взят из выделения (300 DPI).", false);
            }
            catch (Exception error)
            {
                Log.Error("Could not capture selected sport artwork.", error);
                _status(error.Message, true);
            }
            finally
            {
                try { if (temporary != null) File.Delete(temporary); } catch { }
            }
        }

        private void AssignTextBox(string key, string label)
        {
            try
            {
                string page = new SportLayoutService().AssignSelectedTextBox(key);
                _status("Назначен бокс " + label + " на странице «" + page + "».", false);
            }
            catch (Exception error)
            {
                Log.Error("Could not assign sport text box.", error);
                _status(error.Message, true);
            }
        }

        private async void Prepare(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!_templateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_templateGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                _status("Закончите редактирование шаблона.", true);
                return;
            }
            var templates = _templates.ToList();
            if (templates.Count == 0) { _status("Добавьте шаблоны лекал.", true); return; }
            _busy = true;
            _prepare.IsEnabled = _generate.IsEnabled = false;
            try
            {
                SportLayoutOptions options = ReadOptions();
                var sizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var template in templates)
                {
                    string size = SportLayoutService.NormalizeSize(template.Size);
                    if (size.Length == 0 || !sizes.Add(size))
                        throw new InvalidOperationException("Укажите разные размеры в таблице лекал.");
                    if (String.IsNullOrWhiteSpace(template.Path) || !File.Exists(template.Path))
                        throw new InvalidOperationException("Не найден шаблон размера " + size + ".");
                }
                SaveDraft();
                dynamic originalPage = CorelApp.Get().ActiveDocument.ActivePage;
                var prepared = new List<string>();
                var service = new SportLayoutService();
                foreach (var template in templates)
                {
                    originalPage.Activate();
                    _progress.Text = "Подготавливаю " + (prepared.Count + 1) + " из " +
                        templates.Count + ": " + template.Size + "…";
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    prepared.Add(service.PrepareDesign(template, options));
                }
                _progress.Text = "Готовы макеты " + String.Join(", ", prepared) +
                    ". Поправьте каждый размер на его странице, затем запустите этап 2.";
                _status("Страницы-макеты всех размеров готовы для ручной правки.", false);
            }
            catch (Exception error)
            {
                Log.Error("Sport layout master preparation failed.", error);
                _progress.Text = "Макет не подготовлен.";
                _status(error.Message, true);
            }
            finally { _busy = false; _prepare.IsEnabled = _generate.IsEnabled = true; }
        }

        private async void GenerateReplacement(object sender, RoutedEventArgs e,
            SportLayoutPart part)
        {
            if (_busy) return;
            if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
                !_templateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_templateGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                _status("Закончите редактирование таблицы.", true);
                return;
            }
            var row = (sender as FrameworkElement)?.DataContext as SportLayoutPlayer;
            if (row == null) { _status("Не выбрана строка игрока.", true); return; }
            _busy = true;
            _prepare.IsEnabled = _generate.IsEnabled = false;
            try
            {
                SportLayoutOptions options = ReadOptions();
                SaveDraft();
                _progress.Text = "Готовлю деталь для допечатки: " + row.Name + "…";
                await Dispatcher.Yield(DispatcherPriority.Background);
                SportLayoutResult result = new SportLayoutService().GenerateReplacementPiece(
                    row, _templates.ToList(), options, part);
                _progress.Text = "Деталь для допечатки на новой странице " +
                    result.SheetHeightMm.ToString("0.#") + " × " +
                    options.SheetWidthMm.ToString("0.#") + " мм. Сохраните CDR.";
                _status("Деталь " + row.Name + " подготовлена для допечатки.", false);
            }
            catch (Exception error)
            {
                Log.Error("Sport replacement piece generation failed.", error);
                _progress.Text = "Деталь не подготовлена.";
                _status(error.Message, true);
            }
            finally { _busy = false; _prepare.IsEnabled = _generate.IsEnabled = true; }
        }

        private async void Generate(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
                !_templateGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_templateGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                _status("Закончите редактирование таблицы и проверьте значения.", true);
                return;
            }
            SaveDraft();
            _busy = true;
            _prepare.IsEnabled = _generate.IsEnabled = false;
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
                var options = ReadOptions();
                _progress.Text = "Готовлю новую страницу текущего CDR…";
                await Dispatcher.Yield(DispatcherPriority.Background);
                SportLayoutResult result = new SportLayoutService().GenerateInCurrentDocument(
                    players, templates, options, (done, total, label) =>
                        _progress.Text = "Подготовлено " + done + " из " + total + ": " + label);
                _progress.Text = "Готово: " + result.GarmentCount + " комплектов, " +
                    result.PieceCount + " деталей на странице " +
                    result.SheetHeightMm.ToString("0.#") + " × " +
                    options.SheetWidthMm.ToString("0.#") + " мм. Сохраните CDR.";
                _status("Раскладка добавлена в текущий CDR. Сохраните документ.", false);
            }
            catch (Exception error)
            {
                Log.Error("Sport layout generation failed.", error);
                _progress.Text = "Раскладка не завершена.";
                _status(error.Message, true);
            }
            finally { _busy = false; _prepare.IsEnabled = _generate.IsEnabled = true; }
        }

        private SportLayoutOptions ReadOptions()
        {
            return new SportLayoutOptions {
                FrontArtworkPath = _frontArt.Text.Trim(),
                BackArtworkPath = _backArt.Text.Trim(),
                LeftSleeveArtworkPath = _leftSleeveArt.Text.Trim(),
                RightSleeveArtworkPath = _rightSleeveArt.Text.Trim(),
                SheetWidthMm = ParseMillimeters(_sheetWidth.Text, 300, 2000, "Ширина полотна"),
                GapMm = ParseMillimeters(_gap.Text, 0, 100, "Расстояние между деталями"),
                NameStyle = _nameStyle.Read("Имя"),
                NumberStyle = _numberStyle.Read("Номер"),
                FrontNameOnRight = _nameSide.SelectedIndex == 1,
                WhiteText = _nameStyle.Fill.Text.Trim().Equals("#FFFFFF",
                    StringComparison.OrdinalIgnoreCase),
                KeepGuides = _keepGuides.IsChecked == true
            };
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
            if (_loading || _frontArt == null || _backArt == null ||
                _leftSleeveArt == null || _rightSleeveArt == null ||
                _sheetWidth == null || _gap == null || _nameStyle == null ||
                _numberStyle == null || _nameSide == null || _keepGuides == null ||
                _templateGrid == null || _playerGrid == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DraftPath));
                var draft = new Draft {
                    Templates = _templates.ToList(), Players = _players.ToList(),
                    FrontArtworkPath = _frontArt.Text, BackArtworkPath = _backArt.Text,
                    LeftSleeveArtworkPath = _leftSleeveArt.Text,
                    RightSleeveArtworkPath = _rightSleeveArt.Text,
                    SheetWidth = _sheetWidth.Text, Gap = _gap.Text,
                    NameStyle = _nameStyle.Read("Имя"),
                    NumberStyle = _numberStyle.Read("Номер"),
                    FrontNameOnRight = _nameSide.SelectedIndex == 1,
                    WhiteText = _nameStyle.Fill.Text.Trim().Equals("#FFFFFF",
                        StringComparison.OrdinalIgnoreCase),
                    KeepGuides = _keepGuides.IsChecked == true
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
                        _frontArt.Text = draft.FrontArtworkPath ?? draft.PatternPath ?? "";
                        _backArt.Text = draft.BackArtworkPath ?? draft.PatternPath ?? "";
                        _leftSleeveArt.Text = draft.LeftSleeveArtworkPath ?? draft.PatternPath ?? "";
                        _rightSleeveArt.Text = draft.RightSleeveArtworkPath ?? "";
                        _sheetWidth.Text = draft.SheetWidth ?? "1500";
                        _gap.Text = draft.Gap ?? "10";
                        if (draft.NameStyle != null) _nameStyle.Load(draft.NameStyle);
                        else _nameStyle.Load(new SportTextStyle { FontName = draft.FontName ?? "Arial",
                            MaxPoints = 72, FillHex = draft.WhiteText ? "#FFFFFF" : "#000000",
                            OutlineHex = "#000000" });
                        if (draft.NumberStyle != null) _numberStyle.Load(draft.NumberStyle);
                        else _numberStyle.Load(new SportTextStyle { FontName = draft.FontName ?? "Arial",
                            MaxPoints = 150, FillHex = draft.WhiteText ? "#FFFFFF" : "#000000",
                            OutlineHex = "#000000" });
                        _nameSide.SelectedIndex = draft.FrontNameOnRight ? 1 : 0;
                        _keepGuides.IsChecked = draft.KeepGuides;
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

        private DataGridTemplateColumn PartColumn(string header, SportLayoutPart part)
        {
            var button = new FrameworkElementFactory(typeof(System.Windows.Controls.Button));
            button.SetValue(System.Windows.Controls.Button.ContentProperty, "↗");
            button.SetValue(System.Windows.Controls.Button.ToolTipProperty,
                "Создать одну деталь: " + header);
            button.SetValue(System.Windows.Controls.Button.MinHeightProperty, 23.0);
            button.SetValue(System.Windows.Controls.Button.PaddingProperty,
                new Thickness(4, 0, 4, 0));
            button.AddHandler(System.Windows.Controls.Button.ClickEvent,
                new RoutedEventHandler((sender, e) => GenerateReplacement(sender, e, part)));
            return new DataGridTemplateColumn { Header = header,
                CellTemplate = new DataTemplate { VisualTree = button },
                Width = new DataGridLength(80) };
        }

        private static double ParseMillimeters(string text, double minimum,
            double maximum, string label)
        {
            return ParseRange(text, minimum, maximum, label + ", мм");
        }

        private static double ParseRange(string text, double minimum,
            double maximum, string label)
        {
            if (Double.TryParse((text ?? "").Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double width) &&
                !Double.IsNaN(width) && !Double.IsInfinity(width) &&
                width >= minimum && width <= maximum) return width;
            throw new InvalidOperationException(label + ": от " + minimum +
                " до " + maximum + ".");
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
