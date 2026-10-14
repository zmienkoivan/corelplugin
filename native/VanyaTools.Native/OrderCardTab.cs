using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class OrderCardTab : UserControl
    {
        private const string PropertyOwner = "CD899165-445C-40F0-91D7-2DF6703B0AF8";
        private readonly Action<string, bool> _status;
        private readonly TextBox _customer, _designName, _description, _items,
            _server, _yearFolder, _ordersSearch;
        private readonly List<RadioButton> _sourceButtons = new List<RadioButton>();
        private readonly PasswordBox _key;
        private readonly TextBlock _caption, _documentName, _orderInfo, _ordersCount, _unassignedCount,
            _unassignedHint, _photoLabel, _photoMessage;
        private readonly StackPanel _frames, _editor;
        private readonly Expander _frameExpander;
        private readonly Button _send, _updateText, _state, _refreshServer, _unassignedRefresh,
            _saveDocument, _linkOrder, _loadOrders, _blanksPause, _applicationPause, _urgent, _blanksReady,
            _previousPhoto, _nextPhoto;
        private readonly Button[] _listWorkflow = new Button[5];
        private readonly DataGrid _ordersTable, _unassignedTable;
        private readonly Image _orderPhoto;
        private readonly Border _selectedOrderCard;
        private readonly TextBlock _selectedOrderHeading, _selectedOrderNumber, _selectedOrderSummary,
            _selectedOrderDates, _selectedOrderDescription, _selectedOrderItems;
        private readonly Dictionary<string, DataGridColumn> _orderColumns = new Dictionary<string, DataGridColumn>();
        private readonly DispatcherTimer _orderLayoutSaveTimer;
        private readonly CheckBox _onlyAvailable, _hideCompleted;
        private readonly List<OrderListItem> _serverOrders = new List<OrderListItem>();
        private readonly DispatcherTimer _documentWatcher, _serverWatcher;
        private readonly List<CheckBox> _technologies = new List<CheckBox>();
        private readonly CheckBox _delivery;
        private readonly DatePicker _dueDate;
        private OrderCardData _data = new OrderCardData();
        private string _documentPath;
        private string _lastWrittenJson;
        private object _documentIdentity;
        private object _failedDocumentIdentity;
        private string _failedDocumentPath;
        private bool _loadingForm;
        private bool _busy;
        private bool _ordersLoading;
        private bool _unassignedLoading;
        private bool _restoringOrderSelection;
        private int _previewOrderId, _previewIndex, _previewRequest;
        private string _previewKey;
        private readonly Dictionary<string, BitmapSource> _photoThumbs = new Dictionary<string, BitmapSource>();
        private readonly LinkedList<string> _photoThumbOrder = new LinkedList<string>();
        private readonly Dictionary<string, byte[]> _photoOriginals = new Dictionary<string, byte[]>();
        private readonly LinkedList<string> _photoOriginalOrder = new LinkedList<string>();
        private long _photoOriginalBytes;
        private string _sourceValue = "";

        internal FrameworkElement SettingsView { get; }
        internal FrameworkElement OrdersView { get; }
        internal FrameworkElement QuickDocumentFields { get; }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "order-server.bin");
        private static string PreviewSettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "preview-publisher.bin");
        private static string FileSettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "order-files.json");
        private static string ColumnsSettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "order-columns.json");

        internal OrderCardTab(Action<string, bool> status, bool standaloneOrders = false)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(8) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
            _documentName = Note("Откройте CDR или создайте новый документ.");
            var documentRow = new DockPanel();
            _saveDocument = Button("Сохранить CDR", (_, __) => Safe(SaveCurrentDocument));
            DockPanel.SetDock(_saveDocument, Dock.Right);
            documentRow.Children.Add(_saveDocument);
            var reload = Button("↻", (_, __) => Safe(() => LoadDocument(true)));
            reload.MinWidth = 32;
            reload.ToolTip = "Загрузить карточку из CDR повторно";
            DockPanel.SetDock(reload, Dock.Right);
            documentRow.Children.Add(reload);
            documentRow.Children.Add(_documentName);
            panel.Children.Add(documentRow);
            var openPanel = new StackPanel();
            var unassignedBar = new DockPanel { Margin = new Thickness(0, 1, 0, 2) };
            _unassignedRefresh = Button("↻", async (_, __) => await LoadUnassignedTable());
            _unassignedRefresh.MinWidth = 32;
            _unassignedRefresh.ToolTip = "Обновить список заказов без макета";
            DockPanel.SetDock(_unassignedRefresh, Dock.Right);
            unassignedBar.Children.Add(_unassignedRefresh);
            _unassignedCount = Note("Загрузка списка…");
            unassignedBar.Children.Add(_unassignedCount);
            openPanel.Children.Add(unassignedBar);
            _unassignedTable = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
                Height = 102, RowHeight = 25, Margin = new Thickness(0, 0, 0, 2),
                Visibility = Visibility.Collapsed };
            _unassignedTable.Columns.Add(new DataGridTextColumn { Header = "№",
                Binding = new Binding("Number"), Width = new DataGridLength(65) });
            _unassignedTable.Columns.Add(new DataGridTextColumn { Header = "Заказчик",
                Binding = new Binding("Customer"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _unassignedTable.Columns.Add(new DataGridTextColumn { Header = "Срок",
                Binding = new Binding("Due"), Width = new DataGridLength(80) });
            _unassignedTable.AlternationCount = 2;
            _unassignedTable.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(246, 248, 250));
            _unassignedTable.AddHandler(UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(UnassignedRowClicked), true);
            openPanel.Children.Add(_unassignedTable);
            _unassignedHint = Note("Нажмите на заказ, чтобы связать его с текущим CDR.");
            _unassignedHint.Visibility = Visibility.Collapsed;
            openPanel.Children.Add(_unassignedHint);
            var openExpander = new Expander { Header = "Заказы без макета", Content = openPanel,
                Margin = new Thickness(0, 1, 0, 2) };
            openExpander.Expanded += async (_, __) => await LoadUnassignedTable();
            panel.Children.Add(openExpander);
            _orderInfo = Note(""); panel.Children.Add(_orderInfo);
            var ordersPanel = new StackPanel { Margin = new Thickness(8) };
            ordersPanel.Children.Add(Title("Заказы"));
            var ordersTools = new DockPanel();
            _loadOrders = Button("↻", async (_, __) => await LoadOrders());
            _loadOrders.MinWidth = 32; _loadOrders.ToolTip = "Обновить заказы с сервера";
            DockPanel.SetDock(_loadOrders, Dock.Right); ordersTools.Children.Add(_loadOrders);
            var columnsMenu = new ContextMenu();
            Button columnsButton = null;
            columnsButton = Button("Столбцы ▾", (_, __) => {
                columnsMenu.PlacementTarget = columnsButton;
                columnsMenu.Placement = PlacementMode.Bottom;
                columnsMenu.IsOpen = true;
            });
            columnsButton.ToolTip = "Выбрать столбцы таблицы";
            DockPanel.SetDock(columnsButton, Dock.Right); ordersTools.Children.Add(columnsButton);
            _ordersSearch = Input(); _ordersSearch.ToolTip = "Поиск по номеру и заказчику";
            ordersTools.Children.Add(_ordersSearch); ordersPanel.Children.Add(ordersTools);
            if (!standaloneOrders)
                ordersPanel.Children.Add(Button("Открыть заказы отдельным докером ↗", (_, __) => {
                    try { CorelApp.Get().FrameWork.ShowDocker(VanyaOrdersDocker.DockerGuid); }
                    catch (Exception error) { _status("Открытие докера заказов: " + error.Message, true); }
                }));
            var orderFilters = new WrapPanel { Margin = new Thickness(2, 0, 0, 4) };
            _onlyAvailable = new CheckBox { Content = "Только без макета", Margin = new Thickness(2, 0, 0, 4) };
            _onlyAvailable.Checked += (_, __) => FilterOrders();
            _onlyAvailable.Unchecked += (_, __) => FilterOrders();
            orderFilters.Children.Add(_onlyAvailable);
            _hideCompleted = new CheckBox { Content = "Скрыть готовые", Margin = new Thickness(12, 0, 0, 4) };
            _hideCompleted.Checked += (_, __) => FilterOrders();
            _hideCompleted.Unchecked += (_, __) => FilterOrders();
            orderFilters.Children.Add(_hideCompleted);
            ordersPanel.Children.Add(orderFilters);
            _ordersCount = Note("Список загружается с сервера.");
            ordersPanel.Children.Add(_ordersCount);
            _ordersTable = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false,
                CanUserResizeColumns = true, CanUserReorderColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow, Height = 280, RowHeight = 31,
                Margin = new Thickness(1, 0, 1, 3) };
            _ordersTable.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                ScrollBarVisibility.Auto);
            _ordersTable.AlternationCount = 2;
            _ordersTable.RowBackground = Brushes.White;
            _ordersTable.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(246, 248, 250));
            var orderRowStyle = new Style(typeof(DataGridRow));
            var urgentRow = new DataTrigger { Binding = new Binding("Urgent"), Value = true };
            urgentRow.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(255, 238, 238))));
            orderRowStyle.Triggers.Add(urgentRow);
            var completedRow = new DataTrigger { Binding = new Binding("State"), Value = "done" };
            completedRow.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(201, 218, 248))));
            orderRowStyle.Triggers.Add(completedRow);
            var selectedRow = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selectedRow.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(207, 230, 247))));
            selectedRow.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            orderRowStyle.Triggers.Add(selectedRow);
            _ordersTable.RowStyle = orderRowStyle;
            // DataGrid may paint cell backgrounds over row backgrounds; tint both layers.
            var orderCellStyle = new Style(typeof(DataGridCell));
            orderCellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            var urgentCell = new DataTrigger { Binding = new Binding("Urgent"), Value = true };
            urgentCell.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(255, 238, 238))));
            orderCellStyle.Triggers.Add(urgentCell);
            var completedCell = new DataTrigger { Binding = new Binding("State"), Value = "done" };
            completedCell.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(201, 218, 248))));
            orderCellStyle.Triggers.Add(completedCell);
            var selectedCell = new DataTrigger { Binding = new Binding("IsSelected") {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1) },
                Value = true };
            selectedCell.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(207, 230, 247))));
            selectedCell.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            orderCellStyle.Triggers.Add(selectedCell);
            _ordersTable.CellStyle = orderCellStyle;
            var numberColumn = new DataGridTextColumn { Header = "№", Binding = new Binding("Number"),
                Width = new DataGridLength(58), MinWidth = 52 };
            _ordersTable.Columns.Add(numberColumn);
            _orderColumns.Add("number", numberColumn);
            var tableSettings = LoadOrderTableSettings();
            AddOrderColumn(columnsMenu, tableSettings.Visible, "customer", "Заказчик", "Customer",
                new DataGridLength(1, DataGridLengthUnitType.Star), true, 170);
            AddOrderColumn(columnsMenu, tableSettings.Visible, "source", "Источник", "Source",
                new DataGridLength(68), false);
            AddOrderColumn(columnsMenu, tableSettings.Visible, "technologies", "Технология", "Technologies",
                new DataGridLength(110), false);
            AddOrderColumn(columnsMenu, tableSettings.Visible, "created", "Принят", "Created",
                new DataGridLength(94), false);
            AddOrderColumn(columnsMenu, tableSettings.Visible, "due", "Срок", "Due",
                new DataGridLength(83), true);
            AddOrderColumn(columnsMenu, tableSettings.Visible, "updated", "Обновлён", "Updated",
                new DataGridLength(94), false);
            AddOrderColumn(columnsMenu, tableSettings.Visible, "completed", "Завершён", "Completed",
                new DataGridLength(94), false);
            AddOrderColumn(columnsMenu, tableSettings.Visible, "status", "Состояние", "Status",
                new DataGridLength(100), true);
            columnsMenu.Items.Add(new Separator());
            var resetTable = new MenuItem { Header = "Сбросить вид таблицы" };
            resetTable.Click += (_, __) => ResetOrderTableLayout(columnsMenu);
            columnsMenu.Items.Add(resetTable);
            ApplyOrderTableLayout(tableSettings);
            _orderLayoutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _orderLayoutSaveTimer.Tick += (_, __) => {
                _orderLayoutSaveTimer.Stop();
                try { SaveOrderTableSettings(); }
                catch (Exception error) { Log.Error("Order table layout save failed", error); }
            };
            var widthDescriptor = DependencyPropertyDescriptor.FromProperty(
                DataGridColumn.WidthProperty, typeof(DataGridColumn));
            foreach (var column in _orderColumns.Values)
                widthDescriptor.AddValueChanged(column, (_, __) => ScheduleOrderTableSave());
            _ordersTable.ColumnReordered += (_, __) => ScheduleOrderTableSave();
            _ordersTable.AddHandler(UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(OrderCellClicked), true);
            _ordersTable.SelectionChanged += (_, __) => {
                UpdateActions();
                if (!_restoringOrderSelection) _ = ShowSelectedPhoto();
            };
            ordersPanel.Children.Add(_ordersTable);
            var photoBar = new DockPanel { Margin = new Thickness(1, 2, 1, 2) };
            _nextPhoto = Button("›", (_, __) => ChangePhoto(1));
            _nextPhoto.Width = 32; _nextPhoto.ToolTip = "Следующее фото";
            DockPanel.SetDock(_nextPhoto, Dock.Right); photoBar.Children.Add(_nextPhoto);
            _previousPhoto = Button("‹", (_, __) => ChangePhoto(-1));
            _previousPhoto.Width = 32; _previousPhoto.ToolTip = "Предыдущее фото";
            DockPanel.SetDock(_previousPhoto, Dock.Right); photoBar.Children.Add(_previousPhoto);
            var reloadPhoto = Button("↻", (_, __) => {
                if (_previewKey != null) ForgetPhoto(_previewKey);
                _previewKey = null;
                _ = ShowSelectedPhoto();
            });
            reloadPhoto.Width = 32; reloadPhoto.ToolTip = "Повторить загрузку фото";
            DockPanel.SetDock(reloadPhoto, Dock.Right); photoBar.Children.Add(reloadPhoto);
            _photoLabel = Note("Выберите заказ");
            photoBar.Children.Add(_photoLabel); ordersPanel.Children.Add(photoBar);
            var photoStage = new Grid { Height = 175,
                Background = new SolidColorBrush(Color.FromRgb(235, 238, 240)),
                Margin = new Thickness(1, 0, 1, 4) };
            _orderPhoto = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            photoStage.Children.Add(_orderPhoto);
            photoStage.Cursor = Cursors.Hand;
            photoStage.ToolTip = "Нажмите, чтобы увеличить фото";
            photoStage.MouseLeftButtonUp += (_, __) => _ = ZoomSelectedPhoto();
            _photoMessage = Note("Выберите заказ для просмотра фото.");
            _photoMessage.HorizontalAlignment = HorizontalAlignment.Center;
            _photoMessage.VerticalAlignment = VerticalAlignment.Center;
            photoStage.Children.Add(_photoMessage);
            ordersPanel.Children.Add(photoStage);
            var selectedOrderBody = new StackPanel();
            var selectedOrderHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            _selectedOrderNumber = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(_selectedOrderNumber, Dock.Right);
            selectedOrderHeader.Children.Add(_selectedOrderNumber);
            _selectedOrderHeading = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap };
            selectedOrderHeader.Children.Add(_selectedOrderHeading);
            selectedOrderBody.Children.Add(selectedOrderHeader);
            _selectedOrderSummary = OrderDetailText();
            _selectedOrderDates = OrderDetailText();
            _selectedOrderDescription = OrderDetailText();
            _selectedOrderItems = OrderDetailText();
            selectedOrderBody.Children.Add(_selectedOrderSummary);
            selectedOrderBody.Children.Add(_selectedOrderDates);
            selectedOrderBody.Children.Add(_selectedOrderDescription);
            selectedOrderBody.Children.Add(_selectedOrderItems);
            _selectedOrderCard = new Border { Child = selectedOrderBody,
                Background = new SolidColorBrush(Color.FromRgb(247, 249, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(219, 226, 230)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 8, 9, 7), Margin = new Thickness(1, 0, 1, 7),
                Visibility = Visibility.Collapsed };
            ordersPanel.Children.Add(_selectedOrderCard);
            UpdatePhotoNavigation(null);
            var listActions = new UniformGrid { Columns = 2 };
            string[] actions = { "state", "blanks", "application", "urgent", "blanksReady" };
            for (int i = 0; i < actions.Length; i++)
            {
                string action = actions[i];
                _listWorkflow[i] = Button("", async (_, __) => await ChangeWorkflow(action, true));
                listActions.Children.Add(_listWorkflow[i]);
            }
            _linkOrder = Button("Открыть файл CDR", async (_, __) => await LinkSelectedOrder());
            listActions.Children.Add(_linkOrder);
            ordersPanel.Children.Add(listActions);
            OrdersView = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = ordersPanel };
            _ordersSearch.TextChanged += (_, __) => FilterOrders();
            _editor = new StackPanel { IsEnabled = false };
            panel.Children.Add(_editor);
            _editor.Children.Add(Button("Добавить выделение", (_, __) => Safe(AddSelection)));
            _frames = new StackPanel { Margin = new Thickness(0, 3, 0, 5) };
            _frameExpander = new Expander { Header = "Кадры (0)", Content = _frames,
                Margin = new Thickness(0, 2, 0, 3) };
            _editor.Children.Add(_frameExpander);

            _editor.Children.Add(Label("Заказчик"));
            _customer = Input(); _editor.Children.Add(_customer);
            _editor.Children.Add(Label("Имя макета"));
            _designName = Input(); _editor.Children.Add(_designName);
            _editor.Children.Add(Label("Источник"));
            var sourceRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
            foreach (string name in new[] { "ТГ", "ВК", "ПОЧТА", "КП" })
            {
                var radio = new RadioButton { Content = name,
                    Margin = new Thickness(2, 2, 15, 2), VerticalAlignment = VerticalAlignment.Center };
                radio.Checked += (_, __) => { _sourceValue = name; FormChanged(); };
                _sourceButtons.Add(radio);
                sourceRow.Children.Add(radio);
            }
            _editor.Children.Add(sourceRow);
            _editor.Children.Add(Label("Технология печати"));
            var techGrid = new UniformGrid { Columns = 2 };
            foreach (string name in new[] { "ДТФ", "Шелкография", "ПУФ", "Вышивка", "Сублимация", "Лазер" })
            {
                var box = new CheckBox { Content = name, Margin = new Thickness(2, 1, 2, 1) };
                _technologies.Add(box); techGrid.Children.Add(box);
            }
            _editor.Children.Add(techGrid);
            _delivery = new CheckBox { Content = "🚚 ДОСТАВКА",
                Margin = new Thickness(2, 5, 2, 5) };
            _editor.Children.Add(_delivery);
            _editor.Children.Add(Label("Срок сдачи"));
            var dueRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var clearDue = Button("Очистить", (_, __) => _dueDate.SelectedDate = null);
            clearDue.ToolTip = "Убрать срок заказа";
            DockPanel.SetDock(clearDue, Dock.Right);
            dueRow.Children.Add(clearDue);
            _dueDate = new DatePicker { SelectedDateFormat = DatePickerFormat.Short,
                FontSize = 11, MinHeight = 27, Margin = new Thickness(1, 1, 4, 3) };
            _dueDate.ToolTip = "Выберите дату сдачи. Срок сохранится на сервере при публикации карточки.";
            _dueDate.DateValidationError += (_, error) => {
                error.ThrowException = false;
                _status("Срок сдачи: выберите корректную дату.", true);
            };
            dueRow.Children.Add(_dueDate);
            _editor.Children.Add(dueRow);
            _editor.Children.Add(Label("Описание"));
            _description = Input(48); _editor.Children.Add(_description);
            _editor.Children.Add(Label("Изделия и количество"));
            _items = Input(55); _editor.Children.Add(_items);
            _caption = Note("");
            _caption.Padding = new Thickness(5);
            _editor.Children.Add(new Expander { Header = "Подпись Telegram", Content = _caption,
                Margin = new Thickness(0, 4, 0, 4) });
            _send = DockerTheme.Primary(Button("Опубликовать в Telegram", async (_, __) => await Publish(true)));
            _editor.Children.Add(_send);
            _updateText = Button("Обновить текст без экспорта фото", async (_, __) => await Publish(false));
            _editor.Children.Add(_updateText);
            var workflow = new UniformGrid { Columns = 2 };
            _state = Button("", async (_, __) => await ChangeWorkflow("state"));
            _blanksPause = Button("", async (_, __) => await ChangeWorkflow("blanks"));
            _applicationPause = Button("", async (_, __) => await ChangeWorkflow("application"));
            _urgent = Button("", async (_, __) => await ChangeWorkflow("urgent"));
            _blanksReady = Button("", async (_, __) => await ChangeWorkflow("blanksReady"));
            foreach (var button in new[] { _state, _blanksPause, _applicationPause, _urgent, _blanksReady })
                workflow.Children.Add(button);
            _editor.Children.Add(workflow);
            _refreshServer = Button("Загрузить карточку с сервера", async (_, __) => await RefreshServer());
            _editor.Children.Add(_refreshServer);
            var more = new StackPanel();
            more.Children.Add(Button("Новая карточка", (_, __) => Safe(ClearCard)));
            _editor.Children.Add(new Expander { Header = "Ещё", Content = more,
                Margin = new Thickness(0, 3, 0, 0) });

            var settings = new StackPanel { Margin = new Thickness(4, 12, 4, 4) };
            settings.Children.Add(Title("Сервер заказов"));
            settings.Children.Add(Label("Адрес сервера"));
            _server = Input(); _server.Text = "https://evpmerch.com:9443"; settings.Children.Add(_server);
            settings.Children.Add(Label("Ключ публикации"));
            _key = new PasswordBox { Margin = new Thickness(0, 0, 0, 5) };
            settings.Children.Add(_key);
            settings.Children.Add(Button("Сохранить подключение", (_, __) => Safe(() => SaveServerSettings())));
            settings.Children.Add(Button("Проверить сервер", async (_, __) =>
            {
                try
                {
                    await OrderServerClient.Check(_server.Text.Trim(), _key.Password.Trim());
                    _status("Сервер заказов доступен.", false);
                }
                catch (Exception error) { _status("Сервер заказов: " + error.Message, true); }
            }));
            settings.Children.Add(Note("Токен бота хранится на сервере. Здесь нужен ключ публикации."));
            settings.Children.Add(Label("Папка года"));
            var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var browseFolder = Button("", (_, __) => Safe(ChooseYearFolder));
            browseFolder.Width = 36;
            browseFolder.ToolTip = "Выбрать папку года";
            browseFolder.Content = new Viewbox { Width = 18, Height = 18, Child =
                new System.Windows.Shapes.Path {
                    Data = Geometry.Parse("M 2,6 L 8,6 10,8 20,8 20,18 2,18 Z M 2,6 L 2,4 9,4 11,6"),
                    Stroke = Brushes.DarkSlateGray, StrokeThickness = 1.7,
                    StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent } };
            DockPanel.SetDock(browseFolder, Dock.Right);
            folderRow.Children.Add(browseFolder);
            var saveFolder = Button("✓", (_, __) => Safe(SaveFileSettings));
            saveFolder.Width = 36;
            saveFolder.ToolTip = "Сохранить указанный путь, включая сетевой UNC-путь";
            DockPanel.SetDock(saveFolder, Dock.Right);
            folderRow.Children.Add(saveFolder);
            _yearFolder = Input();
            _yearFolder.ToolTip = "Папка года, например \\\\EVP_NAS\\EVP_files\\Макеты\\2026";
            _yearFolder.Margin = new Thickness(0, 1, 4, 3);
            folderRow.Children.Add(_yearFolder);
            settings.Children.Add(folderRow);
            settings.Children.Add(Label("Версия сохранения CDR"));
            var saveVersion = new ComboBox { Margin = new Thickness(0, 0, 0, 4),
                FontSize = 11, MinHeight = 26 };
            saveVersion.Items.Add(new ComboBoxItem { Content = "По умолчанию (как в CorelDRAW)", Tag = 0 });
            int installedVersion = CorelDocumentSaver.InstalledVersion;
            for (int version = Math.Min(installedVersion - 1, 27); version >= 5; version--)
                saveVersion.Items.Add(new ComboBoxItem {
                    Content = CorelDocumentSaver.VersionLabel(version), Tag = version });
            int selectedVersion = CorelDocumentSaver.SelectedVersion;
            for (int index = 0; index < saveVersion.Items.Count; index++)
                if (Convert.ToInt32(((ComboBoxItem)saveVersion.Items[index]).Tag) == selectedVersion)
                    saveVersion.SelectedIndex = index;
            if (saveVersion.SelectedIndex < 0) saveVersion.SelectedIndex = 0;
            saveVersion.SelectionChanged += (_, __) => Safe(() => {
                var choice = saveVersion.SelectedItem as ComboBoxItem;
                if (choice == null) return;
                int version = Convert.ToInt32(choice.Tag);
                CorelDocumentSaver.SelectVersion(version);
                _status(version == 0 ? "CDR будет сохраняться по правилам CorelDRAW." :
                    "Версия сохранения CDR: " + CorelDocumentSaver.VersionLabel(version) + ".", false);
            });
            settings.Children.Add(saveVersion);
            settings.Children.Add(Note("Применяется к сохранению через Vanya Tools. При выборе старой версии новые эффекты Corel могут измениться. Ctrl+S работает по настройкам CorelDRAW."));
            SettingsView = settings;
            LoadServerSettings();
            LoadFileSettings();
            foreach (var input in new[] { _customer, _designName, _description, _items })
                input.TextChanged += (_, __) => FormChanged();
            foreach (var box in _technologies) box.Checked += (_, __) => FormChanged();
            foreach (var box in _technologies) box.Unchecked += (_, __) => FormChanged();
            _delivery.Checked += (_, __) => FormChanged();
            _delivery.Unchecked += (_, __) => FormChanged();
            _dueDate.SelectedDateChanged += (_, __) => FormChanged();
            QuickDocumentFields = CreateQuickDocumentFields();
            _documentWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _documentWatcher.Tick += (_, __) => RefreshDocument();
            _serverWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _serverWatcher.Tick += async (_, __) => await LoadOrders(true);
            Loaded += (_, __) => { _documentWatcher.Start(); RefreshDocument(); };
            Unloaded += (_, __) => { if (!QuickDocumentFields.IsLoaded) _documentWatcher.Stop();
                if (!OrdersView.IsLoaded) _serverWatcher.Stop(); };
            QuickDocumentFields.Loaded += (_, __) => { _documentWatcher.Start(); RefreshDocument(); };
            QuickDocumentFields.Unloaded += (_, __) => { if (!IsLoaded) _documentWatcher.Stop(); };
            OrdersView.Loaded += async (_, __) => { _serverWatcher.Start();
                if (!String.IsNullOrWhiteSpace(_key.Password)) await LoadOrders(); };
            OrdersView.Unloaded += (_, __) => {
                _serverWatcher.Stop();
                if (_orderLayoutSaveTimer.IsEnabled)
                {
                    _orderLayoutSaveTimer.Stop();
                    try { SaveOrderTableSettings(); }
                    catch (Exception error) { Log.Error("Order table layout save failed", error); }
                }
            };
            RenderFrames(); UpdateCaption(); UpdateActions();
        }

        private dynamic EnsureDocument(bool loadIfChanged = true, bool allowUnsaved = false)
        {
            dynamic doc = CorelApp.Get().ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            string path = Convert.ToString(doc.FullFileName);
            if (String.IsNullOrWhiteSpace(path))
            {
                if (!allowUnsaved)
                {
                    if (!SaveNewDocument(doc))
                        throw new OperationCanceledException("Сохранение CDR отменено.");
                    path = Convert.ToString(doc.FullFileName);
                }
                else
                {
                    if (!SameDocument(_documentIdentity, (object)doc) || _documentPath != null)
                    {
                        LoadUnsavedDocument(doc);
                        if (loadIfChanged) LoadDocument(false);
                    }
                    return doc;
                }
            }
            if (loadIfChanged && (!SameDocument(_documentIdentity, (object)doc) ||
                !String.Equals(path, _documentPath, StringComparison.OrdinalIgnoreCase)))
                LoadDocument(false);
            return doc;
        }

        private void RefreshDocument()
        {
            if (_busy) return;
            object candidate = null;
            string path = null;
            try
            {
                dynamic doc = CorelApp.Get().ActiveDocument;
                candidate = (object)doc;
                path = doc == null ? null : Convert.ToString(doc.FullFileName);
                if (doc == null) { ClearDocumentView(); return; }
                if (String.IsNullOrWhiteSpace(path))
                {
                    if (!SameDocument(_documentIdentity, candidate) || _documentPath != null)
                        LoadDocument(false);
                    return;
                }
                if (SameDocument(_failedDocumentIdentity, candidate) &&
                    String.Equals(path, _failedDocumentPath, StringComparison.OrdinalIgnoreCase)) return;
                if (!SameDocument(_documentIdentity, (object)doc) ||
                    !String.Equals(path, _documentPath, StringComparison.OrdinalIgnoreCase))
                    LoadDocument(false);
            }
            catch (Exception error)
            {
                ClearDocumentView();
                _failedDocumentIdentity = candidate;
                _failedDocumentPath = path;
                _documentName.Text = String.IsNullOrEmpty(path) ? "Откройте сохранённый CDR."
                    : Path.GetFileName(path) + " · ошибка загрузки";
                Log.Error("Order card auto-load failed", error);
                _status("Карточка: " + error.Message, true);
            }
        }

        private void ClearDocumentView()
        {
            if (_documentIdentity == null && _failedDocumentIdentity == null && !_editor.IsEnabled) return;
            _documentIdentity = null;
            _documentPath = null;
            _lastWrittenJson = null;
            _failedDocumentIdentity = null;
            _failedDocumentPath = null;
            _data = new OrderCardData();
            _loadingForm = true;
            try
            {
                _customer.Text = ""; _designName.Text = ""; SetSourceSelection("");
                _description.Text = ""; _items.Text = "";
                _dueDate.SelectedDate = null;
                foreach (var box in _technologies) box.IsChecked = false;
                _delivery.IsChecked = false;
                RenderFrames(); UpdateCaption(); UpdateActions();
            }
            finally { _loadingForm = false; }
            _documentName.Text = "Откройте CDR или создайте новый документ.";
            _editor.IsEnabled = false;
            _saveDocument.IsEnabled = false;
        }

        private void LoadUnsavedDocument(dynamic doc)
        {
            ClearDocumentView();
            _documentIdentity = (object)doc;
            _documentPath = null;
            _failedDocumentIdentity = null;
            _failedDocumentPath = null;
            _editor.IsEnabled = true;
            _saveDocument.IsEnabled = true;
            _documentName.Text = "Новый CDR · укажите заказчика и имя макета";
        }

        private static bool SameDocument(object first, object second)
        {
            if (ReferenceEquals(first, second)) return first != null;
            if (first == null || second == null) return false;
            IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try
            {
                a = Marshal.GetIUnknownForObject(first);
                b = Marshal.GetIUnknownForObject(second);
                return a == b;
            }
            catch { return false; }
            finally
            {
                if (a != IntPtr.Zero) Marshal.Release(a);
                if (b != IntPtr.Zero) Marshal.Release(b);
            }
        }

        private void SaveCurrentDocument()
        {
            dynamic doc = CorelApp.Get().ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            if (String.IsNullOrWhiteSpace(Convert.ToString(doc.FullFileName)))
            {
                SaveNewDocument(doc);
                return;
            }
            if (SameDocument(_documentIdentity, (object)doc))
            {
                ReadForm(); SaveData();
            }
            CorelDocumentSaver.Save(doc);
            RememberCurrentOrderFile();
            _documentName.Text = Path.GetFileName(Convert.ToString(doc.FullFileName)) + " · сохранён";
            _status("CDR сохранён.", false);
        }

        private bool SaveNewDocument(dynamic doc, bool confirm = true)
        {
            string year = _yearFolder.Text.Trim();
            if (!Path.IsPathRooted(year))
                throw new InvalidOperationException("Укажите папку года во вкладке «Настройки».");
            string customer = _customer.Text.Trim();
            if (customer.Length == 0)
            {
                _customer.Focus();
                throw new InvalidOperationException("Укажите заказчика перед сохранением CDR.");
            }
            string design = _designName.Text.Trim();
            if (design.Length == 0)
            {
                _designName.Focus();
                throw new InvalidOperationException("Укажите имя макета перед сохранением CDR.");
            }
            string yearPath = Path.GetFullPath(year);
            if (!Directory.Exists(yearPath))
                throw new InvalidOperationException("Папка года не найдена: " + yearPath);
            DateTime today = DateTime.Now;
            string month = today.Month.ToString("00", CultureInfo.InvariantCulture)
                + new CultureInfo("ru-RU").DateTimeFormat.GetMonthName(today.Month)
                + today.Year.ToString(CultureInfo.InvariantCulture);
            string folder = Path.Combine(yearPath, month, SafePathPart(customer));
            string baseName = SafePathPart(design);
            string target = Path.Combine(folder, baseName + ".cdr");
            for (int suffix = 2; File.Exists(target); suffix++)
                target = Path.Combine(folder, baseName + " (" + suffix + ").cdr");
            if (confirm && MessageBox.Show("Сохранить макет?\n" + target, "Vanya Tools",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            Directory.CreateDirectory(folder);
            ReadForm();
            WriteData(doc);
            CorelDocumentSaver.SaveAs(doc, target);
            string saved = Convert.ToString(doc.FullFileName);
            if (String.IsNullOrWhiteSpace(saved) ||
                !String.Equals(Path.GetFullPath(saved), target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CorelDRAW сохранил файл по другому адресу: " + saved);
            LoadDocument(false);
            _status("CDR сохранён: " + target, false);
            return true;
        }

        private static string SafePathPart(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var text = new StringBuilder();
            foreach (char symbol in value.Trim())
                text.Append(char.IsControl(symbol) || invalid.Contains(symbol) ? '_' : symbol);
            string part = text.ToString().Trim().TrimEnd('.');
            if (part.Length > 80) part = part.Substring(0, 80).TrimEnd(' ', '.');
            if (part.Length == 0) throw new InvalidOperationException("Имя папки или макета пусто.");
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4",
                "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) part = "_" + part;
            return part;
        }

        private void LoadDocument(bool report)
        {
            dynamic doc = EnsureDocument(false, true);
            string path = Convert.ToString(doc.FullFileName);
            dynamic properties = doc.Properties;
            var loadedData = new OrderCardData();
            string storedJson = null;
            if (Convert.ToBoolean(properties.Exists(PropertyOwner, 1)))
            {
                object value = properties.GetType().InvokeMember("Item",
                    BindingFlags.GetProperty, null, (object)properties,
                    new object[] { PropertyOwner, 1 });
                storedJson = Convert.ToString(value);
                var loaded = new JavaScriptSerializer().Deserialize<OrderCardData>(storedJson);
                if (loaded != null) loadedData = loaded;
            }
            _data = loadedData;
            if (_data.Frames == null) _data.Frames = new List<OrderCardFrame>();
            if (_data.Technologies == null) _data.Technologies = new List<string>();
            if (TryReadDocumentNamesFromPath(path, out string pathCustomer, out string pathDesign))
            {
                if (String.IsNullOrWhiteSpace(_data.Customer)) _data.Customer = pathCustomer;
                if (String.IsNullOrWhiteSpace(_data.DesignName)) _data.DesignName = pathDesign;
            }
            _documentPath = String.IsNullOrWhiteSpace(path) ? null : path;
            _documentIdentity = (object)doc;
            _failedDocumentIdentity = null;
            _failedDocumentPath = null;
            _lastWrittenJson = storedJson;
            _loadingForm = true;
            try
            {
                _documentName.Text = _documentPath == null
                    ? "Новый CDR · " + _data.Frames.Count + " фото · не сохранён"
                    : Path.GetFileName(path) + " · " + _data.Frames.Count + " фото";
                _customer.Text = _data.Customer ?? "";
                _designName.Text = _data.DesignName ?? (_documentPath == null ? "" : Path.GetFileNameWithoutExtension(path));
                SetSourceSelection(_data.Source);
                _description.Text = _data.Description ?? "";
                _items.Text = _data.Items ?? "";
                _dueDate.SelectedDate = ParseServerDate(_data.DueDate);
                foreach (var box in _technologies)
                    box.IsChecked = _data.Technologies.Contains(Convert.ToString(box.Content));
                _delivery.IsChecked = _data.IsDelivery;
                RenderFrames(); UpdateCaption(); UpdateActions();
            }
            finally { _loadingForm = false; }
            _editor.IsEnabled = true;
            _saveDocument.IsEnabled = true;
            RememberCurrentOrderFile();
            if (report) _status("Карточка загружена из CDR.", false);
        }

        private static bool TryReadDocumentNamesFromPath(string path, out string customer, out string design)
        {
            customer = "";
            design = "";
            if (String.IsNullOrWhiteSpace(path)) return false;
            string customerFolder = Path.GetDirectoryName(path);
            if (String.IsNullOrWhiteSpace(customerFolder)) return false;
            string monthFolder = Path.GetDirectoryName(customerFolder);
            if (String.IsNullOrWhiteSpace(monthFolder)) return false;
            string yearFolder = Path.GetDirectoryName(monthFolder);
            if (String.IsNullOrWhiteSpace(yearFolder)) return false;
            string yearName = new DirectoryInfo(yearFolder).Name;
            if (yearName.Length != 4 || !Int32.TryParse(yearName, NumberStyles.None,
                CultureInfo.InvariantCulture, out int year)) return false;
            string monthName = new DirectoryInfo(monthFolder).Name;
            var months = new CultureInfo("ru-RU").DateTimeFormat;
            bool expectedMonth = Enumerable.Range(1, 12).Any(month => String.Equals(monthName,
                month.ToString("00", CultureInfo.InvariantCulture) + months.GetMonthName(month) +
                year.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
            if (!expectedMonth) return false;
            customer = new DirectoryInfo(customerFolder).Name;
            design = Path.GetFileNameWithoutExtension(path);
            return customer.Length > 0 && design.Length > 0;
        }

        private void FormChanged()
        {
            UpdateCaption();
            if (_loadingForm || _busy || !_editor.IsEnabled || _documentIdentity == null) return;
            try
            {
                if (String.IsNullOrWhiteSpace(_documentPath))
                {
                    ReadForm(); SaveData();
                    return;
                }
                object previous = _documentIdentity;
                dynamic doc = EnsureDocument();
                if (!SameDocument(previous, (object)doc)) return;
                ReadForm(); SaveData();
            }
            catch (Exception error)
            {
                Log.Error("Order card auto-save failed", error);
                _status("Не удалось записать карточку в документ: " + error.Message, true);
            }
        }

        private void ReadForm()
        {
            _data.Customer = _customer.Text.Trim();
            _data.DesignName = _designName.Text.Trim();
            _data.Source = _sourceValue.Trim();
            _data.Description = _description.Text.Trim();
            _data.Items = _items.Text.Trim();
            _data.DueDate = SelectedDueDate();
            _data.Technologies = _technologies.Where(x => x.IsChecked == true)
                .Select(x => Convert.ToString(x.Content)).ToList();
            _data.IsDelivery = _delivery.IsChecked == true;
        }

        private void SetSourceSelection(string source)
        {
            string value = source ?? "";
            foreach (var radio in _sourceButtons)
                radio.IsChecked = String.Equals(Convert.ToString(radio.Content), value,
                    StringComparison.OrdinalIgnoreCase);
            _sourceValue = value;
        }

        private void SaveData()
        {
            dynamic doc = EnsureDocument(false, true);
            WriteData(doc);
            _documentName.Text = _documentPath == null
                ? "Новый CDR · " + _data.Frames.Count + " фото · не сохранён"
                : Path.GetFileName(_documentPath) + " · " + _data.Frames.Count + " фото · " +
                    (CorelDocumentSaver.SelectedVersion == 0 ? "Ctrl+S" : "сохраните через Vanya Tools");
        }

        private void WriteData(dynamic doc)
        {
            _data.SchemaVersion = 5;
            string json = new JavaScriptSerializer().Serialize(_data);
            if (!String.Equals(json, _lastWrittenJson, StringComparison.Ordinal))
            {
                object properties = doc.Properties;
                properties.GetType().InvokeMember("Item", BindingFlags.SetProperty,
                    null, properties, new object[] { PropertyOwner, 1, json });
                _lastWrittenJson = json;
            }
        }

        private void AddSelection()
        {
            EnsureDocument(true, true);
            if (_data.Frames.Count >= 10)
                throw new InvalidOperationException("В карточке может быть не более 10 кадров.");
            var frame = FrameFromSelection();
            ReadForm(); _data.Frames.Add(frame); SaveData(); RenderFrames(); UpdateCaption();
            QueueFrameThumbnail(frame);
            _status("Кадр добавлен. Исходные объекты не сгруппированы.", false);
        }

        private static OrderCardFrame FrameFromSelection()
        {
            dynamic selection = CorelApp.Get().ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) == 0)
                throw new InvalidOperationException("Выделите один макет или группу объектов.");
            var frame = new OrderCardFrame { Shapes = new List<OrderCardShape>() };
            int pageIndex = -1;
            foreach (dynamic shape in selection.Shapes)
            {
                int currentPage = Convert.ToInt32(shape.Page.Index);
                if (pageIndex >= 0 && currentPage != pageIndex)
                    throw new InvalidOperationException("Для одного кадра выделите объекты на одной странице.");
                pageIndex = currentPage;
                frame.Shapes.Add(new OrderCardShape {
                    Page = currentPage, Id = Convert.ToInt32(shape.StaticID) });
            }
            return frame;
        }

        private void RenderFrames()
        {
            _frameExpander.Header = "Кадры (" + _data.Frames.Count + ")";
            _frames.Children.Clear();
            if (_data.Frames.Count == 0) { _frames.Children.Add(Note("Кадров нет. При публикации текущее выделение станет первым кадром.")); return; }
            for (int i = 0; i < _data.Frames.Count; i++)
            {
                int index = i;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var thumbnail = new Border { Width = 54, Height = 54,
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(Color.FromRgb(240, 242, 244)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(213, 219, 224)),
                    BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                    ToolTip = "Нажмите, чтобы обновить миниатюру" };
                try
                {
                    string encoded = _data.Frames[i].Thumbnail;
                    if (!String.IsNullOrEmpty(encoded))
                        thumbnail.Child = new Image { Source = DecodePhoto(Convert.FromBase64String(encoded), 100),
                            Stretch = Stretch.Uniform };
                }
                catch (Exception error) { Log.Error("Order frame thumbnail unavailable", error); }
                thumbnail.MouseLeftButtonUp += (_, __) => Safe(() => RefreshFrameThumbnail(index));
                DockPanel.SetDock(thumbnail, Dock.Left);
                row.Children.Add(thumbnail);
                row.Children.Add(new TextBlock { Text = (i + 1) + ". Кадр · " + _data.Frames[i].Shapes.Count + " объект(ов)",
                    VerticalAlignment = VerticalAlignment.Center, Width = 145 });
                foreach (var action in new[] { ("Показать", (Action)(() => SelectFrame(index))),
                    ("↑", (Action)(() => MoveFrame(index, -1))),
                    ("↓", (Action)(() => MoveFrame(index, 1))),
                    ("×", (Action)(() => RemoveFrame(index))) })
                {
                    var button = Button(action.Item1, (_, __) => Safe(action.Item2));
                    button.MinWidth = action.Item1 == "Показать" ? 67 : 27;
                    row.Children.Add(button);
                }
                _frames.Children.Add(row);
            }
        }

        private void QueueFrameThumbnail(OrderCardFrame frame)
        {
            object document = _documentIdentity;
            Dispatcher.BeginInvoke(new Action(() => {
                try
                {
                    if (_busy || !_data.Frames.Contains(frame) ||
                        !SameDocument(document, (object)CorelApp.Get().ActiveDocument)) return;
                    // The newly added shapes are still selected. A lookup by StaticID
                    // can fail for Corel objects even though they remain on the page.
                    if (!SelectionMatchesFrame(frame)) return;
                    frame.Thumbnail = CaptureFrameThumbnail((dynamic)document, frame, true);
                    SaveData(); RenderFrames();
                }
                catch (Exception error) { Log.Error("Order frame thumbnail capture failed", error); }
            }), DispatcherPriority.ApplicationIdle);
        }

        private void RefreshFrameThumbnail(int index)
        {
            if (_busy) return;
            dynamic doc = EnsureDocument(true, true);
            var frame = _data.Frames[index];
            frame.Thumbnail = CaptureFrameThumbnail(doc, frame, SelectionMatchesFrame(frame));
            SaveData(); RenderFrames();
        }

        private static bool SelectionMatchesFrame(OrderCardFrame frame)
        {
            dynamic selection = CorelApp.Get().ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) != frame.Shapes.Count) return false;
            var remaining = new HashSet<string>(frame.Shapes.Select(shape => shape.Page + ":" + shape.Id));
            foreach (dynamic shape in selection.Shapes)
                if (!remaining.Remove(Convert.ToInt32(shape.Page.Index) + ":" +
                    Convert.ToInt32(shape.StaticID))) return false;
            return remaining.Count == 0;
        }

        private static string CaptureFrameThumbnail(dynamic doc, OrderCardFrame frame,
            bool useCurrentSelection = false)
        {
            dynamic app = CorelApp.Get();
            var original = new List<dynamic>();
            foreach (dynamic shape in app.ActiveSelectionRange.Shapes) original.Add(shape);
            dynamic originalPage = doc.ActivePage;
            string path = null;
            try
            {
                if (!useCurrentSelection) SelectShapes(doc, frame);
                path = AiSelectionCapture.Capture(320, 102400,
                    restoreSelection: false, refreshWindow: false);
                return Convert.ToBase64String(File.ReadAllBytes(path));
            }
            finally
            {
                if (path != null) try { File.Delete(path); } catch { }
                try { if (Convert.ToInt32(doc.ActivePage.Index) != Convert.ToInt32(originalPage.Index))
                    originalPage.Activate(); } catch { }
                try
                {
                    if (original.Count == 0) doc.ClearSelection();
                    else for (int i = 0; i < original.Count; i++)
                    {
                        if (i == 0) original[i].CreateSelection(); else original[i].AddToSelection();
                    }
                }
                catch { }
            }
        }

        private static string ThumbnailFromPhoto(byte[] bytes)
        {
            var image = DecodePhoto(bytes, 180);
            var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = new MemoryStream())
            {
                encoder.Save(output);
                return Convert.ToBase64String(output.ToArray());
            }
        }

        private bool ResolveSelectionFrame(int missingIndex = -1)
        {
            if (missingIndex < 0 && _data.Frames.Count > 0) return true;
            dynamic selection = CorelApp.Get().ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) == 0)
                throw new InvalidOperationException(missingIndex < 0
                    ? "Выделите макет на холсте или добавьте кадр перед публикацией."
                    : "Объект кадра удалён из CDR. Выделите актуальный макет, чтобы заменить кадр.");
            if (missingIndex >= 0 && MessageBox.Show("Кадр " + (missingIndex + 1) +
                    " больше не найден в CDR. Заменить его текущим выделением?",
                    "Vanya Tools", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            var frame = FrameFromSelection();
            if (missingIndex < 0) _data.Frames.Add(frame);
            else _data.Frames[missingIndex] = frame;
            SaveData(); RenderFrames(); UpdateCaption();
            if (!_busy) QueueFrameThumbnail(frame);
            return true;
        }

        private void MoveFrame(int index, int offset)
        {
            EnsureDocument(true, true); int target = index + offset;
            if (target < 0 || target >= _data.Frames.Count) return;
            ReadForm(); var item = _data.Frames[index];
            _data.Frames[index] = _data.Frames[target]; _data.Frames[target] = item;
            SaveData(); RenderFrames();
        }

        private void RemoveFrame(int index)
        {
            EnsureDocument(true, true); ReadForm(); _data.Frames.RemoveAt(index);
            SaveData(); RenderFrames(); _status("Кадр удалён из карточки.", false);
        }

        private void ClearCard()
        {
            EnsureDocument(true, true);
            if (MessageBox.Show("Очистить карточку в этом CDR? Макет останется без изменений.",
                "Vanya Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _data = new OrderCardData(); SaveData(); LoadDocument(false);
            _status("Карточка очищена.", false);
        }

        private void SelectFrame(int index)
        {
            dynamic doc = EnsureDocument(true, true);
            SelectShapes(doc, _data.Frames[index]);
            _status("Объекты кадра выделены на холсте.", false);
        }

        private static void SelectShapes(dynamic doc, OrderCardFrame frame)
        {
            bool first = true;
            foreach (var reference in frame.Shapes)
            {
                dynamic page = null;
                foreach (dynamic candidate in doc.Pages)
                    if (Convert.ToInt32(candidate.Index) == reference.Page) { page = candidate; break; }
                if (page == null) throw new InvalidOperationException("Страница кадра не найдена. Обновите карточку.");
                dynamic shape = page.FindShape(null, 0, reference.Id, true);
                if (shape == null) throw new InvalidOperationException("Объект кадра удалён из CDR. Уберите этот кадр и добавьте его заново.");
                if (first)
                {
                    if (Convert.ToInt32(doc.ActivePage.Index) != reference.Page) page.Activate();
                    shape.CreateSelection(); first = false;
                }
                else shape.AddToSelection();
            }
            if (first) throw new InvalidOperationException("Кадр пуст.");
        }

        private static byte[] CapturePhoto(dynamic doc, OrderCardFrame frame,
            bool useCurrentSelection = false)
        {
            dynamic app = CorelApp.Get();
            var original = new List<dynamic>();
            foreach (dynamic shape in app.ActiveSelectionRange.Shapes) original.Add(shape);
            dynamic originalPage = doc.ActivePage;
            string capturePath = null;
            try
            {
                if (!useCurrentSelection) SelectShapes(doc, frame);
                Log.Info("Order card: rasterizing selected shapes in CorelDRAW.");
                capturePath = AiSelectionCapture.Capture(9500, 24000000,
                    restoreSelection: false, refreshWindow: false);
                BitmapSource image = AiPrintTab.Bitmap(capturePath);
                Log.Info("Order card: captured bitmap " + image.PixelWidth + "x" + image.PixelHeight + ".");
                if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
                    throw new InvalidOperationException("CorelDRAW вернул пустой кадр.");
                EnsureVisiblePhoto(image);
                if (Math.Max(image.PixelWidth, image.PixelHeight) >
                    20 * Math.Min(image.PixelWidth, image.PixelHeight))
                    throw new InvalidOperationException("Кадр слишком узкий для Telegram. Измените выделение.");
                double scale = Math.Min(1.0, 9990.0 / (image.PixelWidth + image.PixelHeight));
                for (int attempt = 0; attempt < 7; attempt++)
                {
                    BitmapSource sized = scale < 1.0 ? new TransformedBitmap(image,
                        new ScaleTransform(scale, scale)) : image;
                    var visual = new DrawingVisual();
                    using (var context = visual.RenderOpen())
                    {
                        context.DrawRectangle(Brushes.White, null,
                            new Rect(0, 0, sized.PixelWidth, sized.PixelHeight));
                        context.DrawImage(sized, new Rect(0, 0, sized.PixelWidth, sized.PixelHeight));
                    }
                    var bitmap = new RenderTargetBitmap(sized.PixelWidth, sized.PixelHeight,
                        96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    foreach (int quality in new[] { 92, 85, 75, 65 })
                    {
                        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var output = new MemoryStream())
                        {
                            encoder.Save(output);
                            if (output.Length <= 9500000)
                            {
                                Log.Info("Order card: Telegram JPEG " + sized.PixelWidth + "x" +
                                    sized.PixelHeight + ", " + output.Length + " bytes.");
                                return output.ToArray();
                            }
                        }
                    }
                    scale *= 0.82;
                }
                throw new InvalidOperationException("Не удалось уложить фото в лимит Telegram 10 МБ.");
            }
            finally
            {
                try { if (capturePath != null) File.Delete(capturePath); } catch { }
                try
                {
                    if (Convert.ToInt32(doc.ActivePage.Index) != Convert.ToInt32(originalPage.Index))
                        originalPage.Activate();
                }
                catch { }
                try
                {
                    for (int i = 0; i < original.Count; i++)
                        if (i == 0) original[i].CreateSelection(); else original[i].AddToSelection();
                }
                catch { }
            }
        }

        private static void EnsureVisiblePhoto(BitmapSource image)
        {
            const int size = 128;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));
                context.DrawImage(image, new Rect(0, 0, size, size));
            }
            var sample = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            sample.Render(visual);
            byte[] pixels = new byte[size * size * 4];
            sample.CopyPixels(pixels, size * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] < 254 || pixels[i + 1] < 254 || pixels[i + 2] < 254)
                    return;
            throw new InvalidOperationException("Кадр получился полностью белым. Проверьте выделение в CDR и повторите публикацию.");
        }

        private sealed class OrderListItem
        {
            public int Id { get; set; }
            public string Number { get; set; }
            public string Period { get; set; }
            public string Customer { get; set; }
            public string Source { get; set; }
            public string Technologies { get; set; }
            public string Description { get; set; }
            public string Items { get; set; }
            public string Created { get; set; }
            public string Due { get; set; }
            public string Updated { get; set; }
            public string Completed { get; set; }
            public DateTimeOffset? CompletedAt { get; set; }
            public string Status { get; set; }
            public int PhotoCount { get; set; }
            public string[] PhotoVersions { get; set; } = new string[0];
            public string SendState { get; set; }
            public string State { get; set; }
            public bool BlanksPaused { get; set; }
            public bool BlanksReady { get; set; }
            public bool ApplicationPaused { get; set; }
            public bool Urgent { get; set; }
            public bool Delivery { get; set; }
            public bool IsAvailable => SendState == "reserved";
        }

        private async void OrderCellClicked(object sender, MouseButtonEventArgs e)
        {
            if (_busy || !_orderColumns.TryGetValue("due", out DataGridColumn dueColumn)) return;
            DependencyObject current = e.OriginalSource as DependencyObject;
            while (current != null && !(current is DataGridCell))
                current = VisualTreeHelper.GetParent(current);
            if (!(current is DataGridCell cell) || cell.Column != dueColumn) return;
            current = cell;
            while (current != null && !(current is DataGridRow))
                current = VisualTreeHelper.GetParent(current);
            if (current is DataGridRow row && row.Item is OrderListItem order)
                await EditOrderDue(order);
        }

        private async Task EditOrderDue(OrderListItem order)
        {
            bool accepted = false;
            string dueDate = null;
            var window = new Window { Title = "Срок заказа №" + order.Number, Width = 285,
                SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false };
            var owner = Window.GetWindow(this);
            if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = "Дата сдачи", Margin = new Thickness(0, 0, 0, 5) });
            var picker = new DatePicker { Width = 175, HorizontalAlignment = HorizontalAlignment.Left,
                SelectedDateFormat = DatePickerFormat.Short };
            if (DateTime.TryParseExact(order.Due, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime existing)) picker.SelectedDate = existing;
            panel.Children.Add(picker);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var save = Button("Сохранить", (_, __) => {
                if (!picker.SelectedDate.HasValue) { _status("Выберите дату или нажмите «Очистить».", true); return; }
                dueDate = picker.SelectedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                accepted = true; window.Close();
            });
            var clear = Button("Очистить", (_, __) => { dueDate = ""; accepted = true; window.Close(); });
            var cancel = Button("Отмена", (_, __) => window.Close());
            foreach (var button in new[] { save, clear, cancel })
            { button.Margin = new Thickness(0, 0, 4, 0); buttons.Children.Add(button); }
            panel.Children.Add(buttons);
            window.Content = panel;
            window.ShowDialog();
            if (!accepted || _busy || (dueDate == "" && String.IsNullOrEmpty(order.Due)) ||
                dueDate == DateTimeToServerDate(order.Due)) return;
            _busy = true; UpdateActions();
            try
            {
                string server = _server.Text.Trim(), key = _key.Password.Trim();
                var current = await OrderServerClient.Get(server, key, order.Id);
                int revision = Convert.ToInt32(current["revision"]);
                var updated = await OrderServerClient.SetDeadline(server, key, order.Id, dueDate, revision);
                if (order.Id == _data.ServerOrderId && _data.ServerRevision == revision)
                    _data.ServerRevision = Convert.ToInt32(updated["revision"]);
                if (order.Id == _data.ServerOrderId) MergeServerDueDate(updated);
                order.Due = OrderDate(OrderValue(updated, "dueDate"), true);
                _ordersTable.Items.Refresh();
                RenderSelectedOrderDetails(_ordersTable.SelectedItem as OrderListItem);
                _status("Срок заказа №" + order.Number + (dueDate == "" ? " очищен." : " сохранён."), false);
            }
            catch (Exception error) { _status("Срок заказа: " + error.Message, true); }
            finally { _busy = false; UpdateActions(); }
            await LoadOrders();
        }

        private static string DateTimeToServerDate(string displayed) =>
            DateTime.TryParseExact(displayed, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime date)
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

        private static DateTime? ParseServerDate(string value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime date) ? date : (DateTime?)null;

        private string SelectedDueDate()
        {
            if (_dueDate.SelectedDate.HasValue)
                return _dueDate.SelectedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!String.IsNullOrWhiteSpace(_dueDate.Text))
                throw new InvalidOperationException("Срок сдачи: выберите корректную дату или очистите поле.");
            return "";
        }

        private sealed class OrderTableSettings
        {
            public OrderTableSettings() { }
            public string[] Visible { get; set; }
            public Dictionary<string, double> Widths { get; set; }
            public string[] Order { get; set; }
        }

        private static OrderTableSettings LoadOrderTableSettings()
        {
            try
            {
                if (!File.Exists(ColumnsSettingsPath)) return new OrderTableSettings();
                string json = File.ReadAllText(ColumnsSettingsPath, Encoding.UTF8).Trim();
                var serializer = new JavaScriptSerializer();
                if (json.StartsWith("[", StringComparison.Ordinal))
                    return new OrderTableSettings { Visible = serializer.Deserialize<string[]>(json) };
                return serializer.Deserialize<OrderTableSettings>(json) ?? new OrderTableSettings();
            }
            catch (Exception error)
            {
                Log.Error("Order table layout could not be loaded", error);
                return new OrderTableSettings();
            }
        }

        private void AddOrderColumn(ContextMenu menu, string[] visible, string key,
            string title, string property, DataGridLength width, bool shownByDefault,
            double minWidth = 45)
        {
            bool shown = visible == null ? shownByDefault : visible.Any(name =>
                String.Equals(name, key, StringComparison.OrdinalIgnoreCase));
            var column = new DataGridTextColumn { Header = title, Binding = new Binding(property),
                Width = width, MinWidth = minWidth,
                Visibility = shown ? Visibility.Visible : Visibility.Collapsed };
            if (key == "customer")
            {
                var nameStyle = new Style(typeof(TextBlock));
                nameStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 15.0));
                nameStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
                nameStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,
                    TextTrimming.CharacterEllipsis));
                nameStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty,
                    VerticalAlignment.Center));
                nameStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,
                    new Thickness(4, 0, 2, 0)));
                column.ElementStyle = nameStyle;
            }
            _ordersTable.Columns.Add(column);
            _orderColumns.Add(key, column);
            var option = new MenuItem { Header = title, Tag = key,
                IsCheckable = true, IsChecked = shown };
            option.Click += (_, __) => {
                column.Visibility = option.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                ScheduleOrderTableSave();
            };
            menu.Items.Add(option);
        }

        private void ApplyOrderTableLayout(OrderTableSettings settings)
        {
            if (settings.Widths != null)
                foreach (var item in settings.Widths)
                    if (_orderColumns.TryGetValue(item.Key, out DataGridColumn column) &&
                        !Double.IsNaN(item.Value) && !Double.IsInfinity(item.Value) &&
                        item.Value >= 40 && item.Value <= 1600)
                        column.Width = new DataGridLength(Math.Max(column.MinWidth, item.Value));
            if (settings.Order == null) return;
            var savedKeys = settings.Order.Where(_orderColumns.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var keys = savedKeys.Concat(_orderColumns.Keys.Where(key =>
                    !savedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
                .ToArray();
            for (int index = 0; index < keys.Length; index++)
                _orderColumns[keys[index]].DisplayIndex = index;
        }

        private void ResetOrderTableLayout(ContextMenu menu)
        {
            var visible = new HashSet<string>(new[] { "customer", "due", "status" });
            foreach (var item in _orderColumns)
                item.Value.Visibility = item.Key == "number" || visible.Contains(item.Key)
                    ? Visibility.Visible : Visibility.Collapsed;
            foreach (var option in menu.Items.OfType<MenuItem>())
                if (option.Tag is string key) option.IsChecked = visible.Contains(key);
            _orderColumns["number"].Width = new DataGridLength(58);
            _orderColumns["customer"].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            _orderColumns["source"].Width = new DataGridLength(68);
            _orderColumns["technologies"].Width = new DataGridLength(110);
            _orderColumns["created"].Width = new DataGridLength(94);
            _orderColumns["due"].Width = new DataGridLength(83);
            _orderColumns["updated"].Width = new DataGridLength(94);
            _orderColumns["completed"].Width = new DataGridLength(94);
            _orderColumns["status"].Width = new DataGridLength(100);
            int displayIndex = 0;
            foreach (var key in _orderColumns.Keys)
                _orderColumns[key].DisplayIndex = displayIndex++;
            _orderLayoutSaveTimer.Stop();
            try { if (File.Exists(ColumnsSettingsPath)) File.Delete(ColumnsSettingsPath); }
            catch (Exception error) { _status("Сброс таблицы: " + error.Message, true); }
        }

        private void ScheduleOrderTableSave()
        {
            if (_orderLayoutSaveTimer == null) return;
            _orderLayoutSaveTimer.Stop();
            _orderLayoutSaveTimer.Start();
        }

        private void SaveOrderTableSettings()
        {
            var widths = new Dictionary<string, double>();
            foreach (var item in _orderColumns)
            {
                double actual = item.Value.ActualWidth;
                if (item.Value.Visibility == Visibility.Visible && actual >= item.Value.MinWidth &&
                    actual <= 1600) widths[item.Key] = actual;
                else if (item.Value.Width.IsAbsolute)
                    widths[item.Key] = item.Value.Width.Value;
            }
            var settings = new OrderTableSettings {
                Visible = _orderColumns.Where(item => item.Key != "number" &&
                    item.Value.Visibility == Visibility.Visible).Select(item => item.Key).ToArray(),
                Widths = widths,
                Order = _orderColumns.OrderBy(item => item.Value.DisplayIndex)
                    .Select(item => item.Key).ToArray()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ColumnsSettingsPath));
            File.WriteAllText(ColumnsSettingsPath,
                new JavaScriptSerializer().Serialize(settings), Encoding.UTF8);
        }

        private static string OrderDate(object value, bool dateOnly = false)
        {
            string raw = Convert.ToString(value);
            if (String.IsNullOrWhiteSpace(raw)) return "";
            if (dateOnly && DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime exact)) return exact.ToString("dd.MM.yyyy");
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                ? parsed.ToLocalTime().ToString(dateOnly ? "dd.MM.yyyy" : "dd.MM.yy HH:mm") : raw;
        }

        private static DateTimeOffset? OrderTimestamp(object value)
        {
            string raw = Convert.ToString(value);
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                ? parsed.ToUniversalTime() : (DateTimeOffset?)null;
        }

        private static object OrderValue(Dictionary<string, object> row, string key) =>
            row.TryGetValue(key, out object value) ? value : null;

        private void UpdatePhotoNavigation(OrderListItem selected)
        {
            int count = selected?.PhotoCount ?? 0;
            _previousPhoto.IsEnabled = count > 1 && _previewIndex > 0;
            _nextPhoto.IsEnabled = count > 1 && _previewIndex < count - 1;
            _photoLabel.Text = selected == null ? "Выберите заказ" :
                "Заказ №" + selected.Number + (count > 0
                    ? " · Фото " + (_previewIndex + 1) + " из " + count : " · Фото нет");
        }

        private void ChangePhoto(int step)
        {
            var selected = _ordersTable.SelectedItem as OrderListItem;
            if (selected == null || selected.PhotoCount < 2) return;
            _previewIndex = Math.Max(0, Math.Min(selected.PhotoCount - 1, _previewIndex + step));
            _ = ShowSelectedPhoto();
        }

        private string PhotoCacheKey(OrderListItem selected, int index)
        {
            string version = index < selected.PhotoVersions.Length
                ? selected.PhotoVersions[index] : "";
            return _server.Text.Trim() + ":" + selected.Id + ":" +
                index + ":" + (version.Length > 0 ? version :
                    selected.PhotoCount + ":" + selected.Updated);
        }

        private static BitmapSource DecodePhoto(byte[] bytes, int maximumWidth)
        {
            var image = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = maximumWidth;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }

        private void RememberThumbnail(string key, BitmapSource image)
        {
            _photoThumbs[key] = image;
            _photoThumbOrder.Remove(key);
            _photoThumbOrder.AddLast(key);
            while (_photoThumbOrder.Count > 64)
            {
                string oldest = _photoThumbOrder.First.Value;
                _photoThumbOrder.RemoveFirst();
                _photoThumbs.Remove(oldest);
            }
        }

        private void RememberOriginal(string key, byte[] bytes)
        {
            const long limit = 64L * 1024 * 1024;
            if (bytes.Length > limit) return;
            if (_photoOriginals.TryGetValue(key, out byte[] previous))
                _photoOriginalBytes -= previous.Length;
            _photoOriginals[key] = bytes;
            _photoOriginalBytes += bytes.Length;
            _photoOriginalOrder.Remove(key);
            _photoOriginalOrder.AddLast(key);
            while (_photoOriginalBytes > limit || _photoOriginalOrder.Count > 4)
            {
                string oldest = _photoOriginalOrder.First.Value;
                _photoOriginalOrder.RemoveFirst();
                _photoOriginalBytes -= _photoOriginals[oldest].Length;
                _photoOriginals.Remove(oldest);
            }
        }

        private void ForgetPhoto(string key)
        {
            _photoThumbOrder.Remove(key);
            _photoThumbs.Remove(key);
            _photoOriginalOrder.Remove(key);
            if (_photoOriginals.TryGetValue(key, out byte[] bytes))
            {
                _photoOriginalBytes -= bytes.Length;
                _photoOriginals.Remove(key);
            }
            OrderPhotoDiskCache.Remove(key);
        }

        private static TextBlock OrderDetailText() => new TextBlock {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };

        private static void SetOrderDetail(TextBlock target, string label, string value)
        {
            string content = (value ?? "").Trim();
            target.Visibility = content.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            target.Text = content.Length == 0 ? "" : label + ": " + content;
        }

        private void RenderSelectedOrderDetails(OrderListItem selected)
        {
            _selectedOrderCard.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
            if (selected == null) return;

            _selectedOrderHeading.Text = String.IsNullOrWhiteSpace(selected.Customer)
                ? "Заказ без имени" : selected.Customer.Trim();
            _selectedOrderNumber.Text = "№" + selected.Number;

            var summary = new List<string>();
            if (!String.IsNullOrWhiteSpace(selected.Status)) summary.Add(selected.Status);
            if (!String.IsNullOrWhiteSpace(selected.Source)) summary.Add("Источник: " + selected.Source);
            if (!String.IsNullOrWhiteSpace(selected.Technologies))
                summary.Add("Технология: " + selected.Technologies);
            _selectedOrderSummary.Text = String.Join(" · ", summary);

            var dates = new List<string>();
            if (!String.IsNullOrWhiteSpace(selected.Created)) dates.Add("Принят: " + selected.Created);
            dates.Add("Срок: " + (String.IsNullOrWhiteSpace(selected.Due) ? "не указан" : selected.Due));
            if (!String.IsNullOrWhiteSpace(selected.Updated)) dates.Add("Обновлён: " + selected.Updated);
            if (!String.IsNullOrWhiteSpace(selected.Completed)) dates.Add("Завершён: " + selected.Completed);
            _selectedOrderDates.Text = String.Join(" · ", dates);

            SetOrderDetail(_selectedOrderDescription, "Описание", selected.Description);
            SetOrderDetail(_selectedOrderItems, "Изделия и количество", selected.Items);
        }

        private async Task ShowSelectedPhoto()
        {
            var selected = _ordersTable.SelectedItem as OrderListItem;
            RenderSelectedOrderDetails(selected);
            if (selected == null)
            {
                _previewRequest++;
                _previewOrderId = 0; _previewIndex = 0; _previewKey = null;
                _orderPhoto.Source = null;
                _photoMessage.Text = "Выберите заказ для просмотра фото.";
                _photoMessage.Visibility = Visibility.Visible;
                UpdatePhotoNavigation(null);
                return;
            }
            if (_previewOrderId != selected.Id)
            {
                _previewOrderId = selected.Id;
                _previewIndex = 0;
            }
            _previewIndex = Math.Min(_previewIndex, Math.Max(0, selected.PhotoCount - 1));
            UpdatePhotoNavigation(selected);
            string key = PhotoCacheKey(selected, _previewIndex);
            if (key == _previewKey) return;
            _previewKey = key;
            int request = ++_previewRequest;
            _orderPhoto.Source = null;
            _photoMessage.Text = selected.PhotoCount == 0 ? "У заказа нет фото." : "Загрузка фото…";
            _photoMessage.Visibility = Visibility.Visible;
            if (selected.PhotoCount == 0) return;
            if (_photoThumbs.TryGetValue(key, out BitmapSource cached))
            {
                RememberThumbnail(key, cached);
                _orderPhoto.Source = cached;
                _photoMessage.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                byte[] stored = await Task.Run(() => OrderPhotoDiskCache.Read(key, false));
                if (request != _previewRequest) return;
                if (stored != null)
                {
                    try
                    {
                        var cachedImage = DecodePhoto(stored, 640);
                        RememberThumbnail(key, cachedImage);
                        _orderPhoto.Source = cachedImage;
                        _photoMessage.Visibility = Visibility.Collapsed;
                        return;
                    }
                    catch (Exception error)
                    {
                        Log.Error("Order thumbnail cache is invalid", error);
                        OrderPhotoDiskCache.Remove(key);
                    }
                }
                await Task.Delay(150);
                if (request != _previewRequest) return;
                int index = _previewIndex;
                byte[] bytes = await OrderServerClient.PhotoThumbnail(_server.Text.Trim(),
                    _key.Password.Trim(), selected.Id, index);
                var image = DecodePhoto(bytes, 640);
                RememberThumbnail(key, image);
                if (request != _previewRequest) return;
                _orderPhoto.Source = image;
                _photoMessage.Visibility = Visibility.Collapsed;
                await Task.Run(() => OrderPhotoDiskCache.Write(key, false, bytes));
            }
            catch (Exception error)
            {
                if (request != _previewRequest) return;
                Log.Error("Order thumbnail unavailable", error);
                _photoMessage.Text = "Фото недоступно.";
            }
        }

        private async Task ZoomSelectedPhoto()
        {
            var selected = _ordersTable.SelectedItem as OrderListItem;
            if (selected == null || selected.PhotoCount == 0 || _orderPhoto.Source == null) return;
            int index = _previewIndex;
            string key = PhotoCacheKey(selected, index);
            var photo = new Image { Source = _orderPhoto.Source, Stretch = Stretch.Fill,
                SnapsToDevicePixels = true };
            var scale = new ScaleTransform(1, 1);
            photo.LayoutTransform = scale;
            var scroll = new ScrollViewer { Content = photo,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Black };
            var bar = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
            var zoomLabel = Note("");
            var message = Note("Загрузка оригинала…");
            Action fit = () => {
                var source = photo.Source as BitmapSource;
                if (source == null) return;
                photo.Width = source.PixelWidth;
                photo.Height = source.PixelHeight;
                double width = Math.Max(1, scroll.ActualWidth - 24);
                double height = Math.Max(1, scroll.ActualHeight - 24);
                double value = Math.Min(width / source.PixelWidth, height / source.PixelHeight);
                value = Math.Max(0.02, Math.Min(8, value));
                scale.ScaleX = scale.ScaleY = value;
                zoomLabel.Text = Math.Round(value * 100) + "%";
                scroll.ScrollToHome();
            };
            Action<double> zoom = factor => {
                double value = Math.Max(0.02, Math.Min(8, scale.ScaleX * factor));
                scale.ScaleX = scale.ScaleY = value;
                zoomLabel.Text = Math.Round(value * 100) + "%";
            };
            foreach (var item in new[] { new { Text = "−", Factor = 1 / 1.25 },
                         new { Text = "+", Factor = 1.25 } })
            {
                double factor = item.Factor;
                var button = Button(item.Text, (_, __) => zoom(factor));
                button.Width = 34;
                DockPanel.SetDock(button, Dock.Left);
                bar.Children.Add(button);
            }
            var fitButton = Button("Вписать", (_, __) => fit());
            DockPanel.SetDock(fitButton, Dock.Left); bar.Children.Add(fitButton);
            zoomLabel.VerticalAlignment = VerticalAlignment.Center;
            zoomLabel.Margin = new Thickness(9, 0, 9, 0);
            DockPanel.SetDock(zoomLabel, Dock.Left); bar.Children.Add(zoomLabel);
            message.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(message);
            var layout = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            layout.Children.Add(bar);
            layout.Children.Add(scroll);
            var window = new Window { Title = "Фото заказа №" + selected.Number + " · " +
                (index + 1) + "/" + selected.PhotoCount,
                Width = 1000, Height = 750, MinWidth = 440, MinHeight = 340,
                Content = layout, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            var owner = Window.GetWindow(this);
            if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            window.ContentRendered += (_, __) => fit();
            scroll.PreviewMouseWheel += (_, args) => {
                zoom(args.Delta > 0 ? 1.25 : 1 / 1.25);
                args.Handled = true;
            };
            window.Show();
            try
            {
                bool downloaded = false, fromDisk = false;
                if (!_photoOriginals.TryGetValue(key, out byte[] bytes))
                {
                    bytes = await Task.Run(() => OrderPhotoDiskCache.Read(key, true));
                    fromDisk = bytes != null;
                    if (bytes == null)
                    {
                        bytes = await OrderServerClient.PhotoOriginal(_server.Text.Trim(),
                            _key.Password.Trim(), selected.Id, index);
                        downloaded = true;
                    }
                }
                BitmapSource image;
                try { image = DecodePhoto(bytes, 4096); }
                catch (Exception error) when (fromDisk)
                {
                    Log.Error("Order original cache is invalid", error);
                    await Task.Run(() => OrderPhotoDiskCache.Remove(key));
                    bytes = await OrderServerClient.PhotoOriginal(_server.Text.Trim(),
                        _key.Password.Trim(), selected.Id, index);
                    downloaded = true;
                    image = DecodePhoto(bytes, 4096);
                }
                RememberOriginal(key, bytes);
                if (downloaded) await Task.Run(() => OrderPhotoDiskCache.Write(key, true, bytes));
                if (!window.IsVisible) return;
                photo.Source = image;
                message.Text = "Колесо мыши — масштаб";
                fit();
            }
            catch (Exception error)
            {
                Log.Error("Order photo unavailable", error);
                if (window.IsVisible) message.Text = "Оригинал недоступен; показана миниатюра.";
            }
        }

        private void FilterOrders()
        {
            if (_ordersTable == null) return;
            int selectedId = (_ordersTable.SelectedItem as OrderListItem)?.Id ?? 0;
            string query = (_ordersSearch?.Text ?? "").Trim();
            var visible = _serverOrders.Where(row =>
                (_onlyAvailable?.IsChecked != true || row.IsAvailable) &&
                (_hideCompleted?.IsChecked != true || row.State != "done") &&
                (query.Length == 0 || (row.Number ?? "").IndexOf(query,
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (row.Customer ?? "").IndexOf(query,
                    StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(row => row.State == "done" ? 1 : 0)
                .ThenByDescending(row => row.State != "done" && row.Urgent)
                .ThenByDescending(row => row.State == "done"
                    ? row.CompletedAt ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue)
                .ThenByDescending(row => row.State == "done" ? row.Id : 0)
                .ToList();
            _restoringOrderSelection = true;
            try
            {
                _ordersTable.ItemsSource = visible;
                _ordersTable.SelectedItem = visible.FirstOrDefault(row => row.Id == selectedId);
            }
            finally { _restoringOrderSelection = false; }
            _ordersCount.Text = "Показано: " + visible.Count + " из " + _serverOrders.Count;
            UpdateActions();
            _ = ShowSelectedPhoto();
        }

        private async Task<IList> LoadUnassignedOrders(string server, string key)
        {
            try
            {
                var response = await OrderServerClient.Unassigned(server, key);
                return response.TryGetValue("orders", out object rows) ? rows as IList : null;
            }
            catch (Exception error)
            {
                Log.Error("Order intake list unavailable", error);
                return null;
            }
        }

        private async Task LoadUnassignedTable()
        {
            if (_unassignedLoading || _busy) return;
            if (String.IsNullOrWhiteSpace(_server.Text) || String.IsNullOrWhiteSpace(_key.Password))
            {
                _unassignedCount.Text = "Укажите сервер и ключ в настройках.";
                return;
            }
            _unassignedLoading = true;
            _unassignedRefresh.IsEnabled = false;
            _unassignedCount.Text = "Загрузка заказов без макета…";
            try
            {
                var response = await OrderServerClient.Unassigned(_server.Text.Trim(), _key.Password.Trim());
                if (!response.TryGetValue("orders", out object value) || !(value is IList rows))
                    throw new InvalidDataException("Сервер вернул неверный список заказов.");
                var available = new List<OrderListItem>();
                foreach (object item in rows)
                {
                    if (!(item is Dictionary<string, object> row)) continue;
                    available.Add(new OrderListItem {
                        Id = Convert.ToInt32(row["id"]),
                        Number = Convert.ToString(row["number"]),
                        Period = Convert.ToString(OrderValue(row, "period")),
                        Customer = Convert.ToString(row["customer"]),
                        Source = Convert.ToString(row["source"]),
                        Due = OrderDate(OrderValue(row, "dueDate"), true),
                        SendState = Convert.ToString(row["sendState"])
                    });
                }
                _unassignedTable.ItemsSource = available;
                _unassignedTable.Visibility = available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                _unassignedHint.Visibility = available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                _unassignedCount.Text = available.Count > 0
                    ? "Найдено: " + available.Count : "Сейчас заказов без макета нет.";
            }
            catch (Exception error)
            {
                _unassignedTable.Visibility = Visibility.Collapsed;
                _unassignedHint.Visibility = Visibility.Collapsed;
                _unassignedCount.Text = "Не удалось загрузить заказы: " + error.Message;
            }
            finally { _unassignedLoading = false; _unassignedRefresh.IsEnabled = true; }
        }

        private async void UnassignedRowClicked(object sender, MouseButtonEventArgs e)
        {
            if (_busy || _unassignedLoading) return;
            DependencyObject current = e.OriginalSource as DependencyObject;
            while (current != null && !(current is DataGridRow))
                current = VisualTreeHelper.GetParent(current);
            if (current is DataGridRow row && row.Item is OrderListItem selected)
                await AssignIntake(selected);
        }

        private static bool SameOrders(IList<OrderListItem> current, IList<OrderListItem> incoming)
        {
            if (current.Count != incoming.Count) return false;
            for (int i = 0; i < current.Count; i++)
            {
                var a = current[i]; var b = incoming[i];
                if (a.Id != b.Id || a.Number != b.Number || a.Period != b.Period ||
                    a.Customer != b.Customer ||
                    a.Source != b.Source || a.Technologies != b.Technologies ||
                    a.Description != b.Description || a.Items != b.Items ||
                    a.Created != b.Created || a.Due != b.Due || a.Updated != b.Updated ||
                    a.Completed != b.Completed || a.CompletedAt != b.CompletedAt ||
                    a.Status != b.Status ||
                    a.SendState != b.SendState || a.State != b.State ||
                    a.PhotoCount != b.PhotoCount || a.Urgent != b.Urgent ||
                    a.Delivery != b.Delivery ||
                    a.BlanksPaused != b.BlanksPaused || a.BlanksReady != b.BlanksReady ||
                    a.ApplicationPaused != b.ApplicationPaused ||
                    !a.PhotoVersions.SequenceEqual(b.PhotoVersions)) return false;
            }
            return true;
        }

        private async Task LoadOrders(bool automatic = false)
        {
            if (_busy || _ordersLoading) return;
            if (String.IsNullOrWhiteSpace(_key.Password))
            {
                _ordersCount.Text = "Укажите ключ публикации в настройках.";
                return;
            }
            _ordersLoading = true; UpdateActions();
            try
            {
                string server = _server.Text.Trim(), key = _key.Password.Trim();
                var pendingTask = LoadUnassignedOrders(server, key);
                var reply = await OrderServerClient.List(server, key);
                if (!reply.TryGetValue("orders", out object value) || !(value is IList orders))
                    throw new InvalidDataException("Сервер вернул неверный список заказов.");
                var all = new List<object>();
                foreach (object order in orders) all.Add(order);
                var pending = await pendingTask;
                // Ignore a snapshot started before a local workflow change.
                if (_busy) return;
                if (pending != null) foreach (object row in pending) all.Add(row);
                var incoming = new List<OrderListItem>();
                var seen = new HashSet<int>();
                foreach (var item in all)
                {
                    var row = item as Dictionary<string, object>;
                    if (row == null) continue;
                    int id = Convert.ToInt32(row["id"]);
                    if (!seen.Add(id)) continue;
                    string sendState = Convert.ToString(row["sendState"]);
                    string state = Convert.ToString(row["state"]);
                    var versions = OrderValue(row, "photoVersions") as IList;
                    var technologies = OrderValue(row, "technologies") as IList;
                    var order = new OrderListItem {
                        Id = id,
                        Number = Convert.ToString(row["number"]),
                        Period = Convert.ToString(OrderValue(row, "period")),
                        Customer = Convert.ToString(row["customer"]),
                        Source = Convert.ToString(row["source"]),
                        Technologies = technologies == null ? "" : String.Join(", ",
                            technologies.Cast<object>().Select(item => Convert.ToString(item))),
                        Description = Convert.ToString(OrderValue(row, "description")),
                        Items = Convert.ToString(OrderValue(row, "items")),
                        Created = OrderDate(OrderValue(row, "createdAt")),
                        Due = OrderDate(OrderValue(row, "dueDate"), true),
                        Updated = OrderDate(OrderValue(row, "updatedAt")),
                        Completed = OrderDate(OrderValue(row, "completedAt")),
                        CompletedAt = OrderTimestamp(OrderValue(row, "completedAt")),
                        PhotoCount = Convert.ToInt32(OrderValue(row, "photoCount") ?? 0),
                        PhotoVersions = versions == null ? new string[0] : versions.Cast<object>()
                            .Select(item => Convert.ToString(item)).ToArray(),
                        SendState = sendState,
                        State = state,
                        BlanksPaused = Flag(row, "blanksPaused"),
                        BlanksReady = Flag(row, "blanksReady"),
                        ApplicationPaused = Flag(row, "applicationPaused"),
                        Urgent = Flag(row, "isUrgent"),
                        Delivery = Flag(row, "isDelivery")
                    };
                    order.Status = OrderStatus(order);
                    incoming.Add(order);
                    if (!_busy && id == _data.ServerOrderId) ApplyWorkflowResult(row);
                }
                int activeId = _data.ServerOrderId;
                if (activeId > 0 && !seen.Contains(activeId))
                {
                    var current = await OrderServerClient.Get(server, key, activeId);
                    if (!_busy && _data.ServerOrderId == activeId) ApplyWorkflowResult(current);
                }
                if (!SameOrders(_serverOrders, incoming))
                {
                    _serverOrders.Clear();
                    _serverOrders.AddRange(incoming);
                    FilterOrders();
                }
                else _ordersCount.Text = "Показано: " + _ordersTable.Items.Count + " из " + _serverOrders.Count;
            }
            catch (Exception error)
            {
                _ordersCount.Text = "Не удалось загрузить заказы: " + error.Message;
                if (!automatic) _status("Список заказов: " + error.Message, true);
            }
            finally { _ordersLoading = false; UpdateActions(); }
        }

        private async Task LinkSelectedOrder()
        {
            var selected = _ordersTable.SelectedItem as OrderListItem;
            if (selected == null) { _status("Выберите заказ в таблице.", true); return; }
            if (selected.IsAvailable) await AssignIntake(selected);
            else await OpenSelectedOrderFile(selected);
        }

        private string YearFolderForOrder(string period, string configuredFolder = null)
        {
            try
            {
                string configured = configuredFolder ?? _yearFolder.Text.Trim();
                if (!Path.IsPathRooted(configured)) return null;
                string root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                string year = (period ?? "").Split('-')[0];
                if (year.Length == 4 && Int32.TryParse(year, out int yearNumber) &&
                    yearNumber >= 2000 && yearNumber <= 2100 &&
                    !String.Equals(Path.GetFileName(root), year, StringComparison.OrdinalIgnoreCase))
                {
                    string parent = Path.GetDirectoryName(root);
                    if (!String.IsNullOrEmpty(parent)) root = Path.Combine(parent, year);
                }
                return Directory.Exists(root) ? root : null;
            }
            catch (Exception error) { Log.Error("Order year folder unavailable", error); return null; }
        }

        private string YearFolderForDocument(string path)
        {
            try
            {
                string customer = Path.GetDirectoryName(path);
                string month = customer == null ? null : Path.GetDirectoryName(customer);
                string year = month == null ? null : Path.GetDirectoryName(month);
                if (year != null && Path.GetFileName(year).Length == 4 &&
                    Int32.TryParse(Path.GetFileName(year), out int parsed) &&
                    parsed >= 2000 && parsed <= 2100 && Directory.Exists(year)) return year;
            }
            catch (Exception error) { Log.Error("Order document year folder unavailable", error); }
            return YearFolderForOrder(null);
        }

        private void RememberCurrentOrderFile()
        {
            if (_data.ServerOrderId <= 0 || String.IsNullOrWhiteSpace(_documentPath) ||
                !File.Exists(_documentPath)) return;
            OrderCdrPathIndex.Remember(_server.Text.Trim(), _data.ServerOrderId,
                _documentPath, YearFolderForDocument(_documentPath));
        }

        private string SuggestedOrderFolder(OrderListItem selected, string yearRoot)
        {
            if (yearRoot == null) return null;
            try
            {
                if (!DateTime.TryParseExact(selected.Period + "-01", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)) return yearRoot;
                string month = date.Month.ToString("00", CultureInfo.InvariantCulture) +
                    new CultureInfo("ru-RU").DateTimeFormat.GetMonthName(date.Month) +
                    date.Year.ToString(CultureInfo.InvariantCulture);
                string folder = Path.Combine(yearRoot, month, SafePathPart(selected.Customer ?? ""));
                return Directory.Exists(folder) ? folder : yearRoot;
            }
            catch { return yearRoot; }
        }

        private async Task OpenSelectedOrderFile(OrderListItem selected)
        {
            if (_busy) return;
            try
            {
                string configured = _yearFolder.Text.Trim(), server = _server.Text.Trim();
                string yearRoot = await Task.Run(() => YearFolderForOrder(selected.Period, configured));
                string path = await Task.Run(() => OrderCdrPathIndex.Find(server, selected.Id, yearRoot));
                if (path == null)
                {
                    var picker = new Microsoft.Win32.OpenFileDialog {
                        Title = "Выберите CDR для заказа №" + selected.Number,
                        Filter = "CorelDRAW (*.cdr)|*.cdr", CheckFileExists = true,
                        Multiselect = false };
                    string folder = SuggestedOrderFolder(selected, yearRoot);
                    if (folder != null) picker.InitialDirectory = folder;
                    if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
                    path = picker.FileName;
                }
                dynamic app = CorelApp.Get();
                bool alreadyOpen = false;
                foreach (dynamic candidate in app.Documents)
                    if (String.Equals(Convert.ToString(candidate.FullFileName), path,
                        StringComparison.OrdinalIgnoreCase))
                    { candidate.Activate(); alreadyOpen = true; break; }
                if (!alreadyOpen) app.OpenDocument(path);
                RefreshDocument();
                if (!SameDocument(_documentIdentity, (object)app.ActiveDocument) ||
                    !String.Equals(_documentPath, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Не удалось прочитать карточку из выбранного CDR.");
                if (_data.ServerOrderId > 0 && _data.ServerOrderId != selected.Id)
                {
                    _status("Файл открыт, но в нём записан другой заказ. Проверьте выбранный CDR.", true);
                    return;
                }
                if (_data.ServerOrderId == 0)
                {
                    string question = "Файл «" + Path.GetFileName(path) +
                        "» ещё не привязан к заказу.\nПривязать его к заказу №" +
                        selected.Number + " и сохранить CDR?";
                    if (MessageBox.Show(question, "Vanya Tools", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        _status("CDR открыт без привязки к заказу №" + selected.Number + ".", false);
                        return;
                    }

                    object openedDocument = (object)app.ActiveDocument;
                    _busy = true;
                    UpdateActions();
                    try
                    {
                        var result = await OrderServerClient.Get(server, _key.Password.Trim(), selected.Id);
                        if (!SameDocument(openedDocument, (object)CorelApp.Get().ActiveDocument))
                            throw new InvalidOperationException("Вернитесь в выбранный CDR и повторите привязку.");
                        if (Convert.ToInt32(result["id"]) != selected.Id)
                            throw new InvalidOperationException("Сервер вернул другой заказ. Привязка отменена.");
                        if (Convert.ToString(result["sendState"]) == "reserved")
                            throw new InvalidOperationException("Заказ ещё без макета. Используйте «Связать с CDR».");
                        string clientKey = Convert.ToString(result["clientKey"]);
                        if (String.IsNullOrWhiteSpace(clientKey))
                            throw new InvalidOperationException("Сервер не вернул ID карточки заказа.");

                        FillEmptyFieldsFromServer(result);
                        ReadForm();
                        _data.ClientKey = clientKey;
                        ApplyServerResult(result);
                        SaveData();
                        CorelDocumentSaver.Save((dynamic)openedDocument);
                        UpdateCaption();
                        _documentName.Text = Path.GetFileName(path) + " · сохранён";
                    }
                    finally { _busy = false; UpdateActions(); }
                }
                await Task.Run(() => OrderCdrPathIndex.Remember(server, selected.Id, path, yearRoot));
                _status("Открыт и привязан CDR заказа №" + selected.Number + ": " + path, false);
            }
            catch (Exception error)
            {
                Log.Error("Order CDR open failed", error);
                _status("Открытие CDR: " + error.Message, true);
            }
        }

        private async Task AssignIntake(OrderListItem selected)
        {
            if (_busy) return;
            bool linked = false;
            try
            {
                if (CorelApp.Get().ActiveDocument == null)
                    throw new InvalidOperationException("Откройте документ CorelDRAW.");
                RefreshDocument();
                dynamic doc = EnsureDocument(true, true);
                if (_data.ServerOrderId > 0) throw new InvalidOperationException("Карточке уже присвоен номер.");
                if (!String.IsNullOrWhiteSpace(selected.Customer) &&
                    String.IsNullOrWhiteSpace(_customer.Text)) _customer.Text = selected.Customer;
                if (String.IsNullOrWhiteSpace(_designName.Text))
                    _designName.Text = "Макет №" + selected.Number;
                bool saveCdr = !String.IsNullOrWhiteSpace(Convert.ToString(doc.FullFileName));
                if (!saveCdr)
                {
                    var choice = AskUnsavedPublish("Привязка заказа",
                        "Этот CDR ещё не сохранён. Как привязать заказ №" + selected.Number + "?");
                    if (choice == UnsavedPublishChoice.Cancel) return;
                    if (choice == UnsavedPublishChoice.Save)
                    {
                        try { saveCdr = SaveNewDocument(doc, false); }
                        catch (Exception saveError)
                        {
                            Log.Error("Order CDR save before assign failed", saveError);
                            if (MessageBox.Show("CDR не сохранён: " + saveError.Message +
                                    "\nПродолжить привязку без сохранения?", "Vanya Tools",
                                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                                return;
                        }
                    }
                }
                ReadForm();
                if (String.IsNullOrEmpty(_data.ClientKey)) _data.ClientKey = Guid.NewGuid().ToString();
                if (_data.PendingIntakeId == 0) _data.PendingIntakeId = selected.Id;
                if (_data.PendingIntakeId != selected.Id)
                    throw new InvalidOperationException("Карточка ожидает другой номер. Выберите прежний заказ.");
                SaveData();
                if (saveCdr) CorelDocumentSaver.Save(doc);
                _busy = true; UpdateActions();
                var result = await OrderServerClient.Assign(_server.Text.Trim(), _key.Password.Trim(),
                    _data.PendingIntakeId, _data.ClientKey);
                if (!SameDocument(doc, (object)CorelApp.Get().ActiveDocument))
                    throw new InvalidOperationException("Вернитесь в исходный CDR и повторите присвоение номера.");
                ReadForm(); ApplyServerResult(result); _data.PendingIntakeId = 0; SaveData();
                linked = true;
                string saveWarning = null;
                try { if (saveCdr) { CorelDocumentSaver.Save(doc); RememberCurrentOrderFile(); } }
                catch (Exception saveError)
                {
                    Log.Error("Order assigned but CDR save failed", saveError);
                    saveWarning = saveError.Message;
                }
                if (saveWarning != null)
                    _status("Заказ №" + _data.ServerOrderNumber +
                        " привязан, но CDR не сохранён: " + saveWarning, true);
                else _status("Заказ №" + _data.ServerOrderNumber +
                    (saveCdr ? " связан с CDR. Добавьте кадры и опубликуйте фото."
                        : " привязан. CDR не сохранён; добавьте кадры и опубликуйте фото."), false);
            }
            catch (Exception error)
            {
                if (error.Message.Contains("другой карточке") || error.Message.Contains("другой номер"))
                { _data.PendingIntakeId = 0; if (_documentPath != null) SaveData(); }
                _status("Присвоение номера: " + error.Message, true);
            }
            finally { _busy = false; UpdateActions(); }
            if (linked)
            {
                var remaining = (_unassignedTable.ItemsSource as IEnumerable<OrderListItem>)?
                    .Where(order => order.Id != selected.Id).ToList();
                if (remaining != null)
                {
                    _unassignedTable.ItemsSource = remaining;
                    _unassignedTable.Visibility = remaining.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    _unassignedHint.Visibility = remaining.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    _unassignedCount.Text = remaining.Count > 0
                        ? "Найдено: " + remaining.Count : "Сейчас заказов без макета нет.";
                }
                if (OrdersView.IsLoaded) await LoadOrders();
                await LoadUnassignedTable();
            }
        }

        private enum UnsavedPublishChoice { Save, Continue, Cancel }

        private UnsavedPublishChoice AskUnsavedPublish(string title = "Публикация карточки",
            string message = "Этот CDR ещё не сохранён. Как опубликовать карточку?")
        {
            var choice = UnsavedPublishChoice.Cancel;
            var window = new Window { Title = title, Width = 475,
                SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false };
            var owner = Window.GetWindow(this);
            if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = message,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var option in new[] {
                new { Text = "Сохранить", Choice = UnsavedPublishChoice.Save },
                new { Text = "Продолжить без сохранения", Choice = UnsavedPublishChoice.Continue },
                new { Text = "Отмена", Choice = UnsavedPublishChoice.Cancel }
            })
            {
                var button = Button(option.Text, (_, __) => { choice = option.Choice; window.Close(); });
                button.Margin = new Thickness(0, 0, 5, 0);
                buttons.Children.Add(button);
            }
            panel.Children.Add(buttons);
            window.Content = panel;
            window.ShowDialog();
            return choice;
        }

        private async Task Publish(bool includePhotos)
        {
            if (_busy)
            {
                _status("Карточка: дождитесь завершения текущей операции.", true);
                return;
            }
            string server = _server.Text.Trim(), key = _key.Password.Trim();
            if (server.Length == 0 || key.Length == 0)
            { _status("Укажите адрес и ключ сервера во вкладке «Настройки».", true); return; }
            _busy = true; UpdateActions();
            _status("Карточка: подготовка публикации…", false);
            Log.Info("Order card publish started.");
            try
            {
                dynamic doc = EnsureDocument(true, true); ReadForm();
                if (_data.PendingIntakeId > 0)
                    throw new InvalidOperationException("Сначала повторите присвоение номера с приёмки.");
                if (_data.Customer.Length == 0) throw new InvalidOperationException("Введите имя заказчика.");
                bool useCurrentSelection = includePhotos && _data.Frames.Count == 0;
                if (includePhotos && !ResolveSelectionFrame()) return;
                string caption = ComposeCaption();
                if (caption.Length > 990) throw new InvalidOperationException("Подпись длиннее 990 символов. Сократите описание.");
                if (_data.ServerOrderId == 0 && _data.TelegramMessageId > 0 &&
                    MessageBox.Show("Эта карточка уже отправлена напрямую в Telegram. Опубликовать новую серверную карточку?",
                        "Vanya Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (String.IsNullOrEmpty(_data.ClientKey)) _data.ClientKey = Guid.NewGuid().ToString();
                bool saveCdr = !String.IsNullOrWhiteSpace(Convert.ToString(doc.FullFileName));
                if (!saveCdr)
                {
                    var choice = AskUnsavedPublish();
                    if (choice == UnsavedPublishChoice.Cancel) return;
                    if (choice == UnsavedPublishChoice.Save)
                    {
                        try { saveCdr = SaveNewDocument(doc, false); }
                        catch (Exception saveError)
                        {
                            Log.Error("Order CDR save before publish failed", saveError);
                            if (MessageBox.Show("CDR не сохранён: " + saveError.Message +
                                    "\nПродолжить публикацию без сохранения?", "Vanya Tools",
                                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                                return;
                        }
                    }
                }
                SaveData(); SaveServerSettings(false);
                int requestRevision = _data.ServerRevision;
                string knownDueDate = _data.ServerDueDate ?? "";
                string currentDueDate = knownDueDate;
                if (_data.ServerOrderId > 0)
                {
                    _status("Проверка изменений заказа на сервере…", false);
                    var current = await OrderServerClient.Get(server, key, _data.ServerOrderId);
                    currentDueDate = ServerDueDate(current);
                    if (!SameDocument((object)doc, (object)CorelApp.Get().ActiveDocument))
                        throw new InvalidOperationException("Вернитесь в исходный CDR и повторите обновление.");
                    if (!String.Equals(Convert.ToString(current["clientKey"]), _data.ClientKey,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Этот CDR связан с другой карточкой заказа.");
                    int latestRevision = Convert.ToInt32(current["revision"]);
                    if (latestRevision != requestRevision)
                    {
                        string currentFingerprint = ServerContentFingerprint(current);
                        bool contentUnchanged = !String.IsNullOrEmpty(_data.ServerContentFingerprint) &&
                            String.Equals(_data.ServerContentFingerprint, currentFingerprint,
                                StringComparison.Ordinal);
                        if (!contentUnchanged && MessageBox.Show(
                            "Заказ №" + _data.ServerOrderNumber +
                            " изменился на сервере после последнего сохранения этого CDR. " +
                            "Там могли обновить текст или фото.\n\n" +
                            "Опубликовать текущие поля и кадры из CDR поверх серверной карточки? " +
                            "При отмене правки останутся в открытом CDR; при необходимости сохраните его Ctrl+S.",
                            "Vanya Tools · конфликт карточки", MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        {
                            _status("Обновление отменено. Поля и кадры в открытом CDR не заменены.", false);
                            return;
                        }
                        requestRevision = latestRevision;
                    }
                }
                var photos = new List<byte[]>();
                if (includePhotos) for (int i = 0; i < _data.Frames.Count; i++)
                {
                    _status("Подготовка кадра " + (i + 1) + " из " + _data.Frames.Count + "…", false);
                    byte[] photo;
                    try { photo = CapturePhoto(doc, _data.Frames[i], useCurrentSelection && i == 0); }
                    catch (InvalidOperationException error) when (
                        error.Message.StartsWith("Объект кадра удалён", StringComparison.Ordinal) ||
                        error.Message.StartsWith("Страница кадра не найдена", StringComparison.Ordinal))
                    {
                        if (!ResolveSelectionFrame(i)) return;
                        photo = CapturePhoto(doc, _data.Frames[i], true);
                    }
                    photos.Add(photo);
                    _data.Frames[i].Thumbnail = ThumbnailFromPhoto(photo);
                }
                if (photos.Count > 0) { SaveData(); RenderFrames(); }
                ReadForm();
                if (_data.Customer.Length == 0) throw new InvalidOperationException("Введите имя заказчика.");
                string requestedDueDate = _data.DueDate ?? "";
                bool deadlineEdited = !String.Equals(requestedDueDate, knownDueDate,
                    StringComparison.Ordinal);
                if (_data.ServerOrderId > 0 && !deadlineEdited &&
                    !String.Equals(currentDueDate, knownDueDate, StringComparison.Ordinal))
                {
                    requestedDueDate = currentDueDate;
                    _data.DueDate = currentDueDate;
                    _data.ServerDueDate = currentDueDate;
                    _loadingForm = true;
                    try { _dueDate.SelectedDate = ParseServerDate(currentDueDate); }
                    finally { _loadingForm = false; }
                }
                else if (_data.ServerOrderId > 0 && deadlineEdited &&
                    !String.Equals(currentDueDate, knownDueDate, StringComparison.Ordinal) &&
                    !String.Equals(currentDueDate, requestedDueDate, StringComparison.Ordinal) &&
                    MessageBox.Show("Срок заказа изменился на сервере после последнего сохранения CDR. " +
                        "Записать дату из этой карточки поверх серверного срока?",
                        "Vanya Tools · конфликт срока", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    _status("Публикация отменена: срок изменился на сервере.", false);
                    return;
                }
                caption = ComposeCaption();
                if (caption.Length > 990) throw new InvalidOperationException("Подпись длиннее 990 символов. Сократите описание.");
                SaveData();
                _status(_data.ServerOrderId == 0 ? "Публикация через сервер…" : "Обновление карточки…", false);
                var details = new Dictionary<string, object> {
                    ["clientKey"] = _data.ClientKey, ["caption"] = caption,
                    ["customer"] = _data.Customer, ["source"] = _data.Source,
                    ["technologies"] = _data.Technologies,
                    ["description"] = _data.Description, ["items"] = _data.Items,
                    ["revision"] = requestRevision
                };
                if (_data.ServerOrderId == 0 || _data.IsDelivery != _data.ServerDelivery)
                    details["isDelivery"] = _data.IsDelivery;
                var result = await OrderServerClient.Publish(server, key, details, photos, _data.ServerOrderId);
                if (!SameDocument((object)doc, (object)CorelApp.Get().ActiveDocument))
                    throw new InvalidOperationException("Вернитесь в исходный CDR и загрузите заказ с сервера.");
                _data.PublishedAtUtc = DateTime.UtcNow.ToString("o");
                ApplyServerResult(result);
                string deadlineWarning = null;
                if (deadlineEdited && !String.Equals(requestedDueDate, ServerDueDate(result),
                    StringComparison.Ordinal))
                {
                    try
                    {
                        _status("Сохраняю срок заказа…", false);
                        var deadlineResult = await OrderServerClient.SetDeadline(server, key,
                            _data.ServerOrderId, requestedDueDate, _data.ServerRevision);
                        if (!SameDocument((object)doc, (object)CorelApp.Get().ActiveDocument))
                            throw new InvalidOperationException("Вернитесь в исходный CDR и загрузите срок с сервера.");
                        ApplyServerResult(deadlineResult);
                    }
                    catch (Exception deadlineError)
                    {
                        Log.Error("Order published but deadline save failed", deadlineError);
                        deadlineWarning = deadlineError.Message;
                    }
                }
                if (!SameDocument((object)doc, (object)CorelApp.Get().ActiveDocument))
                    throw new InvalidOperationException("Вернитесь в исходный CDR и загрузите заказ с сервера.");
                string saveWarning = null;
                try
                {
                    SaveData();
                    if (saveCdr) { CorelDocumentSaver.Save(doc); RememberCurrentOrderFile(); }
                }
                catch (Exception saveError)
                {
                    Log.Error("Order published but CDR save failed", saveError);
                    saveWarning = saveError.Message;
                }
                UpdateActions();
                if (Convert.ToString(result["sendState"]) != "sent" && Convert.ToString(result["sendState"]) != "local")
                    throw new InvalidOperationException("Публикация не завершена. Проверьте канал и загрузите карточку с сервера.");
                if (saveWarning != null || deadlineWarning != null)
                {
                    Log.Info("Order card published: " + _data.ServerOrderNumber + " (save warning).");
                    _status("Заказ №" + _data.ServerOrderNumber +
                        " опубликован." + (deadlineWarning == null ? "" :
                            " Срок не сохранён: " + deadlineWarning + ".") +
                        (saveWarning == null ? "" : " CDR не сохранён: " + saveWarning), true);
                    return;
                }
                Log.Info("Order card published: " + _data.ServerOrderNumber +
                    " (" + _data.ServerSendState + ").");
                _status("Заказ №" + _data.ServerOrderNumber +
                    (_data.ServerSendState == "local" ? " сохранён на сервере." : " сохранён в Telegram.") +
                    (saveCdr ? " CDR сохранён." : " CDR не сохранён."), false);
            }
            catch (Exception error)
            {
                Log.Error("Order card publish failed", error);
                _status(error.Message.Contains("Карточка изменена")
                    ? "Заказ снова изменился во время отправки. Текущий CDR сохраните и повторите обновление."
                    : "Карточка: " + error.Message.Replace(key, "[скрыто]"), true);
            }
            finally { _busy = false; UpdateActions(); }
        }

        private void ApplyServerResult(Dictionary<string, object> result)
        {
            _data.ServerOrderId = Convert.ToInt32(result["id"]);
            _data.ServerOrderNumber = Convert.ToString(result["number"]);
            _data.ServerRevision = Convert.ToInt32(result["revision"]);
            if (result.TryGetValue("clientKey", out object clientKey) &&
                !String.IsNullOrWhiteSpace(Convert.ToString(clientKey)))
                _data.ClientKey = Convert.ToString(clientKey);
            _data.ServerContentFingerprint = ServerContentFingerprint(result);
            ApplyWorkflowResult(result);
            _data.IsDelivery = _data.ServerDelivery;
            _delivery.IsChecked = _data.IsDelivery;
            if (result.TryGetValue("photoIds", out object ids) && ids is object[] photos && photos.Length > 0)
                _data.TelegramMessageId = Convert.ToInt32(photos[0]);
        }

        private static string ServerContentFingerprint(Dictionary<string, object> result)
        {
            string[] Strings(string key) => (OrderValue(result, key) as IList)?
                .Cast<object>().Select(item => Convert.ToString(item)).ToArray() ?? new string[0];
            string json = new JavaScriptSerializer().Serialize(new {
                caption = Convert.ToString(OrderValue(result, "caption")),
                customer = Convert.ToString(OrderValue(result, "customer")),
                source = Convert.ToString(OrderValue(result, "source")),
                technologies = Strings("technologies"),
                description = Convert.ToString(OrderValue(result, "description")),
                items = Convert.ToString(OrderValue(result, "items")),
                photoVersions = Strings("photoVersions")
            });
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(json)));
        }

        private async Task RefreshServer()
        {
            if (_busy || _data.ServerOrderId == 0) return;
            _busy = true; _refreshServer.IsEnabled = false;
            try
            {
                object doc = (object)EnsureDocument();
                var result = await OrderServerClient.Get(_server.Text.Trim(), _key.Password.Trim(), _data.ServerOrderId);
                if (!SameDocument(doc, (object)CorelApp.Get().ActiveDocument))
                    throw new InvalidOperationException("Вернитесь в исходный CDR и повторите загрузку.");
                if (ServerTextDiffers(result) && MessageBox.Show(
                    "Текстовые поля в CDR отличаются от данных на сервере. " +
                    "Заменить заказчика, срок, источник, технологию, описание и изделия серверными данными? " +
                    "Кадры макета останутся в CDR.",
                    "Vanya Tools · загрузка карточки", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    _status("Загрузка отменена. Текстовые поля CDR не изменены.", false);
                    return;
                }
                LoadServerFields(result);
                ReadForm(); ApplyServerResult(result); SaveData(); UpdateCaption(); UpdateActions();
                CorelDocumentSaver.Save(CorelApp.Get().ActiveDocument);
                _status("Заказ №" + _data.ServerOrderNumber + " загружен с сервера. CDR сохранён.", false);
            }
            catch (Exception error) { _status("Загрузка заказа: " + error.Message, true); }
            finally { _busy = false; _refreshServer.IsEnabled = true; }
        }

        private bool ServerTextDiffers(Dictionary<string, object> result)
        {
            if (!String.Equals(SelectedDueDate(), ServerDueDate(result),
                StringComparison.Ordinal)) return true;
            if (!String.Equals(_customer.Text.Trim(), Convert.ToString(OrderValue(result, "customer")),
                StringComparison.Ordinal) ||
                !String.Equals(_sourceValue.Trim(), Convert.ToString(OrderValue(result, "source")),
                    StringComparison.Ordinal) ||
                !String.Equals(_description.Text.Trim(), Convert.ToString(OrderValue(result, "description")),
                    StringComparison.Ordinal) ||
                !String.Equals(_items.Text.Trim(), Convert.ToString(OrderValue(result, "items")),
                    StringComparison.Ordinal)) return true;
            var local = new HashSet<string>(_technologies.Where(box => box.IsChecked == true)
                .Select(box => Convert.ToString(box.Content)), StringComparer.Ordinal);
            var server = new HashSet<string>((OrderValue(result, "technologies") as IList)?
                .Cast<object>().Select(item => Convert.ToString(item)) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            return !local.SetEquals(server);
        }

        private async Task OpenServerOrder(string number)
        {
            if (_busy) return;
            if (!System.Text.RegularExpressions.Regex.IsMatch(number, @"^(?:\d{4}/)?\d{5}$"))
            { _status("Введите номер ММ###, например 10001. Для старого года: 2026/10001.", true); return; }
            try
            {
                dynamic active = CorelApp.Get().ActiveDocument;
                if (active == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
                RefreshDocument();
                object identity = (object)active;
                _busy = true; UpdateActions();
                var result = await OrderServerClient.GetByNumber(_server.Text.Trim(), _key.Password.Trim(), number);
                if (!SameDocument(identity, (object)CorelApp.Get().ActiveDocument))
                    throw new InvalidOperationException("Вернитесь в исходный CDR и повторите открытие заказа.");
                if (Convert.ToString(result["sendState"]) == "reserved")
                    throw new InvalidOperationException("У заказа пока нет макета. Используйте «Связать с CDR».");
                bool unsaved = String.IsNullOrWhiteSpace(Convert.ToString(active.FullFileName));
                if (!unsaved) EnsureDocument();
                if (_data.ServerOrderNumber != number && (_data.ServerOrderId > 0 ||
                    _data.Frames.Count > 0 || !String.IsNullOrWhiteSpace(_data.Customer)) &&
                    MessageBox.Show("Заменить карточку в текущем CDR данными заказа №" + number + "?",
                        "Vanya Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (unsaved)
                {
                    LoadServerFields(result);
                    if (String.IsNullOrWhiteSpace(_designName.Text)) _designName.Text = "Макет №" + number;
                }
                dynamic doc = EnsureDocument();
                if (_data.ServerOrderId != Convert.ToInt32(result["id"]))
                    _data = new OrderCardData { ClientKey = Convert.ToString(result["clientKey"]) };
                LoadServerFields(result);
                ReadForm(); ApplyServerResult(result); SaveData(); RenderFrames(); UpdateCaption(); UpdateActions();
                CorelDocumentSaver.Save(doc);
                _status("Заказ №" + number + " открыт в CDR.", false);
            }
            catch (Exception error) { _status("Открытие заказа: " + error.Message, true); }
            finally { _busy = false; UpdateActions(); }
        }

        private void LoadServerFields(Dictionary<string, object> result)
        {
            _loadingForm = true;
            try
            {
                _dueDate.SelectedDate = ParseServerDate(ServerDueDate(result));
                _customer.Text = Convert.ToString(result["customer"]);
                SetSourceSelection(Convert.ToString(result["source"]));
                _description.Text = Convert.ToString(result["description"]);
                _items.Text = Convert.ToString(result["items"]);
                var technologies = result["technologies"] as object[] ?? new object[0];
                foreach (var box in _technologies)
                    box.IsChecked = technologies.Any(x => Convert.ToString(x) == Convert.ToString(box.Content));
                _delivery.IsChecked = Flag(result, "isDelivery");
            }
            finally { _loadingForm = false; }
        }

        private void FillEmptyFieldsFromServer(Dictionary<string, object> result)
        {
            _loadingForm = true;
            try
            {
                if (!_dueDate.SelectedDate.HasValue)
                    _dueDate.SelectedDate = ParseServerDate(ServerDueDate(result));
                if (String.IsNullOrWhiteSpace(_customer.Text))
                    _customer.Text = Convert.ToString(result["customer"]);
                if (String.IsNullOrWhiteSpace(_sourceValue))
                    SetSourceSelection(Convert.ToString(result["source"]));
                if (String.IsNullOrWhiteSpace(_description.Text))
                    _description.Text = Convert.ToString(result["description"]);
                if (String.IsNullOrWhiteSpace(_items.Text))
                    _items.Text = Convert.ToString(result["items"]);
                if (!_technologies.Any(box => box.IsChecked == true))
                {
                    var technologies = result["technologies"] as object[] ?? new object[0];
                    foreach (var box in _technologies)
                        box.IsChecked = technologies.Any(x =>
                            Convert.ToString(x) == Convert.ToString(box.Content));
                }
                if (_data.IsDelivery == _data.ServerDelivery)
                    _delivery.IsChecked = Flag(result, "isDelivery");
            }
            finally { _loadingForm = false; }
        }

        private static bool Flag(Dictionary<string, object> result, string key) =>
            result.TryGetValue(key, out object value) && value != null && Convert.ToBoolean(value);

        private static string ServerDueDate(Dictionary<string, object> result) =>
            Convert.ToString(OrderValue(result, "dueDate")) ?? "";

        private void MergeServerDueDate(Dictionary<string, object> result)
        {
            if (_dueDate.IsKeyboardFocusWithin) return;
            string serverDate = ServerDueDate(result);
            if (String.Equals(_data.DueDate ?? "", _data.ServerDueDate ?? "",
                StringComparison.Ordinal))
            {
                _data.DueDate = serverDate;
                bool wasLoading = _loadingForm;
                _loadingForm = true;
                try { _dueDate.SelectedDate = ParseServerDate(serverDate); }
                finally { _loadingForm = wasLoading; }
            }
            _data.ServerDueDate = serverDate;
        }

        private void ApplyWorkflowResult(Dictionary<string, object> result)
        {
            // Keep the editor revision: polling must not make stale text safe to overwrite remotely.
            MergeServerDueDate(result);
            _data.ServerState = Convert.ToString(result["state"]);
            _data.ServerSendState = Convert.ToString(result["sendState"]);
            _data.ServerBlanksPaused = Flag(result, "blanksPaused");
            _data.ServerBlanksReady = Flag(result, "blanksReady");
            _data.ServerApplicationPaused = Flag(result, "applicationPaused");
            _data.ServerUrgent = Flag(result, "isUrgent");
            bool serverDelivery = Flag(result, "isDelivery");
            if (_data.IsDelivery == _data.ServerDelivery && _delivery != null)
            {
                _data.IsDelivery = serverDelivery;
                _loadingForm = true;
                try { _delivery.IsChecked = serverDelivery; }
                finally { _loadingForm = false; }
            }
            _data.ServerDelivery = serverDelivery;
        }

        private static string OrderStatus(OrderListItem order) =>
            (order.SendState == "reserved" ? "Без макета" :
                order.State == "done" ? "🔥 Готов" :
                order.SendState == "draft" ? "Черновик" : "🌲 В работе") +
            (order.Urgent ? " ⚡" : "") +
            (order.Delivery ? " 🚚 ДОСТАВКА" : "") +
            (order.BlanksReady ? " ❤️ Заготовки" : "") +
            (order.BlanksPaused ? " ⏸ Заготовки" : "") +
            (order.ApplicationPaused ? " ⏸ Нанесение" : "");

        private static void ApplyWorkflowResult(OrderListItem order, Dictionary<string, object> result)
        {
            order.State = Convert.ToString(result["state"]);
            order.SendState = Convert.ToString(result["sendState"]);
            order.BlanksPaused = Flag(result, "blanksPaused");
            order.BlanksReady = Flag(result, "blanksReady");
            order.ApplicationPaused = Flag(result, "applicationPaused");
            order.Urgent = Flag(result, "isUrgent");
            order.Delivery = Flag(result, "isDelivery");
            order.Updated = OrderDate(OrderValue(result, "updatedAt"));
            order.Completed = OrderDate(OrderValue(result, "completedAt"));
            order.CompletedAt = OrderTimestamp(OrderValue(result, "completedAt"));
            order.Status = OrderStatus(order);
        }

        private static void ApplyOptimisticWorkflow(OrderListItem order, string action)
        {
            switch (action)
            {
                case "state":
                    order.State = order.State == "done" ? "new" : "done";
                    order.Completed = order.State == "done" ? DateTime.Now.ToString("dd.MM.yy HH:mm") : "";
                    order.CompletedAt = order.State == "done" ? DateTimeOffset.Now : (DateTimeOffset?)null;
                    break;
                case "blanks": order.BlanksPaused = !order.BlanksPaused; break;
                case "application": order.ApplicationPaused = !order.ApplicationPaused; break;
                case "urgent": order.Urgent = !order.Urgent; break;
                case "blanksReady": order.BlanksReady = !order.BlanksReady; break;
            }
            order.Updated = DateTime.Now.ToString("dd.MM.yy HH:mm");
            order.Status = OrderStatus(order);
        }

        private async Task ChangeWorkflow(string action, bool fromList = false)
        {
            var selected = fromList ? _ordersTable.SelectedItem as OrderListItem : null;
            int id = fromList ? selected?.Id ?? 0 : _data.ServerOrderId;
            if (_busy || id == 0) return;
            string state = fromList ? selected.State : _data.ServerState;
            bool blanks = fromList ? selected.BlanksPaused : _data.ServerBlanksPaused;
            bool blanksReady = fromList ? selected.BlanksReady : _data.ServerBlanksReady;
            bool application = fromList ? selected.ApplicationPaused : _data.ServerApplicationPaused;
            bool urgent = fromList ? selected.Urgent : _data.ServerUrgent;
            string previousUpdated = selected?.Updated, previousCompleted = selected?.Completed;
            DateTimeOffset? previousCompletedAt = selected?.CompletedAt;
            _busy = true;
            if (selected != null)
            {
                ApplyOptimisticWorkflow(selected, action);
                FilterOrders();
            }
            UpdateActions();
            try
            {
                object doc = fromList ? null : (object)EnsureDocument();
                if (!fromList && id != _data.ServerOrderId)
                    throw new InvalidOperationException("Документ изменился. Проверьте открытую карточку и повторите действие.");
                string server = _server.Text.Trim(), key = _key.Password.Trim();
                var current = await OrderServerClient.Get(server, key, id);
                int revision = Convert.ToInt32(current["revision"]);
                var result = action == "state"
                    ? await OrderServerClient.SetState(server, key, id, state == "done" ? "new" : "done", revision)
                    : action == "urgent"
                    ? await OrderServerClient.SetUrgency(server, key, id, !urgent, revision)
                    : action == "blanksReady"
                    ? await OrderServerClient.SetBlanksReady(server, key, id, !blanksReady, revision)
                    : await OrderServerClient.SetPause(server, key, id, action,
                        action == "blanks" ? !blanks : !application, revision);
                if (selected != null)
                {
                    ApplyWorkflowResult(selected, result);
                    FilterOrders();
                }
                if (id == _data.ServerOrderId && (doc == null ||
                    SameDocument(doc, (object)CorelApp.Get().ActiveDocument)))
                {
                    ApplyWorkflowResult(result);
                    if (_data.ServerRevision == revision)
                        _data.ServerRevision = Convert.ToInt32(result["revision"]);
                    if (doc != null) { SaveData(); CorelDocumentSaver.Save(CorelApp.Get().ActiveDocument); }
                }
                _status("Состояние заказа №" + Convert.ToString(result["number"]) + " обновлено.", false);
            }
            catch (Exception error)
            {
                if (selected != null)
                {
                    selected.State = state;
                    selected.BlanksPaused = blanks;
                    selected.BlanksReady = blanksReady;
                    selected.ApplicationPaused = application;
                    selected.Urgent = urgent;
                    selected.Updated = previousUpdated;
                    selected.Completed = previousCompleted;
                    selected.CompletedAt = previousCompletedAt;
                    selected.Status = OrderStatus(selected);
                    FilterOrders();
                }
                _status("Состояние заказа: " + error.Message, true);
            }
            finally { _busy = false; UpdateActions(); }
            await LoadOrders();
        }

        private string ComposeCaption()
        {
            string customer = _customer.Text.Trim(), source = _sourceValue.Trim();
            string tech = String.Join(", ", _technologies.Where(x => x.IsChecked == true).Select(x => Convert.ToString(x.Content)));
            var lines = new List<string> { (customer + " " + source).Trim(), tech };
            string details = _description.Text.Trim();
            string items = _items.Text.Trim();
            if (details.Length > 0 || items.Length > 0) lines.Add("");
            if (details.Length > 0) lines.Add(details);
            if (items.Length > 0) lines.Add(items);
            return String.Join("\n", lines).Trim();
        }

        private void UpdateCaption() { if (_caption != null) _caption.Text = ComposeCaption(); }

        private void UpdateActions()
        {
            if (_send == null) return;
            bool assigned = _data.ServerOrderId > 0;
            bool draft = _data.ServerSendState == "draft";
            bool published = assigned && !draft;
            _send.Content = published ? "Заменить фото и обновить текст" : draft ? "Опубликовать фото заказа" : "Опубликовать карточку";
            _send.IsEnabled = !_busy;
            _send.ToolTip = _busy ? "Дождитесь завершения операции с карточкой." : null;
            ToolTipService.SetShowOnDisabled(_send, true);
            _loadOrders.IsEnabled = !_busy && !_ordersLoading;
            _linkOrder.IsEnabled = !_busy && _ordersTable.SelectedItem != null;
            _linkOrder.Content = (_ordersTable.SelectedItem as OrderListItem)?.IsAvailable == true
                ? "Связать с CDR" : "Открыть файл CDR";
            _updateText.Visibility = published ? Visibility.Visible : Visibility.Collapsed;
            bool canWorkflow = assigned && (_data.ServerSendState == "sent" || _data.ServerSendState == "local");
            SetWorkflowButtons(new[] { _state, _blanksPause, _applicationPause, _urgent, _blanksReady },
                _data.ServerState, _data.ServerBlanksPaused, _data.ServerApplicationPaused, _data.ServerUrgent,
                _data.ServerBlanksReady, canWorkflow, assigned);
            var selected = _ordersTable.SelectedItem as OrderListItem;
            SetWorkflowButtons(_listWorkflow, selected?.State, selected?.BlanksPaused == true,
                selected?.ApplicationPaused == true, selected?.Urgent == true,
                selected?.BlanksReady == true,
                selected != null && (selected.SendState == "sent" || selected.SendState == "local"), selected != null);
            _refreshServer.Visibility = published ? Visibility.Visible : Visibility.Collapsed;
            _orderInfo.Text = assigned ? "Заказ №" + _data.ServerOrderNumber + " · " +
                (draft ? "карточка заполняется" : _data.ServerState == "done" ? "🔥 Готов" : "🌲 В работе") +
                (_data.ServerUrgent ? " · ⚡ Срочно" : "") +
                (_data.ServerDelivery ? " · 🚚 ДОСТАВКА" : "") +
                (_data.ServerBlanksReady ? " · ❤️ Заготовки готовы" : "") +
                (_data.ServerBlanksPaused ? " · ⏸ Заготовки" : "") +
                (_data.ServerApplicationPaused ? " · ⏸ Нанесение" : "") : "";
        }

        private void SetWorkflowButtons(Button[] buttons, string state, bool blanks, bool application,
            bool urgent, bool blanksReady, bool published, bool assigned)
        {
            string[] labels = { state == "done" ? "🌲 Вернуть в работу" : "🔥 Готов",
                blanks ? "▶ Заготовки продолжить" : "⏸ Заготовки пауза",
                application ? "▶ Нанесение продолжить" : "⏸ Нанесение пауза",
                urgent ? "⚡ Снять срочность" : "⚡ Срочно",
                blanksReady ? "❤️ Снять готовность заготовок" : "🤍 Заготовки готовы" };
            bool[] active = { state == "done", blanks, application, urgent, blanksReady };
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] == null) continue;
                buttons[i].Content = labels[i]; buttons[i].ToolTip = labels[i];
                buttons[i].IsEnabled = !_busy && (i == 3 ? assigned : published);
                buttons[i].FontWeight = active[i] ? FontWeights.Bold : FontWeights.Normal;
                if (active[i]) buttons[i].Foreground = Brushes.Firebrick;
                else buttons[i].ClearValue(Control.ForegroundProperty);
            }
        }

        private void SaveServerSettings(bool report = true)
        {
            string server = _server.Text.Trim(), key = _key.Password.Trim();
            if (server.Length == 0 || key.Length == 0)
                throw new InvalidOperationException("Укажите адрес и ключ сервера.");
            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, string>
                { ["server"] = server, ["key"] = key });
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllBytes(SettingsPath, encrypted);
            if (report) _status("Подключение к серверу сохранено.", false);
        }

        private void LoadServerSettings()
        {
            try
            {
                string file = File.Exists(SettingsPath) ? SettingsPath : PreviewSettingsPath;
                if (!File.Exists(file)) return;
                string json = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser));
                var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
                if (settings.TryGetValue("server", out string server)) _server.Text = server;
                if (settings.TryGetValue("key", out string key)) _key.Password = key;
            }
            catch { _status("Не удалось прочитать настройки сервера. Введите их повторно.", true); }
        }

        private void ChooseYearFolder()
        {
            var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            string selected = WindowsFolderPicker.Pick(source == null ? IntPtr.Zero : source.Handle,
                _yearFolder.Text.Trim());
            if (String.IsNullOrWhiteSpace(selected)) return;
            string previous = _yearFolder.Text;
            _yearFolder.Text = selected;
            try { SaveFileSettings(); }
            catch { _yearFolder.Text = previous; throw; }
        }

        private void SaveFileSettings()
        {
            string path = _yearFolder.Text.Trim();
            if (!Path.IsPathRooted(path))
                throw new InvalidOperationException("Укажите полный путь к папке года.");
            if (!Directory.Exists(path))
                throw new InvalidOperationException("Папка недоступна: " + path +
                    ". Проверьте сетевое подключение и права доступа.");
            path = Path.GetFullPath(path);
            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, string>
                { ["yearFolder"] = path });
            Directory.CreateDirectory(Path.GetDirectoryName(FileSettingsPath));
            File.WriteAllText(FileSettingsPath, json, Encoding.UTF8);
            _yearFolder.Text = path;
            _status("Папка года сохранена.", false);
        }

        private void LoadFileSettings()
        {
            try
            {
                if (!File.Exists(FileSettingsPath)) return;
                var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(FileSettingsPath, Encoding.UTF8));
                if (settings != null && settings.TryGetValue("yearFolder", out string path))
                    _yearFolder.Text = path;
            }
            catch { _status("Не удалось прочитать папку года. Укажите её повторно.", true); }
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception error) { Log.Error("Order card action failed", error); _status(error.Message, true); }
        }
        private static TextBlock Title(string text) => new TextBlock { Text = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
        private static TextBlock Label(string text) => new TextBlock { Text = text, FontSize = 11,
            Margin = new Thickness(0, 5, 0, 2) };
        private static TextBlock Note(string text) => new TextBlock { Text = text, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 2, 0, 4) };
        private static TextBox Input(int height = 0) => new TextBox { MinHeight = height > 0 ? height : 26,
            AcceptsReturn = height > 0, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = height > 0 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 3) };
        private FrameworkElement CreateQuickDocumentFields()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var labels = new[] { "Заказчик", "Имя макета" };
            var originals = new[] { _customer, _designName };
            for (int row = 0; row < labels.Length; row++)
            {
                var label = Label(labels[row]);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, 1, 8, 3);
                Grid.SetRow(label, row);
                grid.Children.Add(label);
                var input = Input();
                input.ToolTip = "Поле карточки заказа. Сохраняется в CDR автоматически.";
                input.SetBinding(TextBox.TextProperty, new Binding("Text")
                {
                    Source = originals[row], Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
                Grid.SetRow(input, row);
                Grid.SetColumn(input, 1);
                grid.Children.Add(input);
            }
            var save = Button("Сохранить", (_, __) => Safe(SaveCurrentDocument));
            save.ToolTip = "Сохранить CDR и данные карточки заказа";
            save.MinWidth = 76;
            save.Margin = new Thickness(5, 1, 0, 3);
            save.VerticalAlignment = VerticalAlignment.Stretch;
            save.SetBinding(IsEnabledProperty, new Binding("IsEnabled") { Source = _saveDocument });
            Grid.SetColumn(save, 2);
            Grid.SetRowSpan(save, 2);
            grid.Children.Add(save);
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(218, 238, 218)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(150, 192, 153)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 4), Margin = new Thickness(5, 4, 5, 5),
                Child = grid
            };
            border.SetBinding(IsEnabledProperty, new Binding("IsEnabled") { Source = _editor });
            return border;
        }
        private static Button Button(string text, RoutedEventHandler handler)
        {
            var button = new Button { Content = text, MinHeight = 29, Margin = new Thickness(1, 1, 1, 3) };
            button.Click += handler; return button;
        }
    }

    public sealed class OrderCardData
    {
        public int SchemaVersion { get; set; } = 5;
        public string Customer { get; set; }
        public string DesignName { get; set; }
        public string Source { get; set; }
        public List<string> Technologies { get; set; } = new List<string>();
        public string Description { get; set; }
        public string Items { get; set; }
        public string DueDate { get; set; }
        public string ServerDueDate { get; set; }
        public bool IsDelivery { get; set; }
        public List<OrderCardFrame> Frames { get; set; } = new List<OrderCardFrame>();
        public string PublishedAtUtc { get; set; }
        public int TelegramMessageId { get; set; }
        public string ClientKey { get; set; }
        public int ServerOrderId { get; set; }
        public string ServerOrderNumber { get; set; }
        public int ServerRevision { get; set; }
        public string ServerContentFingerprint { get; set; }
        public bool ServerBlanksPaused { get; set; }
        public bool ServerBlanksReady { get; set; }
        public bool ServerApplicationPaused { get; set; }
        public bool ServerUrgent { get; set; }
        public bool ServerDelivery { get; set; }
        public string ServerState { get; set; }
        public string ServerSendState { get; set; }
        public int PendingIntakeId { get; set; }
    }
    public sealed class OrderCardFrame
    {
        public List<OrderCardShape> Shapes { get; set; } = new List<OrderCardShape>();
        public string Thumbnail { get; set; }
    }
    public sealed class OrderCardShape
    {
        public int Page { get; set; }
        public int Id { get; set; }
    }
}
