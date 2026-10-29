using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Lines = new List<string>();
        public static void Info(string message) { lock (Gate) Lines.Add(message); }
        internal static void Clear() { lock (Gate) Lines.Clear(); }
        internal static string[] Snapshot() { lock (Gate) return Lines.ToArray(); }
    }
}

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static int Main()
    {
        try
        {
            BlockingOperation(); BackgroundStarvation(); StaleCallbacks();
            Console.WriteLine("Docker watchdog checks passed: " + _checks + ". Isolated WPF dispatchers; no windows or Corel.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void OnDispatcher(Action<Dispatcher, Action> body)
    {
        Exception failed = null;
        var thread = new Thread(() => {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(() => {
                try { body(dispatcher, () => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send)); }
                catch (Exception error) { failed = error; dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        if (!thread.Join(12000)) throw new Exception("Test dispatcher did not stop.");
        if (failed != null) throw failed;
    }

    private static void BlockingOperation()
    {
        Log.Clear();
        OnDispatcher((dispatcher, stop) => {
            var owner = new Border { IsEnabled = false };
            var watchdog = new DockerUiWatchdog(dispatcher, "QA-Main", owner, 40, 200);
            watchdog.Start();
            using (DockerUiWatchdog.Trace(dispatcher, "OuterRefresh"))
                using (DockerUiWatchdog.Trace(dispatcher, "InnerBlockedCorelCall")) Thread.Sleep(2250);
            dispatcher.BeginInvoke(new Action(() => {
                watchdog.Stop("unloaded-test"); watchdog.Stop("duplicate-stop"); stop();
            }), DispatcherPriority.ApplicationIdle);
        });
        string[] lines = Log.Snapshot();
        Check(lines.Any(line => line.Contains("heartbeat delayed") && line.Contains("OuterRefresh") && line.Contains("InnerBlockedCorelCall")),
            "Delayed heartbeat did not identify active nested synchronous callback.");
        Check(lines.Any(line => line.Contains("QA-Main#") && line.Contains("version=") && line.Contains("thread=") && line.Contains("dispatcher=")),
            "Instance/version/thread/dispatcher identity missing.");
        Check(lines.Count(line => line.Contains("Normal probe answered")) == 1 && lines.Any(line => line.Contains("heartbeat resumed")),
            "Delayed episode did not produce exactly one Normal response and recovery.");
        Check(lines.Any(line => line.Contains("IsEnabled=False") && line.Contains("IsHitTestVisible=") && line.Contains("mouse=")),
            "Dispatcher-side input state absent.");
        Check(lines.Count(line => line.Contains("synchronous operation finished")) == 2, "Long synchronous scopes were not recorded.");
        Check(lines.Count(line => line.Contains("stopped (")) == 1, "Stop should log only a running watchdog.");
        Check(Array.FindIndex(lines, line => line.Contains("Normal probe answered")) >
            Array.FindLastIndex(lines, line => line.Contains("synchronous operation finished")),
            "Normal probe ran before the blocking synchronous scopes finished.");
    }

    private static void BackgroundStarvation()
    {
        Log.Clear();
        OnDispatcher((dispatcher, stop) => {
            var watchdog = new DockerUiWatchdog(dispatcher, "QA-Orders", new Border(), 20, 100);
            watchdog.Start();
            var elapsed = Stopwatch.StartNew();
            Action normalWork = null;
            normalWork = () => {
                if (elapsed.ElapsedMilliseconds < 350)
                { Thread.Sleep(5); dispatcher.BeginInvoke(normalWork, DispatcherPriority.Normal); }
                else dispatcher.BeginInvoke(new Action(() => { watchdog.Stop("starvation-test"); stop(); }), DispatcherPriority.ApplicationIdle);
            };
            dispatcher.BeginInvoke(normalWork, DispatcherPriority.Normal);
        });
        string[] lines = Log.Snapshot();
        Check(lines.Count(line => line.Contains("Normal probe answered")) == 1, "Normal-priority starvation probe was absent or flooded.");
        Check(Array.FindIndex(lines, line => line.Contains("Normal probe answered")) >= 0 &&
            Array.FindIndex(lines, line => line.Contains("Normal probe answered")) <
            Array.FindIndex(lines, line => line.Contains("heartbeat resumed")),
            "Normal probe did not answer before the starved Background heartbeat resumed.");
        Check(lines.Any(line => line.Contains("heartbeat delayed")) && lines.Any(line => line.Contains("heartbeat resumed")),
            "Background-priority starvation/recovery was not reported.");
    }

    private static void StaleCallbacks()
    {
        Log.Clear();
        OnDispatcher((dispatcher, stop) => {
            var owner = new Border { IsHitTestVisible = false };
            var watchdog = new DockerUiWatchdog(dispatcher, "QA-Restart", owner, 20, 100);
            watchdog.Start(); Thread.Sleep(250); // queue one Background and one Normal callback, without pumping
            watchdog.Stop("old-generation"); watchdog.Start(); watchdog.Stop("new-generation");
            dispatcher.BeginInvoke(new Action(stop), DispatcherPriority.ApplicationIdle);
        });
        string[] lines = Log.Snapshot();
        Check(lines.Count(line => line.Contains("started.")) == 2 && lines.Count(line => line.Contains("stopped (")) == 2,
            "Restart lifecycle not recorded.");
        Check(!lines.Any(line => line.Contains("Normal probe answered") || line.Contains("heartbeat resumed") || line.Contains("input state changed")),
            "A stale queued callback ran after Stop/Start/Stop.");
    }

}
