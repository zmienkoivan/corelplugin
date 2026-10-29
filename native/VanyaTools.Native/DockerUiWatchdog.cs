using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    // Diagnostics only. Never enable controls, abort a thread or retry native work.
    internal sealed class DockerUiWatchdog
    {
        private sealed class TraceState
        {
            internal readonly List<Scope> Stack = new List<Scope>();
            internal string Last = "none";
        }
        private sealed class Scope : IDisposable
        {
            internal readonly string Name;
            internal readonly long Started = Stopwatch.GetTimestamp();
            private readonly TraceState _state;
            private readonly int _dispatcherId, _thread;
            private int _disposed;
            internal Scope(TraceState state, Dispatcher dispatcher, string name)
            { _state = state; Name = name ?? "unnamed"; _dispatcherId = RuntimeHelpers.GetHashCode(dispatcher); _thread = Thread.CurrentThread.ManagedThreadId; }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                double seconds = Seconds(Stopwatch.GetTimestamp() - Started);
                lock (_state) { _state.Stack.Remove(this); _state.Last = Name + ", elapsed=" + F(seconds) + "s"; }
                if (seconds >= 2) Log.Info("Docker synchronous operation finished: dispatcher=" + _dispatcherId +
                    ", thread=" + _thread + ", operation=" + Name + ", elapsed=" + F(seconds) + "s.");
            }
        }
        private static readonly ConditionalWeakTable<Dispatcher, TraceState> Traces = new ConditionalWeakTable<Dispatcher, TraceState>();
        private static int _nextInstance;
        private readonly Dispatcher _dispatcher;
        private readonly WeakReference<FrameworkElement> _owner;
        private readonly string _prefix;
        private readonly int _intervalMs, _delayMs;
        private readonly object _lifecycle = new object();
        private Timer _timer;
        private long _acknowledged;
        private int _pending, _reported, _generation;
        private string _activity = "запуск докера", _lastInput = "not sampled";
        private bool _lastDisabled;

        internal DockerUiWatchdog(Dispatcher dispatcher, string name = "Main", FrameworkElement owner = null,
            int intervalMs = 2000, int delayMs = 15000)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _owner = owner == null ? null : new WeakReference<FrameworkElement>(owner);
            _intervalMs = Math.Max(10, intervalMs); _delayMs = Math.Max(_intervalMs, delayMs);
            _prefix = "Docker UI [" + name + "#" + Interlocked.Increment(ref _nextInstance) +
                ", version=" + typeof(DockerUiWatchdog).Assembly.GetName().Version.ToString(3) +
                ", thread=" + dispatcher.Thread.ManagedThreadId + ", dispatcher=" + RuntimeHelpers.GetHashCode(dispatcher) + "] ";
        }

        // Use only around synchronous callbacks; do not keep this scope across await.
        internal static IDisposable Trace(Dispatcher dispatcher, string operation)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            TraceState state = Traces.GetValue(dispatcher, _ => new TraceState());
            var scope = new Scope(state, dispatcher, operation);
            lock (state) state.Stack.Add(scope);
            return scope;
        }
        internal void Note(string activity) { Interlocked.Exchange(ref _activity, activity ?? ""); }
        internal void Start()
        {
            Stop("restart");
            lock (_lifecycle)
            {
                int generation = ++_generation;
                Interlocked.Exchange(ref _acknowledged, Stopwatch.GetTimestamp());
                _pending = _reported = 0;
                _lastInput = "not sampled"; _lastDisabled = false;
                _timer = new Timer(Check, generation, _intervalMs, _intervalMs);
            }
            Log.Info(_prefix + "started.");
        }
        internal void Stop(string reason = "stop")
        {
            Timer timer;
            lock (_lifecycle)
            {
                timer = _timer;
                if (timer == null) return;
                _timer = null; ++_generation;
            }
            timer.Dispose();
            Log.Info(_prefix + "stopped (" + reason + "). " + TraceDescription() + "; input=" + _lastInput);
        }

        private bool Current(int generation) => Volatile.Read(ref _timer) != null && Volatile.Read(ref _generation) == generation;
        private void Check(object value)
        {
            int generation = (int)value;
            bool delayed, queueBackground;
            double age;
            lock (_lifecycle)
            {
                if (!Current(generation) || _dispatcher.HasShutdownStarted) return;
                age = Seconds(Stopwatch.GetTimestamp() - Interlocked.Read(ref _acknowledged));
                delayed = age * 1000 >= _delayMs && _reported == 0;
                if (delayed) _reported = 1;
                queueBackground = _pending == 0;
                if (queueBackground) _pending = 1;
            }
            if (delayed && Current(generation))
            {
                Log.Info(_prefix + "heartbeat delayed " + F(age) + "s. " + TraceDescription() +
                    "; last status=" + _activity + "; previous input=" + _lastInput);
                // One higher-priority probe per delayed episode; never an unbounded queue.
                long queued = Stopwatch.GetTimestamp();
                Queue(generation, DispatcherPriority.Normal, () => {
                    string input = ReadInputState(out _);
                    if (Current(generation)) Log.Info(_prefix + "Normal probe answered: queued=" +
                        F(Seconds(Stopwatch.GetTimestamp() - queued)) + "s, background age=" +
                        F(Seconds(Stopwatch.GetTimestamp() - Interlocked.Read(ref _acknowledged))) + "s; " +
                        TraceDescription() + "; previous input=" + _lastInput + "; input=" + input);
                });
            }
            if (!queueBackground || !Current(generation)) return;
            if (!Queue(generation, DispatcherPriority.Background, () => {
                string input = ReadInputState(out bool disabled);
                bool resumed, changed;
                double delay;
                lock (_lifecycle)
                {
                    if (!Current(generation)) return;
                    delay = Seconds(Stopwatch.GetTimestamp() - _acknowledged);
                    resumed = _reported != 0; _reported = 0;
                    changed = input != _lastInput && (disabled || _lastDisabled);
                    _lastDisabled = disabled; _lastInput = input;
                    _acknowledged = Stopwatch.GetTimestamp(); _pending = 0;
                }
                if (resumed) Log.Info(_prefix + "heartbeat resumed after " + F(delay) + "s. " + TraceDescription() + "; input=" + input);
                else if (changed) Log.Info(_prefix + "input state changed: " + input);
            }))
                lock (_lifecycle) { if (Current(generation)) _pending = 0; }
        }
        private bool Queue(int generation, DispatcherPriority priority, Action action)
        {
            try
            {
                _dispatcher.BeginInvoke(new Action(() => { if (Current(generation)) action(); }), priority);
                return true;
            }
            catch { return false; }
        }
        private string TraceDescription()
        {
            TraceState state = Traces.GetValue(_dispatcher, _ => new TraceState());
            lock (state)
            {
                if (state.Stack.Count == 0) return "active operation=none; last synchronous operation=" + state.Last;
                long now = Stopwatch.GetTimestamp();
                var parts = new List<string>();
                foreach (Scope scope in state.Stack) parts.Add(scope.Name + " (" + F(Seconds(now - scope.Started)) + "s)");
                return "active operation=" + String.Join(" > ", parts);
            }
        }
        private string ReadInputState(out bool disabled)
        {
            // All WPF/input reads stay on this dispatcher. Diagnostic failures must not escape.
            _dispatcher.VerifyAccess(); disabled = false;
            try
            {
                string captured = Mouse.Captured?.GetType().FullName ?? "none";
                if (_owner == null || !_owner.TryGetTarget(out var owner)) return "owner unavailable; mouse=" + captured;
                bool enabled = owner.IsEnabled, hit = owner.IsHitTestVisible, visible = owner.IsVisible;
                string hwnd = "none";
                bool windowEnabled = true;
                if (PresentationSource.FromVisual(owner) is HwndSource source && source.Handle != IntPtr.Zero)
                { windowEnabled = IsWindowEnabled(source.Handle); hwnd = windowEnabled.ToString(); }
                disabled = !enabled || !hit || !visible || !windowEnabled;
                return "IsEnabled=" + enabled + ", IsHitTestVisible=" + hit + ", IsVisible=" + visible +
                    ", HwndEnabled=" + hwnd + ", mouse=" + captured;
            }
            catch (Exception error) { return "input state unavailable: " + error.GetType().Name; }
        }
        private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
        private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr window);
    }
}
