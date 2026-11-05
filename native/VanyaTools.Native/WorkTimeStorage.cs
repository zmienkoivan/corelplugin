using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class WorkTimeHistory
    {
        internal WorkTimeSession[] Sessions = new WorkTimeSession[0];
        internal string Warning = "";
    }
    // Two independent writers: a slow/offline NAS never delays local saves.
    internal sealed class WorkTimeWriteQueue
    {
        private sealed class Item { internal WorkTimeSession Value; internal DateTime Due; }
        private readonly Dictionary<string, Item> _items = new Dictionary<string, Item>();
        private readonly Dictionary<string, int> _completed = new Dictionary<string, int>();
        private readonly object _gate = new object();
        private readonly Action<WorkTimeSession> _write;
        private readonly Action<Exception> _error;
        private readonly Timer _retry;
        private bool _running;
        internal WorkTimeWriteQueue(Action<WorkTimeSession> write, Action<Exception> error)
        { _write = write; _error = error; _retry = new Timer(_ => Wake(), null, 30000, 30000); }
        internal void Enqueue(WorkTimeSession value)
        {
            lock (_gate)
            {
                if (_completed.TryGetValue(value.SessionId, out int revision) && revision >= value.Revision) return;
                if (_items.TryGetValue(value.SessionId, out var old) && old.Value.Revision > value.Revision) return;
                _items[value.SessionId] = new Item { Value = value.Copy() };
                Start();
            }
        }
        private void Wake() { lock (_gate) Start(); }
        private void Start()
        {
            if (_running || _items.Count == 0) return;
            // SMB can block for minutes. Keep each writer off the shared pool so
            // a stalled network write cannot starve local checkpoints or UI jobs.
            _running = true;
            Task.Factory.StartNew((Action)Drain, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        private void Drain()
        {
            while (true)
            {
                WorkTimeSession value;
                lock (_gate)
                {
                    var next = _items.Values.FirstOrDefault(item => item.Due <= DateTime.UtcNow);
                    if (next == null) { _running = false; return; }
                    value = next.Value;
                }
                bool success = false;
                try { _write(value); success = true; } catch (Exception error) { _error?.Invoke(error); }
                lock (_gate)
                {
                    if (!_items.TryGetValue(value.SessionId, out var current) || current.Value.Revision != value.Revision) continue;
                    if (success) { _items.Remove(value.SessionId); _completed[value.SessionId] = value.Revision; }
                    else current.Due = DateTime.UtcNow.AddSeconds(30);
                }
            }
        }
    }
    internal sealed class WorkTimeStorage
    {
        private readonly string _localRoot;
        private readonly string _pendingRoot;
        private readonly object _markerGate = new object();
        private readonly WorkTimeWriteQueue _local, _shared;
        private readonly object _readGate = new object();
        private readonly Dictionary<string, Task<WorkTimeHistory>> _reads = new Dictionary<string, Task<WorkTimeHistory>>(StringComparer.OrdinalIgnoreCase);
        private string _localError, _sharedError;
        internal string LastError => String.Join("\n", new[] { Volatile.Read(ref _localError), Volatile.Read(ref _sharedError) }.Where(value => !String.IsNullOrWhiteSpace(value)));
        private sealed class Pending { public string ProjectId { get; set; } public int Revision { get; set; } }
        internal WorkTimeStorage(string localRoot, Action<WorkTimeSession> mirrorOverride = null)
        {
            _localRoot = localRoot; _pendingRoot = Path.Combine(localRoot, ".pending");
            _shared = new WorkTimeWriteQueue(value => {
                if (mirrorOverride == null) Mirror(value); else mirrorOverride(value);
                string marker = Path.Combine(_pendingRoot, value.SessionId + ".json");
                lock (_markerGate)
                    if (File.Exists(marker)) {
                        var pending = new JavaScriptSerializer().Deserialize<Pending>(ReadBounded(marker));
                        if (pending.Revision == value.Revision) File.Delete(marker);
                    }
                Volatile.Write(ref _sharedError, null);
            }, error => { Volatile.Write(ref _sharedError, "Общий журнал недоступен; данные сохранены локально: " + error.Message); Log.Error("Work history mirror failed.", error); });
            _local = new WorkTimeWriteQueue(value => {
                string path = Path.Combine(_localRoot, value.ProjectId, value.SessionId + ".json");
                WriteAtomic(path, Serialize(value));
                lock (_markerGate) WriteAtomic(Path.Combine(_pendingRoot, value.SessionId + ".json"),
                    new JavaScriptSerializer().Serialize(new Pending { ProjectId = value.ProjectId, Revision = value.Revision }));
                Volatile.Write(ref _localError, null); _shared.Enqueue(value);
            }, error => { Volatile.Write(ref _localError, "Не удалось сохранить локальный журнал: " + error.Message); Log.Error("Work history local write failed.", error); });
            Task.Run((Action)Recover);
        }
        internal void Enqueue(WorkTimeSession value) { Validate(value); _local.Enqueue(value); }
        private static string Serialize(WorkTimeSession value) => new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(value);
        private void Recover()
        {
            try {
                if (!Directory.Exists(_pendingRoot)) return;
                foreach (string marker in Directory.EnumerateFiles(_pendingRoot, "*.json"))
                    try {
                        string session = Path.GetFileNameWithoutExtension(marker);
                        if (new FileInfo(marker).Length > 4096) continue;
                        var pending = new JavaScriptSerializer().Deserialize<Pending>(ReadBounded(marker));
                        if (!Guid.TryParse(session, out _) || !Guid.TryParse(pending.ProjectId, out _)) continue;
                        string file = Path.Combine(_localRoot, pending.ProjectId, session + ".json");
                        var value = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<WorkTimeSession>(ReadBounded(file));
                        Validate(value);
                        if (value.ProcessId > 0 && DateTime.TryParse(value.ProcessStartedUtc, out var started)) {
                            try { using (var process = Process.GetProcessById(value.ProcessId))
                                if (Math.Abs((process.StartTime.ToUniversalTime() - started.ToUniversalTime()).TotalSeconds) < 1) continue; }
                            catch (ArgumentException) { }
                        }
                        _shared.Enqueue(value);
                    } catch (Exception error) { Log.Error("Could not recover pending work history.", error); }
            } catch (Exception error) { Log.Error("Could not enumerate pending work history.", error); }
        }
        internal static string SharedFolder(string file, string projectId)
        {
            if (String.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file) || !Guid.TryParse(projectId, out _)) return null;
            string directory = Path.GetDirectoryName(file);
            return String.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, ".VanyaTools", "work-history", projectId);
        }
        internal static string[] SharedFolders(WorkTimeSession value)
        {
            string current = SharedFolder(value.FilePath, value.ProjectId);
            if (current == null) return new string[0];
            string root = Path.GetPathRoot(Path.GetFullPath(current));
            string suffix = Path.Combine(".VanyaTools", "work-history", value.ProjectId);
            return new[] { current }.Concat(value.JournalFolders ?? new string[0]).Where(folder => {
                try {
                    string path = Path.GetFullPath(folder);
                    return String.Equals(Path.GetPathRoot(path), root, StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(Path.DirectorySeparatorChar + suffix, StringComparison.OrdinalIgnoreCase);
                } catch { return false; }
            }).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        }
        private void Mirror(WorkTimeSession value)
        {
            string json = Serialize(value);
            foreach (string folder in SharedFolders(value)) WriteAtomic(Path.Combine(folder, value.SessionId + ".json"), json);
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existing, string next, int flags);
        internal static void WriteAtomic(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            VanyaToolsDataFolder.HideForPath(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(json); writer.Flush(); stream.Flush(true); }
                for (int attempt = 0; ; attempt++)
                {
                    if (MoveFileEx(temporary, path, 1 | 8)) break;
                    int error = Marshal.GetLastWin32Error();
                    // Windows can return ACCESS_DENIED while a concurrent reader
                    // or scanner still releases the replaced file. Retry briefly
                    // on the worker; persistent permission errors remain visible.
                    if ((error != 5 && error != 32 && error != 33) || attempt >= 8)
                        throw new IOException("Не удалось заменить запись журнала: " + path, new Win32Exception(error));
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal async Task<WorkTimeHistory> ReadAsync(WorkTimeSession project)
        {
            var parts = new List<WorkTimeHistory> { await Task.Run(() => ReadFolder(Path.Combine(_localRoot, project.ProjectId), project.ProjectId)) };
            foreach (string folder in SharedFolders(project))
            {
                Task<WorkTimeHistory> read;
                lock (_readGate)
                {
                    if (!_reads.TryGetValue(folder, out read) || read.IsCompleted)
                    {
                        foreach (string key in _reads.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToArray()) _reads.Remove(key);
                        if (_reads.Count >= 8) { parts.Add(new WorkTimeHistory { Warning = "Ожидаются ответы от сетевых папок. Показаны доступные записи." }); continue; }
                        read = Task.Factory.StartNew(() => ReadFolder(folder, project.ProjectId),
                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                        _reads[folder] = read;
                    }
                }
                if (await Task.WhenAny(read, Task.Delay(3000)) == read) parts.Add(await read);
                else parts.Add(new WorkTimeHistory { Warning = "Общий журнал пока не отвечает. Показаны локальные записи." });
            }
            return new WorkTimeHistory { Sessions = Merge(parts.SelectMany(part => part.Sessions)),
                Warning = String.Join("\n", parts.Select(part => part.Warning).Where(text => !String.IsNullOrWhiteSpace(text)).Distinct()) };
        }
        internal static WorkTimeSession[] Merge(IEnumerable<WorkTimeSession> values) => values.GroupBy(value => value.SessionId)
            .Select(group => group.OrderByDescending(value => value.Revision).First()).OrderBy(value => value.StartedUtc).ToArray();
        internal static WorkTimeHistory ReadFolder(string directory, string projectId)
        {
            var values = new List<WorkTimeSession>(); var warnings = new List<string>(); long readBytes = 0;
            try
            {
                if (!Directory.Exists(directory)) return new WorkTimeHistory();
                VanyaToolsDataFolder.HideForPath(directory);
                int count = 0;
                foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    if (++count > 1000 || readBytes > 32 * 1024 * 1024) { warnings.Add("Журнал слишком велик: показана часть сеансов."); break; }
                    try
                    {
                        long length = new FileInfo(file).Length;
                        if (length > 8 * 1024 * 1024) throw new InvalidDataException("Слишком большая запись.");
                        readBytes += length;
                        var value = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<WorkTimeSession>(ReadBounded(file));
                        Validate(value);
                        if (value.ProjectId == projectId) values.Add(value);
                    }
                    catch (Exception error) { warnings.Add("Часть записей журнала не прочитана: " + error.GetType().Name); }
                }
            }
            catch (Exception error) { warnings.Add("Журнал недоступен: " + error.Message); }
            return new WorkTimeHistory { Sessions = values.ToArray(), Warning = String.Join("\n", warnings.Distinct()) };
        }
        internal static void Validate(WorkTimeSession value)
        {
            if (value == null || value.Schema != 1 || !Guid.TryParse(value.SessionId, out _) || !Guid.TryParse(value.ProjectId, out _) ||
                !Guid.TryParse(value.ComputerId, out _) || value.Revision < 0 || String.IsNullOrWhiteSpace(value.Employee) || value.Employee.Length > 64 ||
                value.Events == null || value.Events.Count > 2000 || !DateTime.TryParse(value.StartedUtc, out _) || !DateTime.TryParse(value.UpdatedUtc, out _))
                throw new InvalidDataException("Некорректная запись журнала работы.");
            foreach (double seconds in new[] { value.ActiveSeconds, value.BackgroundSeconds, value.IdleSeconds, value.UnobservedSeconds })
                if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0 || seconds > 366 * 24 * 3600) throw new InvalidDataException("Некорректное время сеанса.");
            foreach (var item in value.Events)
                if (item == null || !DateTime.TryParse(item.AtUtc, out _) || (item.Text ?? "").Length > 1000 ||
                    (item.Detail ?? "").Length > 1000 || Double.IsNaN(item.DurationSeconds) || Double.IsInfinity(item.DurationSeconds) || item.DurationSeconds < 0)
                    throw new InvalidDataException("Некорректное событие журнала.");
        }
        private static string ReadBounded(string path)
        {
            const int limit = 8 * 1024 * 1024;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                if (stream.Length > limit) throw new InvalidDataException("Слишком большая запись журнала.");
                var text = new StringBuilder(); var buffer = new char[4096]; int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                { if (text.Length + count > limit) throw new InvalidDataException("Слишком большая запись журнала."); text.Append(buffer, 0, count); }
                return text.ToString();
            }
        }
    }
}
