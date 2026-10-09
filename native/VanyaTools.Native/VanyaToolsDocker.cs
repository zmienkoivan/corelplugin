using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
        private readonly System.Windows.Controls.RadioButton _trimModeTopLeft;
        private readonly System.Windows.Controls.RadioButton _trimModeBotRight;
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
        private static WeakReference<VanyaToolsDocker> _toolbarTarget;
        private PrintFrameAnchor _printFrameAnchor = PrintFrameAnchor.TopCenter;

        public VanyaToolsDocker()
            : this(null)
        {
        }

        public VanyaToolsDocker(object app)
        {
            CorelApp.SetHostApplication(app);
            _toolbarTarget = new WeakReference<VanyaToolsDocker>(this);
            Log.Info("VanyaToolsDocker constructor started.");

            var outer = new Grid
            {
                Margin = new Thickness(6),
                Background = new SolidColorBrush(Color.FromRgb(236, 239, 241))
            };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            DockerTheme.Apply(outer);
            Content = outer;

            var heading = new TextBlock
            {
                Text = "Vanya Tools · " + typeof(VanyaToolsDocker).Assembly.GetName().Version.ToString(3),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            };
            outer.Children.Add(heading);

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
                Background = new SolidColorBrush(Color.FromRgb(223, 229, 232)),
                CornerRadius = new CornerRadius(5),
                Child = _status
            };
            Grid.SetRow(statusFrame, 1);
            outer.Children.Add(statusFrame);

            var standardTools = new StackPanel { Margin = new Thickness(5) };
            var stickerTools = new StackPanel { Margin = new Thickness(5) };
            var aiTools = new AiPrintTab(ImportAiResult,
                (message, isError) => SetStatus(message, isError), CaptureAiSelection);
            var orderCards = new OrderCardTab((message, isError) => SetStatus(message, isError));
            var standardPage = new DockPanel();
            DockPanel.SetDock(orderCards.QuickDocumentFields, Dock.Bottom);
            standardPage.Children.Add(orderCards.QuickDocumentFields);
            standardPage.Children.Add(standardTools);
            var approvalCopy = new ApprovalCopyTool((message, isError) => SetStatus(message, isError));
            var settings = new StackPanel { Margin = new Thickness(5) };
            settings.Children.Add(Button("Обновить Vanya Tools", (_, __) => RunGitHubUpdate()));
            settings.Children.Add(Button("Добавить кнопки на стандартную панель", (_, __) =>
            {
                try { SetStatus(CorelToolbarInstaller.AddMissingToStandardToolbar(), false); }
                catch (Exception error)
                {
                    Log.Error("Could not add toolbar buttons.", error);
                    SetStatus("Не удалось добавить кнопки: " + error.Message, true);
                }
            }));
            settings.Children.Add(new TextBlock
            {
                Text = "Команды Corel: Настройка → Команды → Макросы. Положение кнопок меняется перетаскиванием в режиме настройки.",
                FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 2, 4, 7)
            });
            _hotkeys = new CorelHotkeyManager(() => RunTrim(true), RunFitFrame,
                approvalCopy.CopySelection,
                (message, isError) => SetStatus(message, isError));
            settings.Children.Add(_hotkeys.SettingsView);
            settings.Children.Add(approvalCopy.SettingsView);
            settings.Children.Add(aiTools.SettingsView);
            settings.Children.Add(orderCards.SettingsView);
            Loaded += (_, __) =>
            {
                _hotkeys.Attach(this);
                Dispatcher.BeginInvoke(new Action(CorelToolbarInstaller.RemoveLegacyDuplicates),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            Unloaded += (_, __) => _hotkeys.Detach();

            var tabs = new TabControl { MinHeight = 360 };
            tabs.Items.Add(ThumbnailTabs.Create("Основная", ThumbnailTabs.Crop,
                Color.FromRgb(46, 112, 124), standardPage));
            tabs.Items.Add(ThumbnailTabs.Create("Стикерпаки", ThumbnailTabs.Stickers,
                Color.FromRgb(171, 111, 43),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stickerTools }));
            tabs.Items.Add(ThumbnailTabs.Create("Развёртка формы", ThumbnailTabs.Shirt,
                Color.FromRgb(74, 114, 174),
                new SublimationTab(ImportAiResult, (message, isError) => SetStatus(message, isError),
                    () => AiSelectionCapture.Capture(2048, 4000000), () => aiTools.GetToken())));
            tabs.Items.Add(ThumbnailTabs.Create("Фамилии", ThumbnailTabs.Names,
                Color.FromRgb(89, 135, 74),
                new NamesGridTab((message, isError) => SetStatus(message, isError))));
            tabs.Items.Add(ThumbnailTabs.Create("Цветопроба", ThumbnailTabs.ColorProof,
                Color.FromRgb(130, 106, 65),
                new ColorProofTab((message, isError) => SetStatus(message, isError))));
            tabs.Items.Add(ThumbnailTabs.Create("Стилизация", ThumbnailTabs.Style,
                Color.FromRgb(167, 83, 123),
                new SimpleAiTab(() => AiSelectionCapture.Capture(4096, 16000000),
                    () => aiTools.GetToken(), ImportAiResult,
                    (message, isError) => SetStatus(message, isError), true)));
            tabs.Items.Add(ThumbnailTabs.Create("Публикация", ThumbnailTabs.Publish,
                Color.FromRgb(57, 120, 138),
                new TabControl
                {
                    Items =
                    {
                        new TabItem { Header = "Карточка заказа", Content = orderCards },
                        new TabItem { Header = "Заказы", Content = orderCards.OrdersView },
                        new TabItem { Header = "Превью", Content =
                            new PreviewLibraryTab((message, isError) => SetStatus(message, isError)) }
                    }
                }));
            tabs.Items.Add(ThumbnailTabs.Create("Настройки", ThumbnailTabs.Settings,
                Color.FromRgb(91, 104, 116),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = settings }));
            tabs.Items.Add(ThumbnailTabs.Create("Шрифт и принт", ThumbnailTabs.Restore,
                Color.FromRgb(95, 88, 152), aiTools));
            tabs.SelectedIndex = 0;
            Grid.SetRow(tabs, 2);
            outer.Children.Add(tabs);
            Panel root = standardTools;

            var quickActions = new Grid();
            quickActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            quickActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            quickActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quickActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quickActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var trimButton = Button("Обрезать растр", (_, __) => RunTrim(true));
            trimButton.Margin = new Thickness(0, 0, 3, 2);
            quickActions.Children.Add(trimButton);
            var fitFrame = Button("Подогнать рамку", (_, __) => RunFitFrame());
            fitFrame.Margin = new Thickness(0, 0, 3, 2);
            fitFrame.ToolTip = "Выделите части принта, затем рамку последней через Shift+щелчок. Точку привязки выберите справа.";
            Grid.SetColumn(fitFrame, 1);
            quickActions.Children.Add(fitFrame);
            var anchorPicker = CreatePrintFrameAnchorPicker();
            Grid.SetColumn(anchorPicker, 2);
            quickActions.Children.Add(anchorPicker);
            var approvalButton = Button("Копировать визуализацию + текст", (_, __) => approvalCopy.CopySelection());
            approvalButton.ToolTip = "Крупное изображение выделения с красной подписью для согласования. Ctrl+Alt+C.";
            Grid.SetRow(approvalButton, 1);
            Grid.SetColumnSpan(approvalButton, 3);
            quickActions.Children.Add(approvalButton);
            root.Children.Add(quickActions);
            var trimSettings = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            AddSettingsExpander(root, "Настройки обрезки растра", trimSettings);
            AddSmallLabel(trimSettings, "По границе");
            AddRadioButton(trimSettings, "Прозрачные пиксели", "TrimMode", true);
            _trimModeTopLeft = AddRadioButton(trimSettings, "Цвет верхнего-левого угла", "TrimMode", false);
            _trimModeBotRight = AddRadioButton(trimSettings, "Цвет нижнего-правого угла", "TrimMode", false);
            AddSmallLabel(trimSettings, "Обрезать стороны");
            var sidesGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 5) };
            _trimTop = AddCheckBox(sidesGrid, "Сверху", true);
            _trimLeft = AddCheckBox(sidesGrid, "Слева", true);
            _trimBottom = AddCheckBox(sidesGrid, "Снизу", true);
            _trimRight = AddCheckBox(sidesGrid, "Справа", true);
            trimSettings.Children.Add(sidesGrid);
            _trimPaddingPx = AddRow(trimSettings, "Отступ, px", "2");

            root.Children.Add(new SimpleAiTab(() => AiSelectionCapture.Capture(4096, 16000000),
                () => aiTools.GetToken(), ImportAiResult,
                (message, isError) => SetStatus(message, isError), false, ImportDtfResult));

            root = stickerTools;
            AddSectionTitle(root, "Подготовка стикерпака");

            AddSectionTitle(root, "1. Выберите размер пака");
            AddSmallLabel(root, "Выделите весь пак.");
            var markPresetGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 5) };
            markPresetGrid.Children.Add(Button("S 48×60", (_, __) => RunCreateCutMarks("S", 48, 60)));
            markPresetGrid.Children.Add(Button("M 105×142", (_, __) => RunCreateCutMarks("M", 105, 142)));
            markPresetGrid.Children.Add(Button("L 142×195", (_, __) => RunCreateCutMarks("L", 142, 195)));
            root.Children.Add(markPresetGrid);
            var sizeSettings = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            AddSettingsExpander(root, "Настройки размера и меток", sizeSettings);
            AddSmallLabel(sizeSettings, "Метки 1×1 мм; рамка 4 мм не печатается.");
            AddSmallLabel(sizeSettings, "Поворот альбомного пака:");
            _rotatePackClockwise = AddRadioButton(sizeSettings, "Вправо (по часовой)", "PackRotationDirection", true);
            _rotatePackCounterClockwise = AddRadioButton(sizeSettings, "Влево (против часовой)", "PackRotationDirection", false);

            AddSeparator(root);
            AddSectionTitle(root, "2. Создайте контур пака");
            AddSmallLabel(root, "Выделите стикеры.");
            root.Children.Add(DockerTheme.Primary(Button("Создать контур реза", (_, __) => RunCutContour())));
            var contourSettings = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            AddSettingsExpander(root, "Настройки контура", contourSettings);
            _offsetMm = AddRow(contourSettings, "Отступ, мм", "2");
            _rasterDpi = AddRow(contourSettings, "Растр DPI", "300");
            _useAlphaMask = AddCheckBox(contourSettings, "Трассировать по альфа-маске", true);
            _alphaThreshold = AddRow(contourSettings, "Порог альфа, 0–254", "10");
            _smoothing = AddRow(contourSettings, "Сглаживание", "70");
            _detail = AddRow(contourSettings, "Детализация", "35");
            _roundSpikesMm = AddRow(contourSettings, "Скругление, мм", "0.7");
            _simplificationToleranceMm = AddRow(contourSettings, "Упрощение контура, мм", "0.05");
            _mergeAdjacentContours = AddCheckBox(contourSettings, "Сливать соседние объекты", false);
            root.Children.Add(Button("Сгладить выбранный контур", (_, __) => RunSmoothSelectedContour()));
            var smoothingSettings = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            AddSettingsExpander(root, "Настройки сглаживания", smoothingSettings);
            _smoothSelectedRadiusMm = AddRow(smoothingSettings, "Радиус скругления, мм", "1.5");
            _smoothSelectedSimplificationMm = AddRow(smoothingSettings, "Допуск упрощения, мм", "0.2");
            AddSmallLabel(smoothingSettings, "Слабый эффект — увеличьте радиус или допуск.");

            AddSeparator(root);
            AddSectionTitle(root, "3. Создайте язычки");
            AddSmallLabel(root, "Выделите контур. Маркеры можно двигать.");
            root.Children.Add(Button("Добавить маркеры язычков", (_, __) => RunAddPeelMarker()));
            var tabSettings = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            AddSettingsExpander(root, "Настройки язычков", tabSettings);
            _tabWidthMm = AddRow(tabSettings, "Ширина, мм", "4");
            _tabHeightMm = AddRow(tabSettings, "Длина, мм", "12");
            _tabRadiusMm = AddRow(tabSettings, "Скругление язычка, мм", "2");
            root.Children.Add(Button("Переприкрепить маркеры", (_, __) => RunReattachMarkers()));

            AddSeparator(root);
            AddSectionTitle(root, "4. Примените язычки");
            AddSmallLabel(root, "Выделите контур или маркер.");
            root.Children.Add(DockerTheme.Primary(Button("Применить язычки", (_, __) => RunApplyPeelTabs())));

            AddSeparator(root);
            var maintenance = new StackPanel { Margin = new Thickness(4, 2, 4, 4) };
            AddSettingsExpander(root, "Дополнительные действия", maintenance);
            maintenance.Children.Add(Button("Удалить контуры пака", (_, __) => RunDeletePackContours()));
            maintenance.Children.Add(Button("Удалить маркеры пака", (_, __) => RunDeletePackMarkers()));
            Log.Info("VanyaToolsDocker constructor finished.");
        }

        private string ImportAiResult(string path, string operation) { return ImportAiResult(path, operation, true); }
        private string ImportDtfResult(string path, string operation) { return ImportAiResult(path, operation, false); }

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
                var mode = _trimModeTopLeft.IsChecked  == true ? TrimMode.TopLeftColor
                         : _trimModeBotRight.IsChecked == true ? TrimMode.BottomRightColor
                         : TrimMode.TransparentPixels;

                var sides = TrimSides.None;
                if (_trimTop.IsChecked    == true) sides |= TrimSides.Top;
                if (_trimBottom.IsChecked == true) sides |= TrimSides.Bottom;
                if (_trimLeft.IsChecked   == true) sides |= TrimSides.Left;
                if (_trimRight.IsChecked  == true) sides |= TrimSides.Right;

                int padding = ParseInt(_trimPaddingPx.Text, 2);
                var result = new BitmapTrimService().TrimSelected(mode, sides, padding, useTiles);
                string m = useTiles ? "тайлы" : "пиксели";
                SetStatus($"Обрезано ({m}): {result.Trimmed}. Пропущено: {result.Skipped}.", false);
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

        private void RunSafe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Command failed.", ex);
                SetStatus(ex.Message, true);
            }
        }

        private void SetStatus(string text, bool isError)
        {
            _status.Text = text;
            _status.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(138, 31, 17))
                : new SolidColorBrush(Color.FromRgb(66, 81, 91));
        }

        private static void AddSettingsExpander(Panel root, string title, Panel content)
        {
            root.Children.Add(new Expander
            {
                Header = title,
                IsExpanded = false,
                Content = content,
                Margin = new Thickness(0, 0, 0, 6)
            });
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
            marker.SetValue(Border.BackgroundProperty, new SolidColorBrush(Colors.White));
            marker.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(109, 127, 138)));
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

            return new Border
            {
                Child = cells,
                Background = new SolidColorBrush(Color.FromRgb(223, 229, 232)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(166, 186, 199)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
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
            root.Children.Add(new TextBlock
            {
                Text       = text,
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
                Margin     = new Thickness(0, 3, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });
        }

        private static void AddSectionTitle(Panel root, string text)
        {
            root.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        private static void AddSeparator(Panel root)
        {
            root.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(200, 206, 211)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 3, 0, 6)
            });
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
