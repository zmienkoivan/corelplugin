using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    // Diagnostics only: never abort a Corel/UI thread or retry an operation.
    internal sealed class DockerUiWatchdog
    {
        private readonly Dispatcher _dispatcher;
        private Timer _timer;
        private long _acknowledged;
        private int _pending, _reported;
        private string _activity = "запуск докера";

        internal DockerUiWatchdog(Dispatcher dispatcher) { _dispatcher = dispatcher; }
        internal void Note(string activity) { Interlocked.Exchange(ref _activity, activity ?? ""); }
        internal void Start()
        {
            Stop(); Interlocked.Exchange(ref _acknowledged, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _pending, 0); Interlocked.Exchange(ref _reported, 0);
            _timer = new Timer(Check, null, 2000, 2000);
            Log.Info("Docker UI heartbeat started.");
        }
        internal void Stop() { var timer = Interlocked.Exchange(ref _timer, null); timer?.Dispose(); }
        private void Check(object ignored)
        {
            if (_timer == null || _dispatcher.HasShutdownStarted) return;
            double seconds = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _acknowledged)) / (double)Stopwatch.Frequency;
            if (seconds >= 15 && Interlocked.CompareExchange(ref _reported, 1, 0) == 0)
                Log.Info("Docker UI heartbeat delayed " + (int)seconds + " seconds. Last status: " + _activity);
            if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
            try
            {
                _dispatcher.BeginInvoke(new Action(() => {
                    double delay = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _acknowledged)) / (double)Stopwatch.Frequency;
                    if (Interlocked.Exchange(ref _reported, 0) != 0)
                        Log.Info("Docker UI heartbeat resumed after " + (int)delay + " seconds. Last status: " + _activity);
                    Interlocked.Exchange(ref _acknowledged, Stopwatch.GetTimestamp()); Interlocked.Exchange(ref _pending, 0);
                }), DispatcherPriority.Background);
            }
            catch { Interlocked.Exchange(ref _pending, 0); }
        }
    }
}
