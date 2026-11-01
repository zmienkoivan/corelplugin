using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class WorkTimeRuntime
    {
        private sealed class DocumentState
        { internal object Document; internal WorkDocumentSnapshot Snapshot; internal WorkProjectInfo Project; internal WorkTimeNativeEvents Events; }
        private static readonly string LocalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "work-history");
        private static readonly string SettingsPath = Path.Combine(LocalRoot, "employee.json");
        private static WorkTimeSettings _settings = LoadSettings();
        private static WorkTimeRuntime _instance;
        private readonly object _application;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly WorkTimeStorage _storage = new WorkTimeStorage(Path.Combine(LocalRoot, "sessions"));
        private readonly Dictionary<string, DocumentState> _documents = new Dictionary<string, DocumentState>();
        private readonly HashSet<string> _ignored = new HashSet<string>();
        private WorkTimeEngine _engine;
        private WorkTimeNativeEvents _events;
        private bool _running, _sampling, _paused;
        private double _lastFlush;
        internal string NativeWarning { get; private set; }
        internal static WorkTimeSettings Settings => new WorkTimeSettings { Employee = _settings.Employee, ComputerId = _settings.ComputerId, Enabled = _settings.Enabled, IdleMinutes = _settings.IdleMinutes };
        internal static bool Paused => _instance?._paused == true;

        private WorkTimeRuntime(Dispatcher dispatcher, object application)
        {
            _dispatcher = dispatcher; _application = application;
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += (_, __) => Safe(Sample);
            Start();
        }
        internal static void Initialize(Dispatcher dispatcher)
        {
            if (_instance != null) return;
            try { _instance = new WorkTimeRuntime(dispatcher, (object)CorelApp.Get()); }
            catch (Exception error) { Log.Error("Could not initialize work-time tracking.", error); }
        }
        internal static void Configure(string employee, bool enabled, int idleMinutes)
        {
            employee = (employee ?? "").Trim();
            if (employee.Length > 64 || employee.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0 || enabled && employee.Length == 0)
                throw new InvalidOperationException("Введите имя сотрудника: от 1 до 64 символов, без переносов строк.");
            if (!new[] { 1, 3, 5, 10 }.Contains(idleMinutes)) throw new InvalidOperationException("Пауза: 1, 3, 5 или 10 минут без ввода.");
            var next = new WorkTimeSettings { Employee = employee, Enabled = enabled, IdleMinutes = idleMinutes, ComputerId = _settings.ComputerId };
            Directory.CreateDirectory(LocalRoot); File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(next));
            _instance?.Stop("Настройка учёта изменена"); _settings = next;
            if (_instance == null) Initialize(Dispatcher.CurrentDispatcher); else _instance.Start();
        }
        private void Start()
        {
            if (!_settings.Enabled || String.IsNullOrWhiteSpace(_settings.Employee)) return;
            _running = true; _paused = false; NativeWarning = "";
            using (var process = Process.GetCurrentProcess())
                _engine = new WorkTimeEngine(_settings.Employee, _settings.ComputerId, Environment.MachineName,
                    process.Id, WorkTimeEngine.Stamp(process.StartTime.ToUniversalTime()));
            _events = new WorkTimeNativeEvents(_application, WorkTimeNativeEvents.ApplicationEvents);
            try
            {
                _events.Add(6, new Action<object, string>((doc, name) => Safe(() => Open(doc, "Открыт файл"))));
                _events.Add(7, new Action<object, bool, string, bool>((doc, template, name, graphics) => Safe(() => Open(doc, "Создан файл", true))));
                _events.Add(8, new Action<object>(doc => Safe(() => Closed(doc))));
                _events.Add(9, new Action<object, bool, string>((doc, saveAs, name) => Safe(() => BeforeSave(doc, name))));
                _events.Add(10, new Action<object, bool, string>((doc, saveAs, name) => Safe(() => Saved(doc))));
                _events.Add(15, new Action<object, object>((doc, window) => Safe(Sample)));
                _events.Add(19, new Action(() => Safe(() => Stop("CorelDRAW закрыт"))));
            }
            catch (Exception error) { NativeWarning = "События Corel недоступны: время считается по опросу, события сохранения могут быть неполными."; Log.Error("Corel work-time events unavailable.", error); }
            Sample(); _timer.Start();
        }
        private void Stop(string reason)
        {
            if (!_running) return;
            _running = false; _timer.Stop();
            _engine.CloseAll(Point(), reason); Flush(_engine);
            _events?.Dispose(); _events = null;
            foreach (var state in _documents.Values) state.Events?.Dispose();
            _documents.Clear(); _ignored.Clear();
        }
        private void Safe(Action work)
        { try { if (_dispatcher.CheckAccess()) work(); } catch (Exception error) { Log.Error("Work-time callback failed.", error); NativeWarning = "Часть событий учёта не прочитана: " + error.Message; } }
        private void Sample()
        {
            if (!_running || _sampling) return;
            _sampling = true;
            try
            {
                using (DockerUiWatchdog.Trace(_dispatcher, "Work time: sample open documents"))
                {
                    _engine.TouchPoint(Point());
                    var snapshots = new List<WorkDocumentSnapshot>(); var seen = new HashSet<string>();
                    dynamic app = _application;
                    foreach (dynamic document in app.Documents)
                    {
                        if (snapshots.Count >= 256) break;
                        var state = Observe((object)document, "Файл уже был открыт", false);
                        if (state != null) { snapshots.Add(state.Snapshot); seen.Add(state.Snapshot.Key); }
                    }
                    _engine.Sample(Point(), snapshots);
                    foreach (string key in _documents.Keys.Where(key => !seen.Contains(key)).ToArray())
                    { _documents[key].Events?.Dispose(); _documents.Remove(key); }
                    if (_clock.Elapsed.TotalSeconds - _lastFlush >= 15) { Flush(_engine); _lastFlush = _clock.Elapsed.TotalSeconds; }
                }
            }
            finally { _sampling = false; }
        }
        private DocumentState Observe(object document, string opened, bool fresh)
        {
            if (document == null) return null;
            string key = Identity(document);
            if (_ignored.Contains(key)) return null;
            dynamic doc = document;
            string path = DocumentPath(document), title = Convert.ToString(doc.Name) ?? "";
            if (Temporary(path)) return null;
            if (!_documents.TryGetValue(key, out var state))
            {
                state = new DocumentState { Document = document,
                    Project = fresh ? new WorkProjectInfo { Id = Guid.NewGuid().ToString("N") } : WorkTimeProjectMetadata.Read(document, path) };
                state.Snapshot = new WorkDocumentSnapshot { Key = key, ProjectId = state.Project.Id };
                _documents.Add(key, state);
                state.Events = new WorkTimeNativeEvents(document, WorkTimeNativeEvents.DocumentEvents);
                try
                {
                    state.Events.Add(21, new Action<object>(_ => { if (_running) _engine.Objects(key, 1, 0); }));
                    state.Events.Add(22, new Action<int>(count => { if (_running) _engine.Objects(key, 0, Math.Max(0, count)); }));
                }
                catch (Exception error) { NativeWarning = "События добавления объектов Corel недоступны."; Log.Error("Document work-time events unavailable.", error); }
            }
            state.Snapshot.FilePath = path; state.Snapshot.Title = title;
            state.Snapshot.JournalFolders = state.Project.Folders;
            _engine.Observe(state.Snapshot, DateTime.UtcNow, opened); return state;
        }
        private void Open(object document, string text, bool fresh = false)
        {
            if (!_running || _sampling) return;
            _engine.TouchPoint(Point()); Observe(document, text, fresh);
        }
        private void Closed(object document)
        {
            if (!_running) return;
            string key = Identity(document);
            _engine.TouchPoint(Point()); _engine.Close(key, DateTime.UtcNow); Flush(_engine);
            if (_documents.TryGetValue(key, out var state))
            { _documents.Remove(key); _dispatcher.BeginInvoke(new Action(() => state.Events?.Dispose())); }
            _ignored.Remove(key);
        }
        private void BeforeSave(object document, string target)
        {
            if (!_running) return;
            var state = Observe(document, "Открыт файл", false);
            if (state == null || Temporary(target)) return;
            string old = WorkTimeStorage.SharedFolder(state.Snapshot.FilePath, state.Project.Id);
            var proposal = new WorkTimeSession { ProjectId = state.Project.Id, FilePath = String.IsNullOrWhiteSpace(target) ? state.Snapshot.FilePath : target,
                JournalFolders = (state.Project.Folders ?? new string[0]).Concat(old == null ? new string[0] : new[] { old }).ToArray() };
            state.Project.Folders = WorkTimeStorage.SharedFolders(proposal);
            WorkTimeProjectMetadata.Write(document, state.Project);
            state.Snapshot.JournalFolders = state.Project.Folders;
        }
        private void Saved(object document)
        {
            if (!_running) return;
            var state = Observe(document, "Файл сохранён", false);
            if (state == null) return;
            _engine.Event(state.Snapshot.Key, DateTime.UtcNow, "save", "Файл сохранён", state.Snapshot.FilePath); Flush(_engine);
        }
        internal static void BeforeNativeSave(object document, string path = null) => _instance?.Safe(() => _instance.BeforeSave(document, path));
        internal static void IgnoreTemporaryDocument(object document)
        {
            if (_instance == null || !_instance._running || document == null) return;
            _instance.Safe(() => {
                string key = Identity(document); _instance._ignored.Add(key); _instance._engine.Ignore(key);
                if (_instance._documents.TryGetValue(key, out var state)) { state.Events?.Dispose(); _instance._documents.Remove(key); }
            });
        }
        internal static void TogglePause()
        {
            if (_instance == null || !_instance._running) return;
            _instance.Safe(() => { _instance._engine.TouchPoint(_instance.Point()); _instance._paused = !_instance._paused;
                _instance._engine.TouchPoint(_instance.Point()); _instance.Flush(_instance._engine); });
        }
        private WorkTimePoint Point()
        {
            string active = null;
            try { object doc = (object)((dynamic)_application).ActiveDocument; if (doc != null) active = Identity(doc); } catch { }
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            var last = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            bool idle = !GetLastInputInfo(ref last) || unchecked((uint)Environment.TickCount - last.Tick) >= (uint)(_settings.IdleMinutes * 60000);
            return new WorkTimePoint { Utc = DateTime.UtcNow, MonotonicSeconds = _clock.Elapsed.TotalSeconds,
                ActiveKey = active, Foreground = pid == Process.GetCurrentProcess().Id, Idle = idle, Paused = _paused };
        }
        private void Flush(WorkTimeEngine engine)
        { foreach (var record in engine.Drain()) try { _storage.Enqueue(record); } catch (Exception error) { Log.Error("Could not queue work-time history.", error); } }
        internal static WorkTimeSession CurrentProject()
        {
            object document = (object)CorelApp.Get().ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте CDR для просмотра истории.");
            _instance?.Safe(_instance.Sample);
            string key = Identity(document);
            var current = _instance?._engine?.Current(key);
            if (current != null) return current;
            string path = DocumentPath(document);
            var project = WorkTimeProjectMetadata.Read(document, path);
            return new WorkTimeSession { ProjectId = project.Id, FilePath = path, JournalFolders = project.Folders, Title = Convert.ToString(((dynamic)document).Name) };
        }
        internal static async Task<WorkTimeHistory> History(WorkTimeSession project)
        {
            if (_instance == null) Initialize(Dispatcher.CurrentDispatcher);
            var live = _instance?._engine?.Snapshot().Where(value => value.ProjectId == project.ProjectId).ToArray() ?? new WorkTimeSession[0];
            var stored = await _instance._storage.ReadAsync(project);
            stored.Sessions = WorkTimeStorage.Merge(live.Concat(stored.Sessions));
            stored.Warning = String.Join("\n", new[] { stored.Warning, _instance.NativeWarning, _instance._storage.LastError }.Where(value => !String.IsNullOrWhiteSpace(value)));
            return stored;
        }
        internal static WorkTimeOperation BeginOperation(object document, string text)
        {
            var runtime = _instance;
            if (runtime == null || !runtime._running) return WorkTimeOperation.Empty;
            try
            {
                document = document ?? (object)((dynamic)runtime._application).ActiveDocument;
                var state = runtime.Observe(document, "Открыт файл", false);
                var session = state == null ? null : runtime._engine.Current(state.Snapshot.Key);
                if (session == null) return WorkTimeOperation.Empty;
                WorkTimeEngine engine = runtime._engine;
                return new WorkTimeOperation((success, detail, seconds) => {
                    Action done = () => { engine.EventForSession(session.SessionId, DateTime.UtcNow, "operation", text + (success ? " · готово" : " · не завершено"), detail, seconds); runtime.Flush(engine); };
                    if (runtime._dispatcher.CheckAccess()) done(); else runtime._dispatcher.BeginInvoke(done);
                });
            }
            catch (Exception error) { Log.Error("Could not start work-time action.", error); return WorkTimeOperation.Empty; }
        }
        internal static async Task<T> TrackAsync<T>(object document, string text, Func<Task<T>> action, Func<T, string> describe)
        {
            using (var operation = BeginOperation(document, text))
            { try { T result = await action(); operation.Complete(describe == null ? "" : describe(result)); return result; }
                catch (Exception error) { operation.Fail(error.Message); throw; } }
        }
        private static bool Temporary(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string full = Path.GetFullPath(path);
            string internalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools") + Path.DirectorySeparatorChar;
            return full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || full.StartsWith(internalRoot, StringComparison.OrdinalIgnoreCase);
        }
        private static string DocumentPath(object document)
        {
            string path = Convert.ToString(((dynamic)document).FullFileName) ?? "";
            try { return Path.IsPathRooted(path) ? Path.GetFullPath(path) : ""; } catch { return ""; }
        }
        private static string Identity(object value)
        { IntPtr pointer = Marshal.GetIUnknownForObject(value); try { return pointer.ToInt64().ToString("X"); } finally { Marshal.Release(pointer); } }
        private static WorkTimeSettings LoadSettings()
        {
            try {
                var value = File.Exists(SettingsPath) ? new JavaScriptSerializer().Deserialize<WorkTimeSettings>(File.ReadAllText(SettingsPath)) : null;
                if (value != null && Guid.TryParse(value.ComputerId, out _) && value.Employee != null && value.Employee.Length <= 64 && new[] { 1,3,5,10 }.Contains(value.IdleMinutes)) return value;
            } catch (Exception error) { Log.Error("Could not load employee work-time settings.", error); }
            return new WorkTimeSettings();
        }
        [StructLayout(LayoutKind.Sequential)] private struct LastInput { internal uint Size, Tick; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInput input);
    }
    internal sealed class WorkTimeOperation : IDisposable
    {
        internal static WorkTimeOperation Empty => new WorkTimeOperation(null);
        private readonly Action<bool, string, double> _finish;
        private readonly Stopwatch _clock = Stopwatch.StartNew(); private bool _done;
        internal WorkTimeOperation(Action<bool, string, double> finish) { _finish = finish; }
        internal void Complete(string detail) => End(true, detail);
        internal void Fail(string detail) => End(false, detail);
        private void End(bool success, string detail) { if (_done) return; _done = true; _finish?.Invoke(success, detail, _clock.Elapsed.TotalSeconds); }
        public void Dispose() { if (!_done) End(false, "Отменено"); }
    }
}
