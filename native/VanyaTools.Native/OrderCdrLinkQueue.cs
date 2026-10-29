using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace VanyaTools.Native
{
    // One worker for all dockers. It holds only immutable managed snapshots:
    // no dispatcher, document, controls or COM objects cross into file work.
    internal sealed class OrderCdrLinkQueue : IDisposable
    {
        internal sealed class Snapshot
        {
            internal readonly string Server, CdrPath, YearFolder;
            internal readonly int OrderId;
            internal readonly bool ResolveDocumentYear;
            internal Snapshot(string server, int orderId, string cdrPath, string yearFolder, bool resolveDocumentYear)
            {
                Server = (server ?? "").Trim(); OrderId = orderId;
                CdrPath = (cdrPath ?? "").Trim(); YearFolder = (yearFolder ?? "").Trim();
                ResolveDocumentYear = resolveDocumentYear;
            }
            internal string Key => Server.ToLowerInvariant() + "\n" + OrderId.ToString(CultureInfo.InvariantCulture);
            internal bool Same(Snapshot other) => other != null && OrderId == other.OrderId &&
                ResolveDocumentYear == other.ResolveDocumentYear &&
                String.Equals(Server, other.Server, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(CdrPath, other.CdrPath, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(YearFolder, other.YearFolder, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Work
        {
            internal Snapshot Value;
            internal long Version, Sequence;
            internal int Attempts;
            internal bool InFlight;
            internal DateTime Due;
        }
        private sealed class Completed
        {
            internal Snapshot Value;
            internal LinkedListNode<string> Node;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Work> _pending = new Dictionary<string, Work>();
        private readonly Dictionary<string, Completed> _completed = new Dictionary<string, Completed>();
        private readonly LinkedList<string> _history = new LinkedList<string>();
        private readonly Func<Snapshot, bool> _process;
        private readonly Action<Exception> _error;
        private readonly Action<long, bool> _finished;
        private readonly TimeSpan _retryDelay, _failureCooldown;
        private readonly int _maxAttempts, _historyLimit;
        private readonly Timer _timer;
        private long _sequence;
        private bool _running, _disposed;

        internal OrderCdrLinkQueue(Func<Snapshot, bool> process, Action<Exception> error = null,
            Action<long, bool> finished = null, TimeSpan? retryDelay = null, TimeSpan? failureCooldown = null,
            int maxAttempts = 3, int historyLimit = 256)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            _error = error; _finished = finished;
            _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
            _failureCooldown = failureCooldown ?? TimeSpan.FromMinutes(5);
            if (_retryDelay <= TimeSpan.Zero || _failureCooldown <= TimeSpan.Zero || maxAttempts < 1 || historyLimit < 1)
                throw new ArgumentOutOfRangeException(nameof(retryDelay));
            _maxAttempts = maxAttempts; _historyLimit = historyLimit;
            _timer = new Timer(Wake, null, Timeout.Infinite, Timeout.Infinite);
        }

        internal void Enqueue(string server, int orderId, string cdrPath, string yearFolder, bool resolveDocumentYear = true)
        {
            if (orderId <= 0 || String.IsNullOrWhiteSpace(cdrPath)) return;
            var value = new Snapshot(server, orderId, cdrPath, yearFolder, resolveDocumentYear);
            lock (_gate)
            {
                if (_disposed) return;
                string key = value.Key;
                if (_pending.TryGetValue(key, out Work pending))
                {
                    if (pending.Value.Same(value)) return;
                    // The most recent path for this order wins, even when an older
                    // attempt is still blocked. Other orders retain their own work.
                    pending.Value = value; pending.Version++; pending.Sequence = ++_sequence;
                    pending.Attempts = 0; pending.Due = DateTime.MinValue;
                }
                else
                {
                    if (_completed.TryGetValue(key, out Completed previous))
                    {
                        if (previous.Value.Same(value)) return;
                        _history.Remove(previous.Node); _completed.Remove(key);
                    }
                    _pending.Add(key, new Work { Value = value, Version = 1, Sequence = ++_sequence });
                }
                if (!_running) StartLocked();
            }
        }

        private void StartLocked()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _running = true;
            Task.Run((Action)Drain);
        }
        private void Wake(object ignored)
        {
            lock (_gate)
                if (!_disposed && !_running && _pending.Count > 0) StartLocked();
        }

        private void Drain()
        {
            while (true)
            {
                Work work = null;
                Snapshot value;
                long version;
                lock (_gate)
                {
                    if (_disposed) { _running = false; return; }
                    DateTime now = DateTime.UtcNow, next = DateTime.MaxValue;
                    foreach (Work candidate in _pending.Values)
                    {
                        if (candidate.InFlight) continue;
                        if (candidate.Due > now) { if (candidate.Due < next) next = candidate.Due; continue; }
                        if (work == null || candidate.Sequence < work.Sequence) work = candidate;
                    }
                    if (work == null)
                    {
                        _running = false;
                        if (next != DateTime.MaxValue)
                            _timer.Change((int)Math.Max(1, Math.Min(Int32.MaxValue, (next - now).TotalMilliseconds)), Timeout.Infinite);
                        return;
                    }
                    work.InFlight = true; value = work.Value; version = work.Version;
                }

                bool success = false;
                var elapsed = Stopwatch.StartNew();
                try { success = _process(value); }
                catch (Exception error) { try { _error?.Invoke(error); } catch { } }
                try { _finished?.Invoke(elapsed.ElapsedMilliseconds, success); } catch { }
                lock (_gate)
                {
                    if (_disposed) { _running = false; return; }
                    work.InFlight = false;
                    if (work.Version != version) continue;
                    if (success)
                    {
                        _pending.Remove(value.Key);
                        var done = new Completed { Value = value, Node = _history.AddLast(value.Key) };
                        _completed[value.Key] = done;
                        while (_completed.Count > _historyLimit)
                        {
                            string oldest = _history.First.Value;
                            _history.RemoveFirst(); _completed.Remove(oldest);
                        }
                    }
                    else
                    {
                        // Keep failed links pending across document switches. An
                        // unavailable share gets slow retries after the initial burst.
                        work.Attempts = Math.Min(_maxAttempts, work.Attempts + 1);
                        work.Due = DateTime.UtcNow + (work.Attempts >= _maxAttempts ? _failureCooldown : _retryDelay);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true; _timer.Dispose(); _pending.Clear(); _completed.Clear(); _history.Clear();
            }
            // File/SMB calls cannot be cancelled safely. Never wait for them on UI.
        }
    }
}
