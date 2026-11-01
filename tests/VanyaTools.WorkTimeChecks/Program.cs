using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using VanyaTools.Native;

namespace VanyaTools.Native { internal static class Log { public static void Error(string text, Exception error) { } } }
internal static class Program
{
    private static int _checks;
    private static readonly string Computer = Guid.NewGuid().ToString("N"), Project = Guid.NewGuid().ToString("N");
    private static readonly DateTime Start = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static WorkDocumentSnapshot Doc(string key, string path = null) => new WorkDocumentSnapshot { Key = key, ProjectId = Project, FilePath = path ?? "", Title = key };
    private static WorkTimePoint Point(double seconds, string key = "A", bool foreground = true, bool idle = false, bool paused = false) =>
        new WorkTimePoint { MonotonicSeconds = seconds, Utc = Start.AddSeconds(seconds), ActiveKey = key, Foreground = foreground, Idle = idle, Paused = paused };
    private static WorkTimeSession Fixture(string file)
    {
        var engine = new WorkTimeEngine("ДИЗАЙНЕР А", Computer, "QA-PC");
        engine.Sample(Point(0), new[] { Doc("A", file) }); engine.Sample(Point(5), new[] { Doc("A", file) });
        return engine.Drain().Single();
    }
    private static void Wait(Func<bool> condition, string message)
    { Check(SpinWait.SpinUntil(condition, 10000), message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidDataException) { _checks++; return; } throw new Exception(message); }
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vanya-WorkTimeChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Accounting(); RenameAndLateActions(); Persistence(root); Queues(root); Validation(root); Metadata();
            Console.WriteLine("Work-time checks passed: " + _checks + ". Synthetic clocks, owned temp journals, fake network; no Corel or NAS."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        // Journal test files remain in this unique temp directory so asynchronous
        // retry workers cannot recreate a deleted tree while the process exits.
    }
    private static void Accounting()
    {
        var engine = new WorkTimeEngine("ДИЗАЙНЕР А", Computer, "QA-PC"); var docs = new[] { Doc("A"), Doc("B") };
        engine.Sample(Point(0), docs); engine.Sample(Point(5), docs); engine.Sample(Point(10, "B"), docs);
        engine.Sample(Point(15, "B", idle: true), docs); engine.Sample(Point(20, "B", foreground: false), docs);
        engine.Sample(Point(25, foreground: false), docs); engine.Sample(Point(30, paused: true), docs);
        engine.Sample(Point(35, paused: true), docs); engine.Sample(Point(40), docs); engine.Sample(Point(45), docs);
        var a = engine.Current("A"); var b = engine.Current("B");
        Check(a.ActiveSeconds == 15 && a.BackgroundSeconds == 20 && a.IdleSeconds == 10 && a.OpenSeconds == 45, "A active/background/pause accounting is wrong");
        Check(b.ActiveSeconds == 5 && b.BackgroundSeconds == 25 && b.IdleSeconds == 15 && b.OpenSeconds == 45, "B accounting double-counted active time");
        engine.Sample(Point(3600), docs); a = engine.Current("A");
        Check(a.ActiveSeconds == 15 && a.UnobservedSeconds == 3555 && a.OpenSeconds == 3600, "Sleep/stalled sampler inflated work time");
        var wallClock = Point(3605); wallClock.Utc = Start.AddDays(-1); engine.Sample(wallClock, docs);
        Check(engine.Current("A").ActiveSeconds == 20, "Wall-clock changes affected monotonic durations");
        Check(engine.Current("A").Employee == "ДИЗАЙНЕР А" && engine.Current("A").ComputerId == Computer, "Actor identity lost");
        var later = new WorkTimeEngine("ДИЗАЙНЕР Б", Guid.NewGuid().ToString("N"), "QA-PC-2");
        later.Sample(Point(0), new[] { Doc("A") }); later.Sample(Point(5), docs);
        Check(later.Current("B").OpenSeconds == 0, "New document received time before it was observed");
        Check(later.Current("A").SessionId != a.SessionId && later.Current("A").Employee == "ДИЗАЙНЕР Б", "Sessions from different employees merged");
    }
    private static void RenameAndLateActions()
    {
        var engine = new WorkTimeEngine("ДИЗАЙНЕР А", Computer, "QA-PC");
        engine.Sample(Point(0), new[] { Doc("A") }); engine.Sample(Point(5), new[] { Doc("A", @"D:\Заказы\Тест.cdr") });
        var named = engine.Current("A"); string session = named.SessionId;
        Check(named.ProjectId == Project && named.ActiveSeconds == 5 && named.Events.Any(item => item.Kind == "name"), "First save lost unsaved work or project identity");
        engine.Sample(Point(10), new[] { Doc("A", @"D:\Заказы\Тест v2.cdr") });
        Check(engine.Current("A").SessionId == session && engine.Current("A").ActiveSeconds == 10 && engine.Current("A").ProjectId == Project, "Save As started another history");
        engine.Objects("A", 4, 0); engine.Sample(Point(15), new[] { Doc("A", @"D:\Заказы\Тест v2.cdr") });
        Check(engine.Current("A").Events.Any(item => item.Text == "Добавлены объекты: 4"), "Added objects not journaled");
        int before = engine.Current("A").Events.Count;
        engine.Objects("A", 7, 7); engine.Sample(Point(20), new[] { Doc("A", @"D:\Заказы\Тест v2.cdr") });
        Check(engine.Current("A").Events.Count == before, "Temporary duplicate/delete polluted user history");
        engine.Close("A", Start.AddSeconds(20)); var closed = engine.Drain().Single();
        engine.EventForSession(session, Start.AddSeconds(25), "operation", "DTF готово", "очередь", 3);
        var completed = engine.Drain().Single();
        Check(completed.SessionId == session && completed.Revision > closed.Revision && completed.Events.Last().DurationSeconds == 3 && completed.OpenSeconds == 20,
            "Async result after document close was lost or inflated session duration");
        for (int i = 0; i < 2100; i++) engine.EventForSession(session, Start.AddSeconds(30 + i), "test", "Event", "", 0);
        completed = engine.Drain().Single();
        Check(completed.Events.Count == 2000 && completed.OmittedEvents > 0 && completed.Events.First().Kind == "open", "Bounded timeline lost first event or silently dropped records");
        engine.Observe(Doc("temporary"), Start); engine.Ignore("temporary");
        Check(engine.Current("temporary") == null, "Owned temporary document not ignored");
    }
    private static void Persistence(string root)
    {
        string directory = Path.Combine(root, "atomic"); Directory.CreateDirectory(directory);
        var record = Fixture(@"D:\Заказы\Тест.cdr");
        string path = Path.Combine(directory, record.SessionId + ".json");
        WorkTimeStorage.WriteAtomic(path, new JavaScriptSerializer().Serialize(record));
        var restored = WorkTimeStorage.ReadFolder(directory, record.ProjectId).Sessions.Single();
        Check(restored.ActiveSeconds == record.ActiveSeconds && restored.Employee == "ДИЗАЙНЕР А" && restored.FilePath == record.FilePath, "Journal round-trip changed identity/time/Unicode");
        record.Revision++; record.ActiveSeconds = 17;
        WorkTimeStorage.WriteAtomic(path, new JavaScriptSerializer().Serialize(record));
        Check(WorkTimeStorage.ReadFolder(directory, Project).Sessions.Single().ActiveSeconds == 17 && !Directory.EnumerateFiles(directory, "*.partial").Any(), "Atomic update left partial file or stale content");
        var merged = WorkTimeStorage.Merge(new[] { restored, record, restored.Copy() });
        Check(merged.Length == 1 && merged[0].Revision == record.Revision, "Local/shared copies counted twice");
        File.WriteAllText(Path.Combine(directory, "corrupt.json"), "broken");
        var history = WorkTimeStorage.ReadFolder(directory, Project);
        Check(history.Sessions.Length == 1 && history.Warning.Length > 0, "One broken record hid valid history or warning");
    }
    private static void Queues(string root)
    {
        string local = Path.Combine(root, "outbox");
        var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var store = new WorkTimeStorage(local, record => { entered.Set(); release.Wait(10000); });
        var first = Fixture(@"D:\Заказы\Тест.cdr"); store.Enqueue(first);
        Wait(() => entered.IsSet, "Fake network worker did not start");
        var latest = first.Copy(); latest.Revision++; latest.ActiveSeconds = 31; store.Enqueue(latest);
        bool saved = SpinWait.SpinUntil(() => WorkTimeStorage.ReadFolder(Path.Combine(local, Project), Project).Sessions.Any(value => value.ActiveSeconds == 31), 10000);
        Check(saved, "Blocked network prevented local checkpoint: " + store.LastError);
        Check(Directory.EnumerateFiles(Path.Combine(local, ".pending"), "*.json").Any(), "Unsynced checkpoint has no durable retry marker");
        release.Set();
        Wait(() => !Directory.EnumerateFiles(Path.Combine(local, ".pending"), "*.json").Any(), "Latest mirrored checkpoint did not clear retry marker");
        string recovery = Path.Combine(root, "recovery"), projectFolder = Path.Combine(recovery, Project), pending = Path.Combine(recovery, ".pending");
        Directory.CreateDirectory(projectFolder); Directory.CreateDirectory(pending);
        WorkTimeStorage.WriteAtomic(Path.Combine(projectFolder, first.SessionId + ".json"), new JavaScriptSerializer().Serialize(first));
        File.WriteAllText(Path.Combine(pending, first.SessionId + ".json"), "{\"ProjectId\":\"" + Project + "\",\"Revision\":" + first.Revision + "}");
        int replayed = 0; var restarted = new WorkTimeStorage(recovery, record => Interlocked.Increment(ref replayed));
        Wait(() => Volatile.Read(ref replayed) == 1, "Pending local history not recovered after restart");
        var order = new WorkTimeWriteQueue(record => Interlocked.Increment(ref replayed), null);
        order.Enqueue(latest); Wait(() => Volatile.Read(ref replayed) == 2, "Queue did not complete latest revision");
        order.Enqueue(first); Thread.Sleep(100);
        Check(Volatile.Read(ref replayed) == 2, "Older revision overwrote a completed newer checkpoint");
    }
    private static void Validation(string root)
    {
        var record = Fixture(@"D:\Заказы\Тест.cdr");
        record.ActiveSeconds = Double.NaN; Reject(() => WorkTimeStorage.Validate(record), "NaN work accepted");
        record.ActiveSeconds = -1; Reject(() => WorkTimeStorage.Validate(record), "Negative work accepted");
        record.ActiveSeconds = 0; record.SessionId = "../elsewhere"; Reject(() => WorkTimeStorage.Validate(record), "Session path traversal accepted");
        record = Fixture(@"D:\Заказы\Тест.cdr");
        record.JournalFolders = new[] { @"\\unknown-host\share\.VanyaTools\work-history\" + Project,
            @"D:\Elsewhere\secrets", @"D:\Предыдущая папка\.VanyaTools\work-history\" + Project };
        string[] folders = WorkTimeStorage.SharedFolders(record);
        Check(folders.Length == 2 && folders.All(value => value.StartsWith("D:\\", StringComparison.OrdinalIgnoreCase)), "CDR metadata could follow another host or arbitrary directory");
        using (var events = new WorkTimeNativeEvents(new object(), WorkTimeNativeEvents.ApplicationEvents))
        { events.Add(8, new Action<object>(_ => throw new Exception())); Check(!events.Available, "Managed checks tried activating COM/Corel"); }
    }
    private static void Metadata()
    {
        var document = new FakeWorkDocument();
        var first = WorkTimeProjectMetadata.Read(document, @"D:\Заказы\Тест.cdr");
        var second = WorkTimeProjectMetadata.Read(document, @"\\NAS\Заказы\Тест.cdr");
        Check(first.Id != second.Id && document.Properties.Writes == 0, "Different legacy files with same name were merged or reading modified CDR");
        Check(first.Id == WorkTimeProjectMetadata.Read(document, @"d:\заказы\тест.cdr").Id, "Windows path casing split a legacy journal");
        first.Folders = new[] { @"D:\Заказы\.VanyaTools\work-history\" + first.Id };
        WorkTimeProjectMetadata.Write(document, first);
        var renamed = WorkTimeProjectMetadata.Read(document, @"D:\Другой месяц\Тест v2.cdr");
        Check(renamed.Id == first.Id && renamed.Folders.SequenceEqual(first.Folders) && document.Properties.Writes == 1, "Save As lost project identity/history roots");
        document.Properties.Data = "invalid";
        Check(Guid.TryParse(WorkTimeProjectMetadata.Read(document, @"D:\Заказы\Тест.cdr").Id, out _) && document.Properties.Writes == 1,
            "Malformed metadata silently modified CDR or prevented fallback");
        var unsaved = new FakeWorkDocument();
        Check(WorkTimeProjectMetadata.Read(unsaved, "").Id != WorkTimeProjectMetadata.Read(unsaved, "").Id && unsaved.Properties.Writes == 0,
            "Unbound new documents shared an ID or reading changed the CDR");
    }
}
public sealed class FakeWorkDocument { public FakeWorkProperties Properties { get; } = new FakeWorkProperties(); }
public sealed class FakeWorkProperties
{
    public string Data; public int Writes;
    public bool Exists(string owner, int index) => Data != null;
    public object this[string owner, int index] { get => Data; set { Data = Convert.ToString(value); Writes++; } }
}
