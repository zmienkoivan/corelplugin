using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VanyaTools.Printing;

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
        public PrintIccOptions[] Icc { get; set; } = new PrintIccOptions[5];
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

        internal PrintIccOptions IccFor(int index)
        {
            if (index < 0 || index >= 5) throw new ArgumentOutOfRangeException(nameof(index));
            return Icc != null && index < Icc.Length && Icc[index] != null ? Icc[index].Copy() : new PrintIccOptions();
        }
        private PrintIccOptions[] CopyIcc()
        {
            var values = new PrintIccOptions[5];
            for (int i = 0; i < values.Length; i++) values[i] = IccFor(i);
            return values;
        }

        internal static string NormalizeFolder(string value, bool requireAvailable = false)
        {
            string path = (value ?? "").Trim().Trim('"');
            if (path.Length == 0)
            {
                if (requireAvailable) throw new InvalidOperationException("Укажите папку этой кнопки в «Настройки → Печать PDF, DTF и UV».");
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
            UvYellowFolder = UvYellowFolder, PrintDpi = PrintDpi, Icc = CopyIcc() };

        private void Commit(PrintExportSettings next)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(next));
            PrintFolder = next.PrintFolder; DtfBlueFolder = next.DtfBlueFolder;
            DtfRedFolder = next.DtfRedFolder; UvGreenFolder = next.UvGreenFolder;
            UvYellowFolder = next.UvYellowFolder; PrintDpi = next.PrintDpi; Icc = next.CopyIcc();
            Changed?.Invoke();
        }

        internal FrameworkElement CreateSettingsView(Action<string, bool> status)
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            string[] titles = { "Сублимация / печать PDF", "DTF · красная", "DTF · синяя", "UV · зелёная", "UV · жёлтая" };
            var fields = new TextBox[5];
            var modes = new ComboBox[5];
            var profiles = new TextBox[5];
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
                PrintIccOptions saved = IccFor(i);
                var mode = new ComboBox { ItemsSource = new[] { "Как в Corel", "Удалить ICC", "Выбранный входной ICC" },
                    SelectedIndex = Enum.IsDefined(typeof(PrintIccMode), saved.Mode) ? (int)saved.Mode : 0,
                    MinHeight = 27, FontSize = 11 };
                modes[i] = mode;
                panel.Children.Add(DockerLayout.FieldRow("ICC для этой кнопки", mode, 0));
                var profile = new TextBox { Text = saved.ProfilePath, MinHeight = 27, FontSize = 11,
                    Margin = new Thickness(0, 0, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
                profiles[i] = profile;
                var pickProfile = new Button { Content = "ICC…" };
                pickProfile.Click += (_, __) => Safe(() => {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Входной ICC · " + titles[index],
                        Filter = "ICC-профили (*.icc;*.icm)|*.icc;*.icm", CheckFileExists = true };
                    if (File.Exists(profile.Text)) dialog.FileName = profile.Text;
                    else
                    {
                        string colors = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
                        if (Directory.Exists(colors)) dialog.InitialDirectory = colors;
                    }
                    if (dialog.ShowDialog() != true) return;
                    IccProfileFile info = IccProfileFile.Read(dialog.FileName);
                    profile.Text = dialog.FileName;
                    status("Выбран входной ICC " + info.Model + ". Нажмите «Сохранить папки и ICC».", false);
                });
                var profileRow = DockerLayout.Trailing(profile, pickProfile);
                profileRow.Margin = new Thickness(0, 0, 0, 4);
                profileRow.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                mode.SelectionChanged += (_, __) => profileRow.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                panel.Children.Add(profileRow);
            }
            var loadedFolders = new string[fields.Length];
            var loadedProfiles = new string[fields.Length];
            var loadedModes = new int[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                loadedFolders[i] = fields[i].Text; loadedProfiles[i] = profiles[i].Text;
                loadedModes[i] = modes[i].SelectedIndex;
            }
            void SyncValues(bool force)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    // A folder can change in the print tab while an ICC edit
                    // is pending here. Synchronize these independent values.
                    if (force || String.Equals(fields[i].Text, loadedFolders[i], StringComparison.Ordinal))
                    {
                        fields[i].Text = Folder(i); loadedFolders[i] = fields[i].Text;
                    }
                    if (force || (String.Equals(profiles[i].Text, loadedProfiles[i], StringComparison.Ordinal) &&
                        modes[i].SelectedIndex == loadedModes[i]))
                    {
                        PrintIccOptions saved = IccFor(i);
                        modes[i].SelectedIndex = Enum.IsDefined(typeof(PrintIccMode), saved.Mode) ? (int)saved.Mode : 0;
                        profiles[i].Text = saved.ProfilePath;
                        loadedProfiles[i] = profiles[i].Text; loadedModes[i] = modes[i].SelectedIndex;
                    }
                }
            }
            var save = new Button { Content = "Сохранить папки и ICC", Margin = new Thickness(0, 4, 0, 2) };
            save.Click += (_, __) => Safe(() => {
                var next = Snapshot();
                next.PrintFolder = NormalizeFolder(fields[0].Text); next.DtfRedFolder = NormalizeFolder(fields[1].Text);
                next.DtfBlueFolder = NormalizeFolder(fields[2].Text); next.UvGreenFolder = NormalizeFolder(fields[3].Text);
                next.UvYellowFolder = NormalizeFolder(fields[4].Text);
                for (int i = 0; i < modes.Length; i++)
                {
                    var options = new PrintIccOptions { Mode = (PrintIccMode)modes[i].SelectedIndex,
                        ProfilePath = profiles[i].Text.Trim().Trim('"') };
                    options.Validate(); next.Icc[i] = options;
                }
                Commit(next); SyncValues(true);
                status("Пять папок печати и индивидуальные режимы ICC сохранены на этом компьютере.", false);
            });
            panel.Children.Add(save);
            DockerHelp.Add(panel, "На главной вкладке над корректировками находятся пять кнопок. «Сублимация» использует папку обычной печати PDF, " +
                "сохранённую в 1.1.45; у четырёх цветных кнопок DTF/UV — ещё четыре отдельные папки. Все действия работают с любым открытым CDR. " +
                "Можно указать папки приёма заданий RIP. Пустой путь отключает отправку этой кнопкой. Существующие PDF не заменяются. " +
                "Эти настройки независимы от архива спортформы и hot folder фотовывода. " +
                "ICC задаётся отдельно для каждой кнопки: «Как в Corel» сохраняет PDF без дополнительной обработки; «Удалить ICC» удаляет профили без пересчёта каналов; " +
                "«Выбранный входной ICC» прикрепляет файл ICC/ICM версии 2/4 к объектам соответствующей модели RGB или CMYK. " +
                "Значения цветов, растры, векторы, размер и DPI не пересчитываются; другие цветовые модели сохраняют свои профили. " +
                "Назначение другого профиля может изменить интерпретацию цветов. Выбирайте входной профиль макета; профиль принтера/материала и преобразование задаются в RIP. " +
                "В RIP нужно разрешить использование входных профилей. Проверка ICC завершается до появления PDF в папке печати.");
            var expander = new Expander { Header = "Печать PDF, DTF и UV", Content = panel, IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 6) };
            expander.Expanded += (_, e) => {
                if (ReferenceEquals(e.Source, expander)) SyncValues(false);
            };
            Changed += () => SyncValues(false);
            return expander;
        }
    }
}
