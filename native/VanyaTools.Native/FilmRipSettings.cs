using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed class FilmRipSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "film-rip.json");
        public string HotFolder { get; set; } = "";
        internal event Action Changed;

        internal static FilmRipSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return new JavaScriptSerializer().Deserialize<FilmRipSettings>(File.ReadAllText(SettingsPath)) ?? new FilmRipSettings();
            }
            catch (Exception error) { Log.Error("Could not load film RIP folder.", error); }
            return new FilmRipSettings();
        }

        internal static string RequireFolder(string value)
        {
            string path = (value ?? "").Trim().Trim('"');
            if (String.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Укажите папку RIP: «Настройки → Фотовывод · RIP → Hot folder».");
            string root = Path.GetPathRoot(path) ?? "";
            bool drive = root.Length == 3 && Char.IsLetter(root[0]) && root[1] == ':' && (root[2] == '\\' || root[2] == '/');
            bool unc = path.StartsWith("\\\\", StringComparison.Ordinal) && root.IndexOf('\\', 2) > 2;
            if (!drive && !unc)
                throw new InvalidOperationException("Укажите полный путь к папке RIP, например D:\\RIP\\HotFolder или сетевой UNC-путь.");
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path))
                throw new InvalidOperationException("Папка RIP недоступна: " + path + ". Проверьте путь, подключение и права доступа.");
            return path;
        }

        internal FrameworkElement CreateSettingsView(Action<string, bool> status)
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(new TextBlock { Text = "Hot folder RIP для фотовывода", FontSize = 11, Margin = new Thickness(0, 0, 0, 3) });
            var field = new TextBox { Text = HotFolder ?? "", FontSize = 11, MinHeight = 27,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 2),
                ToolTip = "Папка приёма PDF в RIP. Можно указать локальный или сетевой UNC-путь." };
            void Save()
            {
                string value = field.Text.Trim();
                string path = String.IsNullOrWhiteSpace(value) ? "" : RequireFolder(value);
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(new FilmRipSettings { HotFolder = path }));
                HotFolder = path; field.Text = path; Changed?.Invoke();
                status(path.Length == 0 ? "Папка RIP сброшена." : "Папка RIP для фотовывода сохранена.", false);
            }
            void Safe(Action action)
            {
                try { action(); }
                catch (Exception error) { Log.Error("Film RIP settings failed.", error); status(error.Message, true); }
            }
            var browse = new Button { Content = "Выбрать…", Margin = new Thickness(0, 0, 0, 2) };
            browse.Click += (_, __) => Safe(() => {
                var source = PresentationSource.FromVisual(panel) as HwndSource;
                string path = WindowsFolderPicker.Pick(source == null ? IntPtr.Zero : source.Handle,
                    field.Text.Trim(), "Hot folder RIP для фотовывода");
                if (String.IsNullOrWhiteSpace(path)) return;
                field.Text = path; Save();
            });
            panel.Children.Add(DockerLayout.Trailing(field, browse));
            var save = new Button { Content = "Сохранить папку RIP", Margin = new Thickness(0, 1, 0, 2) };
            save.Click += (_, __) => Safe(Save); panel.Children.Add(save);
            DockerHelp.Add(panel, "«Отправить на печать» во вкладке «Фотовывод» готовит тот же PDF 1:1, что и экспорт, " +
                "и передаёт его прямо в эту папку без папок года и месяца. Используются те же параметры полос и проверки цветов. " +
                "Укажите существующую папку, которую отслеживает RIP. Путь сохраняется на этом компьютере отдельно от папки архива. " +
                "Задания получают уникальные имена; расширение PDF появляется после окончания записи. " +
                "Дальнейшую обработку и запуск принтера выполняет RIP.");
            return new Expander { Header = "Фотовывод · RIP", Content = panel, IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 6) };
        }
    }
}
