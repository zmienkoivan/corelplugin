using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed class PrintExportSettings
    {
        private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "print-export.json");
        public string PrintFolder { get; set; } = "";
        public string DtfBlueFolder { get; set; } = "";
        public string DtfRedFolder { get; set; } = "";
        public string UvGreenFolder { get; set; } = "";
        public string UvYellowFolder { get; set; } = "";
        public int PrintDpi { get; set; } = 300;
        internal event Action Changed;

        internal static PrintExportSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath)) return new JavaScriptSerializer()
                    .Deserialize<PrintExportSettings>(File.ReadAllText(SettingsPath)) ?? new PrintExportSettings();
            }
            catch (Exception error) { Log.Error("Could not load print PDF folders.", error); }
            return new PrintExportSettings();
        }

        internal string Folder(int index) => index == 0 ? PrintFolder : index == 1 ? DtfRedFolder :
            index == 2 ? DtfBlueFolder : index == 3 ? UvGreenFolder : UvYellowFolder;

        internal static string NormalizeFolder(string value, bool requireAvailable = false)
        {
            string path = (value ?? "").Trim().Trim('"');
            if (path.Length == 0)
            {
                if (requireAvailable) throw new InvalidOperationException("Выберите папку этой кнопки: «Печать → Папка…» или «Настройки → Печать PDF, DTF и UV».");
                return "";
            }
            string root = Path.GetPathRoot(path) ?? "";
            bool drive = root.Length == 3 && Char.IsLetter(root[0]) && root[1] == ':' && (root[2] == '\\' || root[2] == '/');
            bool unc = path.StartsWith("\\\\", StringComparison.Ordinal) && root.IndexOf('\\', 2) > 2;
            if (!drive && !unc) throw new InvalidOperationException("Укажите полный путь к папке, например D:\\RIP\\Print, или сетевой UNC-путь.");
            path = Path.GetFullPath(path);
            if (requireAvailable && !Directory.Exists(path))
                throw new InvalidOperationException("Папка печати недоступна: " + path + ". Проверьте подключение и права доступа.");
            return path;
        }

        internal void SetFolder(int index, string path)
        {
            var next = Snapshot(); path = NormalizeFolder(path);
            if (index == 0) next.PrintFolder = path;
            else if (index == 1) next.DtfRedFolder = path;
            else if (index == 2) next.DtfBlueFolder = path;
            else if (index == 3) next.UvGreenFolder = path;
            else if (index == 4) next.UvYellowFolder = path;
            else throw new ArgumentOutOfRangeException(nameof(index));
            Commit(next);
        }

        internal void SetQuality(int dpi)
        {
            var next = Snapshot(); next.PrintDpi = dpi == 150 ? 150 : 300; Commit(next);
        }

        private PrintExportSettings Snapshot() => new PrintExportSettings { PrintFolder = PrintFolder,
            DtfBlueFolder = DtfBlueFolder, DtfRedFolder = DtfRedFolder, UvGreenFolder = UvGreenFolder,
            UvYellowFolder = UvYellowFolder, PrintDpi = PrintDpi };

        private void Commit(PrintExportSettings next)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(next));
            PrintFolder = next.PrintFolder; DtfBlueFolder = next.DtfBlueFolder;
            DtfRedFolder = next.DtfRedFolder; UvGreenFolder = next.UvGreenFolder;
            UvYellowFolder = next.UvYellowFolder; PrintDpi = next.PrintDpi;
            Changed?.Invoke();
        }

        internal FrameworkElement CreateSettingsView(Action<string, bool> status)
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            string[] titles = { "Печать PDF", "DTF · красная", "DTF · синяя", "UV · зелёная", "UV · жёлтая" };
            var fields = new TextBox[5];
            void Safe(Action action)
            {
                try { action(); }
                catch (Exception error) { Log.Error("Print PDF folder settings failed.", error); status(error.Message, true); }
            }
            for (int i = 0; i < fields.Length; i++)
            {
                int index = i;
                panel.Children.Add(new TextBlock { Text = titles[i], FontSize = 11, Margin = new Thickness(0, 4, 0, 2) });
                var field = new TextBox { Text = Folder(i), MinHeight = 27, FontSize = 11,
                    VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 2) };
                fields[i] = field;
                var browse = new Button { Content = "Выбрать…" };
                browse.Click += (_, __) => Safe(() => {
                    var source = PresentationSource.FromVisual(panel) as HwndSource;
                    string path = WindowsFolderPicker.Pick(source == null ? IntPtr.Zero : source.Handle, field.Text, titles[index]);
                    if (!String.IsNullOrWhiteSpace(path)) field.Text = path;
                });
                panel.Children.Add(DockerLayout.Trailing(field, browse));
            }
            var save = new Button { Content = "Сохранить папки", Margin = new Thickness(0, 4, 0, 2) };
            save.Click += (_, __) => Safe(() => {
                var next = Snapshot();
                next.PrintFolder = NormalizeFolder(fields[0].Text); next.DtfRedFolder = NormalizeFolder(fields[1].Text);
                next.DtfBlueFolder = NormalizeFolder(fields[2].Text); next.UvGreenFolder = NormalizeFolder(fields[3].Text);
                next.UvYellowFolder = NormalizeFolder(fields[4].Text);
                Commit(next); status("Папки PDF и четырёх кнопок DTF/UV сохранены на этом компьютере.", false);
            });
            panel.Children.Add(save);
            DockerHelp.Add(panel, "Вкладка «Печать» работает с любым открытым CDR. У обычной печати своя папка; у четырёх цветных кнопок DTF/UV — ещё четыре отдельные папки. " +
                "Можно указать папки приёма заданий RIP. Пустой путь отключает отправку этой кнопкой. Существующие PDF не заменяются. " +
                "Эти настройки независимы от архива спортформы и hot folder фотовывода.");
            var expander = new Expander { Header = "Печать PDF, DTF и UV · папки", Content = panel, IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 6) };
            expander.Expanded += (_, __) => { for (int i = 0; i < fields.Length; i++) fields[i].Text = Folder(i); };
            return expander;
        }
    }
}
