using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class OrderCardTab : UserControl
    {
        private const string PropertyOwner = "CD899165-445C-40F0-91D7-2DF6703B0AF8";
        private readonly Action<string, bool> _status;
        private readonly TextBox _customer, _description, _items, _channel;
        private readonly ComboBox _source;
        private readonly PasswordBox _token;
        private readonly TextBlock _caption, _documentName;
        private readonly StackPanel _frames;
        private readonly Button _send;
        private readonly List<CheckBox> _technologies = new List<CheckBox>();
        private OrderCardData _data = new OrderCardData();
        private string _documentPath;
        private bool _busy;

        internal FrameworkElement SettingsView { get; }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "telegram-order-cards.bin");

        internal OrderCardTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(8) };
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
            panel.Children.Add(Title("Карточка заказа"));
            _documentName = Note("Откройте сохранённый CDR.");
            panel.Children.Add(_documentName);
            var actions = new UniformGrid { Columns = 2 };
            actions.Children.Add(Button("Загрузить из CDR", (_, __) => Safe(() => LoadDocument(true))));
            actions.Children.Add(Button("Добавить выделение", (_, __) => Safe(AddSelection)));
            panel.Children.Add(actions);
            _frames = new StackPanel { Margin = new Thickness(0, 3, 0, 8) };
            panel.Children.Add(_frames);

            panel.Children.Add(Label("Заказчик"));
            _customer = Input(); panel.Children.Add(_customer);
            panel.Children.Add(Label("Источник"));
            _source = new ComboBox { IsEditable = true, MinHeight = 27,
                ItemsSource = new[] { "ТГ", "ВК", "ПОЧТА", "КП" },
                Margin = new Thickness(0, 0, 0, 3) };
            panel.Children.Add(_source);
            panel.Children.Add(Label("Технология печати"));
            var techGrid = new UniformGrid { Columns = 2 };
            foreach (string name in new[] { "ДТФ", "Шелкография", "Вышивка", "Сублимация", "Лазер" })
            {
                var box = new CheckBox { Content = name, Margin = new Thickness(2, 1, 2, 1) };
                _technologies.Add(box); techGrid.Children.Add(box);
            }
            panel.Children.Add(techGrid);
            panel.Children.Add(Label("Описание"));
            _description = Input(55); panel.Children.Add(_description);
            panel.Children.Add(Label("Изделия и количество, по одному на строку"));
            _items = Input(65); panel.Children.Add(_items);
            panel.Children.Add(Label("Подпись Telegram"));
            _caption = Note("");
            _caption.Background = Brushes.White;
            _caption.Padding = new Thickness(7);
            panel.Children.Add(_caption);
            var saveActions = new UniformGrid { Columns = 2 };
            saveActions.Children.Add(Button("Сохранить в CDR", (_, __) => Safe(SaveForm)));
            saveActions.Children.Add(Button("Новая карточка", (_, __) => Safe(ClearCard)));
            panel.Children.Add(saveActions);
            _send = DockerTheme.Primary(Button("Опубликовать в Telegram", async (_, __) => await Publish()));
            panel.Children.Add(_send);
            panel.Children.Add(Note("Кадры: 1–10. Сначала добавьте бота в администраторы канала."));

            var settings = new StackPanel { Margin = new Thickness(4, 12, 4, 4) };
            settings.Children.Add(Title("Telegram · карточки заказов"));
            settings.Children.Add(Label("Канал: @имя или числовой chat ID"));
            _channel = Input(); settings.Children.Add(_channel);
            settings.Children.Add(Label("Токен бота от BotFather"));
            _token = new PasswordBox { Margin = new Thickness(0, 0, 0, 5) };
            settings.Children.Add(_token);
            settings.Children.Add(Button("Сохранить Telegram", (_, __) => Safe(() => SaveTelegramSettings())));
            settings.Children.Add(Note("Токен хранится только на этом ПК в зашифрованном виде."));
            SettingsView = settings;
            LoadTelegramSettings();
            foreach (var input in new[] { _customer, _description, _items })
                input.TextChanged += (_, __) => UpdateCaption();
            _source.SelectionChanged += (_, __) => UpdateCaption();
            _source.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, __) => UpdateCaption()));
            foreach (var box in _technologies) box.Checked += (_, __) => UpdateCaption();
            foreach (var box in _technologies) box.Unchecked += (_, __) => UpdateCaption();
            Loaded += (_, __) => Safe(() => LoadDocument(false));
            RenderFrames(); UpdateCaption();
        }

        private dynamic EnsureDocument(bool loadIfChanged = true)
        {
            dynamic doc = CorelApp.Get().ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            string path = Convert.ToString(doc.FullFileName);
            if (String.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Сначала сохраните документ как CDR.");
            if (loadIfChanged && !String.Equals(path, _documentPath, StringComparison.OrdinalIgnoreCase))
                LoadDocument(true);
            return doc;
        }

        private void LoadDocument(bool report)
        {
            dynamic doc = EnsureDocument(false);
            string path = Convert.ToString(doc.FullFileName);
            dynamic properties = doc.Properties;
            _data = new OrderCardData();
            if (Convert.ToBoolean(properties.Exists(PropertyOwner, 1)))
            {
                object value = properties.GetType().InvokeMember("Item",
                    BindingFlags.GetProperty, null, (object)properties,
                    new object[] { PropertyOwner, 1 });
                var loaded = new JavaScriptSerializer().Deserialize<OrderCardData>(Convert.ToString(value));
                if (loaded != null) _data = loaded;
            }
            if (_data.Frames == null) _data.Frames = new List<OrderCardFrame>();
            if (_data.Technologies == null) _data.Technologies = new List<string>();
            _documentPath = path;
            _documentName.Text = Path.GetFileName(path) + " · " + _data.Frames.Count + " кадр(ов)";
            _customer.Text = _data.Customer ?? "";
            _source.Text = _data.Source ?? "";
            _description.Text = _data.Description ?? "";
            _items.Text = _data.Items ?? "";
            foreach (var box in _technologies)
                box.IsChecked = _data.Technologies.Contains(Convert.ToString(box.Content));
            RenderFrames(); UpdateCaption();
            if (report) _status("Карточка загружена из CDR.", false);
        }

        private void ReadForm()
        {
            _data.Customer = _customer.Text.Trim();
            _data.Source = _source.Text.Trim();
            _data.Description = _description.Text.Trim();
            _data.Items = _items.Text.Trim();
            _data.Technologies = _technologies.Where(x => x.IsChecked == true)
                .Select(x => Convert.ToString(x.Content)).ToList();
        }

        private void SaveForm()
        {
            EnsureDocument(); ReadForm(); SaveData();
            _status("Карточка сохранена в CDR.", false);
        }

        private void SaveData()
        {
            dynamic doc = EnsureDocument(false);
            _data.SchemaVersion = 1;
            string json = new JavaScriptSerializer().Serialize(_data);
            object properties = doc.Properties;
            properties.GetType().InvokeMember("Item", BindingFlags.SetProperty,
                null, properties, new object[] { PropertyOwner, 1, json });
            doc.Save();
            _documentName.Text = Path.GetFileName(_documentPath) + " · " + _data.Frames.Count + " кадр(ов)";
        }

        private void AddSelection()
        {
            dynamic doc = EnsureDocument();
            dynamic app = CorelApp.Get();
            dynamic selection = app.ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) == 0)
                throw new InvalidOperationException("Выделите один макет или группу объектов.");
            if (_data.Frames.Count >= 10)
                throw new InvalidOperationException("В карточке может быть не более 10 кадров.");
            var frame = new OrderCardFrame { Shapes = new List<OrderCardShape>() };
            foreach (dynamic shape in selection.Shapes)
                frame.Shapes.Add(new OrderCardShape {
                    Page = Convert.ToInt32(shape.Page.Index), Id = Convert.ToInt32(shape.StaticID) });
            ReadForm(); _data.Frames.Add(frame); SaveData(); RenderFrames(); UpdateCaption();
            _status("Выделение добавлено в карточку (" + _data.Frames.Count + "/10).", false);
        }

        private void RenderFrames()
        {
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
            EnsureDocument(); int target = index + offset;
            if (target < 0 || target >= _data.Frames.Count) return;
            ReadForm(); var item = _data.Frames[index];
            _data.Frames[index] = _data.Frames[target]; _data.Frames[target] = item;
            SaveData(); RenderFrames();
        }

        private void RemoveFrame(int index)
        {
            EnsureDocument(); ReadForm(); _data.Frames.RemoveAt(index);
            SaveData(); RenderFrames(); _status("Кадр удалён из карточки.", false);
        }

        private void ClearCard()
        {
            EnsureDocument();
            if (MessageBox.Show("Очистить карточку в этом CDR? Макет останется без изменений.",
                "Vanya Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _data = new OrderCardData(); SaveData(); LoadDocument(false);
            _status("Карточка очищена.", false);
        }

        private void SelectFrame(int index)
        {
            dynamic doc = EnsureDocument();
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
                if (first) { page.Activate(); shape.CreateSelection(); first = false; }
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
            string png = null;
            try
            {
                SelectShapes(doc, frame);
                png = AiSelectionCapture.Capture(1800, 3200000);
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(png); image.EndInit(); image.Freeze();
                if (Math.Max(image.PixelWidth, image.PixelHeight) >
                    20 * Math.Min(image.PixelWidth, image.PixelHeight))
                    throw new InvalidOperationException("Кадр слишком узкий для Telegram. Измените выделение.");
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(Brushes.White, null, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
                    context.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
                }
                var bitmap = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                foreach (int quality in new[] { 88, 75, 60 })
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = new MemoryStream())
                    {
                        encoder.Save(output);
                        if (output.Length < 10L * 1024 * 1024) return output.ToArray();
                    }
                }
                throw new InvalidOperationException("Кадр больше 10 МБ. Уменьшите выделение.");
            }
            finally
            {
                if (png != null) { try { File.Delete(png); } catch { } }
                try { originalPage.Activate(); } catch { }
                try
                {
                    for (int i = 0; i < original.Count; i++)
                        if (i == 0) original[i].CreateSelection(); else original[i].AddToSelection();
                }
                catch { }
            }
        }

        private async Task Publish()
        {
            if (_busy) return;
            string token = _token.Password.Trim(), channel = _channel.Text.Trim();
            if (token.Length == 0 || channel.Length == 0)
            { _status("Укажите канал и токен бота во вкладке «Настройки».", true); return; }
            _busy = true; _send.IsEnabled = false;
            bool sent = false;
            try
            {
                dynamic doc = EnsureDocument(); ReadForm();
                if (_data.Frames.Count == 0) throw new InvalidOperationException("Добавьте хотя бы одно выделение.");
                if (_data.Customer.Length == 0) throw new InvalidOperationException("Введите имя заказчика.");
                string caption = ComposeCaption();
                if (caption.Length > 1024) throw new InvalidOperationException("Подпись длиннее 1024 символов. Сократите описание.");
                SaveData(); SaveTelegramSettings(false);
                var photos = new List<byte[]>();
                for (int i = 0; i < _data.Frames.Count; i++)
                {
                    _status("Подготовка кадра " + (i + 1) + " из " + _data.Frames.Count + "…", false);
                    photos.Add(CapturePhoto(doc, _data.Frames[i]));
                }
                _status("Отправка в Telegram…", false);
                int messageId = await TelegramOrderClient.Send(token, channel, caption, photos);
                sent = true;
                _data.PublishedAtUtc = DateTime.UtcNow.ToString("o");
                _data.TelegramMessageId = messageId;
                SaveData();
                _status("Карточка опубликована в Telegram. Сообщение №" + messageId + ".", false);
            }
            catch (Exception error)
            {
                Log.Info("Order card publish failed: " + error.GetType().Name);
                _status(sent ? "Карточка отправлена, но не удалось сохранить отметку в CDR: " + error.Message
                    : "Публикация: " + error.Message.Replace(token, "[скрыто]"), true);
            }
            finally { _busy = false; _send.IsEnabled = true; }
        }

        private string ComposeCaption()
        {
            string customer = _customer.Text.Trim(), source = _source.Text.Trim();
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

        private void SaveTelegramSettings(bool report = true)
        {
            string channel = _channel.Text.Trim(), token = _token.Password.Trim();
            if (channel.Length == 0 || token.Length == 0)
                throw new InvalidOperationException("Укажите канал и токен бота.");
            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, string>
                { ["channel"] = channel, ["token"] = token });
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllBytes(SettingsPath, encrypted);
            if (report) _status("Подключение Telegram сохранено.", false);
        }

        private void LoadTelegramSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string json = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    File.ReadAllBytes(SettingsPath), null, DataProtectionScope.CurrentUser));
                var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
                if (settings.TryGetValue("channel", out string channel)) _channel.Text = channel;
                if (settings.TryGetValue("token", out string token)) _token.Password = token;
            }
            catch { _status("Не удалось прочитать настройки Telegram. Введите их повторно.", true); }
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
        private static Button Button(string text, RoutedEventHandler handler)
        {
            var button = new Button { Content = text, MinHeight = 29, Margin = new Thickness(1, 1, 1, 3) };
            button.Click += handler; return button;
        }
    }

    public sealed class OrderCardData
    {
        public int SchemaVersion { get; set; } = 1;
        public string Customer { get; set; }
        public string Source { get; set; }
        public List<string> Technologies { get; set; } = new List<string>();
        public string Description { get; set; }
        public string Items { get; set; }
        public List<OrderCardFrame> Frames { get; set; } = new List<OrderCardFrame>();
        public string PublishedAtUtc { get; set; }
        public int TelegramMessageId { get; set; }
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

    internal static class TelegramOrderClient
    {
        internal static async Task<int> Send(string token, string channel, string caption, IList<byte[]> photos)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            bool album = photos.Count > 1;
            string method = album ? "sendMediaGroup" : "sendPhoto";
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var form = new MultipartFormDataContent())
            {
                form.Add(new StringContent(channel), "chat_id");
                if (album)
                {
                    var media = new List<Dictionary<string, string>>();
                    for (int i = 0; i < photos.Count; i++)
                    {
                        var item = new Dictionary<string, string>
                            { ["type"] = "photo", ["media"] = "attach://photo" + i };
                        if (i == 0) item["caption"] = caption;
                        media.Add(item);
                    }
                    form.Add(new StringContent(new JavaScriptSerializer().Serialize(media), Encoding.UTF8), "media");
                }
                else form.Add(new StringContent(caption, Encoding.UTF8), "caption");
                for (int i = 0; i < photos.Count; i++)
                {
                    var content = new ByteArrayContent(photos[i]);
                    content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    form.Add(content, album ? "photo" + i : "photo", "order-card-" + (i + 1) + ".jpg");
                }
                try
                {
                    using (var response = await client.PostAsync(
                        "https://api.telegram.org/bot" + token + "/" + method, form))
                    {
                        string body = await response.Content.ReadAsStringAsync();
                        Dictionary<string, object> result;
                        try { result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body); }
                        catch { throw new InvalidOperationException("Не удалось прочитать ответ Telegram. Проверьте канал перед повторной отправкой."); }
                        if (result == null || !result.ContainsKey("ok"))
                            throw new InvalidOperationException("Telegram вернул неполный ответ. Проверьте канал перед повторной отправкой.");
                        if (!response.IsSuccessStatusCode || !Convert.ToBoolean(result["ok"]))
                        {
                            string description = result.ContainsKey("description") ? Convert.ToString(result["description"]) : response.ReasonPhrase;
                            throw new InvalidOperationException("Telegram: " + description.Replace(token, "[скрыто]"));
                        }
                        object message = result["result"];
                        if (album)
                        {
                            var messages = message as object[];
                            if (messages == null || messages.Length == 0)
                                throw new InvalidOperationException("Telegram не вернул сообщения альбома.");
                            message = messages[0];
                        }
                        var data = (Dictionary<string, object>)message;
                        return Convert.ToInt32(data["message_id"]);
                    }
                }
                catch (TaskCanceledException)
                {
                    throw new InvalidOperationException("Telegram не ответил вовремя. Проверьте канал перед повторной отправкой, чтобы не создать дубль.");
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException("Сбой соединения с Telegram. Проверьте канал перед повторной отправкой, чтобы не создать дубль.");
                }
            }
        }
    }
}
