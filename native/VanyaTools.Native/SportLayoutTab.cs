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
using System.Windows.Input;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal sealed partial class SportLayoutTab : UserControl
    {
        private sealed class Draft
        {
            public int ProjectStorageVersion { get; set; }
            public List<SportLayoutTemplate> Templates { get; set; }
            public List<SportLayoutPlayer> Players { get; set; }
            public string PatternPath { get; set; }
            public string FrontArtworkPath { get; set; }
            public string BackArtworkPath { get; set; }
            public string LeftSleeveArtworkPath { get; set; }
            public string RightSleeveArtworkPath { get; set; }
            public string NeckArtworkPath { get; set; }
            public string ModelPath { get; set; }
            public int? ArtworkMaxMegapixels { get; set; }
            public bool RasterArtworkCapture { get; set; }
            public bool? RotateSleeves { get; set; }
            public bool? CutAllow180 { get; set; }
            public bool? CutSizeTags { get; set; }
            public string TagWidth { get; set; }
            public string TagHeight { get; set; }
            public string TagBridge { get; set; }
            public string TagLetter { get; set; }
            public string TagLabelHex { get; set; }
            public bool? CutTagLetter { get; set; }
            public string TagFirstOffset { get; set; }
            public string TagSizeStep { get; set; }
            public string SheetWidth { get; set; }
            public string CutSheetWidth { get; set; }
            public string MaxPageLength { get; set; }
            public string CutMaxPageLength { get; set; }
            public string Gap { get; set; }
            public string CutGap { get; set; }
            public string PrintFolder { get; set; }
            public string CutFolder { get; set; }
            public bool CutDxfSingleColor { get; set; }
            public string CutDxfColorHex { get; set; }
            public int PrintDpi { get; set; }
            public string FontName { get; set; }
            public bool FrontNameOnRight { get; set; }
            public int FrontNameCase { get; set; }
            public bool FrontNameTwoLines { get; set; }
            public string FrontLineSpacing { get; set; }
            public bool WhiteText { get; set; }
            public SportTextStyle NameStyle { get; set; }
            public SportTextStyle NumberStyle { get; set; }
            public bool KeepGuides { get; set; }
            public int WorkflowTab { get; set; }
            public int? WorkflowUiVersion { get; set; }
            public int? NestingMode { get; set; }
            public int? GroupingMode { get; set; }
            public int? CutNestingMode { get; set; }
            public int? CutGroupingMode { get; set; }
            public int PressMode { get; set; }
            public int? PressGrouping { get; set; }
            public int? PressOrientation { get; set; }
            public double? PressSheetWidthMm { get; set; }
            public double? PressLeftMm { get; set; }
            public double? PressRightMm { get; set; }
            public double? PressTopMm { get; set; }
            public double? PressBottomMm { get; set; }
            public bool? PressBorders { get; set; }
        }

        private static SportTextStyle LegacyStyle(string font, float points, bool white)
        {
            return new SportTextStyle { FontName = String.IsNullOrWhiteSpace(font) ? "Arial" : font,
                MaxPoints = points, FillHex = white ? "#FFFFFF" : "#000000", OutlineHex = "#000000",
                Bold = false, Italic = false, OutlineWidthMm = 0 };
        }

        private static readonly string DraftPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "sport-layout-draft.json");

        private readonly Action<string, bool> _status;
        private readonly ObservableCollection<SportLayoutTemplate> _templates =
            new ObservableCollection<SportLayoutTemplate>();
        private readonly ObservableCollection<string> _sizeOptions =
            new ObservableCollection<string>();
        private readonly ObservableCollection<SportLayoutPlayer> _players =
            new ObservableCollection<SportLayoutPlayer>();
        private readonly Dictionary<SportLayoutPart, CheckBox> _partHeaderChecks = new Dictionary<SportLayoutPart, CheckBox>();
        private readonly DataGrid _templateGrid, _playerGrid;
        private readonly TextBox _frontArt, _backArt, _leftSleeveArt, _rightSleeveArt,
            _neckArt, _modelPath;
        private readonly TextBox _sheetWidth, _cutSheetWidth, _maxPageLength, _gap,
            _cutMaxPageLength, _cutGap, _printFolder, _cutFolder;
        private readonly TextBox _tagWidth, _tagHeight, _tagBridge, _tagLetter, _tagFirstOffset, _tagSizeStep, _tagLabelHex;
        private readonly Button _tagLabelColor;
        private readonly ComboBox _nameSide, _frontCase, _batchSize, _printQuality;
        private readonly ComboBox _artworkLimit, _artworkCapture;
        private readonly ComboBox _nestingMode, _groupingMode, _cutNestingMode, _cutGroupingMode;
        private readonly TextBlock _pdfProgress;
        // Kept only for older masters with rectangle zones; text samples use Corel attributes.
        private SportTextStyle _legacyNameStyle = LegacyStyle("Arial", 72, false);
        private SportTextStyle _legacyNumberStyle = LegacyStyle("Arial", 150, false);
        private bool _legacyTwoLines, _whiteCaptions;
        private double _legacyLineSpacing = 100;
        private int _pressMode;
        private int _pressGrouping, _pressOrientation = 1;
        private double _pressSheetWidthMm = 750;
        private double _pressLeftMm = 20, _pressRightMm = 20, _pressTopMm = 20, _pressBottomMm = 20;
        private bool _pressBorders = true;
        private readonly CheckBox _keepGuides, _rotateSleeves, _cutAllow180, _cutSizeTags, _tagCutLetter;
        private readonly TextBlock _count, _progress;
        private readonly Button _prepare, _generate, _generateCut, _saveCut, _exportPrint, _choosePrintFolder;
        private readonly Button _exportCut, _chooseCutFolder, _combineCut;
        private readonly CheckBox _cutDxfSingleColor;
        private readonly TextBox _cutDxfColorHex;
        private readonly Button _cutDxfColor;
        private readonly Button _tagSamples;
        private readonly Button _expandPlayers;
        private readonly Grid _playerEditor;
        private Window _playerWindow;
        private bool _loading, _busy;
        private static readonly string[] StepNames = { "Лекала", "Рисунки", "Игроки", "Печать", "Резка" };
        private static readonly int[] StepStates = { 0, 1, 2, 3, 5 };
        private readonly TabControl _steps;
        private readonly TabItem[] _stepTabs = new TabItem[5];
        private readonly TextBlock[] _stepMarks = new TextBlock[5];
        private readonly bool[] _completedSteps = new bool[6];
        private readonly string[] _stepDetails = new string[6];
        private string[] _stepInputs;
        private readonly DispatcherTimer _documentWatch = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(1) };
        private string _workflowDocument, _workflowSession, _workflowId, _savedWorkflow;
        private object _projectDocument;
        private string _savedProjectPlayers;
        private bool _switchingPlayers;

        public SportLayoutTab(Action<string, bool> status)
        {
            _status = status;
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            var intro = new StackPanel { Margin = new Thickness(7, 0, 7, 3) };
            intro.Children.Add(Title("Раскладка спортформы"));
            DockerHelp.Add(intro, "Сначала заполните таблицу игроков, назначьте размеры и отметьте кого печатать. " +
                "Подготовьте страницу-макет и поправьте рисунки " +
                "под видимыми контурами. Затем создайте комплекты и раскладку. " +
                "Галочки на вкладках показывают назначенные лекала, заполненную таблицу и успешные действия в текущем CDR. " +
                "После изменения данных зависимые отметки снимаются. Отметки действий хранятся в самом CDR; " +
                "сохраните документ, чтобы оставить их после закрытия Corel. " +
                "PDF отмечает экспорт активного листа; при нескольких листах экспортируйте каждый отдельно.");
            root.Children.Add(intro);
            var panels = Enumerable.Range(0, 5).Select(_ => new StackPanel { Margin = new Thickness(7) }).ToArray();
            _steps = CreateSteps(panels);
            Grid.SetRow(_steps, 1); root.Children.Add(_steps);
            StackPanel panel = panels[0];

            panel.Children.Add(Title("Лекала по размерам"));
            _modelPath = Field("");
            _modelPath.IsReadOnly = true;
            panel.Children.Add(PathRow(_modelPath, "Выбрать модель CDR", ChooseLayeredModel));
            DockerHelp.Add(panel, "Новая модель: одна страница; поля переменной информации персональны для каждого размера на слое VT_ZONES_<размер>. " +
                "в каждом слое размера — группа VT_TOP с пятью именованными деталями. " +
                "Названия слоёв размеров станут вариантами в таблице игроков.");
            DockerHelp.Add(panel, "В группе VT_TOP: VT_FRONT, VT_BACK, VT_SLEEVE_LEFT, " +
                "VT_SLEEVE_RIGHT, VT_NECK. На персональном слое полей: VT_SPORT_ZONE_FRONT_LEFT, " +
                "VT_SPORT_ZONE_FRONT_RIGHT, VT_SPORT_ZONE_BACK, " +
                "VT_SPORT_ZONE_BACK_NUMBER, VT_SPORT_ZONE_LEFT_SLEEVE, " +
                "VT_SPORT_ZONE_RIGHT_SLEEVE; VT_SPORT_ZONE_NECK — по желанию.");
            _templateGrid = new DataGrid { Height = 132, AutoGenerateColumns = false,
                CanUserAddRows = false, CanUserDeleteRows = false, IsReadOnly = false, FontSize = 12,
                Margin = new Thickness(0, 3, 0, 4), ItemsSource = _templates };
            _templateGrid.Columns.Add(Column("Размер", "Size", 80));
            _templateGrid.Columns.Add(Column("Слой", "LayerName", 110));
            _templateGrid.Columns.Add(Column("Масштаб %", "ScalePercent", 85));
            _templateGrid.Columns.Add(Column("Файл CDR", "Path", 290));
            panel.Children.Add(_templateGrid);
            panel.Children.Add(Button("Создать персональные поля в модели", (_, __) =>
            {
                if (_busy) return;
                try { _status(new SportLayoutService().CreatePersonalSizeZones(), false); }
                catch (Exception error) { Log.Error("Could not initialize personal size zones.", error); _status(error.Message, true); }
            }));
            DockerHelp.Add(panel, "Откройте исходную страницу модели в Corel. Кнопка создаёт отдельный слой VT_ZONES_S, VT_ZONES_M и т. д. " +
                "с независимыми копиями полей; существующие персональные слои сохраняются. Настройте положение и шрифты каждого размера и сохраните CDR модели. " +
                "Этап 1 берёт только его поля. Общий VT_ZONES служит запасным вариантом для старых моделей без персональных слоёв. " +
                "Удаление поля с персонального слоя отключает его для этого размера; пустой персональный слой отключает все его поля. " +
                "Готовые макеты не перезаписываются: их поля редактируйте на слое размера либо удалите слой размера и повторите этап 1.");
            var templateButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            templateButtons.Children.Add(Button("Добавить шаблон CDR", AddTemplate));
            templateButtons.Children.Add(Button("Сделать S и XL из М", AddApproximateSizes));
            templateButtons.Children.Add(Button("Убрать выбранный", (_, __) =>
            {
                if (_templateGrid.SelectedItem is SportLayoutTemplate item) _templates.Remove(item);
                SaveDraft();
            }));
            panel.Children.Add(templateButtons);
            DockerHelp.Add(panel, "Для размера М: перед, спина, верхний левый рукав и нижний правый. " +
                "Временные S и XL масштабируются целиком; 95% и 110% можно изменить. " +
                "Позже укажите отдельные файлы CDR и масштаб 100%.");

            panel = panels[1];
            panel.Children.Add(Title("Рисунки и подготовка макетов"));
            DockerHelp.Add(panel, "Выберите файл или выделите готовый рисунок в Corel и нажмите " +
                "«Взять выделение» для нужной детали. На странице-макете рисунок " +
                "лежит под контуром; выступающие края обрежутся при генерации. " +
                "По умолчанию копируются сами объекты в CDR: текст, кривые, заливки и группы остаются редактируемыми. " +
                "Встроенные растры сохраняются растрами; автоматической трассировки нет. Исходный рисунок не меняется.");
            _artworkCapture = new ComboBox { MinHeight = 27, ItemsSource = new[] {
                "Объекты Corel · редактируемые", "PNG · снимок 300 DPI" }, SelectedIndex = 0 };
            panel.Children.Add(DockerLayout.FieldRow("Из выделения Corel", _artworkCapture, 0));
            _artworkLimit = new ComboBox { Width = 90, MinHeight = 27, FontSize = 12,
                Margin = new Thickness(3, 0, 0, 0), ItemsSource = new[] { 100, 150, 200, 250 }, SelectedItem = 150 };
            var artworkLimitRow = DockerLayout.FieldRow("Предел захвата PNG, Мп", _artworkLimit, 94);
            artworkLimitRow.Visibility = Visibility.Collapsed; panel.Children.Add(artworkLimitRow);
            _artworkCapture.SelectionChanged += (_, __) => {
                artworkLimitRow.Visibility = _artworkCapture.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                SaveDraft();
            };
            DockerHelp.Add(panel, "Режим объектов сохраняет выделение отдельным CDR в папке рисунков плагина и не зависит от DPI или лимита мегапикселей. " +
                "Каждый размер получает независимую группу рисунка: выберите элемент внутри группы через Ctrl+щелчок и поправьте его. " +
                "Сохраняйте внешнюю группу рисунка детали, чтобы этап 2 использовал ваши изменения. " +
                "Для PNG по умолчанию 150 Мп. Для тяжёлых рисунков можно выбрать до 250 Мп. " +
                "Перед захватом проверяется свободная ОЗУ и доступная память Windows; " +
                "DPI автоматически не уменьшается. Предел относится к одному захвату, а не ко всей раскладке.");
            _frontArt = ArtworkField(panel, "Перед", "переда", SportLayoutPart.Front);
            _backArt = ArtworkField(panel, "Спина", "спины", SportLayoutPart.Back);
            _leftSleeveArt = ArtworkField(panel, "Левый рукав (верхний на шаблоне)",
                "левого рукава", SportLayoutPart.LeftSleeve);
            _rightSleeveArt = ArtworkField(panel, "Правый рукав (нижний, необязательно)",
                "правого рукава", SportLayoutPart.RightSleeve);
            _neckArt = ArtworkField(panel, "Горловина (для модели с пятью деталями)",
                "горловины", SportLayoutPart.Neck);
            DockerHelp.Add(panel, "Крестик убирает рисунок только у выбранной детали. " +
                "Пустое поле оставляет деталь без печатного рисунка; на готовой обзорной " +
                "странице рисунок убирается сразу. «Обновить в макетах» заменяет рисунок этой детали " +
                "только в уже подготовленных размерах отмеченных игроков. Ручные правки заменяемого рисунка будут сброшены; " +
                "лекала, текстовые образцы и подписи сохраняются. Замену можно отменить одним Ctrl+Z. " +
                "Для старых PNG возьмите исходный вектор заново и нажмите эту кнопку; сам PNG вектором не становится.");
            _prepare = DockerTheme.Primary(Button("Этап 1 · Подготовить / добавить размеры", Prepare));
            panel.Children.Add(_prepare);
            DockerHelp.Add(panel, "Этап 1 берёт только размеры строк с галочкой «Печатать» в таблице игроков. " +
                "При первом запуске комплекты располагаются по два размера в ряд, следующие пары ниже. " +
                "Повторный запуск добавляет только недостающие размеры на ту же страницу. " +
                "Готовые размеры и ваши правки сохраняются. Каждый размер остаётся на отдельном слое для ручной правки рисунков, " +
                "контуров, текстовых образцов и подписей в подгибке. Оформление и положение " +
                "подписей изменяйте в Corel. Чтобы пересоздать размер, удалите его слой на обзорной странице и повторите этап 1.");
            DockerHelp.Add(panel, "Исходный CDR модели остаётся открытым в отдельной вкладке. " +
                "Повторный запуск использует его; рабочий документ остаётся активным.");
            DockerHelp.Add(panel, "Создайте фигурный текст: XXXXXXXXX для имени, две строки X для " +
                "имени в две строки, XX или XXX для номера. Настройте его в Corel, выделите и назначьте " +
                "кнопкой ниже. Образец заменит прежний бокс только своего размера. " +
                "Кнопки работают и в готовом макете, и в исходной модели: разместите образец на слое размера или на его VT_ZONES_<размер>.");
            DockerHelp.Add(panel, "Ненужное текстовое поле можно удалить на слое размера: " +
                "например, номер правого рукава только для маленьких размеров. Этап 2 пропустит " +
                "удалённое поле. Текст XX на рукаве заменяется номером игрока.");
            var zoneButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            zoneButtons.Children.Add(Button("Имя груди слева", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_FRONT_LEFT", "имени на груди слева")));
            zoneButtons.Children.Add(Button("Имя груди справа", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_FRONT_RIGHT", "имени на груди справа")));
            zoneButtons.Children.Add(Button("Имя на спине", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_BACK", "имени на спине")));
            zoneButtons.Children.Add(Button("Номер на спине", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_BACK_NUMBER", "номера на спине")));
            zoneButtons.Children.Add(Button("Номер левого рукава", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_LEFT_SLEEVE", "номера левого рукава")));
            zoneButtons.Children.Add(Button("Номер правого рукава", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_RIGHT_SLEEVE", "номера правого рукава")));
            zoneButtons.Children.Add(Button("Номер горловины", (_, __) => AssignTextBox(
                "VT_SPORT_ZONE_NECK", "номера горловины")));
            panel.Children.Add(zoneButtons);

            panel = panels[2];
            panel.Children.Add(Title("Игроки и допечатка"));
            DockerHelp.Add(panel, "Скопируйте диапазон из Excel или Google Таблиц. Ctrl+V вставляет " +
                "его с выбранной ячейки; кнопка ниже добавляет строки в конец. Столбцы без " +
                "заголовка: размер, имя, номер, количество. Один столбец в пустой таблице " +
                "или через кнопку добавления — имена; размеры можно назначить после вставки. " +
                "Заголовки распознаются автоматически. " +
                "Кнопка ↗ создаёт выбранную деталь для допечатки.");
            _playerEditor = new Grid();
            _playerEditor.RowDefinitions.Add(new RowDefinition());
            _playerEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var playerControls = new StackPanel();
            Grid.SetRow(playerControls, 1); _playerEditor.Children.Add(playerControls);
            _playerGrid = new DataGrid { Height = 224, AutoGenerateColumns = false,
                CanUserAddRows = false, CanUserDeleteRows = false, IsReadOnly = false, FontSize = 12,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
                SelectionMode = DataGridSelectionMode.Extended,
                Margin = new Thickness(0, 3, 0, 4), ItemsSource = _players };
            _playerGrid.PreviewKeyDown += PlayerGridPasteKeyDown;
            DataObject.AddPastingHandler(_playerGrid, PlayerGridPasting);
            _playerGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Размер",
                ItemsSource = _sizeOptions, SelectedItemBinding = new Binding("Size") {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(85) });
            _playerGrid.Columns.Add(Column("Имя", "Name", 185));
            _playerGrid.Columns.Add(Column("№", "Number", 60));
            _playerGrid.Columns.Add(Column("Шт.", "Quantity", 55));
            var printCheck = new FrameworkElementFactory(typeof(CheckBox));
            printCheck.SetValue(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            printCheck.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            printCheck.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(SportLayoutPlayer.PrintEnabled)) {
                Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            _playerGrid.Columns.Add(new DataGridTemplateColumn { Header = "Печатать", Width = 75,
                CellTemplate = new DataTemplate { VisualTree = printCheck } });
            _playerGrid.Columns.Add(PartColumn("Перед", SportLayoutPart.Front));
            _playerGrid.Columns.Add(PartColumn("Спина", SportLayoutPart.Back));
            _playerGrid.Columns.Add(PartColumn("Лев. рук.", SportLayoutPart.LeftSleeve));
            _playerGrid.Columns.Add(PartColumn("Прав. рук.", SportLayoutPart.RightSleeve));
            _playerGrid.Columns.Add(PartColumn("Горловина", SportLayoutPart.Neck));
            _playerEditor.Children.Add(_playerGrid);
            DockerHelp.Add(playerControls, "Галочки в столбцах деталей задают только печать этой детали. " +
                "Снятые детали остаются в полном комплекте резки. Галочка в заголовке меняет эту деталь сразу у всех строк; " +
                "в ячейке — только у одного игрока. Общая галочка «Печатать» выбирает строку заказа для печати и резки. " +
                "Кнопка ↗ создаёт отдельную деталь для допечатки по вашему запросу, независимо от галочки детали.");
            var playerButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
            playerButtons.Children.Add(Button("Добавить строку", (_, __) =>
            {
                _players.Add(new SportLayoutPlayer { Size = _sizeOptions.FirstOrDefault() ?? "М",
                    Quantity = 1 });
                _playerGrid.SelectedIndex = _players.Count - 1;
                SaveDraft();
            }));
            playerButtons.Children.Add(Button("Добавить из Excel / Google Таблиц", PastePlayers));
            playerButtons.Children.Add(Button("Убрать строки", RemovePlayers));
            playerControls.Children.Add(playerButtons);
            var playerFiles = new WrapPanel();
            playerFiles.Children.Add(Button("Сохранить список CSV…", SavePlayersFile));
            playerFiles.Children.Add(Button("Загрузить список CSV…", LoadPlayersFile));
            playerControls.Children.Add(playerFiles);
            DockerHelp.Add(playerControls, "Таблица, галочки, модель, шаблоны, ссылки на рисунки и настройки формы сохраняются внутри текущего CDR. " +
                "При переключении файлов восстанавливаются их собственные данные. Сохраните CDR (Ctrl+S), чтобы записать изменения на диск. " +
                "CSV — отдельная копия списка для Блокнота или Excel.");
            var printButtons = new WrapPanel();
            printButtons.Children.Add(Button("Печатать всех", (_, __) => SetPlayersEnabled(true)));
            printButtons.Children.Add(Button("Снять все", (_, __) => SetPlayersEnabled(false)));
            _expandPlayers = Button("Открыть таблицу большим окном", OpenPlayersEditor);
            printButtons.Children.Add(_expandPlayers); playerControls.Children.Add(printButtons);
            var batchSizeRow = new StackPanel { Margin = new Thickness(0, 1, 0, 3) };
            batchSizeRow.Children.Add(Label("Размер выделенным"));
            _batchSize = new ComboBox { ItemsSource = _sizeOptions,
                Margin = new Thickness(2, 1, 4, 3), MinHeight = 27,
                ToolTip = "Выделите строки или ячейки игроков и выберите размер для них" };
            batchSizeRow.Children.Add(DockerLayout.Trailing(_batchSize, Button("Назначить", ApplySizeToSelection)));
            playerControls.Children.Add(batchSizeRow);
            DockerHelp.Add(playerControls, "Для группы одного размера выделите диапазон ячеек с Shift " +
                "или несколько строк с Ctrl, выберите размер выше и нажмите «Назначить».");
            DockerHelp.Add(playerControls, "Этап 2 и резка включают только строки с галочкой «Печатать». " +
                "Кнопка ↗ в строке по-прежнему создаёт одну деталь для допечатки.");
            _count = Note("");
            playerControls.Children.Add(_count);
            panel.Children.Add(_playerEditor);

            panel = panels[3];
            panel.Children.Add(Title("Полотно, надписи и печатная раскладка"));
            var roll = new StackPanel { Margin = new Thickness(0, 3, 0, 2) };
            _sheetWidth = Field("1500");
            _sheetWidth.Width = 72;
            roll.Children.Add(DockerLayout.FieldRow("Ширина печати, мм", _sheetWidth));
            _gap = Field("10");
            _gap.Width = 60;
            roll.Children.Add(DockerLayout.FieldRow("Между деталями, мм", _gap));
            panel.Children.Add(roll);
            _maxPageLength = Field("4"); _maxPageLength.Width = 72;
            panel.Children.Add(DockerLayout.FieldRow("Макс. длина страницы, м", _maxPageLength));
            panel.Children.Add(Label("Плотность раскладки"));
            _nestingMode = new ComboBox { MinHeight = 27, Margin = new Thickness(0, 0, 0, 4) };
            foreach (string label in new[] { "Прямоугольники · быстрее", "Упрощённые фигуры · баланс", "Полный контур · плотнее" })
                _nestingMode.Items.Add(label);
            _nestingMode.SelectedIndex = 2; panel.Children.Add(_nestingMode);
            DockerHelp.Add(panel, "Прямоугольники резервируют полные габариты. Упрощённые фигуры " +
                "используют наружные полосы по 20 мм; полный контур — профиль с шагом до 2 мм. " +
                "Упрощение увеличивает занятую область, не обрезая реальные детали. " +
                "Для печати и резки режим выбирается отдельно; изменение одной раскладки " +
                "не снимает отметку другой. Геометрия деталей не меняется.");
            panel.Children.Add(Label("Как держать детали заказа"));
            _groupingMode = new ComboBox { MinHeight = 27, Margin = new Thickness(0, 0, 0, 4) };
            foreach (string label in new[] { "Свободно · максимальное уплотнение", "Парами · перед + спина, затем рукава", "Комплектом · все пять деталей рядом" })
                _groupingMode.Items.Add(label);
            _groupingMode.SelectedIndex = 0; panel.Children.Add(_groupingMode);
            DockerHelp.Add(panel, "Парами: перед и спина одного комплекта укладываются единым блоком; " +
                "рукава и горловины заполняют свободные места на всех листах, не раздвигая пары. " +
                "Комплектом: все имеющиеся детали одного комплекта остаются одним блоком на одном листе. " +
                "Смежные блоки могут сближаться по контуру. Группировка может увеличить расход материала. " +
                "Если целый блок не помещается, увеличьте лист или выберите свободную раскладку.");
            _rotateSleeves = new CheckBox { Content = "Поворачивать рукава и горловину на 90° для плотной раскладки",
                FontSize = 11, IsChecked = true, Margin = new Thickness(0, 0, 0, 5) };
            panel.Children.Add(_rotateSleeves);
            DockerHelp.Add(panel, "Печать раскладывается по контуру контейнера с припуском +6 мм; " +
                "видимые подписи и рамки за контуром тоже занимают место. " +
                "Поиск перестановок заполняет свободные участки рядом с крупными деталями. " +
                "При разрешённом повороте весь рисунок детали поворачивается вместе с ней. " +
                "Страницы создаются в пределах заданной длины для печати и резки. " +
                "Сначала заполняются свободные места на предыдущих страницах; " +
                "нижний ряд по возможности выравнивается. Отступа от края полотна нет; " +
                "расстояние применяется только между деталями. Для этапа 2 берутся текущие " +
                "положения рисунков и зон со страницы-макета; после замены исходного файла " +
                "рисунка подготовьте макет заново.");
            DockerHelp.Add(panel, "У текстового образца берутся шрифт, кегль, начертание, цвет, " +
                "обводка, выравнивание и интервалы из Corel. Ширина образца ограничивает надпись: " +
                "длинный текст уменьшается пропорционально, короткий сохраняет кегль образца. " +
                "Оформление меняйте в самом текстовом образце на странице-макете.");
            _nameSide = new ComboBox { Width = 110, Height = 27, FontSize = 12,
                Margin = new Thickness(0) };
            _nameSide.Items.Add("Слева");
            _nameSide.Items.Add("Справа");
            _nameSide.SelectedIndex = 0;
            panel.Children.Add(DockerLayout.FieldRow("Имя спереди (сторона футболки)", _nameSide, 110));
            panel.Children.Add(Label("Регистр имени на груди"));
            _frontCase = new ComboBox { Height = 27, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4) };
            _frontCase.Items.Add("Как в таблице");
            _frontCase.Items.Add("все строчные");
            _frontCase.Items.Add("Каждое слово с заглавной");
            _frontCase.Items.Add("ВСЕ ПРОПИСНЫЕ");
            _frontCase.SelectedIndex = 0;
            panel.Children.Add(_frontCase);
            DockerHelp.Add(panel, "В текстовом образце две строки X задают две строки имени. " +
                "Перенос — после первого слова; символ | в таблице задаёт его вручную. " +
                "Имя и номер на спине назначаются отдельными текстовыми объектами.");
            _keepGuides = new CheckBox { Content = "Оставить красные рамки в готовом CDR",
                FontSize = 11, Margin = new Thickness(0, 0, 0, 8), IsChecked = false };
            panel.Children.Add(_keepGuides);
            DockerHelp.Add(panel, "Свободная зона переда и зоны рукавов сохраняются как именованные " +
                "фреймы для дополнительной графики. Подписи каждой детали находятся в нижнем припуске.");

            _generate = DockerTheme.Primary(Button("Подобрать раскладку печати…", Generate));
            _generate.Margin = new Thickness(0, 7, 0, 4);
            panel.Children.Add(_generate);
            panel.Children.Add(Title("Экспорт печати в PDF"));
            _printFolder = Field(""); _printFolder.IsReadOnly = true;
            var folderRow = new Grid { Margin = new Thickness(0, 2, 0, 5) };
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            folderRow.Children.Add(_printFolder);
            _choosePrintFolder = Button("Папка печати…", ChoosePrintFolder);
            Grid.SetColumn(_choosePrintFolder, 1); folderRow.Children.Add(_choosePrintFolder);
            panel.Children.Add(folderRow);
            _printQuality = new ComboBox { Margin = new Thickness(0, 0, 4, 5), MinHeight = 27 };
            _printQuality.Items.Add("PDF · компактный · изображения до 150 DPI");
            _printQuality.Items.Add("PDF · детальный · изображения до 300 DPI");
            _printQuality.SelectedIndex = 0;
            panel.Children.Add(_printQuality);
            _exportPrint = Button("Экспортировать активный лист печати в PDF", ExportPrint);
            panel.Children.Add(_exportPrint);
            DockerHelp.Add(panel, "Сохраняется активная страница после этапа 2, целиком и в масштабе 1:1. " +
                "Надписи остаются векторными; изображения сжимаются с высоким качеством. " +
                "При нескольких листах откройте нужный и повторите экспорт. Папка и качество запоминаются; " +
                "каждый экспорт создаёт новый файл.");
            _pdfProgress = Note(""); panel.Children.Add(_pdfProgress);
            panel = panels[4];
            panel.Children.Add(Title("Раскладка резки"));
            _cutSheetWidth = Field("1500"); _cutSheetWidth.Width = 72;
            panel.Children.Add(DockerLayout.FieldRow("Ширина ткани для резки, мм", _cutSheetWidth));
            _cutMaxPageLength = Field("4"); _cutMaxPageLength.Width = 72;
            panel.Children.Add(DockerLayout.FieldRow("Макс. длина листа резки, м", _cutMaxPageLength));
            _cutGap = Field("10"); _cutGap.Width = 60;
            panel.Children.Add(DockerLayout.FieldRow("Между деталями резки, мм", _cutGap));
            panel.Children.Add(Label("Плотность раскладки резки"));
            _cutNestingMode = CopyChoice(_nestingMode); panel.Children.Add(_cutNestingMode);
            panel.Children.Add(Label("Как держать детали заказа при резке"));
            _cutGroupingMode = CopyChoice(_groupingMode); panel.Children.Add(_cutGroupingMode);
            DockerHelp.Add(panel, "Размер листа, расстояние между деталями, плотность и группировка резки задаются независимо от печати. " +
                "Изменение этих настроек снимает только отметку резки; готовая печать и PDF сохраняют свои отметки.");
            DockerHelp.Add(panel, "В каждой группе детали нужен замкнутый контур silhouette. " +
                "Для печати она расширяется наружу на 6 мм; для резки берутся все исходные " +
                "кривые группы, включая silhouette и надсечки. Рисунки и надписи не добавляются. " +
                "Размеры и количество — из таблицы игроков. Ширина ткани, длина листа и зазор задаются на этой вкладке.");
            _cutAllow180 = new CheckBox { Content = "Разрешить поворот деталей резки на 180°",
                FontSize = 11, IsChecked = true, Margin = new Thickness(0, 0, 0, 5) };
            panel.Children.Add(_cutAllow180);
            _cutSizeTags = new CheckBox { Content = "Съёмный язычок на каждой детали",
                FontSize = 11, IsChecked = true, Margin = new Thickness(0, 0, 0, 5) };
            panel.Children.Add(_cutSizeTags);
            _tagWidth = TagField(panel, "Ширина язычка, мм", "24");
            _tagHeight = TagField(panel, "Высота язычка, мм (не меньше 12)", "12");
            _tagBridge = TagField(panel, "Длина каждого мостика, мм", "2");
            _tagLetter = TagField(panel, "Высота буквы, мм", "8");
            _tagCutLetter = new CheckBox { Content = "Прорезать размер внутри язычка", IsChecked = true,
                FontSize = 11, Margin = new Thickness(0, 1, 0, 4) };
            panel.Children.Add(_tagCutLetter);
            _tagLabelHex = new TextBox { Text = "#000000", FontSize = 11, MinHeight = 26,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            _tagLabelColor = new Button { Content = new Border { Width = 22, Height = 18,
                Background = Brushes.Black, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                Padding = new Thickness(4), ToolTip = "Выбрать цвет подписи язычка" };
            _tagLabelColor.Click += (_, __) => ChooseTagLabelColor();
            panel.Children.Add(DockerLayout.FieldRow("Цвет подписи язычка",
                DockerLayout.Trailing(_tagLabelHex, _tagLabelColor), 142));
            DockerHelp.Add(panel, "Подпись имеет отдельный цвет для назначения мощности в программе лазера. " +
                "Выберите цвет, отличающийся от контура детали. Используется в новой раскладке и на листе пробной резки.");
            var largerTag = Button("Пресет 30×20 мм · мостики 4 мм", (_, __) => {
                if (_busy) return;
                bool wasLoading = _loading; _loading = true;
                try { _tagWidth.Text = "30"; _tagHeight.Text = "20"; _tagBridge.Text = "4"; _tagLetter.Text = "8"; }
                finally { _loading = wasLoading; }
                SaveDraft();
                _status("Выбран пробный язычок 30×20 мм, мостики по 4 мм. Проверьте его на листе образцов.", false);
            });
            panel.Children.Add(largerTag);
            _tagFirstOffset = TagField(panel, "Отступ первого размера слева, мм", "10");
            _tagSizeStep = TagField(panel, "Шаг следующих размеров, мм", "20");
            DockerHelp.Add(panel, "П-образный язычок без ножек; два непрорезанных мостика на линии детали. " +
                "Надпись — однолинейные чертёжные знаки с трафаретными разрывами. " +
                "Для длинного размера ширина увеличивается, высота букв сохраняется. " +
                "Без прорезной буквы площадка остаётся цельной; размер определяется положением язычка. " +
                "Пресет 30×20 мм с мостиками 4 мм — кандидат для пробной резки, а не проверенный для любой ткани режим.");
            DockerHelp.Add(panel, "Левый угол крепления: 10, 30, 50 мм и далее от начала прямого нижнего края " +
                "до поворота в раскладке. Порядок размеров — от меньшей площади силуэта переда к большей " +
                "только среди размеров отмеченных игроков текущей резки. Отступ и шаг можно изменить. " +
                "Если на заданном месте нет свободного прямого нижнего края, появится сообщение с размером и деталью.");
            _tagSamples = Button("Лист язычков для пробной резки", CreateTagSamples);
            panel.Children.Add(_tagSamples);
            DockerHelp.Add(panel, "Сравнительный лист: 24×12 мм с мостиками 2 мм, 30×20 и 40×25 мм с мостиками 3 и 4 мм, " +
                "пары с буквой M высотой 8 мм и без буквы; также текущие настройки и примеры S, XS, XL, XXL, 116, 128, 152. " +
                "На самих образцах нет мелкого прорезного кода. Отдельная страница-памятка показывает размеры и положение образцов " +
                "в той же сетке. Сохраняйте порядок образцов при разборе. В DXF отправляется только страница резки, памятка остаётся в CDR.");
            _generateCut = Button("Подобрать раскладку резки…", GenerateCutting);
            panel.Children.Add(_generateCut);
            _combineCut = Button("Объединить контуры готового листа", CombineCutting);
            panel.Children.Add(_combineCut);
            DockerHelp.Add(panel, "Контур, язычок и надсечки каждой детали становятся одним объектом-кривой. " +
                "Буква остаётся отдельной внутри группы детали. Положение деталей не меняется; операция отменяется одним Undo.");
            _saveCut = Button("Сохранить страницу резки в отдельный CDR…", SaveCutting);
            panel.Children.Add(_saveCut);
            DockerHelp.Add(panel, "Раскладка резки создаётся на отдельной странице текущего CDR. " +
                "Сохранение копирует только эту страницу; версия CDR берётся из настроек Vanya Tools. " +
                "При нескольких страницах переключайтесь между листами раскладки и сохраняйте каждый отдельно. " +
                "Раскладка учитывает контуры деталей и заданный зазор. " +
                "Повороты подбираются по контуру: 0° или 180°. Поворота на 90° для резки нет.");
            panel.Children.Add(Title("Экспорт резки в DXF"));
            _cutFolder = Field(""); _cutFolder.IsReadOnly = true;
            var cutFolderRow = new Grid { Margin = new Thickness(0, 2, 0, 5) };
            cutFolderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cutFolderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cutFolderRow.Children.Add(_cutFolder);
            _chooseCutFolder = Button("Папка резки…", ChooseCutFolder);
            Grid.SetColumn(_chooseCutFolder, 1); cutFolderRow.Children.Add(_chooseCutFolder);
            panel.Children.Add(cutFolderRow);
            _cutDxfSingleColor = new CheckBox { Content = "Один цвет для контуров резки",
                IsChecked = false, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) };
            panel.Children.Add(_cutDxfSingleColor);
            _cutDxfColorHex = new TextBox { Text = "#000000", FontSize = 11, MinHeight = 26,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            _cutDxfColor = new Button { Content = new Border { Width = 22, Height = 18,
                Background = Brushes.Black, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                Padding = new Thickness(4), ToolTip = "Выбрать единый цвет контуров DXF" };
            _cutDxfColor.Click += (_, __) => ChooseCutDxfColor();
            panel.Children.Add(DockerLayout.FieldRow("Цвет контуров DXF",
                DockerLayout.Trailing(_cutDxfColorHex, _cutDxfColor), 142));
            DockerHelp.Add(panel, "Единый цвет применяется в DXF к контурам деталей, язычков и надсечкам. " +
                "Буквы на язычках сохраняют свой отдельный цвет подписи. " +
                "Цвета исходного CDR сохраняются. Без галочки экспортируются прежние цвета.");
            _exportCut = Button("Экспортировать активный лист резки в DXF", ExportCut);
            panel.Children.Add(_exportCut);
            DockerHelp.Add(panel, "DXF AutoCAD 2000 · миллиметры · масштаб 1:1. Экспортируется активный лист резки " +
                "целиком; каждая деталь с надсечками, язычком и буквой записывается одним блоком DXF. " +
                "Цвет буквы сохраняется отдельно. Папка запоминается; каждый экспорт создаёт новый файл. " +
                "При нескольких листах откройте нужный и повторите экспорт.");
            _progress = Note("");
            var progressView = new ScrollViewer { Content = _progress, MaxHeight = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(7, 3, 7, 0) };
            Grid.SetRow(progressView, 2); root.Children.Add(progressView);

            _templates.CollectionChanged += (_, e) => {
                if (e.OldItems != null) foreach (SportLayoutTemplate row in e.OldItems) row.PropertyChanged -= TemplateChanged;
                if (e.NewItems != null) foreach (SportLayoutTemplate row in e.NewItems) row.PropertyChanged += TemplateChanged;
                RefreshSizes(); SaveDraft(); };
            _players.CollectionChanged += (_, e) => {
                if (e.OldItems != null) foreach (SportLayoutPlayer row in e.OldItems) row.PropertyChanged -= PlayerChanged;
                if (e.NewItems != null) foreach (SportLayoutPlayer row in e.NewItems) row.PropertyChanged += PlayerChanged;
                UpdateCount(); SaveDraft(); };
            _templateGrid.CellEditEnding += (_, __) => SaveSoon();
            _playerGrid.CellEditEnding += (_, __) => SaveSoon();
            _frontArt.TextChanged += (_, __) => SaveDraft();
            _backArt.TextChanged += (_, __) => SaveDraft();
            _leftSleeveArt.TextChanged += (_, __) => SaveDraft();
            _rightSleeveArt.TextChanged += (_, __) => SaveDraft();
            _neckArt.TextChanged += (_, __) => SaveDraft();
            _modelPath.TextChanged += (_, __) => SaveDraft();
            _artworkLimit.SelectionChanged += (_, __) => SaveDraft();
            _nestingMode.SelectionChanged += (_, __) => SaveDraft();
            _groupingMode.SelectionChanged += (_, __) => SaveDraft();
            _cutNestingMode.SelectionChanged += (_, __) => SaveDraft();
            _cutGroupingMode.SelectionChanged += (_, __) => SaveDraft();
            _sheetWidth.TextChanged += (_, __) => SaveDraft();
            _cutSheetWidth.TextChanged += (_, __) => SaveDraft();
            _maxPageLength.TextChanged += (_, __) => SaveDraft();
            _cutMaxPageLength.TextChanged += (_, __) => SaveDraft();
            _cutGap.TextChanged += (_, __) => SaveDraft();
            _printFolder.TextChanged += (_, __) => SaveDraft();
            _cutFolder.TextChanged += (_, __) => SaveDraft();
            _cutDxfSingleColor.Checked += (_, __) => { UpdateCutDxfColor(); SaveDraft(); };
            _cutDxfSingleColor.Unchecked += (_, __) => { UpdateCutDxfColor(); SaveDraft(); };
            _cutDxfColorHex.TextChanged += (_, __) => { UpdateCutDxfColor(); SaveDraft(); };
            _printQuality.SelectionChanged += (_, __) => SaveDraft();
            _gap.TextChanged += (_, __) => SaveDraft();
            _nameSide.SelectionChanged += (_, __) => SaveDraft();
            _frontCase.SelectionChanged += (_, __) => SaveDraft();
            _keepGuides.Checked += (_, __) => SaveDraft();
            _keepGuides.Unchecked += (_, __) => SaveDraft();
            _rotateSleeves.Checked += (_, __) => SaveDraft();
            _rotateSleeves.Unchecked += (_, __) => SaveDraft();
            _cutAllow180.Checked += (_, __) => SaveDraft();
            _cutAllow180.Unchecked += (_, __) => SaveDraft();
            _cutSizeTags.Checked += (_, __) => SaveDraft();
            _cutSizeTags.Unchecked += (_, __) => SaveDraft();
            _tagCutLetter.Checked += (_, __) => { UpdateTagLabelColor(); SaveDraft(); };
            _tagCutLetter.Unchecked += (_, __) => { UpdateTagLabelColor(); SaveDraft(); };
            _tagLabelHex.TextChanged += (_, __) => { UpdateTagLabelColor(); SaveDraft(); };
            foreach (var field in new[] { _tagWidth, _tagHeight, _tagBridge, _tagLetter, _tagFirstOffset, _tagSizeStep })
                field.TextChanged += (_, __) => SaveDraft();
            LoadDraft();
            UpdateCutDxfColor();
            UpdateTagLabelColor();
            RefreshSizes();
            UpdateCount();
            RefreshWorkflow();
            _steps.SelectionChanged += (_, e) => { if (e.Source == _steps) SaveDraft(); };
            _documentWatch.Tick += (_, __) => { if (!_busy) RefreshWorkflowDocument(); };
            PreviewMouseDown += (_, __) => { if (!_busy && !_loading) RefreshWorkflowDocument(); };
            _playerGrid.BeginningEdit += GuardProjectEdit;
            _templateGrid.BeginningEdit += GuardProjectEdit;
            IsVisibleChanged += (_, __) => UpdateDocumentWatch();
            Loaded += (_, __) => UpdateDocumentWatch();
            Unloaded += (_, __) => _documentWatch.Stop();
        }

        private TabControl CreateSteps(StackPanel[] panels)
        {
            var tabs = new TabControl { TabStripPlacement = Dock.Top, BorderThickness = new Thickness(0) };
            // A single equal-width row keeps all tabs at the top even in
            // a narrow docker. Each page owns its vertical scroll position.
            var dock = new FrameworkElementFactory(typeof(DockPanel));
            var headers = new FrameworkElementFactory(typeof(UniformGrid));
            headers.SetValue(UniformGrid.RowsProperty, 1);
            headers.SetValue(UniformGrid.ColumnsProperty, StepNames.Length);
            headers.SetValue(Panel.IsItemsHostProperty, true);
            headers.SetValue(DockPanel.DockProperty, Dock.Top);
            headers.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Once);
            dock.AppendChild(headers);
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.White);
            border.SetValue(Border.BorderBrushProperty, Brushes.LightSlateGray);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var content = new FrameworkElementFactory(typeof(ContentPresenter), "PART_SelectedContentHost");
            content.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
            border.AppendChild(content); dock.AppendChild(border);
            tabs.Template = new ControlTemplate(typeof(TabControl)) { VisualTree = dock };
            for (int index = 0; index < StepNames.Length; index++)
            {
                var header = new StackPanel();
                _stepMarks[index] = new TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                    FontSize = 11, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
                header.Children.Add(_stepMarks[index]);
                header.Children.Add(new TextBlock { Text = StepNames[index], FontSize = 10,
                    TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var item = new TabItem { Header = header, Padding = new Thickness(2, 3, 2, 3),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, MinWidth = 0,
                    Content = new ScrollViewer { Content = panels[index],
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
                _stepTabs[index] = item;
                tabs.Items.Add(item);
            }
            tabs.SelectedIndex = 0;
            return tabs;
        }

        private void RefreshStepHeaders()
        {
            for (int index = 0; index < StepNames.Length; index++)
            {
                int state = StepStates[index];
                bool done = _completedSteps[state] || (state == 3 && _completedSteps[4]);
                _stepMarks[index].Text = (index + 1).ToString(CultureInfo.InvariantCulture) + (done ? " ✓" : "");
                _stepMarks[index].Foreground = done ? Brushes.ForestGreen : Brushes.DimGray;
                string detail = state == 3 && _completedSteps[4] ? _stepDetails[4] : _stepDetails[state];
                string description = StepNames[index] + ": " + (done ? detail ?? "выполнено" : "не выполнено");
                _stepTabs[index].ToolTip = description;
                AutomationProperties.SetName(_stepTabs[index], description);
            }
            if (_pdfProgress != null) _pdfProgress.Text = _completedSteps[4] ? "✓ Активный лист сохранён в PDF" : "";
        }

        private static ComboBox CopyChoice(ComboBox source)
        {
            // Copy labels without a shared collection view or selection binding.
            return new ComboBox { ItemsSource = source.Items.Cast<string>().ToArray(),
                IsSynchronizedWithCurrentItem = false, SelectedIndex = source.SelectedIndex,
                MinHeight = 27, Margin = new Thickness(0, 0, 0, 4) };
        }

        private static bool TemplateConfigured(SportLayoutTemplate template)
        {
            if (template == null || String.IsNullOrWhiteSpace(template.Size) || String.IsNullOrWhiteSpace(template.Path))
                return false;
            double scale = template.ScalePercent <= 0 ? 100 : template.ScalePercent;
            return !Double.IsNaN(scale) && !Double.IsInfinity(scale) && scale >= 50 && scale <= 200 &&
                template.Path.EndsWith(".cdr", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshWorkflow()
        {
            if (_loading || _stepTabs[4] == null || _tagSizeStep == null || _keepGuides == null) return;
            var json = new JavaScriptSerializer();
            var selected = _players.Where(player => player != null && player.PrintEnabled).ToArray();
            string models = json.Serialize(_templates.Select(template => template == null ? null : new {
                Size = SportLayoutService.NormalizeSize(template.Size), Path = template.Path ?? "",
                Layer = template.LayerName ?? "",
                Scale = (template.ScalePercent <= 0 ? 100 : template.ScalePercent).ToString("R", CultureInfo.InvariantCulture) }).ToArray());
            string players = json.Serialize(selected.Select(player => new {
                Size = SportLayoutService.NormalizeSize(player.Size), Name = player.Name ?? "",
                Number = player.Number ?? "", player.Quantity }).ToArray());
            string printPlayers = json.Serialize(selected.Select(player => new {
                Size = SportLayoutService.NormalizeSize(player.Size), Name = player.Name ?? "", Number = player.Number ?? "",
                player.Quantity, player.PrintFront, player.PrintBack, player.PrintLeftSleeve, player.PrintRightSleeve, player.PrintNeck }).ToArray());
            string preparation = json.Serialize(new { Models = models,
                Sizes = selected.Select(player => SportLayoutService.NormalizeSize(player.Size))
                    .Distinct().OrderBy(size => size, StringComparer.Ordinal).ToArray(),
                Art = new[] { _frontArt.Text, _backArt.Text, _leftSleeveArt.Text, _rightSleeveArt.Text, _neckArt.Text } });
            string printing = json.Serialize(new { Preparation = preparation, Players = printPlayers,
                Width = _sheetWidth.Text, Gap = _gap.Text, Length = _maxPageLength.Text,
                Rotate = _rotateSleeves.IsChecked, Guides = _keepGuides.IsChecked,
                NameSide = _nameSide.SelectedIndex, NameCase = _frontCase.SelectedIndex,
                Density = _nestingMode.SelectedIndex, Grouping = _groupingMode.SelectedIndex,
                Press = _pressMode, PressLeft = _pressLeftMm, PressRight = _pressRightMm,
                PressGrouping = _pressGrouping, PressOrientation = _pressOrientation, PressWidth = _pressSheetWidthMm,
                PressTop = _pressTopMm, PressBottom = _pressBottomMm, PressBorders = _pressBorders });
            string pdf = json.Serialize(new { Print = printing, Folder = _printFolder.Text, Quality = _printQuality.SelectedIndex });
            string cutting = json.Serialize(new { Models = models, Players = players,
                Width = _cutSheetWidth.Text, Gap = _cutGap.Text, Length = _cutMaxPageLength.Text,
                Rotate = _cutAllow180.IsChecked, Tags = _cutSizeTags.IsChecked,
                CutTagLetter = _tagCutLetter.IsChecked,
                TagLabelHex = _tagLabelHex.Text,
                Density = _cutNestingMode.SelectedIndex, Grouping = _cutGroupingMode.SelectedIndex,
                Tag = new[] { _tagWidth.Text, _tagHeight.Text, _tagBridge.Text, _tagLetter.Text,
                    _tagFirstOffset.Text, _tagSizeStep.Text } });
            string[] inputs = { models, preparation, players, printing, pdf, cutting };
            if (_stepInputs != null)
                foreach (int index in new[] { 1, 3, 4, 5 })
                    if (!String.Equals(_stepInputs[index], inputs[index], StringComparison.Ordinal))
                        _completedSteps[index] = false;
            _stepInputs = inputs;
            _completedSteps[0] = _templates.Any(TemplateConfigured);
            _stepDetails[0] = "размеры и файлы CDR назначены";
            _completedSteps[2] = selected.Length > 0 && selected.All(player =>
                player.Quantity >= 1 && player.Quantity <= 100 && (player.Name ?? "").Length <= 100 &&
                (player.Number ?? "").Length <= 20 && !String.IsNullOrWhiteSpace(player.Size) &&
                _templates.Count(template => TemplateConfigured(template) &&
                    SportLayoutService.NormalizeSize(template.Size) == SportLayoutService.NormalizeSize(player.Size)) == 1) &&
                selected.Sum(player => (long)player.Quantity) <= 300;
            _stepDetails[2] = "отмеченные строки заполнены, размеры назначены";
            RefreshStepHeaders();
        }

        private void ClearCompletedSteps(params int[] steps)
        {
            foreach (int step in steps) _completedSteps[step] = false;
            RefreshStepHeaders();
            PersistWorkflow(false);
        }

        private void CompleteStep(int step, string detail)
        {
            RefreshWorkflowDocument();
            RefreshWorkflow();
            _completedSteps[step] = true;
            _stepDetails[step] = detail;
            RefreshStepHeaders();
            PersistWorkflow(true);
            Log.Info("Sport workflow: step completed=" + step);
        }

        private void RefreshWorkflowDocument()
        {
            if (_switchingPlayers) return;
            try
            {
                dynamic app = CorelApp.Get();
                object document = (object)app.ActiveDocument;
                if (document == null)
                {
                    if (Convert.ToInt32(app.Documents.Count) != 0) return;
                    BindProjectData(null);
                    _workflowDocument = _workflowSession = _workflowId = _savedWorkflow = null;
                    ClearCompletedSteps(1, 3, 4, 5);
                    return;
                }
                BindProjectData(document);
                string session = SportWorkflowProgress.SessionKey(document);
                SportWorkflowProgress stored = SportWorkflowProgress.Read(document);
                // A missing property during a native transition or Undo must
                // not erase a successful action in the same open document.
                if (stored == null && session == _workflowSession && _workflowId != null) return;
                string key = (stored?.DocumentId ?? "session") + "|" + session;
                if (key == _workflowDocument) return;
                _workflowDocument = key; _workflowSession = session;
                _workflowId = stored?.DocumentId; _savedWorkflow = stored?.Serialize();
                foreach (int step in new[] { 1, 3, 4, 5 }) _completedSteps[step] = false;
                _stepInputs = stored?.Inputs.ToArray();
                if (stored != null)
                    for (int step = 0; step < 6; step++)
                    { _completedSteps[step] = stored.Completed[step]; _stepDetails[step] = stored.Details[step]; }
                _progress.Text = "";
                RefreshWorkflow();
                Log.Info("Sport workflow: document context restored, saved marks=" + (stored != null));
            }
            catch (Exception error) { ReportProjectError("Не удалось открыть данные формы", error); }
        }

        private void PersistWorkflow(bool create)
        {
            if (_stepInputs == null || (!create && _workflowId == null)) return;
            try
            {
                object document = (object)CorelApp.Get().ActiveDocument;
                if (document == null || SportWorkflowProgress.SessionKey(document) != _workflowSession) return;
                var state = new SportWorkflowProgress { DocumentId = _workflowId ?? Guid.NewGuid().ToString("D"),
                    Inputs = _stepInputs.ToArray(), Completed = _completedSteps.ToArray(), Details = _stepDetails.ToArray() };
                string json = state.Serialize();
                if (json == _savedWorkflow) return;
                SportWorkflowProgress.Write(document, json);
                _workflowId = state.DocumentId; _savedWorkflow = json;
                _workflowDocument = _workflowId + "|" + _workflowSession;
            }
            catch (Exception error) { Log.Error("Could not persist sport workflow marks.", error); }
        }

        private void UpdateDocumentWatch()
        {
            if (IsLoaded && IsVisible)
            {
                if (!_busy) RefreshWorkflowDocument();
                _documentWatch.Start();
            }
            else _documentWatch.Stop();
        }

        private async void SavePlayersFile(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            try
            {
                RefreshWorkflowDocument();
                if (_projectDocument == null) throw new InvalidOperationException("Откройте CDR проекта.");
                if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true))
                    throw new InvalidOperationException("Закончите редактирование ячейки.");
                SaveDraft();
                var dialog = new SaveFileDialog { Filter = "Список игроков CSV (*.csv)|*.csv", DefaultExt = ".csv",
                    FileName = Path.GetFileNameWithoutExtension(Convert.ToString(((dynamic)_projectDocument).FileName)) + " - игроки.csv" };
                if (dialog.ShowDialog() != true) return;
                string csv = SportProjectPlayers.Csv(_players), path = dialog.FileName;
                await System.Threading.Tasks.Task.Run(() => File.WriteAllText(path, csv, new UTF8Encoding(true)));
                _status("Список игроков сохранён: " + path, false);
            }
            catch (Exception error) { Log.Error("Could not save project players CSV.", error); _status(error.GetBaseException().Message, true); }
        }

        private async void LoadPlayersFile(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            try
            {
                RefreshWorkflowDocument();
                if (_projectDocument == null) throw new InvalidOperationException("Откройте CDR проекта.");
                object project = _projectDocument;
                var dialog = new OpenFileDialog { Filter = "Список игроков CSV (*.csv)|*.csv", CheckFileExists = true };
                if (dialog.ShowDialog() != true) return;
                string path = dialog.FileName;
                var rows = await System.Threading.Tasks.Task.Run(() => {
                    string csv;
                    try { csv = File.ReadAllText(path, new UTF8Encoding(false, true)); }
                    catch (DecoderFallbackException) { csv = File.ReadAllText(path, Encoding.GetEncoding(1251)); }
                    return SportProjectPlayers.ParseCsv(csv);
                });
                RefreshWorkflowDocument();
                if (!SportProjectPlayers.SameDocument(project, _projectDocument))
                    throw new InvalidOperationException("Проект сменился. Повторите загрузку для нужного CDR.");
                if (_players.Count > 0 && MessageBox.Show("Заменить текущий список игроков списком из CSV?", "Список игроков",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                _loading = true;
                try { EnsurePlayerSizes(rows); _players.Clear(); foreach (SportLayoutPlayer row in rows) _players.Add(row); RefreshSizes(); }
                finally { _loading = false; }
                _playerGrid.Items.Refresh(); UpdateCount(); SaveDraft();
                _status("Загружено игроков: " + rows.Count + ". Список привязан к текущему CDR.", false);
            }
            catch (Exception error) { Log.Error("Could not load project players CSV.", error); _status(error.GetBaseException().Message, true); }
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

        private void ChooseLayeredModel(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "CorelDRAW (*.cdr)|*.cdr",
                CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            try
            {
                IList<string> layers = new SportLayoutService().ScanLayeredModel(dialog.FileName);
                _loading = true;
                _templates.Clear();
                foreach (string layer in layers)
                    _templates.Add(new SportLayoutTemplate { Size = layer,
                        LayerName = layer, Path = dialog.FileName, ScalePercent = 100 });
                _modelPath.Text = dialog.FileName;
                _loading = false;
                RefreshSizes();
                _status("Загружена модель: " + String.Join(", ", layers) + ".", false);
                SaveDraft();
            }
            catch (Exception error)
            {
                _loading = false;
                Log.Error("Could not load layered sport model.", error);
                _status(error.Message, true);
            }
        }

        private void RefreshSizes()
        {
            if (_sizeOptions == null) return;
            string[] sizes = _templates.Select(x => x.Size?.Trim())
                .Concat(_players.Select(row => row.Size))
                .Where(x => !String.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).ToArray();
            // Do not reset SelectedItem bindings to null while rebuilding the list.
            foreach (string size in sizes) if (!_sizeOptions.Contains(size)) _sizeOptions.Add(size);
            for (int i = _sizeOptions.Count - 1; i >= 0; i--)
                if (!sizes.Contains(_sizeOptions[i], StringComparer.Ordinal)) _sizeOptions.RemoveAt(i);
        }

        private void EnsurePlayerSizes(IEnumerable<SportLayoutPlayer> players)
        {
            foreach (SportLayoutPlayer row in players)
                if (!String.IsNullOrWhiteSpace(row.Size) && !_sizeOptions.Contains(row.Size)) _sizeOptions.Add(row.Size);
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
            if (!String.IsNullOrWhiteSpace(medium.LayerName))
            {
                _status("Для слоистой модели добавьте реальные слои размеров в CDR и выберите модель заново.", true);
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
            try { ImportPlayers(Clipboard.GetText(), true); }
            catch (Exception error) { _status(error.Message, true); }
        }

        private static bool IsTableClipboard(string text) =>
            !String.IsNullOrEmpty(text) && text.IndexOfAny(new[] { '\t', '\r', '\n', ';' }) >= 0;

        private void PlayerGridPasteKeyDown(object sender, KeyEventArgs e)
        {
            if (!(e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0) &&
                !(e.Key == Key.Insert && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) return;
            try
            {
                string text = Clipboard.GetText();
                if (!IsTableClipboard(text) && Keyboard.FocusedElement is TextBox) return;
                e.Handled = true;
                ImportPlayers(text, false);
            }
            catch (Exception error) { e.Handled = true; _status(error.Message, true); }
        }

        private void PlayerGridPasting(object sender, DataObjectPastingEventArgs e)
        {
            string text = e.DataObject.GetData(DataFormats.UnicodeText) as string ??
                e.DataObject.GetData(DataFormats.Text) as string;
            if (!IsTableClipboard(text)) return;
            e.CancelCommand();
            e.Handled = true;
            try { ImportPlayers(text, false); }
            catch (Exception error) { _status(error.Message, true); }
        }

        private void ImportPlayers(string text, bool append)
        {
            if (_busy) throw new InvalidOperationException("Дождитесь завершения текущей операции.");
            if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true))
                throw new InvalidOperationException("Закончите редактирование ячейки перед вставкой.");
            bool placeholder = _players.Count == 1 && String.IsNullOrWhiteSpace(_players[0].Name) &&
                String.IsNullOrWhiteSpace(_players[0].Number) && _players[0].Quantity == 1;
            var visibleRows = _playerGrid.Items.OfType<SportLayoutPlayer>().ToList();
            int start = append ? _players.Count : visibleRows.IndexOf(_playerGrid.CurrentCell.Item as SportLayoutPlayer);
            if (start < 0) start = _players.Count;
            int column = append || _playerGrid.CurrentCell.Column == null ? 0 :
                _playerGrid.Columns.IndexOf(_playerGrid.CurrentCell.Column);
            if (column < 0 || column > 3) column = 0;
            if (!append && _playerGrid.SelectedCells.Count > 1)
            {
                var selected = _playerGrid.SelectedCells.Where(cell => _playerGrid.Columns.IndexOf(cell.Column) < 4 &&
                    cell.Item is SportLayoutPlayer).ToList();
                if (selected.Count > 0)
                {
                    start = selected.Min(cell => visibleRows.IndexOf((SportLayoutPlayer)cell.Item));
                    column = selected.Min(cell => _playerGrid.Columns.IndexOf(cell.Column));
                }
            }
            if (placeholder) start = 0;
            bool autoColumn = append || _playerGrid.CurrentCell.Column == null ||
                !(_playerGrid.CurrentCell.Item is SportLayoutPlayer);
            IList<SportPlayerClipboard.Row> input = SportPlayerClipboard.Parse(text, column, autoColumn);
            bool namesOnly = input.All(row => row.Cells.Count == 1 && row.Cells.ContainsKey(1));
            if (autoColumn) column = input[0].Cells.Keys.Min();
            string defaultSize = _sizeOptions.FirstOrDefault() ?? "М";
            if (start < visibleRows.Count && _sizeOptions.Contains(visibleRows[start].Size))
                defaultSize = visibleRows[start].Size;
            var rows = new List<SportLayoutPlayer>();
            var targetIndices = new List<int>();
            var errors = new List<string>();
            for (int i = 0; i < input.Count; i++)
            {
                var cells = input[i].Cells;
                SportLayoutPlayer previous = !append && !placeholder && start + i < visibleRows.Count ?
                    visibleRows[start + i] : null;
                targetIndices.Add(placeholder && i == 0 ? 0 : previous == null ? -1 : _players.IndexOf(previous));
                var row = previous == null ? new SportLayoutPlayer {
                    Size = namesOnly ? "" : defaultSize, Quantity = 1 } :
                    new SportLayoutPlayer { Size = previous.Size, Name = previous.Name,
                        Number = previous.Number, Quantity = previous.Quantity, PrintEnabled = previous.PrintEnabled,
                        PrintFront = previous.PrintFront, PrintBack = previous.PrintBack, PrintLeftSleeve = previous.PrintLeftSleeve,
                        PrintRightSleeve = previous.PrintRightSleeve, PrintNeck = previous.PrintNeck };
                try
                {
                    if (cells.TryGetValue(0, out string size)) row.Size = ResolvePastedSize(size, defaultSize);
                    if (cells.TryGetValue(1, out string name)) row.Name = name;
                    if (cells.TryGetValue(2, out string number)) row.Number = number;
                    if (cells.TryGetValue(3, out string quantity))
                    {
                        if (String.IsNullOrWhiteSpace(quantity)) row.Quantity = 1;
                        else if (!Decimal.TryParse(quantity.Replace(',', '.').Replace(" ", "").Replace("\u00a0", ""),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out decimal count) ||
                            count != Decimal.Truncate(count) || count < 1 || count > 100)
                            throw new InvalidOperationException("количество должно быть целым числом от 1 до 100.");
                        else row.Quantity = (int)count;
                    }
                    if ((row.Name ?? "").Length > 100 || (row.Number ?? "").Length > 20)
                        throw new InvalidOperationException("имя длиннее 100 символов или номер длиннее 20.");
                    rows.Add(row);
                }
                catch (Exception error) { errors.Add("Строка вставки " + input[i].Line + ": " + error.Message); }
            }
            if (errors.Count > 0)
                throw new InvalidOperationException("Диапазон не вставлен. " + String.Join(" ", errors.Take(5)) +
                    (errors.Count > 5 ? " Ещё ошибок: " + (errors.Count - 5) + "." : ""));
            int updated = placeholder ? 0 : targetIndices.Count(index => index >= 0);
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                for (int i = 0; i < rows.Count; i++)
                    if (targetIndices[i] >= 0) _players[targetIndices[i]] = rows[i];
                    else _players.Add(rows[i]);
            }
            finally { _loading = wasLoading; }
            _playerGrid.Items.Refresh();
            _playerGrid.CurrentCell = new DataGridCellInfo(rows[0], _playerGrid.Columns[column]);
            _playerGrid.ScrollIntoView(rows[0]);
            UpdateCount();
            SaveDraft();
            _status("Вставлено строк: " + rows.Count + ". Добавлено: " + (rows.Count - updated) +
                ", обновлено: " + updated + "." +
                (namesOnly && rows.Any(row => String.IsNullOrWhiteSpace(row.Size)) ?
                    " Теперь назначьте размеры строкам." : ""), false);
        }

        private string ResolvePastedSize(string value, string fallback)
        {
            string size = String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            string normalized = SportLayoutService.NormalizeSize(size);
            string match = _sizeOptions.FirstOrDefault(option =>
                SportLayoutService.NormalizeSize(option) == normalized);
            if (match != null) return match;
            if (Regex.IsMatch(size, @"^\d+$"))
            {
                var matches = _sizeOptions.Where(option => Regex.Match(option, @"\d+$").Value == size).ToList();
                if (matches.Count == 1) return matches[0];
            }
            if (_sizeOptions.Count == 0) return size;
            throw new InvalidOperationException("размер «" + size + "» отсутствует в списке лекал. " +
                "Укажите имя нужного слоя размера.");
        }

        private List<SportLayoutPlayer> SelectedPlayers()
        {
            var selected = _playerGrid.SelectedCells.Select(cell => cell.Item as SportLayoutPlayer)
                .Where(row => row != null).Distinct().ToList();
            if (selected.Count == 0 && _playerGrid.SelectedItem is SportLayoutPlayer current)
                selected.Add(current);
            return selected;
        }

        private void ApplySizeToSelection(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            try
            {
                if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                    !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true))
                    throw new InvalidOperationException("Закончите редактирование ячейки перед назначением размера.");
                var selected = SelectedPlayers();
                if (selected.Count == 0)
                    throw new InvalidOperationException("Выделите строки или ячейки игроков для назначения размера.");
                string size = _batchSize.SelectedItem as string;
                if (String.IsNullOrWhiteSpace(size) || !_sizeOptions.Contains(size))
                    throw new InvalidOperationException("Выберите размер из списка лекал.");
                bool wasLoading = _loading;
                _loading = true;
                try { foreach (var row in selected) row.Size = size; }
                finally { _loading = wasLoading; }
                _playerGrid.Items.Refresh();
                _playerGrid.SelectedCells.Clear();
                foreach (var row in selected)
                    _playerGrid.SelectedCells.Add(new DataGridCellInfo(row, _playerGrid.Columns[0]));
                _playerGrid.CurrentCell = new DataGridCellInfo(selected[0], _playerGrid.Columns[0]);
                SaveDraft();
                _status("Размер «" + size + "» назначен строкам: " + selected.Count + ".", false);
            }
            catch (Exception error) { _status(error.Message, true); }
        }

        private void RemovePlayers(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var selected = SelectedPlayers();
            bool wasLoading = _loading;
            _loading = true;
            try { foreach (var row in selected) _players.Remove(row); }
            finally { _loading = wasLoading; }
            UpdateCount(); SaveDraft();
        }

        private TextBox ArtworkField(Panel panel, string title, string partName,
            SportLayoutPart part)
        {
            panel.Children.Add(Label(title));
            TextBox field = Field("");
            field.MinWidth = 0;
            field.SetBinding(ToolTipProperty, new Binding("Text") { Source = field });
            var preview = new SportArtworkThumbnail(field, title);
            field.Tag = preview;
            var block = new Grid();
            block.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            block.ColumnDefinitions.Add(new ColumnDefinition());
            block.Children.Add(preview);
            var editors = new StackPanel();
            Grid.SetColumn(editors, 1); block.Children.Add(editors);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(field);
            var browse = Button("Выбрать файл", (_, __) => BrowseArtwork(field));
            Grid.SetColumn(browse, 1);
            row.Children.Add(browse);
            var clear = Button("×", (_, __) => ClearArtwork(field, partName, part));
            clear.ToolTip = "Убрать рисунок " + partName;
            clear.Width = 29;
            clear.Padding = new Thickness(0);
            Grid.SetColumn(clear, 2);
            row.Children.Add(clear);
            editors.Children.Add(row);
            var actions = DockerLayout.Cards(150, 2);
            actions.Children.Add(Button("Взять выделение из Corel", (_, __) => CaptureArtwork(field, partName)));
            actions.Children.Add(Button("Обновить в макетах", (_, __) => ReplaceArtwork(field, partName, part)));
            editors.Children.Add(actions);
            panel.Children.Add(block);
            return field;
        }

        private void ReplaceArtwork(TextBox field, string partName, SportLayoutPart part)
        {
            if (_busy) return;
            if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
                !_templateGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !_templateGrid.CommitEdit(DataGridEditingUnit.Row, true))
            { _status("Закончите редактирование таблицы.", true); return; }
            _busy = true; SetActionButtons(false);
            try
            {
                var templates = SportLayoutService.RequestedTemplates(_templates, _players);
                int changed = new SportLayoutService().ReplacePreparedArtwork(part, field.Text.Trim(), templates.Select(t => t.Size));
                if (changed == 0) { _status("Нет готовых макетов выбранных размеров. Сначала выполните этап 1.", true); return; }
                ClearCompletedSteps(3, 4);
                _status("Рисунок " + partName + " обновлён в размерах: " + changed + ". Поправьте объекты и создайте печать заново.", false);
            }
            catch (Exception error) { Log.Error("Could not replace prepared sport artwork.", error); _status(error.Message, true); }
            finally { _busy = false; SetActionButtons(true); }
        }

        private void ClearArtwork(TextBox field, string partName, SportLayoutPart part)
        {
            if (_busy) return;
            field.Clear();
            try
            {
                int changed = new SportLayoutService().ClearPreparedArtwork(part);
                ClearCompletedSteps(3, 4);
                _status(changed > 0 ? "Рисунок " + partName + " убран на " + changed +
                    " размерах обзорной страницы." : "Рисунок " + partName +
                    " убран. Подготовьте макет на этапе 1.", false);
            }
            catch (Exception error)
            {
                Log.Error("Could not clear prepared sport artwork.", error);
                _status("Путь к рисунку убран, но на странице-макете его убрать не удалось: " +
                    error.Message, true);
            }
        }

        private static void BrowseArtwork(TextBox field)
        {
            var dialog = new OpenFileDialog { Filter =
                "Рисунки CDR и изображения|*.cdr;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|CorelDRAW (*.cdr)|*.cdr|Изображения|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|Все файлы (*.*)|*.*",
                CheckFileExists = true };
            if (dialog.ShowDialog() == true)
            {
                field.Text = dialog.FileName;
                (field.Tag as SportArtworkThumbnail)?.Refresh();
            }
        }

        private void CaptureArtwork(TextBox field, string partName)
        {
            if (_busy) return;
            string temporary = null;
            _busy = true; SetActionButtons(false);
            try
            {
                if (_artworkCapture.SelectedIndex != 1)
                {
                    field.Text = SportArtworkCapture.CaptureObjects();
                    _status("Рисунок " + partName + " взят объектами CDR. После этапа 1 его можно редактировать отдельно в каждом размере.", false);
                    return;
                }
                int megapixels = _artworkLimit.SelectedItem is int value ? value : 150;
                temporary = AiSelectionCapture.CaptureForArtwork(300, checked(megapixels * 1000000));
                string folder = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "sport-patterns");
                Directory.CreateDirectory(folder);
                string destination = Path.Combine(folder, "artwork-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                File.Move(temporary, destination);
                temporary = null;
                field.Text = destination;
                _status("Рисунок " + partName + " взят из выделения (300 DPI; предел " + megapixels + " Мп).", false);
            }
            catch (Exception error)
            {
                Log.Error("Could not capture selected sport artwork.", error);
                _status(error.Message, true);
            }
            finally
            {
                try { if (temporary != null) File.Delete(temporary); } catch { }
                _busy = false; SetActionButtons(true);
            }
        }

        private void AssignTextBox(string key, string label)
        {
            if (_busy) return;
            try
            {
                string page = new SportLayoutService().AssignSelectedTextBox(key, out bool prepared);
                if (prepared) { RefreshWorkflowDocument(); ClearCompletedSteps(3, 4); }
                _status("Назначен образец " + label + ": «" + page + "».", false);
            }
            catch (Exception error)
            {
                Log.Error("Could not assign sport text sample.", error);
                _status(error.Message, true);
            }
        }

        private async void Prepare(object sender, RoutedEventArgs e)
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
            _busy = true;
            SetActionButtons(false);
            try
            {
                SportLayoutOptions options = ReadOptions();
                var templates = SportLayoutService.RequestedTemplates(_templates, _players);
                SaveDraft();
                var service = new SportLayoutService();
                _progress.Text = "Проверяю и добавляю выбранные размеры: " +
                    String.Join(", ", templates.Select(t => SportLayoutService.NormalizeSize(t.Size))) + "…";
                await Dispatcher.Yield(DispatcherPriority.Background);
                SportOverviewResult prepared = service.PrepareOverview(templates, options);
                if (prepared.AddedSizes.Count > 0) ClearCompletedSteps(3, 4, 5);
                CompleteStep(1, "макеты выбранных размеров подготовлены");
                Log.Info("Sport overview: returned to docker");
                string added = prepared.AddedSizes.Count == 0 ? "Новые размеры не требуются." :
                    "Добавлены размеры: " + String.Join(", ", prepared.AddedSizes) + ".";
                string kept = prepared.KeptSizes.Count == 0 ? "" :
                    " Уже подготовлены: " + String.Join(", ", prepared.KeptSizes) + ".";
                _progress.Text = added + kept + " Поправьте макеты и запустите этап 2.";
                _status(added + kept, false);
                Log.Info("Sport overview: docker status updated");
            }
            catch (Exception error)
            {
                Log.Error("Sport layout master preparation failed.", error);
                ClearCompletedSteps(1, 3, 4, 5);
                _progress.Text = "Макет не подготовлен.";
                _status(error.Message, true);
            }
            finally { _busy = false; SetActionButtons(true); }
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
            SetActionButtons(false);
            try
            {
                SportLayoutOptions options = ReadOptions();
                SaveDraft();
                _progress.Text = "Готовлю деталь для допечатки: " + row.Name + "…";
                await Dispatcher.Yield(DispatcherPriority.Background);
                SportLayoutResult result = new SportLayoutService().GenerateReplacementPiece(
                    row, _templates.ToList(), options, part);
                ClearCompletedSteps(4);
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
            finally { _busy = false; SetActionButtons(true); }
        }

        private void Generate(object sender, RoutedEventArgs e) => GenerateLayout(false);

        private void GenerateCutting(object sender, RoutedEventArgs e) => GenerateLayout(true);

        private void SetActionButtons(bool enabled)
        {
            if (enabled) PersistWorkflow(false);
            RefreshWorkflowDocument();
            _steps.IsEnabled = enabled;
            _playerEditor.IsEnabled = enabled;
            _prepare.IsEnabled = _generate.IsEnabled = _generateCut.IsEnabled = _saveCut.IsEnabled = enabled;
            _exportPrint.IsEnabled = _choosePrintFolder.IsEnabled = _printQuality.IsEnabled = enabled;
            _exportCut.IsEnabled = _chooseCutFolder.IsEnabled = enabled;
            _combineCut.IsEnabled = enabled;
            _tagSamples.IsEnabled = enabled;
            _expandPlayers.IsEnabled = enabled;
            _artworkLimit.IsEnabled = enabled;
            _artworkCapture.IsEnabled = enabled;
        }

        private async void CreateTagSamples(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true; SetActionButtons(false);
            try
            {
                SportCutTagSettings settings = ReadTagSettings();
                SaveDraft();
                _progress.Text = "Создаю лист язычков для пробной резки…";
                await Dispatcher.Yield(DispatcherPriority.Render);
                int count = SportCutTagSamples.Create(settings);
                _progress.Text = "Готово: " + count + " образцов и отдельная страница-памятка. Пары с буквой и без неё.";
                _status("Сравнительный лист язычков и памятка созданы. Активен лист резки.", false);
            }
            catch (Exception error)
            {
                Log.Error("Could not create cutting tag samples.", error);
                _progress.Text = "Лист образцов не создан.";
                _status(error.Message, true);
            }
            finally { _busy = false; SetActionButtons(true); }
        }

        private SportCutTagSettings ReadTagSettings()
        {
            var settings = new SportCutTagSettings {
                WidthMm = ParseMillimeters(_tagWidth.Text, 12, 150, "Ширина язычка"),
                HeightMm = ParseMillimeters(_tagHeight.Text, 12, 50, "Высота язычка"),
                BridgeMm = ParseMillimeters(_tagBridge.Text, .3, 5, "Длина каждого мостика"),
                LetterHeightMm = _tagCutLetter.IsChecked == true ? ParseMillimeters(_tagLetter.Text, 3, 40, "Высота буквы") : 8,
                CutLabel = _tagCutLetter.IsChecked == true,
                LabelHex = _tagLabelHex.Text.Trim(),
                FirstOffsetMm = ParseMillimeters(_tagFirstOffset.Text, 0, 500, "Отступ первого размера"),
                SizeStepMm = ParseMillimeters(_tagSizeStep.Text, 0, 100, "Шаг следующих размеров")
            };
            settings.Validate();
            return settings;
        }

        private void UpdateTagLabelColor()
        {
            bool enabled = _tagCutLetter.IsChecked == true;
            _tagLetter.IsEnabled = _tagLabelHex.IsEnabled = _tagLabelColor.IsEnabled = enabled;
            var swatch = (Border)_tagLabelColor.Content;
            swatch.Background = SportCutTagSettings.TryLabelColor(_tagLabelHex.Text, out Color color)
                ? new SolidColorBrush(color) : Brushes.Transparent;
        }

        private void ChooseTagLabelColor()
        {
            if (_busy) return;
            try
            {
                SportCutTagSettings.TryLabelColor(_tagLabelHex.Text, out Color current);
                var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                Color? picked = WindowsColorPicker.Pick(source == null ? IntPtr.Zero : source.Handle, current);
                if (picked.HasValue)
                    _tagLabelHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                        picked.Value.R, picked.Value.G, picked.Value.B);
            }
            catch (Exception error)
            {
                Log.Error("Could not choose cutting tab label color.", error);
                _status("Не удалось выбрать цвет подписи: " + error.GetBaseException().Message, true);
            }
        }

        private void UpdateCutDxfColor()
        {
            _cutDxfColorHex.IsEnabled = _cutDxfColor.IsEnabled = _cutDxfSingleColor.IsChecked == true;
            ((Border)_cutDxfColor.Content).Background = SportCutTagSettings.TryLabelColor(_cutDxfColorHex.Text, out Color color)
                ? new SolidColorBrush(color) : Brushes.Transparent;
        }

        private void ChooseCutDxfColor()
        {
            if (_busy) return;
            try
            {
                SportCutTagSettings.TryLabelColor(_cutDxfColorHex.Text, out Color current);
                var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                Color? picked = WindowsColorPicker.Pick(source == null ? IntPtr.Zero : source.Handle, current);
                if (picked.HasValue)
                    _cutDxfColorHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                        picked.Value.R, picked.Value.G, picked.Value.B);
            }
            catch (Exception error)
            {
                Log.Error("Could not choose single DXF outline color.", error);
                _status("Не удалось выбрать цвет DXF: " + error.GetBaseException().Message, true);
            }
        }

        private void ChoosePrintFolder(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            try
            {
                var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                string selected = WindowsFolderPicker.Pick(source == null ? IntPtr.Zero : source.Handle,
                    _printFolder.Text.Trim(), "Папка для готовых листов печати");
                if (!String.IsNullOrWhiteSpace(selected)) _printFolder.Text = selected;
            }
            catch (Exception error)
            { Log.Error("Could not choose sport print folder.", error); _status(error.Message, true); }
        }

        private async void ExportPrint(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (String.IsNullOrWhiteSpace(_printFolder.Text)) ChoosePrintFolder(sender, e);
            if (String.IsNullOrWhiteSpace(_printFolder.Text)) return;
            string folder = _printFolder.Text.Trim();
            int dpi = _printQuality.SelectedIndex == 1 ? 300 : 150;
            SaveDraft();
            _busy = true; SetActionButtons(false);
            try
            {
                _progress.Text = "Экспортирую активный лист печати в PDF…";
                await Dispatcher.Yield(DispatcherPriority.Render);
                SportPrintExporter.Result result = SportPrintExporter.ExportCurrentPage(folder, dpi);
                CompleteStep(4, "активный лист сохранён в PDF");
                _progress.Text = "PDF готов: " + result.WidthMm.ToString("0.#") + " × " +
                    result.HeightMm.ToString("0.#") + " мм · 1:1 · " + dpi + " DPI · " +
                    (result.Bytes / 1048576.0).ToString("0.#") + " МБ.\n" + result.Path;
                _status(result.Warning ?? "Лист печати сохранён в PDF: " + result.Path,
                    !String.IsNullOrEmpty(result.Warning));
            }
            catch (Exception error)
            {
                Log.Error("Could not export sport print PDF.", error);
                ClearCompletedSteps(4);
                _progress.Text = "PDF печати не сохранён.";
                _status("PDF печати не сохранён: " + error.Message, true);
            }
            finally { _busy = false; SetActionButtons(true); }
        }

        private void ChooseCutFolder(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            try
            {
                var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                string selected = WindowsFolderPicker.Pick(source == null ? IntPtr.Zero : source.Handle,
                    _cutFolder.Text.Trim(), "Папка для готовых листов резки DXF");
                if (!String.IsNullOrWhiteSpace(selected)) _cutFolder.Text = selected;
            }
            catch (Exception error)
            { Log.Error("Could not choose sport cutting folder.", error); _status(error.Message, true); }
        }

        private async void ExportCut(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (String.IsNullOrWhiteSpace(_cutFolder.Text)) ChooseCutFolder(sender, e);
            if (String.IsNullOrWhiteSpace(_cutFolder.Text)) return;
            string folder = _cutFolder.Text.Trim();
            SaveDraft();
            _busy = true; SetActionButtons(false);
            try
            {
                _progress.Text = "Экспортирую активный лист резки в DXF…";
                Color? singleColor = null;
                if (_cutDxfSingleColor.IsChecked == true)
                {
                    if (!SportCutTagSettings.TryLabelColor(_cutDxfColorHex.Text, out Color color))
                        throw new InvalidOperationException("Цвет контуров DXF должен быть в формате #RRGGBB.");
                    singleColor = color;
                }
                await Dispatcher.Yield(DispatcherPriority.Render);
                SportCutDxfExporter.Result result = SportCutDxfExporter.ExportCurrentPage(folder, singleColor);
                CompleteStep(5, "активный лист резки сохранён в DXF");
                _progress.Text = "DXF готов: " + result.WidthMm.ToString("0.#") + " × " +
                    result.HeightMm.ToString("0.#") + " мм · 1:1 · " +
                    result.PartCount + " деталей блоками · " +
                    (result.Bytes / 1048576.0).ToString("0.#") + " МБ.\n" + result.Path;
                _status(result.Warning ?? "Лист резки сохранён в DXF: " + result.Path,
                    !String.IsNullOrEmpty(result.Warning));
            }
            catch (Exception error)
            {
                Log.Error("Could not export sport cutting DXF.", error);
                ClearCompletedSteps(5);
                _progress.Text = "DXF резки не сохранён.";
                _status("DXF резки не сохранён: " + error.Message, true);
            }
            finally { _busy = false; SetActionButtons(true); }
        }

        private async void CombineCutting(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true; SetActionButtons(false);
            try
            {
                _progress.Text = "Объединяю контуры деталей готового листа…";
                await Dispatcher.Yield(DispatcherPriority.Render);
                int count = SportCutPiece.CombineCurrentPage();
                _progress.Text = count == 0 ? "Контуры уже объединены." : "Объединены контуры деталей: " + count + ".";
                _status(_progress.Text, false);
            }
            catch (Exception error)
            {
                Log.Error("Could not combine cutting contours.", error);
                _status("Не удалось объединить весь лист: " + error.GetBaseException().Message, true);
            }
            finally { _busy = false; SetActionButtons(true); }
        }

        private void SaveCutting(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dialog = new SaveFileDialog { Filter = "CorelDRAW (*.cdr)|*.cdr",
                DefaultExt = ".cdr", AddExtension = true, OverwritePrompt = true,
                FileName = "Спортформа - резка.cdr" };
            if (dialog.ShowDialog() != true) return;
            _busy = true;
            SetActionButtons(false);
            try
            {
                new SportLayoutService().SaveCurrentCuttingPage(dialog.FileName);
                CompleteStep(5, "активный лист резки сохранён в CDR");
                _status("CDR резки сохранён: " + dialog.FileName, false);
            }
            catch (Exception error)
            {
                Log.Error("Could not save sport cutting CDR.", error);
                ClearCompletedSteps(5);
                _status("CDR резки не сохранён: " + error.Message, true);
            }
            finally { _busy = false; SetActionButtons(true); }
        }

        private async void GenerateLayout(bool cutting)
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
            SetActionButtons(false);
            try
            {
                var players = _players.Where(row => row.PrintEnabled).ToList();
                if (!cutting) players = players.Where(row => row.HasPrintedParts).ToList();
                if (players.Count == 0)
                    throw new InvalidOperationException(cutting ? "Отметьте строку заказа галочкой «Печатать»." : "Выберите строки заказа и детали для печати.");
                var templates = SportLayoutService.RequestedTemplates(_templates, players);
                if (players.Count == 0)
                    throw new InvalidOperationException("Отметьте галочкой «Печатать» хотя бы одного игрока.");
                if (players.Any(row => row.Quantity < 1 || row.Quantity > 100))
                    throw new InvalidOperationException("Количество в каждой строке: от 1 до 100.");
                long totalLong = players.Sum(row => (long)row.Quantity);
                if (totalLong > 300)
                    throw new InvalidOperationException("За один запуск можно подготовить не более 300 комплектов.");
                var options = ReadOptions(cutting);
                _progress.Text = "Один раз готовлю детали для окна подбора…";
                await Dispatcher.Yield(DispatcherPriority.Background);
                var service = new SportLayoutService();
                Action<int, int, string> progress = (done, total, label) =>
                    _progress.Text = "Подготовлено " + done + " из " + total + ": " + label;
                Func<IList<SportSheetNesting.Item>, IList<string>, SportNestingChoice> preview = (items, labels) =>
                {
                    var window = new SportNestingWindow(items, labels, options, cutting);
                    Window owner = Window.GetWindow(this);
                    if (owner != null) window.Owner = owner;
                    else
                    {
                        var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                        if (source != null) new System.Windows.Interop.WindowInteropHelper(window).Owner = source.Handle;
                    }
                    _progress.Text = "Подготовка завершена. Подберите вариант в окне раскладки.";
                    return window.ShowDialog() == true ? window.Choice : null;
                };
                SportLayoutResult result = cutting
                    ? service.GenerateCuttingInCurrentDocument(players, templates, options, progress, preview)
                    : service.GenerateInCurrentDocument(players, templates, options, progress, preview);
                ApplyNestingOptions(options, cutting);
                if (!cutting) ClearCompletedSteps(4);
                CompleteStep(cutting ? 5 : 3, cutting ? "раскладка резки создана" : "печатная раскладка создана");
                _progress.Text = "Готово: " + result.GarmentCount + " комплектов, " +
                    result.PieceCount + " деталей, страниц: " + result.PageCount +
                    ", общая длина " + (result.SheetHeightMm / 1000).ToString("0.###") + " м.";
                _status(cutting ? "Раскладка резки создана. Сохраните отдельный CDR кнопкой ниже." :
                    "Раскладка добавлена в текущий CDR. Сохраните документ.", false);
            }
            catch (OperationCanceledException)
            {
                _progress.Text = "Подбор закрыт. Новые страницы не созданы.";
                _status(_progress.Text, false);
            }
            catch (Exception error)
            {
                Log.Error("Sport layout generation failed.", error);
                if (cutting) ClearCompletedSteps(5);
                else ClearCompletedSteps(3, 4);
                _progress.Text = "Раскладка не завершена.";
                _status(error.Message, true);
            }
            finally { _busy = false; SetActionButtons(true); }
        }

        private void ApplyNestingOptions(SportLayoutOptions options, bool cutting)
        {
            bool loading = _loading; _loading = true;
            try
            {
                (cutting ? _cutSheetWidth : _sheetWidth).Text = options.SheetWidthMm.ToString("0.###");
                (cutting ? _cutMaxPageLength : _maxPageLength).Text = (options.MaxPageLengthMm / 1000).ToString("0.###");
                (cutting ? _cutGap : _gap).Text = options.GapMm.ToString("0.###");
                (cutting ? _cutNestingMode : _nestingMode).SelectedIndex = (int)options.NestingMode;
                (cutting ? _cutGroupingMode : _groupingMode).SelectedIndex = (int)options.GroupingMode;
                if (cutting) _cutAllow180.IsChecked = options.CutAllow180;
                else
                {
                    _rotateSleeves.IsChecked = options.RotateSleeves; _pressMode = options.PressMode;
                    _pressGrouping = options.PressGrouping; _pressOrientation = options.PressOrientation;
                    _pressSheetWidthMm = options.PressSheetWidthMm;
                    _pressLeftMm = options.PressLeftMm; _pressRightMm = options.PressRightMm;
                    _pressTopMm = options.PressTopMm; _pressBottomMm = options.PressBottomMm;
                    _pressBorders = options.PressBorders;
                }
            }
            finally { _loading = loading; }
            SaveDraft(); RefreshWorkflow();
        }

        private static double ValidPressMargin(double? value) => value.HasValue &&
            !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) && value >= 0 && value <= 200 ? value.Value : 20;

        private SportLayoutOptions ReadOptions(bool cutting = false)
        {
            BindProjectData((object)CorelApp.Get().ActiveDocument);
            return new SportLayoutOptions {
                FrontArtworkPath = _frontArt.Text.Trim(),
                BackArtworkPath = _backArt.Text.Trim(),
                LeftSleeveArtworkPath = _leftSleeveArt.Text.Trim(),
                RightSleeveArtworkPath = _rightSleeveArt.Text.Trim(),
                NeckArtworkPath = _neckArt.Text.Trim(),
                SheetWidthMm = cutting
                    ? ParseMillimeters(_cutSheetWidth.Text, 300, 4000, "Ширина ткани для резки")
                    : ParseMillimeters(_sheetWidth.Text, 300, 4000, "Ширина печати"),
                MaxPageLengthMm = ParseMillimeters((cutting ? _cutMaxPageLength : _maxPageLength).Text, 0.5, 45,
                    cutting ? "Максимальная длина листа резки, м" : "Максимальная длина листа печати, м") * 1000,
                GapMm = ParseMillimeters((cutting ? _cutGap : _gap).Text, 0, 100,
                    cutting ? "Расстояние между деталями резки" : "Расстояние между деталями печати"),
                RotateSleeves = _rotateSleeves.IsChecked == true,
                NestingMode = (SportNestingMode)(cutting ? _cutNestingMode : _nestingMode).SelectedIndex,
                GroupingMode = (SportGroupingMode)(cutting ? _cutGroupingMode : _groupingMode).SelectedIndex,
                CutAllow180 = _cutAllow180.IsChecked == true,
                PressMode = cutting ? 0 : _pressMode, PressLeftMm = _pressLeftMm, PressRightMm = _pressRightMm,
                PressGrouping = _pressGrouping, PressOrientation = _pressOrientation, PressSheetWidthMm = _pressSheetWidthMm,
                PressTopMm = _pressTopMm, PressBottomMm = _pressBottomMm, PressBorders = _pressBorders,
                CutSizeTags = cutting && _cutSizeTags.IsChecked == true,
                CutTag = cutting && _cutSizeTags.IsChecked == true ? ReadTagSettings() : new SportCutTagSettings(),
                NameStyle = cutting ? null : _legacyNameStyle,
                NumberStyle = cutting ? null : _legacyNumberStyle,
                FrontNameOnRight = _nameSide.SelectedIndex == 1,
                FrontNameCase = (NamesGridLetterCase)_frontCase.SelectedIndex,
                FrontNameTwoLines = _legacyTwoLines,
                FrontLineSpacingPercent = _legacyLineSpacing,
                WhiteText = _whiteCaptions,
                KeepGuides = _keepGuides.IsChecked == true
            };
        }

        private void UpdateCount()
        {
            _count.Text = _players.Count + " строк · отмечено " + _players.Count(row => row.PrintEnabled) +
                " · к печати " + _players.Where(row => row.PrintEnabled && row.HasPrintedParts)
                    .Sum(row => Math.Max(0L, (long)row.Quantity)) + " · к резке " +
                _players.Where(row => row.PrintEnabled).Sum(row => Math.Max(0L, (long)row.Quantity)) + " комплектов";
            foreach (var item in _partHeaderChecks)
            {
                int enabled = _players.Count(row => row.Prints(item.Key));
                item.Value.IsChecked = enabled == 0 ? false : enabled == _players.Count ? true : (bool?)null;
                item.Value.IsEnabled = _players.Count > 0;
            }
        }

        private void PlayerChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_loading) return;
            UpdateCount(); SaveDraft();
        }

        private void SetPlayersEnabled(bool enabled)
        {
            if (_busy || !_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
            bool oldLoading = _loading; _loading = true;
            try { foreach (var row in _players) row.PrintEnabled = enabled; }
            finally { _loading = oldLoading; }
            UpdateCount(); SaveDraft();
        }

        private void OpenPlayersEditor(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_playerWindow != null) { _playerWindow.Activate(); return; }
            if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
            var parent = (Panel)_playerEditor.Parent;
            int position = parent.Children.IndexOf(_playerEditor);
            double oldHeight = _playerGrid.Height;
            DataGridLength oldNameWidth = _playerGrid.Columns[1].Width;
            var root = new DockPanel { Margin = new Thickness(10) };
            var close = Button("Вернуться в докер", (_, __) => _playerWindow.Close());
            DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
            var window = new Window { Title = "Vanya Tools — таблица игроков", Content = root,
                Width = Math.Min(1450, SystemParameters.WorkArea.Width - 40),
                Height = Math.Min(900, SystemParameters.WorkArea.Height - 40),
                MinWidth = 720, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            DockerTheme.Apply(root);
            Window owner = Window.GetWindow(this);
            if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            window.Closing += (_, args) => {
                if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                    !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true)) args.Cancel = true;
            };
            _playerWindow = window;
            parent.Children.Remove(_playerEditor);
            try
            {
                _playerGrid.Height = Double.NaN;
                _playerGrid.Columns[1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                root.Children.Add(_playerEditor);
                window.ShowDialog();
            }
            catch (Exception error)
            {
                Log.Error("Could not open large player table.", error);
                _status("Таблица не открыта: " + error.Message, true);
            }
            finally
            {
                root.Children.Remove(_playerEditor);
                _playerGrid.Height = oldHeight; _playerGrid.Columns[1].Width = oldNameWidth;
                parent.Children.Insert(position, _playerEditor);
                _playerWindow = null;
                UpdateCount(); SaveDraft();
            }
        }

        private void SaveSoon()
        {
            Dispatcher.BeginInvoke(new Action(() => { UpdateCount(); SaveDraft(); }),
                DispatcherPriority.Background);
        }

        private void SaveDraft()
        {
            if (_loading || _switchingPlayers || _frontArt == null || _backArt == null ||
                _leftSleeveArt == null || _rightSleeveArt == null ||
                _neckArt == null || _modelPath == null || _rotateSleeves == null ||
                _sheetWidth == null || _cutSheetWidth == null || _maxPageLength == null || _gap == null ||
                _cutMaxPageLength == null || _cutGap == null || _nameSide == null || _frontCase == null ||
                _printFolder == null || _cutFolder == null || _printQuality == null || _artworkLimit == null || _artworkCapture == null ||
                _keepGuides == null || _cutAllow180 == null || _cutSizeTags == null || _tagCutLetter == null ||
                _tagWidth == null || _tagHeight == null || _tagBridge == null || _tagLetter == null ||
                _tagFirstOffset == null || _tagSizeStep == null || _tagLabelHex == null ||
                _cutDxfSingleColor == null || _cutDxfColorHex == null ||
                _templateGrid == null || _playerGrid == null) return;
            try
            {
                PersistProjectData();
                if (IsLoaded && !_busy) RefreshWorkflowDocument();
                RefreshWorkflow();
                PersistProjectData();
                PersistWorkflow(false);
            }
            catch (Exception error) { ReportProjectError("Не удалось сохранить данные формы", error); }
        }

        private Draft CaptureDraft()
        {
            return new Draft {
                ProjectStorageVersion = 2,
                Templates = _templates.ToList(), Players = _players.ToList(),
                FrontArtworkPath = _frontArt.Text, BackArtworkPath = _backArt.Text,
                LeftSleeveArtworkPath = _leftSleeveArt.Text,
                RightSleeveArtworkPath = _rightSleeveArt.Text,
                NeckArtworkPath = _neckArt.Text, ModelPath = _modelPath.Text,
                ArtworkMaxMegapixels = _artworkLimit.SelectedItem is int value ? value : 150,
                RasterArtworkCapture = _artworkCapture.SelectedIndex == 1,
                RotateSleeves = _rotateSleeves.IsChecked == true,
                CutAllow180 = _cutAllow180.IsChecked == true,
                CutSizeTags = _cutSizeTags.IsChecked == true,
                TagWidth = _tagWidth.Text, TagHeight = _tagHeight.Text,
                TagBridge = _tagBridge.Text, TagLetter = _tagLetter.Text,
                CutTagLetter = _tagCutLetter.IsChecked == true,
                TagLabelHex = _tagLabelHex.Text,
                TagFirstOffset = _tagFirstOffset.Text, TagSizeStep = _tagSizeStep.Text,
                SheetWidth = _sheetWidth.Text, Gap = _gap.Text,
                CutSheetWidth = _cutSheetWidth.Text,
                MaxPageLength = _maxPageLength.Text,
                CutMaxPageLength = _cutMaxPageLength.Text, CutGap = _cutGap.Text,
                PrintFolder = _printFolder.Text,
                CutFolder = _cutFolder.Text,
                CutDxfSingleColor = _cutDxfSingleColor.IsChecked == true,
                CutDxfColorHex = _cutDxfColorHex.Text,
                PrintDpi = _printQuality.SelectedIndex == 1 ? 300 : 150,
                NameStyle = _legacyNameStyle,
                NumberStyle = _legacyNumberStyle,
                FrontNameOnRight = _nameSide.SelectedIndex == 1,
                FrontNameCase = _frontCase.SelectedIndex,
                FrontNameTwoLines = _legacyTwoLines,
                FrontLineSpacing = _legacyLineSpacing.ToString(CultureInfo.InvariantCulture),
                WhiteText = _whiteCaptions,
                KeepGuides = _keepGuides.IsChecked == true,
                WorkflowTab = _steps.SelectedIndex, WorkflowUiVersion = 2,
                NestingMode = _nestingMode.SelectedIndex, GroupingMode = _groupingMode.SelectedIndex,
                CutNestingMode = _cutNestingMode.SelectedIndex, CutGroupingMode = _cutGroupingMode.SelectedIndex,
                PressMode = _pressMode, PressLeftMm = _pressLeftMm, PressRightMm = _pressRightMm,
                PressGrouping = _pressGrouping, PressOrientation = _pressOrientation, PressSheetWidthMm = _pressSheetWidthMm,
                PressTopMm = _pressTopMm, PressBottomMm = _pressBottomMm, PressBorders = _pressBorders
            };
        }

        private void ApplyDraft(Draft draft)
        {
            bool loading = _loading; _loading = true;
            try
            {
                _templates.Clear(); _players.Clear();
                foreach (var row in draft.Templates ?? new List<SportLayoutTemplate>())
                    _templates.Add(row);
                foreach (var row in draft.Players ?? new List<SportLayoutPlayer>())
                    _players.Add(row);
                _frontArt.Text = draft.FrontArtworkPath ?? draft.PatternPath ?? "";
                _backArt.Text = draft.BackArtworkPath ?? draft.PatternPath ?? "";
                _leftSleeveArt.Text = draft.LeftSleeveArtworkPath ?? draft.PatternPath ?? "";
                _rightSleeveArt.Text = draft.RightSleeveArtworkPath ?? "";
                _neckArt.Text = draft.NeckArtworkPath ?? "";
                _modelPath.Text = draft.ModelPath ?? "";
                int artworkLimit = draft.ArtworkMaxMegapixels ?? 150;
                _artworkLimit.SelectedItem = new[] { 100, 150, 200, 250 }.Contains(artworkLimit) ? artworkLimit : 150;
                _artworkCapture.SelectedIndex = draft.RasterArtworkCapture ? 1 : 0;
                _rotateSleeves.IsChecked = draft.RotateSleeves ?? true;
                _cutAllow180.IsChecked = draft.CutAllow180 ?? true;
                _cutSizeTags.IsChecked = draft.CutSizeTags ?? true;
                _tagWidth.Text = draft.TagWidth ?? "24";
                _tagHeight.Text = draft.TagHeight ?? "12";
                _tagBridge.Text = draft.TagBridge ?? "2";
                _tagLetter.Text = draft.TagLetter ?? "8";
                _tagCutLetter.IsChecked = draft.CutTagLetter ?? true;
                _tagLabelHex.Text = draft.TagLabelHex ?? "#000000";
                _tagFirstOffset.Text = draft.TagFirstOffset ?? "10";
                _tagSizeStep.Text = draft.TagSizeStep ?? "20";
                _sheetWidth.Text = draft.SheetWidth ?? "1500";
                _cutSheetWidth.Text = draft.CutSheetWidth ?? draft.SheetWidth ?? "1500";
                _maxPageLength.Text = draft.MaxPageLength ?? "4";
                _gap.Text = draft.Gap ?? "10";
                _cutMaxPageLength.Text = draft.CutMaxPageLength ?? draft.MaxPageLength ?? "4";
                _cutGap.Text = draft.CutGap ?? draft.Gap ?? "10";
                _printFolder.Text = draft.PrintFolder ?? "";
                _cutFolder.Text = draft.CutFolder ?? "";
                _cutDxfSingleColor.IsChecked = draft.CutDxfSingleColor;
                _cutDxfColorHex.Text = draft.CutDxfColorHex ?? "#000000";
                _printQuality.SelectedIndex = draft.PrintDpi == 300 ? 1 : 0;
                _legacyNameStyle = draft.NameStyle ?? LegacyStyle(draft.FontName, 72, draft.WhiteText);
                _legacyNumberStyle = draft.NumberStyle ?? LegacyStyle(draft.FontName, 150, draft.WhiteText);
                _whiteCaptions = draft.WhiteText;
                _nameSide.SelectedIndex = draft.FrontNameOnRight ? 1 : 0;
                _frontCase.SelectedIndex = draft.FrontNameCase >= 0 &&
                    draft.FrontNameCase <= 3 ? draft.FrontNameCase : 0;
                _legacyTwoLines = draft.FrontNameTwoLines;
                if (Double.TryParse((draft.FrontLineSpacing ?? "100").Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double spacing) &&
                    spacing >= 10 && spacing <= 300) _legacyLineSpacing = spacing;
                _keepGuides.IsChecked = draft.KeepGuides;
                int tab = draft.WorkflowUiVersion == 2 ? draft.WorkflowTab :
                    draft.WorkflowTab == 4 ? 3 : draft.WorkflowTab == 5 ? 4 : draft.WorkflowTab;
                _steps.SelectedIndex = tab >= 0 && tab < 5 ? tab : 0;
                _nestingMode.SelectedIndex = draft.NestingMode >= 0 && draft.NestingMode <= 2 ? draft.NestingMode.Value : 2;
                _groupingMode.SelectedIndex = draft.GroupingMode >= 0 && draft.GroupingMode <= 2 ? draft.GroupingMode.Value : 0;
                // Older drafts used one common choice; use it as the initial value for both operations.
                _cutNestingMode.SelectedIndex = draft.CutNestingMode >= 0 && draft.CutNestingMode <= 2
                    ? draft.CutNestingMode.Value : _nestingMode.SelectedIndex;
                _cutGroupingMode.SelectedIndex = draft.CutGroupingMode >= 0 && draft.CutGroupingMode <= 2
                    ? draft.CutGroupingMode.Value : _groupingMode.SelectedIndex;
                _pressMode = draft.PressMode >= 0 && draft.PressMode <= 2 ? draft.PressMode : 0;
                _pressGrouping = draft.PressGrouping >= 0 && draft.PressGrouping <= 2 ? draft.PressGrouping.Value : 0;
                _pressOrientation = draft.PressOrientation >= 0 && draft.PressOrientation <= 2 ? draft.PressOrientation.Value : 1;
                _pressSheetWidthMm = draft.PressSheetWidthMm.HasValue && !Double.IsNaN(draft.PressSheetWidthMm.Value) &&
                    !Double.IsInfinity(draft.PressSheetWidthMm.Value) && draft.PressSheetWidthMm >= 0 &&
                    draft.PressSheetWidthMm <= 1200 ? draft.PressSheetWidthMm.Value : 750;
                _pressLeftMm = ValidPressMargin(draft.PressLeftMm);
                _pressRightMm = ValidPressMargin(draft.PressRightMm);
                _pressTopMm = ValidPressMargin(draft.PressTopMm);
                _pressBottomMm = ValidPressMargin(draft.PressBottomMm);
                _pressBorders = draft.PressBorders ?? true;
                UpdateCutDxfColor(); UpdateTagLabelColor(); RefreshSizes(); UpdateCount();
            }
            finally { _loading = loading; }
        }

        private static DataGridTextColumn Column(string header, string path, double width)
        {
            return new DataGridTextColumn { Header = header,
                Binding = new Binding(path) { Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(width) };
        }

        private DataGridTemplateColumn PartColumn(string header, SportLayoutPart part)
        {
            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            var all = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0),
                ToolTip = "Печатать деталь «" + header + "» у всех строк. На резку не влияет." };
            _partHeaderChecks.Add(part, all);
            all.Click += (_, __) => {
                if (_busy) return;
                bool enabled = all.IsChecked == true;
                if (!_playerGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !_playerGrid.CommitEdit(DataGridEditingUnit.Row, true))
                { UpdateCount(); return; }
                bool wasLoading = _loading; _loading = true;
                try { foreach (var row in _players) row.SetPrinted(part, enabled); }
                finally { _loading = wasLoading; }
                UpdateCount(); SaveDraft();
            };
            heading.Children.Add(all); heading.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center });
            var cell = new FrameworkElementFactory(typeof(StackPanel));
            cell.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            cell.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(CheckBox.MarginProperty, new Thickness(2, 0, 4, 0));
            check.SetValue(CheckBox.ToolTipProperty, "Печатать деталь «" + header + "». На резку не влияет.");
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding(SportLayoutPlayer.PrintProperty(part)) {
                Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            cell.AppendChild(check);
            var button = new FrameworkElementFactory(typeof(System.Windows.Controls.Button));
            button.SetValue(System.Windows.Controls.Button.ContentProperty, "↗");
            button.SetValue(System.Windows.Controls.Button.ToolTipProperty,
                "Создать одну деталь для допечатки независимо от галочки: " + header);
            button.SetValue(System.Windows.Controls.Button.MinHeightProperty, 23.0);
            button.SetValue(System.Windows.Controls.Button.PaddingProperty,
                new Thickness(4, 0, 4, 0));
            button.AddHandler(System.Windows.Controls.Button.ClickEvent,
                new RoutedEventHandler((sender, e) => GenerateReplacement(sender, e, part)));
            cell.AppendChild(button);
            return new DataGridTemplateColumn { Header = heading,
                CellTemplate = new DataTemplate { VisualTree = cell },
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

        private static TextBox TagField(Panel panel, string label, string value)
        {
            var field = Field(value); field.Width = 64;
            panel.Children.Add(DockerLayout.FieldRow(label, field, 72));
            return field;
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
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2) };
        }

        private static TextBlock Label(string value)
        {
            return new TextBlock { Text = value, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 2) };
        }

        private static TextBlock Note(string value)
        {
            return new TextBlock { Text = value, FontSize = 11,
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 5) };
        }
    }
}
