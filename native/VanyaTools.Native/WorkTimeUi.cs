using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal static class WorkTimeUi
    {
        internal static FrameworkElement Settings(Action<string, bool> status)
        {
            var saved = WorkTimeRuntime.Settings;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Имя сотрудника на этом ПК", FontSize = 11 });
            var name = new TextBox { Text = saved.Employee, MaxLength = 64, MinHeight = 28, Margin = new Thickness(0, 3, 0, 4) };
            panel.Children.Add(name);
            var enabled = new CheckBox { Content = "Вести учёт работы с CDR", IsChecked = saved.Enabled, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) };
            panel.Children.Add(enabled);
            var idle = new ComboBox { ItemsSource = new[] { 1, 3, 5, 10 }, SelectedItem = saved.IdleMinutes, MinHeight = 27, FontSize = 11 };
            panel.Children.Add(DockerLayout.Trailing(new TextBlock { Text = "Пауза без ввода, мин", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, idle));
            var save = new Button { Content = "Сохранить сотрудника и учёт", MinHeight = 28, Margin = new Thickness(0, 5, 0, 2) };
            save.Click += (_, __) => {
                try { WorkTimeRuntime.Configure(name.Text, enabled.IsChecked == true, (int)idle.SelectedItem);
                    status(enabled.IsChecked == true ? "Учёт включён: " + name.Text.Trim() + ". Основное время — активный файл на переднем плане." : "Учёт времени выключен.", false); }
                catch (Exception error) { status(error.Message, true); }
            };
            panel.Children.Add(save);
            panel.Children.Add(new TextBlock { Text = "Активное время, фон и паузы считаются отдельно. Общая история — рядом с CDR в .VanyaTools; локальная копия сохраняется при недоступной сети. Учёт начинается после включения на каждом ПК.",
                FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            return DockerSettings.Section("Учёт времени · сотрудник", panel);
        }
        internal static Button HistoryButton(FrameworkElement owner, Action<string, bool> status)
        {
            var button = new Button { Content = "⏱ История работы с файлом", FontSize = 11, MinHeight = 27, Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "Время сотрудников и события активного CDR: сохранение, добавление объектов, карточка и отправка на печать." };
            button.Click += (_, __) => {
                try { Show(owner, WorkTimeRuntime.CurrentProject()); }
                catch (Exception error) { Log.Error("Could not open work history.", error); status("История: " + error.Message, true); }
            };
            return button;
        }
        private sealed class PersonRow
        { public string Employee { get; set; } public string Active { get; set; } public string Background { get; set; } public string Idle { get; set; } public string Unknown { get; set; } public string Open { get; set; } }
        private sealed class EventRow
        { public string Time { get; set; } public string Employee { get; set; } public string Computer { get; set; } public string Event { get; set; } public string Duration { get; set; } public string Detail { get; set; } }
        internal static string Duration(double seconds)
        { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return ((int)time.TotalHours).ToString("00") + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00"); }
        private static DataGrid Grid(params string[] columns)
        {
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                EnableRowVirtualization = true, EnableColumnVirtualization = true, Margin = new Thickness(0, 4, 0, 5),
                ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader };
            for (int i = 0; i < columns.Length; i += 2)
                grid.Columns.Add(new DataGridTextColumn { Header = columns[i], Binding = new Binding(columns[i + 1]),
                    Width = i == columns.Length - 2 ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                    MinWidth = 65 });
            return grid;
        }
        private static void Show(FrameworkElement owner, WorkTimeSession project)
        {
            var window = new Window { Title = "История работы · " + project.Title, Width = 1080, Height = 720,
                MinWidth = 760, MinHeight = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            DockerTheme.Apply(window);
            if (Window.GetWindow(owner) is Window parent) window.Owner = parent;
            else if (PresentationSource.FromVisual(owner) is HwndSource source) new WindowInteropHelper(window).Owner = source.Handle;
            var root = new System.Windows.Controls.Grid { Margin = new Thickness(12) }; window.Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(175) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            var refresh = new Button { Content = "Обновить", MinHeight = 30, Padding = new Thickness(10, 3, 10, 3) };
            var pause = new Button { Content = WorkTimeRuntime.Paused ? "Продолжить учёт" : "Пауза учёта на этом ПК", MinHeight = 30, Margin = new Thickness(6, 0, 0, 0) };
            pause.IsEnabled = WorkTimeRuntime.Settings.Enabled;
            pause.Click += (_, __) => { WorkTimeRuntime.TogglePause(); pause.Content = WorkTimeRuntime.Paused ? "Продолжить учёт" : "Пауза учёта на этом ПК"; };
            tools.Children.Add(refresh); tools.Children.Add(pause); root.Children.Add(tools);
            var people = Grid("Сотрудник", "Employee", "Активно", "Active", "Фон", "Background", "Пауза", "Idle", "Нет наблюдения", "Unknown", "Всего открыт", "Open");
            System.Windows.Controls.Grid.SetRow(people, 1); root.Children.Add(people);
            var events = Grid("Когда", "Time", "Сотрудник", "Employee", "ПК", "Computer", "Действие", "Event", "Длительность", "Duration", "Подробности", "Detail");
            System.Windows.Controls.Grid.SetRow(events, 2); root.Children.Add(events);
            var note = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
            System.Windows.Controls.Grid.SetRow(note, 3); root.Children.Add(note);
            bool loading = false, closed = false;
            Action<WorkTimeHistory> display = history => {
                people.ItemsSource = history.Sessions.GroupBy(value => value.Employee, StringComparer.CurrentCultureIgnoreCase)
                    .Select(group => new PersonRow { Employee = group.Key, Active = Duration(group.Sum(value => value.ActiveSeconds)),
                        Background = Duration(group.Sum(value => value.BackgroundSeconds)), Idle = Duration(group.Sum(value => value.IdleSeconds)),
                        Unknown = Duration(group.Sum(value => value.UnobservedSeconds)), Open = Duration(group.Sum(value => value.OpenSeconds)) }).ToArray();
                events.ItemsSource = history.Sessions.SelectMany(session => session.Events.Select(item => new { Session = session, Item = item }))
                    .OrderByDescending(value => value.Item.AtUtc).Select(value => new EventRow {
                        Time = DateTime.Parse(value.Item.AtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),
                        Employee = value.Session.Employee, Computer = value.Session.Computer, Event = value.Item.Text,
                        Duration = value.Item.DurationSeconds > 0 ? Duration(value.Item.DurationSeconds) : "", Detail = value.Item.Detail }).ToArray();
                note.Text = (!WorkTimeRuntime.Settings.Enabled ? "Учёт на этом ПК выключен. Имя сотрудника и включение — в настройках докера. " : "") +
                    "Основное время — «Активно». Добавление объектов включает вставку, импорт и рисование. Длительность отправки — подготовка файла и передача в очередь, без времени работы принтера. " + history.Warning;
            };
            Func<System.Threading.Tasks.Task> update = async () => {
                if (loading || closed) return;
                loading = true; refresh.IsEnabled = false;
                try { var history = await WorkTimeRuntime.History(project); if (!closed) display(history); }
                catch (Exception error) { if (!closed) note.Text = "История не прочитана: " + error.Message; }
                finally { loading = false; if (!closed) refresh.IsEnabled = true; }
            };
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += async (_, __) => await update(); refresh.Click += async (_, __) => await update();
            window.Loaded += async (_, __) => { timer.Start(); await update(); };
            window.Closed += (_, __) => { closed = true; timer.Stop(); };
            window.Show();
        }
    }
}
