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
        private sealed class PersonRow
        { public string Employee { get; set; } public string Active { get; set; } public string Background { get; set; } public string Idle { get; set; } public string Unknown { get; set; } public string Open { get; set; } }
        private sealed class EventRow
        {
            public string Time { get; set; } public string Employee { get; set; } public string Computer { get; set; } public string Event { get; set; } public string Duration { get; set; } public string Detail { get; set; }
            public string Summary => Employee + " · " + Event;
            public string FullText => Time + " · " + Employee + " · " + Computer + (String.IsNullOrWhiteSpace(Duration) ? "" : " · " + Duration) + "\n" + Detail;
        }
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

        internal static FrameworkElement CreateTab(FrameworkElement owner, Action<string, bool> status) =>
            CreateHistoryView(owner, WorkTimeRuntime.CurrentProject, true, status);

        private static void Show(FrameworkElement owner, WorkTimeSession project)
        {
            var window = new Window { Title = "История работы · " + project.Title,
                Width = Math.Min(1080, SystemParameters.WorkArea.Width - 40), Height = Math.Min(720, SystemParameters.WorkArea.Height - 40),
                MinWidth = 760, MinHeight = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            DockerTheme.Apply(window);
            if (Window.GetWindow(owner) is Window parent) window.Owner = parent;
            else if (PresentationSource.FromVisual(owner) is HwndSource source) new WindowInteropHelper(window).Owner = source.Handle;
            window.Content = CreateHistoryView(owner, () => project, false, null);
            window.Show();
        }

        private static FrameworkElement CreateHistoryView(FrameworkElement owner, Func<WorkTimeSession> currentProject,
            bool compact, Action<string, bool> status)
        {
            var root = new System.Windows.Controls.Grid { Margin = new Thickness(compact ? 6 : 12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // local employee settings
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // file
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // actions
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(compact ? 140 : 175) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // selected event
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // read status
            Func<System.Threading.Tasks.Task> update = null;
            if (compact)
            {
                var preferences = Settings((message, error) => { status?.Invoke(message, error); if (!error && update != null) _ = update(); });
                if (preferences is Expander expander) expander.IsExpanded = !WorkTimeRuntime.Settings.Enabled;
                root.Children.Add(preferences);
            }
            var title = new TextBlock { Text = "История активного файла", FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 4) };
            System.Windows.Controls.Grid.SetRow(title, 1); root.Children.Add(title);
            var tools = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = compact ? 3 : 2 };
            var refresh = new Button { Content = "Обновить", MinHeight = 28, FontSize = 11 };
            var pause = new Button { Content = "Пауза", MinHeight = 28, FontSize = 11,
                ToolTip = "Приостановить или продолжить учёт на этом ПК" };
            Action syncPause = () => { pause.IsEnabled = WorkTimeRuntime.Settings.Enabled; pause.Content = WorkTimeRuntime.Paused ? "Продолжить" : "Пауза"; };
            syncPause(); pause.Click += (_, __) => { WorkTimeRuntime.TogglePause(); syncPause(); if (update != null) _ = update(); };
            tools.Children.Add(refresh); tools.Children.Add(pause);
            if (compact)
            {
                var expand = new Button { Content = "Развернуть", FontSize = 11, MinHeight = 28, ToolTip = "Полная таблица в отдельном окне" };
                expand.Click += (_, __) => { try { Show(owner, currentProject()); } catch (Exception error) { status?.Invoke(error.Message, true); } };
                tools.Children.Add(expand);
            }
            System.Windows.Controls.Grid.SetRow(tools, 2); root.Children.Add(tools);
            var people = compact ? Grid("Сотрудник", "Employee", "Активно", "Active", "Фон", "Background", "Пауза", "Idle") :
                Grid("Сотрудник", "Employee", "Активно", "Active", "Фон", "Background", "Пауза", "Idle", "Нет наблюдения", "Unknown", "Всего открыт", "Open");
            var events = compact ? Grid("Когда", "Time", "Действие", "Summary") :
                Grid("Когда", "Time", "Сотрудник", "Employee", "ПК", "Computer", "Действие", "Event", "Длительность", "Duration", "Подробности", "Detail");
            people.FontSize = events.FontSize = 11;
            if (compact)
            {
                people.Columns[0].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                foreach (var column in people.Columns) column.MinWidth = 55;
                events.Columns[0].Width = new DataGridLength(112);
                events.SelectionUnit = DataGridSelectionUnit.FullRow;
                events.SelectionMode = DataGridSelectionMode.Single;
            }
            System.Windows.Controls.Grid.SetRow(people, 3); root.Children.Add(people);
            System.Windows.Controls.Grid.SetRow(events, 4); root.Children.Add(events);
            var detail = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap };
            var detailView = new ScrollViewer { Content = detail, MaxHeight = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Controls.Grid.SetRow(detailView, 5); root.Children.Add(detailView);
            events.SelectionChanged += (_, __) => detail.Text = (events.SelectedItem as EventRow)?.FullText ?? "";
            var note = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            var notes = new ScrollViewer { Content = note, MaxHeight = 95, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Controls.Grid.SetRow(notes, 6); root.Children.Add(notes);
            bool loading = false, pending = false, attached = false; int visibilityRevision = 0;
            WorkTimeSession displayed = null;
            Action clear = () => { people.ItemsSource = null; events.ItemsSource = null; detail.Text = ""; displayed = null; };
            Action<WorkTimeSession, WorkTimeHistory> display = (project, history) => {
                title.Text = project.Title; title.ToolTip = project.FilePath; displayed = project;
                people.ItemsSource = history.Sessions.GroupBy(value => value.Employee, StringComparer.CurrentCultureIgnoreCase)
                    .Select(group => new PersonRow { Employee = group.Key, Active = Duration(group.Sum(value => value.ActiveSeconds)),
                        Background = Duration(group.Sum(value => value.BackgroundSeconds)), Idle = Duration(group.Sum(value => value.IdleSeconds)),
                        Unknown = Duration(group.Sum(value => value.UnobservedSeconds)), Open = Duration(group.Sum(value => value.OpenSeconds)) }).ToArray();
                events.ItemsSource = history.Sessions.SelectMany(session => session.Events.Select(item => new { Session = session, Item = item }))
                    .OrderByDescending(value => value.Item.AtUtc).Select(value => new EventRow {
                        Time = DateTime.Parse(value.Item.AtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),
                        Employee = value.Session.Employee, Computer = value.Session.Computer, Event = value.Item.Text,
                        Duration = value.Item.DurationSeconds > 0 ? Duration(value.Item.DurationSeconds) : "", Detail = value.Item.Detail }).ToArray();
                note.Text = (!WorkTimeRuntime.Settings.Enabled ? "Учёт на этом ПК выключен. Включение — в разделе сотрудника на этой вкладке. " : "") +
                    "Активное время — основное; фон и паузы отдельно. Длительность отправки — подготовка PDF и передача в очередь, без работы принтера. " + history.Warning;
            };
            update = async () => {
                if (!attached || !root.IsVisible) return;
                if (loading) { pending = true; return; }
                loading = true; pending = false; refresh.IsEnabled = false; syncPause();
                int revision = visibilityRevision;
                try
                {
                    WorkTimeSession project = currentProject();
                    if (displayed != null && !SameProject(displayed, project)) clear();
                    title.Text = project.Title; title.ToolTip = project.FilePath; note.Text = "Загружаю историю…";
                    var history = await WorkTimeRuntime.History(project);
                    if (!attached || !root.IsVisible || revision != visibilityRevision) return;
                    if (!SameProject(project, currentProject())) { clear(); pending = true; return; }
                    display(project, history);
                }
                catch (Exception error) { if (attached && root.IsVisible && revision == visibilityRevision) { clear(); title.Text = "История активного файла"; title.ToolTip = null; note.Text = error.GetBaseException().Message; } }
                finally { loading = false; refresh.IsEnabled = true; if (pending && attached && root.IsVisible) { pending = false; _ = update(); } }
            };
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
            Action visibility = () => { visibilityRevision++; if (attached && root.IsVisible) { timer.Start(); _ = update(); } else { timer.Stop(); pending = false; } };
            timer.Tick += async (_, __) => await update(); refresh.Click += async (_, __) => await update();
            root.Loaded += (_, __) => { attached = true; visibility(); }; root.IsVisibleChanged += (_, __) => { if (attached) visibility(); };
            root.Unloaded += (_, __) => { attached = false; visibilityRevision++; timer.Stop(); pending = false; };
            return root;
        }
        private static bool SameProject(WorkTimeSession a, WorkTimeSession b) => a != null && b != null &&
            (a.ProjectId == b.ProjectId || !WorkTimeRuntime.Settings.Enabled && String.IsNullOrWhiteSpace(a.FilePath) && String.IsNullOrWhiteSpace(b.FilePath) && a.Title == b.Title) &&
            String.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
    }
}
