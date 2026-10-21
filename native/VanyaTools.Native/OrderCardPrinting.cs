using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class OrderServerRequestException : InvalidOperationException
    {
        internal int StatusCode { get; }
        internal OrderServerRequestException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    }
    internal sealed partial class OrderCardTab
    {
        private Button _printLabelButton, _refreshNetworkPrinters;
        private ComboBox _printFormat, _fullNetworkPrinter, _miniNetworkPrinter;
        private TextBlock _printLabelResult, _networkPrinterStatus;
        private bool _loadingPrinterSettings;
        private string _fullPrinterId = "", _miniPrinterId = "";
        private List<NetworkPrinterChoice> _networkPrinterChoices = new List<NetworkPrinterChoice>();
        private static string PrinterPreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "order-printers.json");
        private static string PendingLabelPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "order-print-pending.json");

        private sealed class NetworkPrinterChoice
        {
            public string Id { get; set; }
            public string Format { get; set; }
            public string Name { get; set; }
            public bool Online { get; set; }
            public override string ToString() => Name;
        }

        private void AddLabelPrinting(StackPanel editor)
        {
            _printLabelButton = Button("Печать этикетки", async (_, __) => await PrintCardLabel());
            _printLabelResult = Note("");
            _printLabelResult.TextWrapping = TextWrapping.Wrap;
            editor.Children.Add(_printLabelButton);
            editor.Children.Add(_printLabelResult);
        }

        private void AddNetworkPrinterSettings(StackPanel settings)
        {
            settings.Children.Add(Title("Печать этикеток"));
            settings.Children.Add(Label("Формат этикетки"));
            _printFormat = new ComboBox { Margin = new Thickness(0, 0, 0, 5), SelectedValuePath = "Tag" };
            _printFormat.Items.Add(new ComboBoxItem { Content = "Полная · 80×120 мм", Tag = "full" });
            _printFormat.Items.Add(new ComboBoxItem { Content = "Маленькая · 45×22,5 мм", Tag = "mini" });
            _printFormat.SelectedValue = "full";
            settings.Children.Add(_printFormat);
            _fullNetworkPrinter = new ComboBox { Margin = new Thickness(0, 0, 0, 5), SelectedValuePath = "Id" };
            _miniNetworkPrinter = new ComboBox { Margin = new Thickness(0, 0, 0, 5), SelectedValuePath = "Id" };
            settings.Children.Add(Label("Сетевой принтер · полная этикетка")); settings.Children.Add(_fullNetworkPrinter);
            settings.Children.Add(Label("Сетевой принтер · маленькая этикетка")); settings.Children.Add(_miniNetworkPrinter);
            _networkPrinterStatus = Note("Выберите принтер из общей очереди рабочего сервера.");
            settings.Children.Add(_networkPrinterStatus);
            _refreshNetworkPrinters = Button("Обновить список принтеров", async (_, __) => await LoadNetworkPrinters());
            settings.Children.Add(_refreshNetworkPrinters);
            DockerHelp.Add(settings, "На ПК с принтером должен работать модуль печати. На этом ПК модуль не требуется. Выбор сохраняется автоматически.");
            _printFormat.SelectionChanged += (_, __) => SavePrinterPreferences();
            _fullNetworkPrinter.SelectionChanged += (_, __) => {
                if (_loadingPrinterSettings) return;
                _fullPrinterId = Convert.ToString(_fullNetworkPrinter.SelectedValue); SavePrinterPreferences();
            };
            _miniNetworkPrinter.SelectionChanged += (_, __) => {
                if (_loadingPrinterSettings) return;
                _miniPrinterId = Convert.ToString(_miniNetworkPrinter.SelectedValue); SavePrinterPreferences();
            };
            settings.Loaded += async (_, __) => await LoadNetworkPrinters();
        }

        private void LoadPrinterPreferences()
        {
            _loadingPrinterSettings = true;
            try
            {
                if (!File.Exists(PrinterPreferencesPath)) return;
                var value = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(PrinterPreferencesPath, Encoding.UTF8));
                if (value == null || !String.Equals(PrintValue(value, "server"), _server.Text.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return;
                _fullPrinterId = PrintValue(value, "full"); _miniPrinterId = PrintValue(value, "mini");
                _printFormat.SelectedValue = PrintValue(value, "format") == "mini" ? "mini" : "full";
            }
            catch (Exception error) { Log.Error("Order printer settings could not be read", error); }
            finally { _loadingPrinterSettings = false; }
        }

        private static string PrintValue<T>(Dictionary<string, T> value, string name) => value != null && value.TryGetValue(name, out T found) ? Convert.ToString(found) : "";

        private void SavePrinterPreferences()
        {
            if (_loadingPrinterSettings) return;
            try
            {
                WritePrintJson(PrinterPreferencesPath, new Dictionary<string, string> {
                    ["server"] = _server.Text.Trim().TrimEnd('/'), ["format"] = Convert.ToString(_printFormat.SelectedValue),
                    ["full"] = _fullPrinterId, ["mini"] = _miniPrinterId
                });
            }
            catch (Exception error) { _status("Настройки принтера: " + error.Message, true); }
        }

        private static void WritePrintJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        private async Task<bool> LoadNetworkPrinters()
        {
            string server = _server.Text.Trim(), key = _key.Password.Trim();
            if (server.Length == 0 || key.Length == 0) { _networkPrinterStatus.Text = "Укажите адрес и ключ сервера заказов."; return false; }
            _refreshNetworkPrinters.IsEnabled = false;
            _networkPrinterStatus.Text = "Загрузка принтеров…";
            try
            {
                var catalog = await OrderServerClient.NetworkPrinters(server, key);
                var choices = new List<NetworkPrinterChoice>();
                foreach (object raw in catalog.TryGetValue("stations", out object stations) ? stations as IEnumerable ?? new object[0] : new object[0])
                {
                    var station = raw as Dictionary<string, object>;
                    if (station == null) continue;
                    bool online = station.TryGetValue("online", out object flag) && Convert.ToBoolean(flag);
                    foreach (object item in station.TryGetValue("printers", out object printers) ? printers as IEnumerable ?? new object[0] : new object[0])
                    {
                        var printer = item as Dictionary<string, object>;
                        if (printer == null) continue;
                        choices.Add(new NetworkPrinterChoice {
                            Id = PrintValue(station, "id") + "/" + PrintValue(printer, "id"), Format = PrintValue(printer, "format"), Online = online,
                            Name = PrintValue(station, "name") + " · " + PrintValue(printer, "name") + (online ? "" : " · ПК не в сети")
                        });
                    }
                }
                _networkPrinterChoices = choices;
                _loadingPrinterSettings = true;
                try
                {
                    foreach (var pair in new[] { new { Box = _fullNetworkPrinter, Format = "full", Id = _fullPrinterId }, new { Box = _miniNetworkPrinter, Format = "mini", Id = _miniPrinterId } })
                    {
                        pair.Box.ItemsSource = new[] { new NetworkPrinterChoice { Id = "", Name = "Выберите сетевой принтер" } }.Concat(choices.Where(choice => choice.Format == pair.Format)).ToList();
                        pair.Box.SelectedValue = pair.Id;
                        if (pair.Box.SelectedIndex < 0) pair.Box.SelectedIndex = 0;
                    }
                }
                finally { _loadingPrinterSettings = false; }
                _networkPrinterStatus.Text = choices.Count == 0 ? "Нет зарегистрированных принтеров. Запустите модуль печати на ПК с принтером." : "Доступно профилей: " + choices.Count(choice => choice.Online) + " из " + choices.Count + ".";
                return true;
            }
            catch (Exception error) { _networkPrinterStatus.Text = "Принтеры: " + error.Message; return false; }
            finally { _refreshNetworkPrinters.IsEnabled = !_busy; }
        }

        internal async Task<ConnectionStatus> LabelPrinterStatus()
        {
            string server = _server.Text.Trim(), key = _key.Password.Trim();
            string id = Convert.ToString(_printFormat.SelectedValue) == "mini" ? _miniPrinterId : _fullPrinterId;
            if (server.Length == 0 || key.Length == 0) return new ConnectionStatus { Message = "Этикетки: адрес или ключ сервера не задан" };
            if (String.IsNullOrWhiteSpace(id)) return new ConnectionStatus { Message = "Этикетки: принтер выбранного формата не назначен в настройках" };
            try
            {
                var catalog = await OrderServerClient.NetworkPrintersQuick(server, key);
                foreach (object raw in catalog.TryGetValue("stations", out object stations) ? stations as IEnumerable ?? new object[0] : new object[0])
                {
                    var station = raw as Dictionary<string, object>;
                    if (station == null) continue;
                    bool online = station.TryGetValue("online", out object flag) && Convert.ToBoolean(flag);
                    foreach (object item in station.TryGetValue("printers", out object printers) ? printers as IEnumerable ?? new object[0] : new object[0])
                    {
                        var printer = item as Dictionary<string, object>;
                        if (printer == null || PrintValue(station, "id") + "/" + PrintValue(printer, "id") != id) continue;
                        if (printer.TryGetValue("online", out object printerFlag)) online &= Convert.ToBoolean(printerFlag);
                        return new ConnectionStatus { Available = online, Message = "Этикетки: " + PrintValue(printer, "name") +
                            (online ? " · модуль печати в сети" : " · нет связи с модулем печати") };
                    }
                }
                return new ConnectionStatus { Message = "Этикетки: выбранный принтер отсутствует в списке сервера" };
            }
            catch (Exception error) { return new ConnectionStatus { Message = "Этикетки: " + error.GetBaseException().Message }; }
        }

        private void UpdateLabelPrinting()
        {
            if (_printLabelButton == null) return;
            _printLabelButton.IsEnabled = !_busy && _data.ServerOrderId > 0;
            _printLabelButton.ToolTip = _data.ServerOrderId > 0 ? "Напечатать сохранённые на сервере данные этой карточки на выбранном сетевом принтере." : "Сначала опубликуйте карточку или присвойте номер с приёмки.";
            ToolTipService.SetShowOnDisabled(_printLabelButton, true);
            if (_printFormat != null) _printFormat.IsEnabled = !_busy;
            if (_fullNetworkPrinter != null) _fullNetworkPrinter.IsEnabled = !_busy;
            if (_miniNetworkPrinter != null) _miniNetworkPrinter.IsEnabled = !_busy;
            if (_refreshNetworkPrinters != null) _refreshNetworkPrinters.IsEnabled = !_busy;
        }

        private async Task PrintCardLabel()
        {
            if (_busy || _data.ServerOrderId < 1) return;
            FileStream printLock;
            try { Directory.CreateDirectory(Path.GetDirectoryName(PendingLabelPath)); printLock = new FileStream(PendingLabelPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { _status("Другая отправка этикетки ещё выполняется. Дождитесь результата.", true); return; }
            string server = _server.Text.Trim().TrimEnd('/'), key = _key.Password.Trim();
            string format = Convert.ToString(_printFormat.SelectedValue) == "mini" ? "mini" : "full";
            int id = _data.ServerOrderId;
            _busy = true; UpdateActions(); _printLabelButton.Content = "Отправка этикетки…";
            _printLabelResult.Text = "Подготовка печати…"; _printLabelResult.ClearValue(TextBlock.ForegroundProperty);
            try
            {
                Dictionary<string, object> job = null;
                if (File.Exists(PendingLabelPath)) job = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PendingLabelPath, Encoding.UTF8));
                if (job != null && (PrintValue(job, "server") != server || Convert.ToInt32(job["orderId"]) != id || PrintValue(job, "format") != format))
                    throw new InvalidOperationException("Осталось непроверенное задание заказа №" + PrintValue(job, "orderNumber") + ". Откройте его и повторите кнопку с прежним форматом: результат проверится без повторной отправки.");
                if (job == null)
                {
                    if (!await LoadNetworkPrinters()) throw new InvalidOperationException(_networkPrinterStatus.Text);
                    string selected = format == "mini" ? _miniPrinterId : _fullPrinterId;
                    var printer = _networkPrinterChoices.FirstOrDefault(choice => choice.Id == selected && choice.Format == format);
                    if (printer == null) throw new InvalidOperationException("Выберите сетевой принтер для этого формата во вкладке «Настройки».");
                    if (!printer.Online) throw new InvalidOperationException("ПК с принтером не в сети: " + printer.Name + ". Запустите на нём модуль печати.");
                    var destination = selected.Split('/');
                    job = new Dictionary<string, object> { ["server"] = server, ["clientKey"] = Guid.NewGuid().ToString(), ["orderId"] = id, ["orderNumber"] = _data.ServerOrderNumber,
                        ["format"] = format, ["stationId"] = destination[0], ["printerId"] = destination[1] };
                    WritePrintJson(PendingLabelPath, job);
                }
                string clientKey = PrintValue(job, "clientKey");
                Dictionary<string, object> result;
                try { result = await OrderServerClient.SubmitPrint(server, key, job); }
                catch (OrderServerRequestException error) when (error.StatusCode >= 400 && error.StatusCode < 500) { File.Delete(PendingLabelPath); throw; }
                DateTime until = DateTime.UtcNow.AddSeconds(100);
                string state = PrintValue(result, "status");
                while ((state == "queued" || state == "claimed") && DateTime.UtcNow < until)
                {
                    _printLabelResult.Text = state == "queued" ? "В очереди: ждём ПК с принтером…" : "Задание получено. Ожидаем ответ Windows…";
                    await Task.Delay(1200);
                    result = await OrderServerClient.PrintStatus(server, key, clientKey); state = PrintValue(result, "status");
                }
                if (state == "submitted" || state == "failed" || state == "uncertain") File.Delete(PendingLabelPath);
                if (state == "submitted") { _printLabelResult.Text = "Передано в очередь Windows · " + PrintValue(result, "printerName"); _status(_printLabelResult.Text, false); }
                else if (state == "failed" || state == "uncertain") throw new InvalidOperationException(PrintValue(result, "error") + (state == "uncertain" ? " Проверьте бумагу и очередь перед повторной печатью." : ""));
                else _printLabelResult.Text = "Задание обрабатывается. Повторная кнопка проверит результат без повторной печати.";
            }
            catch (Exception error) { _printLabelResult.Text = "Печать: " + error.Message; _printLabelResult.Foreground = Brushes.Firebrick; _status(_printLabelResult.Text, true); }
            finally { printLock.Dispose(); _busy = false; _printLabelButton.Content = "Печать этикетки"; UpdateActions(); }
        }
    }
}
