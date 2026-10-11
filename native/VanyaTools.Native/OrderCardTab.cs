using System;
using System.Collections;
using System.Collections.Generic;
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
            _server, _orderNumber, _yearFolder, _ordersSearch;
        private readonly List<RadioButton> _sourceButtons = new List<RadioButton>();
        private readonly PasswordBox _key;
        private readonly TextBlock _caption, _documentName, _orderInfo, _ordersCount,
            _photoLabel, _photoMessage;
        private readonly StackPanel _frames, _editor;
        private readonly Expander _frameExpander;
        private readonly Button _send, _updateText, _state, _refreshServer,
            _saveDocument, _linkOrder, _loadOrders, _blanksPause, _applicationPause, _urgent, _blanksReady,
            _previousPhoto, _nextPhoto;
        private readonly Button[] _listWorkflow = new Button[5];
        private readonly DataGrid _ordersTable;
        private readonly Image _orderPhoto;
        private readonly Dictionary<string, DataGridColumn> _orderColumns = new Dictionary<string, DataGridColumn>();
        private readonly CheckBox _onlyAvailable;
        private readonly List<OrderListItem> _serverOrders = new List<OrderListItem>();
        private readonly DispatcherTimer _documentWatcher, _serverWatcher;
        private readonly List<CheckBox> _technologies = new List<CheckBox>();
        private OrderCardData _data = new OrderCardData();
        private string _documentPath;
        private string _lastWrittenJson;
        private object _documentIdentity;
        private object _failedDocumentIdentity;
        private string _failedDocumentPath;
        private bool _loadingForm;
        private bool _busy;
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

        internal OrderCardTab(Action<string, bool> status)
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
            var openRow = new DockPanel { Margin = new Thickness(0, 1, 0, 2) };
            var openButton = Button("Открыть заказ", async (_, __) => await OpenServerOrder());
            DockPanel.SetDock(openButton, Dock.Right); openRow.Children.Add(openButton);
            var openLabel = Label("ММ###"); openLabel.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(openLabel, Dock.Left); openRow.Children.Add(openLabel);
            _orderNumber = Input(); openRow.Children.Add(_orderNumber);
            panel.Children.Add(new Expander { Header = "Открыть прежний заказ", Content = openRow,
                Margin = new Thickness(0, 1, 0, 2) });
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
            _onlyAvailable = new CheckBox { Content = "Только без макета", Margin = new Thickness(2, 0, 0, 4) };
            _onlyAvailable.Checked += (_, __) => FilterOrders();
            _onlyAvailable.Unchecked += (_, __) => FilterOrders();
            ordersPanel.Children.Add(_onlyAvailable);
            _ordersCount = Note("Список загружается с сервера.");
            ordersPanel.Children.Add(_ordersCount);
            _ordersTable = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow, Height = 280, RowHeight = 24,
                Margin = new Thickness(1, 0, 1, 3) };
            _ordersTable.Columns.Add(new DataGridTextColumn { Header = "№", Binding = new Binding("Number"),
                Width = new DataGridLength(65) });
            var visibleColumns = LoadOrderColumns();
            AddOrderColumn(columnsMenu, visibleColumns, "customer", "Заказчик", "Customer",
                new DataGridLength(1, DataGridLengthUnitType.Star), true);
            AddOrderColumn(columnsMenu, visibleColumns, "source", "Источник", "Source",
                new DataGridLength(68), false);
            AddOrderColumn(columnsMenu, visibleColumns, "technologies", "Технология", "Technologies",
                new DataGridLength(110), false);
            AddOrderColumn(columnsMenu, visibleColumns, "created", "Принят", "Created",
                new DataGridLength(94), true);
            AddOrderColumn(columnsMenu, visibleColumns, "due", "Срок", "Due",
                new DataGridLength(78), true);
            AddOrderColumn(columnsMenu, visibleColumns, "updated", "Обновлён", "Updated",
                new DataGridLength(94), false);
            AddOrderColumn(columnsMenu, visibleColumns, "completed", "Завершён", "Completed",
                new DataGridLength(94), false);
            AddOrderColumn(columnsMenu, visibleColumns, "status", "Состояние", "Status",
                new DataGridLength(105), true);
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
            UpdatePhotoNavigation(null);
            var listActions = new UniformGrid { Columns = 2 };
            string[] actions = { "state", "blanks", "application", "urgent", "blanksReady" };
            for (int i = 0; i < actions.Length; i++)
            {
                string action = actions[i];
                _listWorkflow[i] = Button("", async (_, __) => await ChangeWorkflow(action, true));
                listActions.Children.Add(_listWorkflow[i]);
            }
            ordersPanel.Children.Add(listActions);
            _linkOrder = Button("Связать выбранный с CDR", async (_, __) => await LinkSelectedOrder());
            ordersPanel.Children.Add(_linkOrder);
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
            SettingsView = settings;
            LoadServerSettings();
            LoadFileSettings();
            foreach (var input in new[] { _customer, _designName, _description, _items })
                input.TextChanged += (_, __) => FormChanged();
            foreach (var box in _technologies) box.Checked += (_, __) => FormChanged();
            foreach (var box in _technologies) box.Unchecked += (_, __) => FormChanged();
            QuickDocumentFields = CreateQuickDocumentFields();
            _documentWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _documentWatcher.Tick += (_, __) => RefreshDocument();
            _serverWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _serverWatcher.Tick += async (_, __) => await LoadOrders(true);
            Loaded += async (_, __) => { _documentWatcher.Start(); _serverWatcher.Start(); RefreshDocument();
                if (!String.IsNullOrWhiteSpace(_key.Password)) await LoadOrders(); };
            Unloaded += (_, __) => { if (!QuickDocumentFields.IsLoaded) _documentWatcher.Stop();
                if (!OrdersView.IsLoaded) _serverWatcher.Stop(); };
            QuickDocumentFields.Loaded += (_, __) => { _documentWatcher.Start(); RefreshDocument(); };
            QuickDocumentFields.Unloaded += (_, __) => { if (!IsLoaded) _documentWatcher.Stop(); };
            OrdersView.Loaded += async (_, __) => { _serverWatcher.Start();
                if (!String.IsNullOrWhiteSpace(_key.Password)) await LoadOrders(); };
            OrdersView.Unloaded += (_, __) => { if (!IsLoaded) _serverWatcher.Stop(); };
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
            _orderNumber.Text = "";
            _loadingForm = true;
            try
            {
                _customer.Text = ""; _designName.Text = ""; SetSourceSelection("");
                _description.Text = ""; _items.Text = "";
                foreach (var box in _technologies) box.IsChecked = false;
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
            doc.Save();
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
            object options = CorelApp.Get().CreateStructSaveAsOptions();
            doc.GetType().InvokeMember("SaveAs", BindingFlags.InvokeMethod,
                null, (object)doc, new object[] { target, options });
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
                _orderNumber.Text = _data.ServerOrderNumber ?? "";
                _customer.Text = _data.Customer ?? "";
                _designName.Text = _data.DesignName ?? (_documentPath == null ? "" : Path.GetFileNameWithoutExtension(path));
                SetSourceSelection(_data.Source);
                _description.Text = _data.Description ?? "";
                _items.Text = _data.Items ?? "";
                foreach (var box in _technologies)
                    box.IsChecked = _data.Technologies.Contains(Convert.ToString(box.Content));
                RenderFrames(); UpdateCaption(); UpdateActions();
            }
            finally { _loadingForm = false; }
            _editor.IsEnabled = true;
            _saveDocument.IsEnabled = true;
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
            _data.Technologies = _technologies.Where(x => x.IsChecked == true)
                .Select(x => Convert.ToString(x.Content)).ToList();
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
                : Path.GetFileName(_documentPath) + " · " + _data.Frames.Count + " фото · Ctrl+S";
        }

        private void WriteData(dynamic doc)
        {
            _data.SchemaVersion = 4;
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
            dynamic doc = EnsureDocument(true, true);
            dynamic app = CorelApp.Get();
            dynamic selection = app.ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) == 0)
                throw new InvalidOperationException("Выделите один макет или группу объектов.");
            if (_data.Frames.Count >= 10)
                throw new InvalidOperationException("В карточке может быть не более 10 кадров.");
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
            ReadForm(); _data.Frames.Add(frame); SaveData(); RenderFrames(); UpdateCaption();
            _status("Кадр добавлен. Исходные объекты не сгруппированы.", false);
        }

        private void RenderFrames()
        {
            _frameExpander.Header = "Кадры (" + _data.Frames.Count + ")";
            _frames.Children.Clear();
            if (_data.Frames.Count == 0) { _frames.Children.Add(Note("Кадров нет. Выделите макет и нажмите «Добавить выделение».")); return; }
            for (int i = 0; i < _data.Frames.Count; i++)
            {
                int index = i;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = (i + 1) + ". Кадр · " + _data.Frames[i].Shapes.Count + " объект(ов)",
                    VerticalAlignment = VerticalAlignment.Center, Width = 175 });
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

        private static byte[] CapturePhoto(dynamic doc, OrderCardFrame frame)
        {
            dynamic app = CorelApp.Get();
            var original = new List<dynamic>();
            foreach (dynamic shape in app.ActiveSelectionRange.Shapes) original.Add(shape);
            dynamic originalPage = doc.ActivePage;
            string capturePath = null;
            try
            {
                SelectShapes(doc, frame);
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
            public string Customer { get; set; }
            public string Source { get; set; }
            public string Technologies { get; set; }
            public string Created { get; set; }
            public string Due { get; set; }
            public string Updated { get; set; }
            public string Completed { get; set; }
            public string Status { get; set; }
            public int PhotoCount { get; set; }
            public string[] PhotoVersions { get; set; } = new string[0];
            public string SendState { get; set; }
            public string State { get; set; }
            public bool BlanksPaused { get; set; }
            public bool BlanksReady { get; set; }
            public bool ApplicationPaused { get; set; }
            public bool Urgent { get; set; }
            public bool IsAvailable => SendState == "reserved";
        }

        private static HashSet<string> LoadOrderColumns()
        {
            try
            {
                if (!File.Exists(ColumnsSettingsPath)) return null;
                var names = new JavaScriptSerializer().Deserialize<string[]>(
                    File.ReadAllText(ColumnsSettingsPath, Encoding.UTF8));
                return new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception error)
            {
                Log.Error("Order columns could not be loaded", error);
                return null;
            }
        }

        private void AddOrderColumn(ContextMenu menu, HashSet<string> visible, string key,
            string title, string property, DataGridLength width, bool shownByDefault)
        {
            bool shown = visible == null ? shownByDefault : visible.Contains(key);
            var column = new DataGridTextColumn { Header = title, Binding = new Binding(property),
                Width = width, Visibility = shown ? Visibility.Visible : Visibility.Collapsed };
            _ordersTable.Columns.Add(column);
            _orderColumns.Add(key, column);
            var option = new MenuItem { Header = title, IsCheckable = true, IsChecked = shown };
            option.Click += (_, __) => {
                column.Visibility = option.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                try { SaveOrderColumns(); }
                catch (Exception error) { _status("Столбцы заказов: " + error.Message, true); }
            };
            menu.Items.Add(option);
        }

        private void SaveOrderColumns()
        {
            var visible = _orderColumns.Where(item => item.Value.Visibility == Visibility.Visible)
                .Select(item => item.Key).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(ColumnsSettingsPath));
            File.WriteAllText(ColumnsSettingsPath,
                new JavaScriptSerializer().Serialize(visible), Encoding.UTF8);
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
                selected.PhotoCount + ":" + index + ":" + version;
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
        }

        private async Task ShowSelectedPhoto()
        {
            var selected = _ordersTable.SelectedItem as OrderListItem;
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
                if (!_photoOriginals.TryGetValue(key, out byte[] bytes))
                {
                    bytes = await OrderServerClient.PhotoOriginal(_server.Text.Trim(),
                        _key.Password.Trim(), selected.Id, index);
                    RememberOriginal(key, bytes);
                }
                else RememberOriginal(key, bytes);
                if (!window.IsVisible) return;
                photo.Source = DecodePhoto(bytes, 4096);
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
                (query.Length == 0 || (row.Number ?? "").IndexOf(query,
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (row.Customer ?? "").IndexOf(query,
                    StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(row => row.Urgent && row.State != "done").ToList();
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

        private async Task LoadOrders(bool automatic = false)
        {
            if (_busy) return;
            if (String.IsNullOrWhiteSpace(_key.Password))
            {
                _ordersCount.Text = "Укажите ключ публикации в настройках.";
                return;
            }
            _busy = true; UpdateActions();
            try
            {
                var reply = await OrderServerClient.List(_server.Text.Trim(), _key.Password.Trim());
                _serverOrders.Clear();
                if (!reply.TryGetValue("orders", out object value) || !(value is IList orders))
                    throw new InvalidDataException("Сервер вернул неверный список заказов.");
                var all = new List<object>();
                foreach (object order in orders) all.Add(order);
                try
                {
                    var available = await OrderServerClient.Unassigned(_server.Text.Trim(), _key.Password.Trim());
                    if (available.TryGetValue("orders", out object pending) && pending is IList rows)
                        foreach (object row in rows) all.Add(row);
                }
                catch (Exception error) { Log.Error("Order intake list unavailable", error); }
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
                    _serverOrders.Add(new OrderListItem {
                        Id = id,
                        Number = Convert.ToString(row["number"]),
                        Customer = Convert.ToString(row["customer"]),
                        Source = Convert.ToString(row["source"]),
                        Technologies = technologies == null ? "" : String.Join(", ",
                            technologies.Cast<object>().Select(item => Convert.ToString(item))),
                        Created = OrderDate(OrderValue(row, "createdAt")),
                        Due = OrderDate(OrderValue(row, "dueDate"), true),
                        Updated = OrderDate(OrderValue(row, "updatedAt")),
                        Completed = OrderDate(OrderValue(row, "completedAt")),
                        PhotoCount = Convert.ToInt32(OrderValue(row, "photoCount") ?? 0),
                        PhotoVersions = versions == null ? new string[0] : versions.Cast<object>()
                            .Select(item => Convert.ToString(item)).ToArray(),
                        SendState = sendState,
                        State = state,
                        BlanksPaused = Flag(row, "blanksPaused"),
                        BlanksReady = Flag(row, "blanksReady"),
                        ApplicationPaused = Flag(row, "applicationPaused"),
                        Urgent = Flag(row, "isUrgent"),
                        Status = (sendState == "reserved" ? "Без макета" :
                            state == "done" ? "🔥 Готов" :
                            sendState == "draft" ? "Черновик" : "🌲 В работе") +
                            (Flag(row, "isUrgent") ? " ⚡" : "") +
                            (Flag(row, "blanksReady") ? " ❤️ Заготовки" : "") +
                            (Flag(row, "blanksPaused") ? " ⏸ Заготовки" : "") +
                            (Flag(row, "applicationPaused") ? " ⏸ Нанесение" : "")
                    });
                    if (id == _data.ServerOrderId) ApplyWorkflowResult(row);
                }
                int activeId = _data.ServerOrderId;
                if (activeId > 0 && !seen.Contains(activeId))
                {
                    var current = await OrderServerClient.Get(_server.Text.Trim(), _key.Password.Trim(), activeId);
                    if (_data.ServerOrderId == activeId) ApplyWorkflowResult(current);
                }
                FilterOrders();
            }
            catch (Exception error)
            {
                _ordersCount.Text = "Не удалось загрузить заказы: " + error.Message;
                if (!automatic) _status("Список заказов: " + error.Message, true);
            }
            finally { _busy = false; UpdateActions(); }
        }

        private async Task LinkSelectedOrder()
        {
            var selected = _ordersTable.SelectedItem as OrderListItem;
            if (selected == null) { _status("Выберите заказ в таблице.", true); return; }
            if (selected.IsAvailable) await AssignIntake(selected);
            else await OpenServerOrder(selected.Number);
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
                if (!String.IsNullOrWhiteSpace(selected.Customer) &&
                    String.IsNullOrWhiteSpace(_customer.Text)) _customer.Text = selected.Customer;
                if (String.IsNullOrWhiteSpace(_designName.Text))
                    _designName.Text = "Макет №" + selected.Number;
                var doc = (object)EnsureDocument(); ReadForm();
                if (_data.ServerOrderId > 0) throw new InvalidOperationException("Карточке уже присвоен номер.");
                if (String.IsNullOrEmpty(_data.ClientKey)) _data.ClientKey = Guid.NewGuid().ToString();
                if (_data.PendingIntakeId == 0) _data.PendingIntakeId = selected.Id;
                if (_data.PendingIntakeId != selected.Id)
                    throw new InvalidOperationException("Карточка ожидает другой номер. Выберите прежний заказ.");
                SaveData();
                doc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, doc, null);
                _busy = true; UpdateActions();
                var result = await OrderServerClient.Assign(_server.Text.Trim(), _key.Password.Trim(),
                    _data.PendingIntakeId, _data.ClientKey);
                if (!SameDocument(doc, (object)CorelApp.Get().ActiveDocument))
                    throw new InvalidOperationException("Вернитесь в исходный CDR и повторите присвоение номера.");
                ReadForm(); ApplyServerResult(result); _data.PendingIntakeId = 0; SaveData();
                doc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, doc, null);
                _status("Заказ №" + _data.ServerOrderNumber + " связан с CDR.", false);
                linked = true;
            }
            catch (Exception error)
            {
                if (error.Message.Contains("другой карточке") || error.Message.Contains("другой номер"))
                { _data.PendingIntakeId = 0; if (_documentPath != null) SaveData(); }
                _status("Присвоение номера: " + error.Message, true);
            }
            finally { _busy = false; UpdateActions(); }
            if (linked) await LoadOrders();
        }

        private enum UnsavedPublishChoice { Save, Continue, Cancel }

        private UnsavedPublishChoice AskUnsavedPublish()
        {
            var choice = UnsavedPublishChoice.Cancel;
            var window = new Window { Title = "Публикация карточки", Width = 475,
                SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false };
            var owner = Window.GetWindow(this);
            if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "Этот CDR ещё не сохранён. Как опубликовать карточку?",
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
            if (_busy) return;
            string server = _server.Text.Trim(), key = _key.Password.Trim();
            if (server.Length == 0 || key.Length == 0)
            { _status("Укажите адрес и ключ сервера во вкладке «Настройки».", true); return; }
            _busy = true; _send.IsEnabled = false;
            try
            {
                dynamic doc = EnsureDocument(true, true); ReadForm();
                if (_data.PendingIntakeId > 0)
                    throw new InvalidOperationException("Сначала повторите присвоение номера с приёмки.");
                if (includePhotos && _data.Frames.Count == 0)
                    throw new InvalidOperationException("Добавьте хотя бы одно выделение.");
                if (_data.Customer.Length == 0) throw new InvalidOperationException("Введите имя заказчика.");
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
                var photos = new List<byte[]>();
                if (includePhotos) for (int i = 0; i < _data.Frames.Count; i++)
                {
                    _status("Подготовка кадра " + (i + 1) + " из " + _data.Frames.Count + "…", false);
                    photos.Add(CapturePhoto(doc, _data.Frames[i]));
                }
                _status(_data.ServerOrderId == 0 ? "Публикация через сервер…" : "Обновление карточки…", false);
                var details = new Dictionary<string, object> {
                    ["clientKey"] = _data.ClientKey, ["caption"] = caption,
                    ["customer"] = _data.Customer, ["source"] = _data.Source,
                    ["technologies"] = _data.Technologies,
                    ["description"] = _data.Description, ["items"] = _data.Items,
                    ["revision"] = _data.ServerRevision
                };
                var result = await OrderServerClient.Publish(server, key, details, photos, _data.ServerOrderId);
                if (!SameDocument((object)doc, (object)CorelApp.Get().ActiveDocument))
                    throw new InvalidOperationException("Вернитесь в исходный CDR и загрузите заказ с сервера.");
                _data.PublishedAtUtc = DateTime.UtcNow.ToString("o");
                ApplyServerResult(result);
                string saveWarning = null;
                try
                {
                    SaveData();
                    if (saveCdr) doc.Save();
                }
                catch (Exception saveError)
                {
                    Log.Error("Order published but CDR save failed", saveError);
                    saveWarning = saveError.Message;
                }
                UpdateActions();
                if (Convert.ToString(result["sendState"]) != "sent" && Convert.ToString(result["sendState"]) != "local")
                    throw new InvalidOperationException("Публикация не завершена. Проверьте канал и загрузите карточку с сервера.");
                if (saveWarning != null)
                {
                    _status("Заказ №" + _data.ServerOrderNumber +
                        " опубликован, но CDR не сохранён: " + saveWarning, true);
                    return;
                }
                _status("Заказ №" + _data.ServerOrderNumber +
                    (_data.ServerSendState == "local" ? " сохранён на сервере." : " сохранён в Telegram.") +
                    (saveCdr ? " CDR сохранён." : " CDR не сохранён."), false);
            }
            catch (Exception error)
            {
                Log.Error("Order card publish failed", error);
                _status("Карточка: " + error.Message.Replace(key, "[скрыто]"), true);
            }
            finally { _busy = false; UpdateActions(); }
        }

        private void ApplyServerResult(Dictionary<string, object> result)
        {
            _data.ServerOrderId = Convert.ToInt32(result["id"]);
            _data.ServerOrderNumber = Convert.ToString(result["number"]);
            _data.ServerRevision = Convert.ToInt32(result["revision"]);
            ApplyWorkflowResult(result);
            if (result.TryGetValue("photoIds", out object ids) && ids is object[] photos && photos.Length > 0)
                _data.TelegramMessageId = Convert.ToInt32(photos[0]);
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
                LoadServerFields(result);
                ReadForm(); ApplyServerResult(result); SaveData(); UpdateCaption(); UpdateActions();
                CorelApp.Get().ActiveDocument.Save();
                _status("Заказ №" + _data.ServerOrderNumber + " загружен с сервера. CDR сохранён.", false);
            }
            catch (Exception error) { _status("Загрузка заказа: " + error.Message, true); }
            finally { _busy = false; _refreshServer.IsEnabled = true; }
        }

        private Task OpenServerOrder() => OpenServerOrder(_orderNumber.Text.Trim());

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
                doc.Save();
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
                _customer.Text = Convert.ToString(result["customer"]);
                SetSourceSelection(Convert.ToString(result["source"]));
                _description.Text = Convert.ToString(result["description"]);
                _items.Text = Convert.ToString(result["items"]);
                var technologies = result["technologies"] as object[] ?? new object[0];
                foreach (var box in _technologies)
                    box.IsChecked = technologies.Any(x => Convert.ToString(x) == Convert.ToString(box.Content));
            }
            finally { _loadingForm = false; }
        }

        private static bool Flag(Dictionary<string, object> result, string key) =>
            result.TryGetValue(key, out object value) && value != null && Convert.ToBoolean(value);

        private void ApplyWorkflowResult(Dictionary<string, object> result)
        {
            // Keep the editor revision: polling must not make stale text safe to overwrite remotely.
            _data.ServerState = Convert.ToString(result["state"]);
            _data.ServerSendState = Convert.ToString(result["sendState"]);
            _data.ServerBlanksPaused = Flag(result, "blanksPaused");
            _data.ServerBlanksReady = Flag(result, "blanksReady");
            _data.ServerApplicationPaused = Flag(result, "applicationPaused");
            _data.ServerUrgent = Flag(result, "isUrgent");
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
            _busy = true; UpdateActions();
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
                if (id == _data.ServerOrderId && (doc == null ||
                    SameDocument(doc, (object)CorelApp.Get().ActiveDocument)))
                {
                    ApplyWorkflowResult(result);
                    if (_data.ServerRevision == revision)
                        _data.ServerRevision = Convert.ToInt32(result["revision"]);
                    if (doc != null) { SaveData(); CorelApp.Get().ActiveDocument.Save(); }
                }
                _status("Состояние заказа №" + Convert.ToString(result["number"]) + " обновлено.", false);
            }
            catch (Exception error) { _status("Состояние заказа: " + error.Message, true); }
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
            _send.Content = published ? "Заменить фото и обновить текст" : draft ? "Сохранить карточку заказа" : "Опубликовать карточку";
            _send.IsEnabled = !_busy;
            _loadOrders.IsEnabled = !_busy;
            _linkOrder.IsEnabled = !_busy && _ordersTable.SelectedItem != null;
            _linkOrder.Content = (_ordersTable.SelectedItem as OrderListItem)?.IsAvailable == true
                ? "Связать с CDR" : "Открыть в CDR";
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
                (_data.ServerBlanksReady ? " · ❤️ Заготовки готовы" : "") +
                (_data.ServerBlanksPaused ? " · ⏸ Заготовки" : "") +
                (_data.ServerApplicationPaused ? " · ⏸ Нанесение" : "") : "";
            if (assigned) _orderNumber.Text = _data.ServerOrderNumber;
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
        public int SchemaVersion { get; set; } = 4;
        public string Customer { get; set; }
        public string DesignName { get; set; }
        public string Source { get; set; }
        public List<string> Technologies { get; set; } = new List<string>();
        public string Description { get; set; }
        public string Items { get; set; }
        public List<OrderCardFrame> Frames { get; set; } = new List<OrderCardFrame>();
        public string PublishedAtUtc { get; set; }
        public int TelegramMessageId { get; set; }
        public string ClientKey { get; set; }
        public int ServerOrderId { get; set; }
        public string ServerOrderNumber { get; set; }
        public int ServerRevision { get; set; }
        public bool ServerBlanksPaused { get; set; }
        public bool ServerBlanksReady { get; set; }
        public bool ServerApplicationPaused { get; set; }
        public bool ServerUrgent { get; set; }
        public string ServerState { get; set; }
        public string ServerSendState { get; set; }
        public int PendingIntakeId { get; set; }
    }
    public sealed class OrderCardFrame
    {
        public List<OrderCardShape> Shapes { get; set; } = new List<OrderCardShape>();
    }
    public sealed class OrderCardShape
    {
        public int Page { get; set; }
        public int Id { get; set; }
    }
}
