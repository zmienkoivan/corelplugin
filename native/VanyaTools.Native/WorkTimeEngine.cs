using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    // Pure managed accounting. Native/file operations do not run in this class.
    internal sealed class WorkTimeEngine
    {
        private sealed class State { internal WorkTimeSession Session; internal bool Dirty = true; internal int Added, Deleted; }
        private readonly Dictionary<string, State> _open = new Dictionary<string, State>();
        private readonly Dictionary<string, State> _retired = new Dictionary<string, State>();
        private readonly Queue<string> _retiredOrder = new Queue<string>();
        private readonly string _employee, _computerId, _computer;
        private readonly int _processId; private readonly string _processStarted;
        private WorkTimePoint _previous;
        internal WorkTimeEngine(string employee, string computerId, string computer, int processId = 0, string processStarted = null)
        {
            _employee = employee; _computerId = computerId; _computer = computer;
            _processId = processId; _processStarted = processStarted;
        }
        internal static string Stamp(DateTime value) => value.ToUniversalTime().ToString("o");
        internal void Observe(WorkDocumentSnapshot document, DateTime now, string opened = "Открыт файл")
        {
            if (!_open.TryGetValue(document.Key, out var state))
            {
                state = new State { Session = new WorkTimeSession {
                    SessionId = Guid.NewGuid().ToString("N"), ProjectId = document.ProjectId,
                    Employee = _employee, ComputerId = _computerId, Computer = _computer,
                    ProcessId = _processId, ProcessStartedUtc = _processStarted,
                    FilePath = document.FilePath ?? "", Title = document.Title ?? "",
                    StartedUtc = Stamp(now), UpdatedUtc = Stamp(now), JournalFolders = document.JournalFolders
                } };
                _open.Add(document.Key, state);
                Add(state, now, "open", opened, document.FilePath, 0);
            }
            else
            {
                var session = state.Session;
                if (!String.Equals(session.FilePath ?? "", document.FilePath ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    string previous = session.FilePath;
                    session.FilePath = document.FilePath ?? ""; state.Dirty = true;
                    Add(state, now, "name", String.IsNullOrWhiteSpace(previous) ? "Файл получил имя" : "Сохранён под другим именем",
                        (previous ?? "") + " → " + session.FilePath, 0);
                }
                if (session.ProjectId != document.ProjectId) { session.ProjectId = document.ProjectId; state.Dirty = true; }
                if (session.Title != document.Title) { session.Title = document.Title; state.Dirty = true; }
                session.JournalFolders = document.JournalFolders;
            }
        }
        internal void Sample(WorkTimePoint point, IList<WorkDocumentSnapshot> documents)
        {
            Advance(point);
            var keys = new HashSet<string>(documents.Select(value => value.Key));
            foreach (string key in _open.Keys.Where(key => !keys.Contains(key)).ToArray()) Close(key, point.Utc);
            foreach (var document in documents) Observe(document, point.Utc);
            _previous = point;
            FlushObjectChanges(point.Utc);
        }
        private void Advance(WorkTimePoint point)
        {
            if (_previous == null) return;
            double elapsed = point.MonotonicSeconds - _previous.MonotonicSeconds;
            if (elapsed <= 0 || Double.IsNaN(elapsed) || Double.IsInfinity(elapsed)) return;
            foreach (var state in _open.Values)
            {
                var session = state.Session;
                // A suspended/stalled sampler cannot prove employee activity.
                if (elapsed > 15) session.UnobservedSeconds += elapsed;
                else if (_previous.Paused) session.IdleSeconds += elapsed;
                else if (!_previous.Foreground || !ReferenceEquals(state, ActiveState(_previous.ActiveKey))) session.BackgroundSeconds += elapsed;
                else if (_previous.Idle) session.IdleSeconds += elapsed;
                else session.ActiveSeconds += elapsed;
                session.UpdatedUtc = Stamp(point.Utc); state.Dirty = true;
            }
        }
        private State ActiveState(string key) => key != null && _open.TryGetValue(key, out var state) ? state : null;
        internal void TouchPoint(WorkTimePoint point)
        { Advance(point); _previous = point; }
        internal void Close(string key, DateTime now, string text = "Файл закрыт")
        {
            if (!_open.TryGetValue(key, out var state)) return;
            FlushObjectChanges(now);
            Add(state, now, "close", text, "", 0);
            state.Session.ClosedUtc = Stamp(now); state.Session.UpdatedUtc = Stamp(now); state.Session.Revision++;
            state.Dirty = true; _retired[state.Session.SessionId] = state; _retiredOrder.Enqueue(state.Session.SessionId);
            while (_retiredOrder.Count > 128) _retired.Remove(_retiredOrder.Dequeue());
            _open.Remove(key);
        }
        internal void CloseAll(WorkTimePoint point, string reason)
        {
            Advance(point);
            foreach (string key in _open.Keys.ToArray()) Close(key, point.Utc, reason);
            _previous = null;
        }
        internal void Ignore(string key) { _open.Remove(key); }
        internal void Objects(string key, int added, int deleted)
        { if (_open.TryGetValue(key, out var state)) { state.Added = Math.Min(1000000, state.Added + added); state.Deleted = Math.Min(1000000, state.Deleted + deleted); } }
        private void FlushObjectChanges(DateTime now)
        {
            foreach (var state in _open.Values)
            {
                int delta = state.Added - state.Deleted; state.Added = state.Deleted = 0;
                if (delta != 0) Add(state, now, "objects", delta > 0 ? "Добавлены объекты: " + delta : "Удалены объекты: " + (-delta), "Вставка, импорт или редактирование в Corel", 0);
            }
        }
        internal void Event(string key, DateTime now, string kind, string text, string detail = "", double seconds = 0)
        { if (_open.TryGetValue(key, out var state)) Add(state, now, kind, text, detail, seconds); }
        internal void EventForSession(string sessionId, DateTime now, string kind, string text, string detail, double seconds)
        {
            var state = _open.Values.FirstOrDefault(item => item.Session.SessionId == sessionId);
            if (state != null) Add(state, now, kind, text, detail, seconds);
            else
            {
                if (_retired.TryGetValue(sessionId, out var closed)) Add(closed, now, kind, text, detail, seconds);
            }
        }
        private static void Add(State state, DateTime now, string kind, string text, string detail, double seconds)
        {
            var session = state.Session;
            if (session.Events.Count >= 2000) { session.Events.RemoveAt(1); session.OmittedEvents++; }
            session.Events.Add(new WorkTimeEvent { AtUtc = Stamp(now), Kind = kind, Text = text,
                Detail = (detail ?? "").Length > 1000 ? detail.Substring(0, 1000) : detail ?? "", DurationSeconds = Math.Max(0, seconds) });
            session.UpdatedUtc = Stamp(now); state.Dirty = true;
        }
        internal WorkTimeSession Current(string key) => ActiveState(key)?.Session.Copy();
        internal WorkTimeSession[] Snapshot() => _open.Values.Concat(_retired.Values).Select(item => item.Session.Copy()).ToArray();
        internal WorkTimeSession[] Drain()
        {
            var result = new List<WorkTimeSession>();
            foreach (var state in _open.Values.Concat(_retired.Values))
                if (state.Dirty) { state.Session.Revision++; result.Add(state.Session.Copy()); state.Dirty = false; }
            return result.ToArray();
        }
    }
}
