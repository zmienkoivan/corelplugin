using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal sealed class PhotoshopExportTool
    {
        private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "photoshop-export.json");
        private sealed class Settings { public string Executable { get; set; } = ""; public int Dpi { get; set; } = 300; }
        private readonly Action<string, bool> _status;
        private readonly Settings _settings;
        private readonly TextBlock _path;
        private bool _busy;
        internal Button ExportButton { get; }
        internal FrameworkElement SettingsView { get; }

        internal PhotoshopExportTool(Action<string, bool> status)
        {
            _status = status;
            try { _settings = File.Exists(SettingsPath) ? new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(SettingsPath)) : null; }
            catch (Exception error) { Log.Error("Could not read Photoshop settings.", error); }
            _settings = _settings ?? new Settings();
            if (_settings.Dpi != 150 && _settings.Dpi != 300 && _settings.Dpi != 400 && _settings.Dpi != 600) _settings.Dpi = 300;
            ExportButton = new Button { Content = "В Photoshop", FontSize = 11, MinHeight = 27,
                Margin = new Thickness(0, 2, 0, 2), ToolTip = "Открыть выделение в Photoshop одним PNG с прозрачностью. Без выделения — активная страница после подтверждения." };
            ExportButton.Click += (_, __) => Export();
            var panel = new StackPanel();
            _path = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) };
            RefreshPath(); panel.Children.Add(_path);
            var browse = new Button { Content = "Выбрать Photoshop…", MinHeight = 27 };
            browse.Click += (_, __) => { try { ChooseExecutable(); } catch (Exception error) { _status(error.Message, true); } };
            var automatic = new Button { Content = "Авто", MinHeight = 27 };
            automatic.Click += (_, __) => { try { _settings.Executable = ""; Save(); RefreshPath(); } catch (Exception error) { _status(error.Message, true); } };
            panel.Children.Add(DockerLayout.Trailing(browse, automatic));
            var dpi = new ComboBox { ItemsSource = new[] { 150, 300, 400, 600 }, SelectedItem = _settings.Dpi,
                MinHeight = 27, FontSize = 11, Margin = new Thickness(0, 4, 0, 3) };
            panel.Children.Add(DockerLayout.Trailing(new TextBlock { Text = "Разрешение, DPI", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center }, dpi));
            dpi.SelectionChanged += (_, __) => { try { _settings.Dpi = (int)dpi.SelectedItem; Save(); } catch (Exception error) { _status(error.Message, true); } };
            panel.Children.Add(new TextBlock { Text = "PNG · RGB · прозрачность. Копии остаются в локальной папке VanyaTools → Photoshop. Изменения Photoshop сохраняйте отдельно.",
                FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 2) });
            var copies = new Button { Content = "Папка копий", MinHeight = 27 };
            copies.Click += (_, __) => {
                try
                {
                    string directory = ExportDirectory; Directory.CreateDirectory(directory);
                    using (Process process = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })) { }
                }
                catch (Exception error) { _status("Папка Photoshop: " + error.Message, true); }
            };
            panel.Children.Add(copies);
            SettingsView = DockerSettings.Section("Photoshop · экспорт", panel);
        }

        private async void Export()
        {
            if (_busy) return;
            _busy = true; ExportButton.IsEnabled = false;
            string output = null;
            try
            {
                _status("Ищу Photoshop…", false);
                string saved = _settings.Executable;
                string executable = await Task.Run(() => PhotoshopLauncher.Find(saved));
                if (executable.Length == 0)
                {
                    if (!ChooseExecutable()) { _status("Экспорт в Photoshop отменён.", false); return; }
                    executable = _settings.Executable;
                }
                _status("Готовлю PNG для Photoshop…", false);
                await Dispatcher.Yield(DispatcherPriority.Background);
                dynamic app = CorelApp.Get(), document = app.ActiveDocument;
                if (document == null) throw new InvalidOperationException("Сначала откройте документ CorelDRAW.");
                dynamic range = app.ActiveSelectionRange;
                bool selected = range != null && Convert.ToInt32(range.Count) > 0;
                if (!selected && MessageBox.Show("Нет выделения. Открыть активную страницу в Photoshop?", "Vanya Tools",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                { _status("Экспорт в Photoshop отменён.", false); return; }
                string name = Convert.ToString(document.Name);
                string directory = ExportDirectory;
                Directory.CreateDirectory(directory);
                foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
                name = Path.GetFileNameWithoutExtension(name);
                if (String.IsNullOrWhiteSpace(name)) name = "CorelDRAW";
                if (name.Length > 65) name = name.Substring(0, 65);
                output = Path.Combine(directory, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                PhotoshopExportPlan plan = PhotoshopPngExport.Export((object)app, output, _settings.Dpi, selected, RasterCaptureMemory.EnsureAvailable);
                using (Process process = Process.Start(PhotoshopLauncher.StartInfo(executable, output))) { }
                _status("Отправлено в Photoshop: " + plan.Width + " × " + plan.Height + " px · " + plan.Dpi + " DPI.", false);
            }
            catch (Exception error)
            {
                Log.Error("Photoshop export failed.", error);
                _status("Photoshop: " + error.GetBaseException().Message +
                    (output != null && File.Exists(output) ? " PNG сохранён: " + output : ""), true);
            }
            finally { _busy = false; ExportButton.IsEnabled = true; }
        }
        private bool ChooseExecutable()
        {
            var dialog = new OpenFileDialog { Title = "Выберите Photoshop.exe", Filter = "Adobe Photoshop|Photoshop.exe", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return false;
            _settings.Executable = PhotoshopLauncher.NormalizeExecutable(dialog.FileName); Save(); RefreshPath(); return true;
        }
        private void Save()
        { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)); File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(_settings)); }
        private static string ExportDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "Photoshop");
        private void RefreshPath()
        { _path.Text = String.IsNullOrWhiteSpace(_settings.Executable) ? "Photoshop: автоматический поиск" : _settings.Executable; _path.ToolTip = _path.Text; }
    }
}
