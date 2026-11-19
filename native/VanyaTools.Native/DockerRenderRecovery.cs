using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    // A native host may consume MilChannelNotify before WPF sees it. The UI
    // dispatcher keeps answering while MediaContext waits forever for Presented
    // and accumulates uncommitted frames. Drain the real completion packets;
    // never fabricate a presentation, reset WPF fields, or replay a UI command.
    internal sealed class DockerRenderRecovery
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ContextField = typeof(Dispatcher).GetField("_reserved0", Instance);
        private static readonly Type ContextType = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext");
        private static readonly FieldInfo StateField = ContextType?.GetField("_interlockState", Instance);
        private static readonly FieldInfo PendingField = ContextType?.GetField("_needToCommitChannel", Instance);
        private static readonly FieldInfo CommitField = ContextType?.GetField("_lastCommitTime", Instance);
        private static readonly FieldInfo DisposedField = ContextType?.GetField("_isDisposed", Instance);
        private static readonly FieldInfo ConnectedField = ContextType?.GetField("_isConnected", Instance);
        private static readonly FieldInfo RenderingField = ContextType?.GetField("_isRendering", Instance);
        private static readonly MethodInfo NotifyMethod = ContextType?.GetMethod("NotifyChannelMessage", Instance, null, Type.EmptyTypes, null);
        private readonly Dispatcher _dispatcher;
        private readonly WeakReference<FrameworkElement> _owner;
        private readonly Func<bool> _hasActiveOperation;
        private readonly int _stallMs;
        private readonly DispatcherTimer _fallback;
        private long _observedCommit, _observedAt, _lastEmptyAttempt;
        private bool _observing, _pumping, _unavailable, _emptyReported;
        internal int RecoveryCount { get; private set; }
        internal bool IsPolling => _fallback.IsEnabled;
        internal static bool IsSupported => ContextField != null && StateField != null && PendingField != null &&
            CommitField != null && DisposedField != null && ConnectedField != null && RenderingField != null && NotifyMethod != null;

        internal DockerRenderRecovery(Dispatcher dispatcher, FrameworkElement owner, Func<bool> hasActiveOperation,
            int stallMs = 3000, int pollMs = 50)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _dispatcher.VerifyAccess();
            _owner = new WeakReference<FrameworkElement>(owner ?? throw new ArgumentNullException(nameof(owner)));
            if (!ReferenceEquals(owner.Dispatcher, dispatcher)) throw new ArgumentException("Owner belongs to another dispatcher.", nameof(owner));
            _hasActiveOperation = hasActiveOperation ?? (() => false);
            _stallMs = Math.Max(1, stallMs);
            _fallback = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                { Interval = TimeSpan.FromMilliseconds(Math.Max(10, pollMs)) };
            _fallback.Tick += (_, __) => Check();
            owner.Unloaded += (_, __) => Stop();
        }

        internal void Stop()
        {
            if (!_dispatcher.CheckAccess())
            {
                if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(Stop));
                return;
            }
            _fallback.Stop(); ResetObservation(); _lastEmptyAttempt = 0;
        }

        internal void Check()
        {
            _dispatcher.VerifyAccess();
            if (_unavailable || _pumping || !IsSupported || _dispatcher.HasShutdownStarted) return;
            if (!_owner.TryGetTarget(out var owner) || !owner.IsLoaded || !owner.IsVisible)
            { Stop(); return; }
            var source = PresentationSource.FromVisual(owner) as HwndSource;
            if (source == null || source.IsDisposed || source.CompositionTarget == null)
            { Stop(); return; }
            if (_hasActiveOperation()) { ResetObservation(); return; }
            try
            {
                object context = ContextField.GetValue(_dispatcher);
                if (context == null || !ContextType.IsInstanceOfType(context) ||
                    (bool)DisposedField.GetValue(context) || !(bool)ConnectedField.GetValue(context) ||
                    (bool)RenderingField.GetValue(context))
                { ResetObservation(); return; }
                var before = Read(context);
                if (!before.Waiting || !before.Pending) { ResetObservation(); return; }
                long now = Stopwatch.GetTimestamp();
                if (!_observing || before.Commit != _observedCommit)
                { _observing = true; _observedCommit = before.Commit; _observedAt = now; _emptyReported = false; }
                double stalledMs = ElapsedMs(now, _observedAt);
                if (!_fallback.IsEnabled && stalledMs < _stallMs) return;
                // A real renderer failure has no completion packet to drain.
                // Do not turn that into a tight polling loop or a UI-thread wait.
                if (_lastEmptyAttempt != 0 && ElapsedMs(now, _lastEmptyAttempt) < 250) return;
                _pumping = true;
                try { NotifyMethod.Invoke(context, null); }
                finally { _pumping = false; }
                if ((bool)DisposedField.GetValue(context) || !owner.IsLoaded || !owner.IsVisible || source.IsDisposed)
                { Stop(); return; }
                var after = Read(context);
                if (after.Commit != before.Commit || !after.Pending || !after.Waiting)
                {
                    RecoveryCount++;
                    _lastEmptyAttempt = 0; ResetObservation();
                    if (!_fallback.IsEnabled)
                    {
                        _fallback.Start();
                        Log.Info("Vanya render notifications recovered: dispatcher=" + _dispatcher.Thread.ManagedThreadId +
                            ", root=" + owner.GetType().Name + ", stalledMs=" + (long)stalledMs +
                            ", commit=" + before.Commit + " -> " + after.Commit +
                            ". Real WPF completion packets drained; notification fallback enabled.");
                    }
                }
                else
                {
                    _lastEmptyAttempt = now;
                    if (!_emptyReported)
                    {
                        _emptyReported = true;
                        Log.Info("Vanya render still waiting: no completed presentation packet; commit=" + before.Commit +
                            ". No WPF state reset or blocking render flush attempted.");
                    }
                }
            }
            catch (Exception error)
            {
                _unavailable = true; Stop();
                Log.Error("Could not process pending Vanya WPF render notifications.", error.GetBaseException());
            }
        }

        private void ResetObservation() { _observing = false; _emptyReported = false; }
        private static double ElapsedMs(long now, long then) => (now - then) * 1000.0 / Stopwatch.Frequency;
        private static Snapshot Read(object context) => new Snapshot {
            Waiting = String.Equals(StateField.GetValue(context)?.ToString(), "WaitingForResponse", StringComparison.Ordinal),
            Pending = (bool)PendingField.GetValue(context), Commit = (long)CommitField.GetValue(context) };
        private struct Snapshot { internal bool Waiting, Pending; internal long Commit; }
    }
}
