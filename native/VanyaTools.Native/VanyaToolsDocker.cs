using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VanyaTools.Native
{
    public sealed class VanyaToolsDocker : UserControl
    {
        private readonly TextBlock _status;
        private readonly TextBox _trimPaddingPx;
        private readonly ComboBox _trimMode;
        private readonly System.Windows.Controls.CheckBox _trimTop;
        private readonly System.Windows.Controls.CheckBox _trimBottom;
        private readonly System.Windows.Controls.CheckBox _trimLeft;
        private readonly System.Windows.Controls.CheckBox _trimRight;
        private readonly TextBox _offsetMm;
        private readonly TextBox _rasterDpi;
        private readonly TextBox _alphaThreshold;
        private readonly System.Windows.Controls.CheckBox _useAlphaMask;
        private readonly TextBox _smoothing;
        private readonly TextBox _detail;
        private readonly TextBox _roundSpikesMm;
        private readonly TextBox _simplificationToleranceMm;
        private readonly TextBox _smoothSelectedRadiusMm;
        private readonly TextBox _smoothSelectedSimplificationMm;
        private readonly TextBox _tabWidthMm;
        private readonly TextBox _tabHeightMm;
        private readonly TextBox _tabRadiusMm;
        private readonly System.Windows.Controls.CheckBox _mergeAdjacentContours;
        private readonly System.Windows.Controls.RadioButton _rotatePackClockwise;
        private readonly System.Windows.Controls.RadioButton _rotatePackCounterClockwise;
        private readonly CorelHotkeyManager _hotkeys;
        private readonly DockerUiWatchdog _uiWatchdog;
        private static WeakReference<VanyaToolsDocker> _toolbarTarget;
        private PrintFrameAnchor _printFrameAnchor = PrintFrameAnchor.TopCenter;
        private static readonly string TabsPlacementPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "tabs-placement.txt");

        public VanyaToolsDocker()
            : this(null)
        {
        }

        public VanyaToolsDocker(object app)
        {
            CorelApp.SetHostApplication(app);
            _toolbarTarget = new WeakReference<VanyaToolsDocker>(this);
            Log.Info("VanyaToolsDocker constructor started.");
            _uiWatchdog = new DockerUiWatchdog(Dispatcher, "Main", this);
            Loaded += (_, __) => _uiWatchdog.Start();
            Unloaded += (_, __) => _uiWatchdog.Stop("unloaded");

            var outer = new Grid
            {
                Margin = new Thickness(6)
            };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            DockerTheme.Apply(this);
            DockerTheme.UseTheme(outer, Panel.BackgroundProperty, DockerTheme.WindowBackground);
            Content = outer;

            var heading = new TextBlock
            {
                Text = "Vanya Tools · " + typeof(VanyaToolsDocker).Assembly.GetName().Version.ToString(3),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            };
            var headingRow = new DockPanel();
            headingRow.Children.Add(heading);
            outer.Children.Add(headingRow);

            _status = new TextBlock
            {
                Text = "Готово",
                FontSize = 11,
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(0, 0, 0, 3),
                TextWrapping = TextWrapping.Wrap
            };
            var statusFrame = new Border
            {
                CornerRadius = new CornerRadius(5),
                Child = _status
            };
            DockerTheme.UseTheme(statusFrame, Border.BackgroundProperty, DockerTheme.StatusBackground);
            Grid.SetRow(statusFrame, 1);
            outer.Children.Add(statusFrame);

            var standardTools = new StackPanel { Margin = new Thickness(5) };
            var stickerTools = new StackPanel { Margin = new Thickness(5) };
            var aiTools = new AiPrintTab(ImportAiResult,
                (message, isError) => SetStatus(message, isError), CaptureAiSelection);
            var orderCards = new OrderCardTab((message, isError) => SetStatus(message, isError));
            var connectionIndicators = new ConnectionIndicators(() => aiTools.GetToken(), orderCards.LabelPrinterStatus, RunGitHubUpdate);
            DockPanel.SetDock(connectionIndicators, Dock.Right);
            headingRow.Children.Insert(0, connectionIndicators);
            var standardPage = new DockPanel();
            DockPanel.SetDock(orderCards.QuickDocumentFields, Dock.Bottom);
            standardPage.Children.Add(orderCards.QuickDocumentFields);
            standardPage.Children.Add(new ScrollViewer { Content = standardTools,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var approvalCopy = new ApprovalCopyTool((message, isError) => SetStatus(message, isError));
            var photoshop = new PhotoshopExportTool(SetStatus);
            var settings = new StackPanel { Margin = new Thickness(5) };
            var outlineSettings = DtfOutlineSettings.Load();
            var filmRipSettings = FilmRipSettings.Load();
            var printExportSettings = PrintExportSettings.Load();
            var printExport = new PrintExportTab(printExportSettings, SetStatus);
            var interfaceSettings = new StackPanel();
            interfaceSettings.Children.Add(DockerTheme.CreateSettingsView(SetStatus));
            interfaceSettings.Children.Add(DockerRenderingCompatibility.CreateSettingsView(SetStatus));
            interfaceSettings.Children.Add(Button("Обновить Vanya Tools", (_, __) => RunGitHubUpdate()));
            interfaceSettings.Children.Add(Button("Восстановить кнопки на панели Corel", (_, __) =>
            {
                try { SetStatus(CorelToolbarInstaller.RestoreButtons(), false); }
                catch (Exception error)
                {
                    Log.Error("Could not restore Vanya Tools toolbar buttons.", error);
                    SetStatus("Не удалось восстановить кнопки: " + error.Message, true);
                }
            }));
            interfaceSettings.Children.Add(Button("Показать / скрыть кнопки действий", (_, __) =>
            {
                try
                {
                    SetStatus(CorelToolbarInstaller.ToggleButtons(), false);
                }
                catch (Exception error)
                {
                    Log.Error("Could not toggle Vanya Tools toolbar buttons.", error);
                    SetStatus("Не удалось переключить кнопки: " + error.Message, true);
                }
            }));
            var tabsPlacement = LoadTabsPlacement();
            var tabsPlacementSettings = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            var horizontalTabsButton = AddRadioButton(tabsPlacementSettings,
                "Горизонтально (сверху)", "DockerTabsPlacement", tabsPlacement == Dock.Top);
            var leftTabsButton = AddRadioButton(tabsPlacementSettings,
                "Вертикально (слева)", "DockerTabsPlacement", tabsPlacement == Dock.Left);
            var rightTabsButton = AddRadioButton(tabsPlacementSettings,
                "Вертикально (справа)", "DockerTabsPlacement", tabsPlacement == Dock.Right);
            interfaceSettings.Children.Add(new TextBlock { Text = "Расположение вкладок", FontSize = 11,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 6, 4, 2) });
            interfaceSettings.Children.Add(tabsPlacementSettings);
            settings.Children.Add(DockerSettings.Section("Обновление и интерфейс", interfaceSettings));
            _hotkeys = new CorelHotkeyManager(() => RunTrim(true), RunFitFrame,
                approvalCopy.CopySelection,
                (message, isError) => SetStatus(message, isError));
            settings.Children.Add(_hotkeys.SettingsView);
            settings.Children.Add(approvalCopy.SettingsView);
            settings.Children.Add(photoshop.SettingsView);
            settings.Children.Add(DockerSettings.Format(printExportSettings.CreateSettingsView(SetStatus), "Печать PDF, DTF и UV"));
            settings.Children.Add(DockerSettings.Format(filmRipSettings.CreateSettingsView(SetStatus), "Фотовывод · RIP"));
            settings.Children.Add(DockerSettings.Format(outlineSettings.CreateSettingsView(SetStatus), "Пресеты обводки ДТФ"));
            settings.Children.Add(aiTools.SettingsView);
            settings.Children.Add(orderCards.SettingsView);
            Loaded += (_, __) =>
            {
                _hotkeys.Attach(this);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    using (DockerUiWatchdog.Trace(Dispatcher, "CorelToolbarInstaller.RemoveLegacyDuplicates"))
                        CorelToolbarInstaller.RemoveLegacyDuplicates();
                    using (DockerUiWatchdog.Trace(Dispatcher, "CorelToolbarInstaller.ApplySavedVisibility"))
                        CorelToolbarInstaller.ApplySavedVisibility();
                }),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            Unloaded += (_, __) => _hotkeys.Detach();

            var tabs = new TabControl
            {
                MinHeight = 360,
                TabStripPlacement = tabsPlacement
            };
            horizontalTabsButton.Checked += (_, __) => SetTabsPlacement(tabs, Dock.Top);
            leftTabsButton.Checked += (_, __) => SetTabsPlacement(tabs, Dock.Left);
            rightTabsButton.Checked += (_, __) => SetTabsPlacement(tabs, Dock.Right);
            tabs.Items.Add(ThumbnailTabs.Create("Основная", ThumbnailTabs.Crop,
                Color.FromRgb(46, 112, 124), standardPage));
            tabs.Items.Add(ThumbnailTabs.Create("Публикация", ThumbnailTabs.Publish,
                Color.FromRgb(57, 120, 138),
                new TabControl
                {
                    Items =
                    {
                        new TabItem { Header = "Карточка заказа", Content = orderCards },
                        new TabItem { Header = "Ссылка на принт", Content =
                            new PreviewLibraryTab((message, isError) => SetStatus(message, isError)) }
                    }
                }));
            tabs.Items.Add(ThumbnailTabs.Create("Заказы", ThumbnailTabs.Orders,
                Color.FromRgb(68, 126, 104), orderCards.OrdersView));
            tabs.Items.Add(ThumbnailTabs.Create("Стикерпаки", ThumbnailTabs.Stickers,
                Color.FromRgb(171, 111, 43),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stickerTools }));
            tabs.Items.Add(ThumbnailTabs.Create("Развёртка формы", ThumbnailTabs.Shirt,
                Color.FromRgb(74, 114, 174),
                new SublimationTab(SublimationInsertion.Import, (message, isError) => SetStatus(message, isError),
                    SublimationInsertion.Capture, () => aiTools.GetToken())));
            tabs.Items.Add(ThumbnailTabs.Create("Раскладка спортформы", ThumbnailTabs.SportLayout,
                Color.FromRgb(54, 126, 132),
                new SportLayoutTab((message, isError) => SetStatus(message, isError))));
            tabs.Items.Add(ThumbnailTabs.Create("Фамилии", ThumbnailTabs.Names,
                Color.FromRgb(89, 135, 74),
                new NamesGridTab((message, isError) => SetStatus(message, isError))));
            tabs.Items.Add(ThumbnailTabs.Create("Цветопроба", ThumbnailTabs.ColorProof,
                Color.FromRgb(130, 106, 65),
                new ColorProofTab((message, isError) => SetStatus(message, isError))));
            tabs.Items.Add(ThumbnailTabs.Create("Фотовывод", ThumbnailTabs.FilmOutput,
                Color.FromRgb(35, 35, 35), new FilmOutputTab(() => orderCards.FilmOutputYearFolder, filmRipSettings, SetStatus)));
            tabs.Items.Add(ThumbnailTabs.Create("Печать", ThumbnailTabs.Print,
                Color.FromRgb(56, 105, 145), printExport));
            Func<SimpleAiMode, ScrollViewer> photoTools = mode =>
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new SimpleAiTab(() => AiSelectionCapture.Capture(4096, 16000000),
                        () => aiTools.GetToken(), ImportAiResult,
                        (message, isError) => SetStatus(message, isError), mode, ImportDtfResult, outlineSettings) };
            tabs.Items.Add(ThumbnailTabs.Create("Фото и ИИ", ThumbnailTabs.Style,
                Color.FromRgb(167, 83, 123),
                new TabControl
                {
                    Items =
                    {
                        new TabItem { Header = "Заливка", ToolTip = "Генеративная заливка выделенной области", Content =
                            new GenerativeFillTab(() => aiTools.GetToken(), ImportGenerativeFillResult,
                                (message, isError) => SetStatus(message, isError)) },
                        new TabItem { Header = "ИИ промпт", Content = photoTools(SimpleAiMode.Prompt) },
                        new TabItem { Header = "ИИ стили", Content = photoTools(SimpleAiMode.AiStyles) },
                        new TabItem { Header = "Стили", ToolTip = "Обычные эффекты фото без интернета", Content =
                            photoTools(SimpleAiMode.LocalStyles) }
                    }
                }));
            tabs.Items.Add(ThumbnailTabs.Create("Настройки", ThumbnailTabs.Settings,
                Color.FromRgb(91, 104, 116),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = settings }));
            tabs.Items.Add(ThumbnailTabs.Create("Экспериментальное", ThumbnailTabs.Experiment,
                Color.FromRgb(120, 92, 139), new TabControl { Items = {
                    new TabItem { Header = "Рулетка", Content = new CurveMeasureTab(SetStatus) },
                    new TabItem { Header = "Шум ДТФ", Content = new DtfNoiseTab(ImportDtfResult, SetStatus) },
                    new TabItem { Header = "Шрифт", Content = new FontSearchTab(aiTools.FontToolsView, SetStatus) },
                    new TabItem { Header = "Восстановление принта", Content = aiTools }
                } }));
            tabs.SelectedIndex = 0;
            Grid.SetRow(tabs, 2);
            outer.Children.Add(tabs);
            Panel root = standardTools;

            var rasterTools = DockerLayout.ToolGroup("Растр и рамка");
            root.Children.Add(rasterTools);
            var quickActions = new Grid();
            quickActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            quickActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            quickActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quickActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quickActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var trimButton = Button("Обрезать растр", (_, __) => RunTrim(true));
            trimButton.MinHeight = 27;
            trimButton.Margin = new Thickness(0, 0, 3, 2);
            quickActions.Children.Add(trimButton);
            var fitFrame = Button("Подогнать рамку", (_, __) => RunFitFrame());
            fitFrame.MinHeight = 27;
            fitFrame.Margin = new Thickness(0, 0, 3, 2);
            fitFrame.ToolTip = "Выделите части принта, затем рамку последней через Shift+щелчок. Точку привязки выберите справа.";
            Grid.SetColumn(fitFrame, 1);
            quickActions.Children.Add(fitFrame);
            var anchorPicker = CreatePrintFrameAnchorPicker();
            Grid.SetColumn(anchorPicker, 2);
            Grid.SetRowSpan(anchorPicker, 2);
            quickActions.Children.Add(anchorPicker);
            var approvalButton = Button("Копировать согласование", (_, __) => approvalCopy.CopySelection());
            approvalButton.MinHeight = 27;
            approvalButton.Margin = new Thickness(0, 0, 3, 2);
            approvalButton.ToolTip = "Крупное изображение выделения с красной подписью для согласования. Ctrl+Alt+C.";
            Grid.SetRow(approvalButton, 1);
            Grid.SetColumnSpan(approvalButton, 2);
            quickActions.Children.Add(approvalButton);
            rasterTools.Children.Add(quickActions);
            _trimMode = new ComboBox { ItemsSource = new[] { "Обрезка: прозрачные поля", "Обрезка: цвет угла ↖", "Обрезка: цвет угла ↘" },
                SelectedIndex = 0, FontSize = 11, MinHeight = 27, Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "Для однотонного фона выберите цвет угла. Обрезаются внешние поля; фон внутри рисунка удаляется кнопкой «Удалить фон…»." };
            rasterTools.Children.Add(_trimMode);
            rasterTools.Children.Add(photoshop.ExportButton);
            var trimSettings = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            rasterTools.Children.Add(new Expander { Header = "Параметры обрезки", Content = trimSettings,
                FontSize = 11, IsExpanded = false, Margin = new Thickness(1, 2, 0, 0) });
            AddSmallLabel(trimSettings, "Обрезать стороны");
            var sidesGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 5) };
            _trimTop = AddCheckBox(sidesGrid, "Сверху", true);
            _trimLeft = AddCheckBox(sidesGrid, "Слева", true);
            _trimBottom = AddCheckBox(sidesGrid, "Снизу", true);
            _trimRight = AddCheckBox(sidesGrid, "Справа", true);
            trimSettings.Children.Add(sidesGrid);
            _trimPaddingPx = AddRow(trimSettings, "Отступ, px", "2");

            var rasterEditor = new SimpleAiTab(() => AiSelectionCapture.Capture(4096, 16000000),
                () => aiTools.GetToken(), ImportAiResult,
                (message, isError) => SetStatus(message, isError), SimpleAiMode.Raster, ImportDtfResult, outlineSettings,
                printExport.QuickButtons);
            root.Children.Add(rasterEditor);
            settings.Children.Insert(1, rasterEditor.RasterSettingsView);

            root = stickerTools;
            var sizeTools = DockerLayout.ToolGroup("1. Размер и метки", "Выделите весь пак и нажмите его размер. " +
                "Кнопка ставит метки 1×1 мм и при необходимости поворачивает и пропорционально уменьшает пак. " +
                "Внутренняя рамка 4 мм не печатается. Размеры указаны в миллиметрах.");
            root.Children.Add(sizeTools);
            var markPresetGrid = new UniformGrid { Columns = 3 };
            markPresetGrid.Children.Add(StickerPreset("S", 48, 60));
            markPresetGrid.Children.Add(StickerPreset("M", 105, 142));
            markPresetGrid.Children.Add(StickerPreset("L", 142, 195));
            sizeTools.Children.Add(markPresetGrid);
            var sizeSettings = new UniformGrid { Columns = 2 };
            sizeTools.Children.Add(StickerOptions("Поворот пака", sizeSettings));
            _rotatePackClockwise = AddRadioButton(sizeSettings, "Вправо", "PackRotationDirection", true);
            _rotatePackClockwise.ToolTip = "Альбомный пак: повернуть на 90° по часовой стрелке";
            _rotatePackCounterClockwise = AddRadioButton(sizeSettings, "Влево", "PackRotationDirection", false);
            _rotatePackCounterClockwise.ToolTip = "Альбомный пак: повернуть на 90° против часовой стрелки";

            var contourTools = DockerLayout.ToolGroup("2. Контур реза", "Для создания контура выделите стикеры. " +
                "«Сгладить» обрабатывает уже готовый выделенный контур и использует отдельные параметры. " +
                "Если эффект сглаживания слабый, увеличьте его радиус или допуск.");
            root.Children.Add(contourTools);
            var contourActions = new UniformGrid { Columns = 2 };
            contourActions.Children.Add(StickerAction("Создать контур", "Создать контур реза для выделенных стикеров",
                (_, __) => RunCutContour(), true));
            contourActions.Children.Add(StickerAction("Сгладить", "Сгладить выбранный готовый контур",
                (_, __) => RunSmoothSelectedContour()));
            contourTools.Children.Add(contourActions);
            var contourSettings = new StackPanel();
            contourTools.Children.Add(StickerOptions("Параметры создания", contourSettings));
            var contourFields = new UniformGrid { Columns = 2 };
            contourSettings.Children.Add(contourFields);
            _offsetMm = AddStickerField(contourFields, "Отступ, мм", "2");
            _rasterDpi = AddStickerField(contourFields, "Растр, DPI", "300");
            _smoothing = AddStickerField(contourFields, "Сглаживание", "70");
            _detail = AddStickerField(contourFields, "Детализация", "35");
            _roundSpikesMm = AddStickerField(contourFields, "Скругление, мм", "0.7");
            _simplificationToleranceMm = AddStickerField(contourFields, "Упрощение, мм", "0.05",
                "Допуск упрощения создаваемого контура, мм");
            var alphaSettings = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _useAlphaMask = AddCheckBox(alphaSettings, "По альфа-маске", true);
            _useAlphaMask.ToolTip = "Трассировать растр по маске прозрачности";
            _alphaThreshold = new TextBox { Text = "10", MinHeight = 27, FontSize = 11,
                TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 1, 3, 1), ToolTip = "Порог альфа-маски · 0–254" };
            System.Windows.Automation.AutomationProperties.SetName(_alphaThreshold, "Порог альфа-маски, 0–254");
            contourSettings.Children.Add(DockerLayout.Trailing(alphaSettings,
                DockerLayout.FieldRow("Порог", _alphaThreshold, 56)));
            _mergeAdjacentContours = AddCheckBox(contourSettings, "Сливать соседние объекты", false);
            var smoothingSettings = new UniformGrid { Columns = 2 };
            contourTools.Children.Add(StickerOptions("Параметры сглаживания", smoothingSettings));
            _smoothSelectedRadiusMm = AddStickerField(smoothingSettings, "Радиус, мм", "1.5",
                "Радиус скругления выбранного готового контура, мм");
            _smoothSelectedSimplificationMm = AddStickerField(smoothingSettings, "Упрощение, мм", "0.2",
                "Допуск упрощения выбранного готового контура, мм");

            var tabTools = DockerLayout.ToolGroup("3. Язычки", "Выделите контур и добавьте маркеры — их можно двигать. " +
                "«Переприкрепить» заново привязывает маркеры к контуру. Для применения выделите контур или маркер. " +
                "Ширина, длина и радиус общие для добавления, переприкрепления и применения язычков.");
            root.Children.Add(tabTools);
            var markerActions = new UniformGrid { Columns = 2 };
            markerActions.Children.Add(StickerAction("Добавить маркеры", "Добавить подвижные маркеры язычков",
                (_, __) => RunAddPeelMarker()));
            markerActions.Children.Add(StickerAction("Переприкрепить", "Переприкрепить маркеры язычков к контуру",
                (_, __) => RunReattachMarkers()));
            tabTools.Children.Add(markerActions);
            var tabSettings = new UniformGrid { Columns = 3 };
            tabTools.Children.Add(StickerOptions("Параметры язычков", tabSettings));
            _tabWidthMm = AddStickerField(tabSettings, "Ширина, мм", "4");
            _tabHeightMm = AddStickerField(tabSettings, "Длина, мм", "12");
            _tabRadiusMm = AddStickerField(tabSettings, "Радиус, мм", "2", "Радиус скругления язычка, мм");
            tabTools.Children.Add(StickerAction("Применить язычки", "Применить язычки по маркерам к выбранному контуру",
                (_, __) => RunApplyPeelTabs(), true));

            var maintenance = new UniformGrid { Columns = 2 };
            root.Children.Add(StickerOptions("Удаление", maintenance));
            maintenance.Children.Add(StickerAction("Удалить контуры", "Удалить контуры выбранного пака",
                (_, __) => RunDeletePackContours()));
            maintenance.Children.Add(StickerAction("Удалить маркеры", "Удалить маркеры выбранного пака",
                (_, __) => RunDeletePackMarkers()));
            Log.Info("VanyaToolsDocker constructor finished.");
        }

        private string ImportAiResult(string path, string operation) { return ImportAiResult(path, operation, true); }
        private string ImportDtfResult(string path, string operation) { return ImportAiResult(path, operation, false); }

        private string ImportGenerativeFillResult(string path, string operation,
            GenerativeFillCapture.Prepared prepared)
        {
            if (prepared == null || prepared.WidthMm <= 0 || prepared.HeightMm <= 0)
                throw new InvalidOperationException("Положение результата AI не сохранено.");
            string warning = ImportAiResult(path, operation, false);
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            dynamic shape = app.ActiveShape;
            if (doc == null || shape == null || Convert.ToInt32(shape.Type) != CorelConstants.CdrBitmapShape)
                return (warning ?? "Результат вставлен.") +
                    " CorelDRAW не вернул импортированный растр для размещения; проверьте его размер и положение.";
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                shape.SetSize(prepared.WidthMm, prepared.HeightMm);
                shape.Move(prepared.LeftMm + 10.0 - Convert.ToDouble(shape.LeftX),
                    prepared.TopMm + 10.0 - Convert.ToDouble(shape.TopY));
            }
            catch (Exception placementError)
            {
                Log.Error("Generative fill result inserted but placement failed.", placementError);
                warning = (warning ?? "Результат вставлен.") +
                    " Не удалось выставить исходный размер и положение: " + placementError.Message;
            }
            finally { doc.Unit = oldUnit; }
            try { app.ActiveWindow.Refresh(); } catch { }
            return warning;
        }

        private string ImportAiResult(string path, string operation, bool trimAfterImport)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("Файл результата AI не найден. Сначала выполните обработку.", path);

                dynamic app = CorelApp.Get();
                dynamic doc = app.ActiveDocument;
                double sourceLeftMm = 0, sourceTopMm = 0;
                bool hasSourcePosition = false;
                try
                {
                    dynamic source = app.ActiveSelectionRange;
                    if (source != null && Convert.ToInt32(source.Count) > 0)
                    {
                        int oldUnit = Convert.ToInt32(doc.Unit);
                        try
                        {
                            doc.Unit = CorelConstants.CdrMillimeter;
                            sourceLeftMm = Convert.ToDouble(source.LeftX);
                            sourceTopMm = Convert.ToDouble(source.TopY);
                            hasSourcePosition = true;
                        }
                        finally { doc.Unit = oldUnit; }
                    }
                }
                catch (Exception positionError)
                {
                    Log.Error("Could not read source position for raster import.", positionError);
                }
                // Use Automation marshaling explicitly, including a real options object.
                // The DLR call with null options fails with DISP_E_TYPEMISMATCH on Corel 27.
                object layer = doc.ActiveLayer;
                object options = app.CreateStructImportOptions();
                Log.Info("AI result ImportEx starting: " + Path.GetFileName(path));
                dynamic importFilter = layer.GetType().InvokeMember("ImportEx",
                    BindingFlags.InvokeMethod, null, layer,
                    new object[] { new BStrWrapper(Path.GetFullPath(path)), 0, options });
                importFilter.Finish();
                Log.Info("AI result ImportEx finished: " + Path.GetFileName(path));
                dynamic importedShape = null;
                try { importedShape = app.ActiveShape; } catch { }
                // DTF outputs are already cropped on the worker thread. Other
                // results still use Corel's alpha-aware crop after import.
                string trimWarning = null;
                if (trimAfterImport) try
                {
                    var trim = new BitmapTrimService().TrimSelected(
                        TrimMode.TransparentPixels, TrimSides.All, 2, true);
                    Log.Info("AI result transparent-edge trim: trimmed=" + trim.Trimmed + ", skipped=" + trim.Skipped + ".");
                }
                catch (Exception trimError)
                {
                    Log.Error("AI image inserted, but transparent-edge trimming failed.", trimError);
                    trimWarning = "Изображение вставлено. Не удалось обрезать прозрачные поля: " + trimError.Message;
                }
                try
                {
                    if (importedShape != null && Convert.ToInt32(importedShape.Type) == CorelConstants.CdrBitmapShape)
                    {
                        importedShape.Name = "Vanya Tools · " + (String.IsNullOrWhiteSpace(operation) ? "Обработка растра" : operation.Trim()) +
                            " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        if (hasSourcePosition)
                        {
                            int oldUnit = Convert.ToInt32(doc.Unit);
                            try
                            {
                                doc.Unit = CorelConstants.CdrMillimeter;
                                importedShape.Move(sourceLeftMm + 10.0 - Convert.ToDouble(importedShape.LeftX),
                                    sourceTopMm + 10.0 - Convert.ToDouble(importedShape.TopY));
                            }
                            finally { doc.Unit = oldUnit; }
                        }
                    }
                }
                catch (Exception placementError)
                {
                    Log.Error("Raster inserted, but naming or offset failed.", placementError);
                    trimWarning = (trimWarning ?? "Изображение вставлено.") +
                        " Не удалось задать имя или сдвиг: " + placementError.Message;
                }
                // A refresh failure must not turn a completed import into a retry/duplicate.
                if (trimAfterImport) try { app.ActiveWindow.Refresh(); } catch { }
                if (String.IsNullOrEmpty(trimWarning)) SetStatus("AI-результат импортирован в Corel: " + Path.GetFileName(path), false);
                return trimWarning;
            }
            catch (Exception ex)
            {
                Log.Error("AI result import failed.", ex);
                throw;
            }
        }

        private string CaptureAiSelection()
        {
            string path = AiSelectionCapture.Capture();
            SetStatus("Выделение Corel подготовлено для AI · PNG, прозрачный фон.", false);
            return path;
        }
        private void RunTrim(bool useTiles)
        {
            RunSafe(() =>
            {
                var mode = (TrimMode)Math.Max(0, Math.Min(2, _trimMode.SelectedIndex));

                var sides = TrimSides.None;
                if (_trimTop.IsChecked    == true) sides |= TrimSides.Top;
                if (_trimBottom.IsChecked == true) sides |= TrimSides.Bottom;
                if (_trimLeft.IsChecked   == true) sides |= TrimSides.Left;
                if (_trimRight.IsChecked  == true) sides |= TrimSides.Right;

                int padding = ParseInt(_trimPaddingPx.Text, 2);
                var result = new BitmapTrimService().TrimSelected(mode, sides, padding, useTiles);
                string m = useTiles ? "тайлы" : "пиксели";
                string hint = result.Trimmed == 0 && mode == TrimMode.TransparentPixels
                    ? " Прозрачных полей не найдено. Для однотонного фона выберите «цвет угла» под кнопками обрезки." : "";
                SetStatus($"Обрезано ({m}): {result.Trimmed}. Пропущено: {result.Skipped}." + hint, false);
            });
        }

        private void RunFitFrame()
        {
            RunSafe(() =>
            {
                int parts = PrintFrameFitService.FitSelected(_printFrameAnchor);
                SetStatus(parts > 1 ? "Части принта сгруппированы; рамка подогнана."
                    : "Рамка подогнана под принт.", false);
            });
        }

        internal static bool TryRunToolbarTrim()
        {
            VanyaToolsDocker docker;
            if (_toolbarTarget == null || !_toolbarTarget.TryGetTarget(out docker)) return false;
            docker.RunTrim(true);
            return true;
        }

        internal static bool TryRunToolbarFitFrame()
        {
            VanyaToolsDocker docker;
            if (_toolbarTarget == null || !_toolbarTarget.TryGetTarget(out docker)) return false;
            docker.RunFitFrame();
            return true;
        }

        private void RunCutContour()
        {
            RunSafe(() =>
            {
                double offset = ParseDouble(_offsetMm.Text, 2);
                int dpi = ParseInt(_rasterDpi.Text, 300);
                int smoothing = ParseInt(_smoothing.Text, 70);
                int detail = ParseInt(_detail.Text, 25);
                double roundSpikes = ParseDouble(_roundSpikesMm.Text, 0.7);
                double simplifyToleranceMm = ParseDouble(_simplificationToleranceMm.Text, 0.05);

                bool mergeAdjacent = _mergeAdjacentContours.IsChecked == true;
                bool useAlphaMask = _useAlphaMask.IsChecked == true;
                int alphaThreshold = ParseInt(_alphaThreshold.Text, 10);
                var cutService = new StickerCutService();
                cutService.CreateCutContour(offset, dpi, smoothing, detail, roundSpikes, simplifyToleranceMm, mergeAdjacent, useAlphaMask, alphaThreshold);
                string maskStatus = cutService.LastUsedAlphaMask ? $"применена, порог {alphaThreshold}" : "не применена";
                SetStatus($"Контур реза создан: {offset:0.###} мм. Альфа-маска: {maskStatus}. Слияние соседних объектов: {(mergeAdjacent ? "вкл." : "выкл.")}", false);
            });
        }

        private void RunSmoothSelectedContour()
        {
            RunSafe(() =>
            {
                double roundSpikes = ParseDouble(_smoothSelectedRadiusMm.Text, 1.5);
                double simplifyToleranceMm = ParseDouble(_smoothSelectedSimplificationMm.Text, 0.2);
                var service = new StickerCutService();
                int processed = service.SmoothSelectedContours(roundSpikes, simplifyToleranceMm);
                SetStatus($"Сглажено контуров: {processed}. Узлов: {service.LastSmoothNodesBefore} → {service.LastSmoothNodesAfter}. Имена паков сохранены.", false);
            });
        }

        private void RunDeletePackContours()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().DeletePackContours();
                SetStatus($"Удалено контуров: {count}. Создайте контур заново.", false);
            });
        }

        private void RunDeletePackMarkers()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().DeletePackMarkers();
                SetStatus($"Удалено маркеров: {count}.", false);
            });
        }

        private void RunReattachMarkers()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().ReattachMarkers(
                    ParseDouble(_tabWidthMm.Text, 4),
                    ParseDouble(_tabHeightMm.Text, 12),
                    ParseDouble(_tabRadiusMm.Text, 2));
                SetStatus($"Переприкреплено маркеров: {count}.", false);
            });
        }

        private void RunAddPeelMarker()
        {
            RunSafe(() =>
            {
                var service = new PeelTabService();
                int count = service.AddMarker(
                    ParseDouble(_tabWidthMm.Text, 4),
                    ParseDouble(_tabHeightMm.Text, 12),
                    ParseDouble(_tabRadiusMm.Text, 2));
                string skipped = service.LastSkippedCount > 0
                    ? $" Пропущено без свободного места: {service.LastSkippedCount}."
                    : "";
                SetStatus($"Маркеры добавлены: {count}.{skipped} Нажмите «Применить язычки».", false);
            });
        }

        private void RunApplyPeelTabs()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().ApplyMarkers(
                    ParseDouble(_tabWidthMm.Text, 4),
                    ParseDouble(_tabHeightMm.Text, 12),
                    ParseDouble(_tabRadiusMm.Text, 2));
                SetStatus($"Применено язычков: {count}.", false);
            });
        }

        private void RunCreateCutMarks(string preset, double widthMm, double heightMm)
        {
            RunSafe(() =>
            {
                double scaleFactor;
                bool rotatedToPortrait;
                bool rotateClockwise = _rotatePackClockwise.IsChecked == true;
                int count = new CutMarkService().CreateMarks(preset, widthMm, heightMm, rotateClockwise, out scaleFactor, out rotatedToPortrait);
                string orientationMessage = rotatedToPortrait
                    ? $" Пак повернут на 90° {(rotateClockwise ? "по часовой" : "против часовой")} в книжную ориентацию."
                    : "";
                string resizeMessage = scaleFactor < 0.999999
                    ? $" Пак уменьшен пропорционально до {scaleFactor * 100:0.#}%."
                    : " Размер пака уже помещается во внутреннюю рамку.";
                SetStatus($"Метки {preset} {widthMm:0}×{heightMm:0} мм созданы: {count} отрезков; внутренняя рамка 4 мм не печатается.{orientationMessage}{resizeMessage}", false);
            });
        }
        private void RunGitHubUpdate()
        {
            var answer = MessageBox.Show(
                "Updater проверит номер версии и скачает пакет, только если доступно обновление. Сохраните документы; после загрузки закройте CorelDRAW для установки.",
                "Обновление Vanya Tools",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                string updaterHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VanyaTools");
                Directory.CreateDirectory(updaterHome);
                string version = typeof(VanyaToolsDocker).Assembly.GetName().Version.ToString(3);
                string updaterPath = Path.Combine(updaterHome, "VanyaTools.Updater.exe");
                if (!File.Exists(updaterPath))
                    throw new InvalidOperationException("Updater не найден. Переустановите пакет Vanya Tools версии 1.0.14 или новее.");
                string corelExecutable = Process.GetCurrentProcess().MainModule.FileName;
                string programsPath = Path.GetDirectoryName(corelExecutable);
                if (!String.Equals(Path.GetFileName(corelExecutable), "CorelDRW.exe", StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(Path.GetFileName(programsPath), "Programs64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Не удалось определить папку запущенного CorelDRAW.");
                string addonsPath = Path.Combine(programsPath, "Addons");

                var startInfo = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = "--installed-version " + version + " --corel-addons-path \"" + addonsPath + "\"",
                    WorkingDirectory = updaterHome,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                SetStatus($"Updater {version} запущен. Сохраните документы и закройте CorelDRAW.", false);
            }
            catch (Exception ex)
            {
                Log.Error("Could not start GitHub updater.", ex);
                SetStatus(ex.Message, true);
            }
        }
        private void RefreshSelectionStatus()
        {
            RunSafe(() =>
            {
                dynamic app = CorelApp.Get();
                int count = (int)app.ActiveSelectionRange.Count;
                SetStatus(count > 0 ? $"Selected objects: {count}" : "No selection", false);
            });
        }

        private void RunSafe(Action action, [CallerMemberName] string operation = null)
        {
            try
            {
                using (DockerUiWatchdog.Trace(Dispatcher, "Main command: " + operation)) action();
            }
            catch (Exception ex)
            {
                Log.Error("Command failed.", ex);
                SetStatus(ex.Message, true);
            }
        }

        private void SetStatus(string text, bool isError)
        {
            _uiWatchdog?.Note(text);
            _status.Text = text;
            DockerTheme.UseTheme(_status, TextBlock.ForegroundProperty,
                isError ? DockerTheme.ErrorTextBrush : DockerTheme.TextBrush);
        }

        private static Dock LoadTabsPlacement()
        {
            try
            {
                if (!File.Exists(TabsPlacementPath)) return Dock.Top;
                string saved = File.ReadAllText(TabsPlacementPath).Trim();
                if (String.Equals(saved, "right", StringComparison.OrdinalIgnoreCase)) return Dock.Right;
                if (String.Equals(saved, "vertical", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(saved, "left", StringComparison.OrdinalIgnoreCase)) return Dock.Left;
                return Dock.Top;
            }
            catch (Exception error)
            {
                Log.Error("Could not read docker tabs placement.", error);
                return Dock.Top;
            }
        }

        private void SetTabsPlacement(TabControl tabs, Dock placement)
        {
            tabs.TabStripPlacement = placement;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TabsPlacementPath));
                File.WriteAllText(TabsPlacementPath,
                    placement == Dock.Right ? "right" : placement == Dock.Left ? "left" : "horizontal");
            }
            catch (Exception error)
            {
                Log.Error("Could not save docker tabs placement.", error);
                SetStatus("Расположение вкладок изменено, но не сохранено: " + error.Message, true);
            }
        }

        private Button StickerPreset(string name, double widthMm, double heightMm)
        {
            string size = widthMm.ToString("0", CultureInfo.InvariantCulture) + "×" +
                heightMm.ToString("0", CultureInfo.InvariantCulture);
            var caption = new StackPanel();
            caption.Children.Add(new TextBlock { Text = name, FontSize = 11, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center });
            caption.Children.Add(new TextBlock { Text = size, FontSize = 11, TextAlignment = TextAlignment.Center });
            var button = StickerAction(name, "Поставить метки " + name + " · " + size + " мм",
                (_, __) => RunCreateCutMarks(name, widthMm, heightMm));
            button.Content = caption; button.MinHeight = 37;
            return button;
        }

        private static Button StickerAction(string title, string help, RoutedEventHandler action, bool primary = false)
        {
            var button = Button(title, action);
            button.MinHeight = 27; button.Margin = new Thickness(2, 1, 2, 2); button.ToolTip = help;
            System.Windows.Automation.AutomationProperties.SetName(button, help);
            return primary ? DockerTheme.Primary(button) : button;
        }

        private static Expander StickerOptions(string title, Panel content)
        {
            content.Margin = new Thickness(4, 4, 4, 4);
            return new Expander { Header = new TextBlock { Text = title, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                IsExpanded = false, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(1, 2, 1, 3) };
        }

        private static TextBox AddStickerField(Panel fields, string title, string value, string help = null)
        {
            var input = new TextBox { Text = value, FontSize = 11, MinHeight = 27,
                TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 1, 3, 1), ToolTip = help ?? title };
            System.Windows.Automation.AutomationProperties.SetName(input, help ?? title);
            var field = DockerLayout.LabeledField(title, input);
            field.Margin = new Thickness(2, 0, 2, 4); fields.Children.Add(field);
            return input;
        }

        private static Button Button(string text, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 11,
                MinHeight = 24,
                Margin = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(5, 1, 5, 1)
            };
            button.Click += handler;
            return button;
        }

        private FrameworkElement CreatePrintFrameAnchorPicker()
        {
            var positions = new[]
            {
                "Верхний левый угол", "Верх по центру", "Верхний правый угол",
                "Слева по центру", "Центр", "Справа по центру",
                "Нижний левый угол", "Низ по центру", "Нижний правый угол"
            };
            var cells = new UniformGrid { Rows = 3, Columns = 3, Width = 39, Height = 39 };
            var marker = new FrameworkElementFactory(typeof(Border));
            marker.Name = "Marker";
            marker.SetResourceReference(Border.BackgroundProperty, DockerTheme.InputBackground);
            marker.SetResourceReference(Border.BorderBrushProperty, DockerTheme.BorderBrush);
            marker.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            marker.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
            marker.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
            var template = new ControlTemplate(typeof(System.Windows.Controls.RadioButton)) { VisualTree = marker };
            var selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(55, 106, 139))) { TargetName = "Marker" });
            selected.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(36, 75, 102))) { TargetName = "Marker" });
            template.Triggers.Add(selected);
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2)) { TargetName = "Marker" });
            template.Triggers.Add(focused);
            var style = new Style(typeof(System.Windows.Controls.RadioButton));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));

            for (int i = 0; i < positions.Length; i++)
            {
                PrintFrameAnchor position = (PrintFrameAnchor)i;
                var radio = new System.Windows.Controls.RadioButton
                {
                    GroupName = "PrintFrameAnchor",
                    Style = style,
                    ToolTip = positions[i]
                };
                radio.Checked += (_, __) => _printFrameAnchor = position;
                radio.IsChecked = position == PrintFrameAnchor.TopCenter;
                cells.Children.Add(radio);
            }

            var picker = new Border
            {
                Child = cells,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockerTheme.UseTheme(picker, Border.BackgroundProperty, DockerTheme.StatusBackground);
            DockerTheme.UseTheme(picker, Border.BorderBrushProperty, DockerTheme.BorderBrush);
            return picker;
        }

        private static TextBox AddRow(Panel root, string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            var text = new TextBlock
            {
                Text = label,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var input = new TextBox
            {
                Text = value,
                FontSize = 11,
                Height = 22,
                TextAlignment = TextAlignment.Right,
                Padding = new Thickness(3, 1, 3, 1)
            };
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);

            root.Children.Add(grid);
            return input;
        }

        private static System.Windows.Controls.RadioButton AddRadioButton(Panel root, string text, string groupName, bool isChecked)
        {
            var rb = new System.Windows.Controls.RadioButton
            {
                Content   = text,
                FontSize  = 11,
                GroupName = groupName,
                IsChecked = isChecked,
                Margin    = new Thickness(0, 0, 0, 2)
            };
            root.Children.Add(rb);
            return rb;
        }

        private static System.Windows.Controls.CheckBox AddCheckBox(Panel root, string text, bool isChecked)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content   = text,
                FontSize  = 11,
                IsChecked = isChecked,
                Margin    = new Thickness(0, 1, 0, 1)
            };
            root.Children.Add(cb);
            return cb;
        }

        private static void AddSmallLabel(Panel root, string text)
        {
            var label = new TextBlock
            {
                Text       = text,
                FontSize   = 10,
                Margin     = new Thickness(0, 3, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            DockerTheme.UseTheme(label, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
            root.Children.Add(label);
        }

        private static double ParseDouble(string text, double fallback)
        {
            text = (text ?? string.Empty).Replace(",", ".").Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : fallback;
        }

        private static int ParseInt(string text, int fallback)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }
    }
}
