using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class Log
    {
        internal static readonly List<string> Lines = new List<string>();
        public static void Info(string value) { Lines.Add(value); }
        public static void Error(string value, Exception error) { throw new Exception(value, error); }
    }
}

public static class DockerRenderRegression
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < 5500) Pump(20);
        Check(condition(), failure);
    }
    [STAThread]
    private static int Main() => Run();
    // Also run from the established release watchdog check, so this regression
    // cannot be missed by the existing GitHub release workflow.
    public static int Run()
    {
        using (var deadline = new Timer(_ => { Console.Error.WriteLine("Render fixture exceeded 35 seconds."); Environment.Exit(91); }, null, 35000, -1))
        try
        {
            Check(DockerRenderRecovery.IsSupported, "Installed .NET Framework WPF probe is unsupported");
            IdleAndDetached(); LostNotifications(false); LostNotifications(true); WatchdogRecoversLivePanel();
            Console.WriteLine("Docker render recovery checks passed: " + _checks + ". Real MIL notifications withheld on owned off-screen HWNDs; no Corel or document changes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void IdleAndDetached()
    {
        var detached = new Border();
        var recovery = new DockerRenderRecovery(Dispatcher.CurrentDispatcher, detached, () => false, 60, 20);
        recovery.Check(); Pump(80); recovery.Check();
        Check(!recovery.IsPolling && recovery.RecoveryCount == 0, "Detached root started render polling");
        using (var fixture = new Fixture(false, false))
        {
            var idle = new DockerRenderRecovery(Dispatcher.CurrentDispatcher, fixture.Root, () => false, 60, 20);
            idle.Check(); Pump(100); idle.Check();
            Check(!idle.IsPolling && idle.RecoveryCount == 0, "An idle, fully committed source started recovery");
            bool rejected = false;
            var other = new Thread(() => { try { idle.Check(); } catch (InvalidOperationException) { rejected = true; } });
            other.Start(); Check(other.Join(1500) && rejected, "Cross-thread render probing was accepted");
            idle.Stop(); idle.Stop();
        }
    }
    private static void LostNotifications(bool software)
    {
        using (var fixture = new Fixture(software, true))
        {
            bool busy = true;
            var recovery = new DockerRenderRecovery(Dispatcher.CurrentDispatcher, fixture.Root, () => busy, 80, 20);
            fixture.Block = true;
            fixture.Text.Text = "A changed frame";
            fixture.Root.InvalidateVisual();
            Until(() => fixture.Dropped > 0 && fixture.Waiting && fixture.Pending, "Did not reproduce lost presentation notification");
            long frozenCommit = fixture.Commit;
            int heartbeats = 0;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            heartbeat.Tick += (_, __) => heartbeats++;
            heartbeat.Start(); Pump(140); heartbeat.Stop();
            Check(heartbeats >= 3 && fixture.Commit == frozenCommit && fixture.Pending,
                "Reproduction must have a live dispatcher and a stalled uncommitted frame, as in the user dump");
            recovery.Check(); Pump(120); recovery.Check();
            Check(recovery.RecoveryCount == 0 && fixture.Commit == frozenCommit, "Recovery ran inside an active native operation");
            busy = false; recovery.Check(); recovery.Check();
            Check(recovery.RecoveryCount == 0, "A transient presentation wait was treated as a hang");
            Pump(120); recovery.Check();
            Check(recovery.RecoveryCount > 0 && recovery.IsPolling && fixture.Commit != frozenCommit,
                "Real queued completion packets did not resume frame commits");
            long firstRecoveredCommit = fixture.Commit;
            int firstDropped = fixture.Dropped;
            Pump(350);
            Check(fixture.Dropped > firstDropped && recovery.RecoveryCount > 1 && fixture.Commit != firstRecoveredCommit,
                "Further lost notifications froze the source again");
            Check(ReferenceEquals(fixture.Source.RootVisual, fixture.Root), "Recovery recreated the root visual");
            Check(fixture.Editor.Text == "Keep these user settings" && fixture.Editor.SelectionStart == 5 && fixture.Editor.SelectionLength == 5,
                "Recovery changed editor values or selection");
            Check(!fixture.Command.IsEnabled && fixture.CommandClicks == 0, "Recovery enabled or replayed a command");
            Check(fixture.Source.CompositionTarget.RenderMode == (software ? RenderMode.SoftwareOnly : RenderMode.Default),
                "Recovery changed the host's rendering mode");
            fixture.Unblock();
            fixture.Source.RootVisual = null; Pump(80);
            Check(!recovery.IsPolling, "Unloaded root retained its polling timer");
            int afterUnload = recovery.RecoveryCount; Pump(80); recovery.Check();
            Check(recovery.RecoveryCount == afterUnload, "A stale timer ran after unload");
            Check(Log.Lines.Exists(line => line.Contains("Real WPF completion packets drained")), "Recovery evidence was not logged");
        }
    }
    private static void WatchdogRecoversLivePanel()
    {
        using (var fixture = new Fixture(true, true))
        {
            var watchdog = new DockerUiWatchdog(Dispatcher.CurrentDispatcher, "RenderIntegration", fixture.Root, 40, 200);
            watchdog.Start();
            var recovery = (DockerRenderRecovery)typeof(DockerUiWatchdog).GetField("_renderRecovery", All).GetValue(watchdog);
            try
            {
                fixture.Block = true; fixture.Text.Text = "Watchdog render integration"; fixture.Root.InvalidateVisual();
                Until(() => fixture.Dropped > 0 && fixture.Waiting && fixture.Pending, "Integration did not lose a render notification");
                long frozen = fixture.Commit;
                Until(() => recovery.RecoveryCount > 0, "A live panel watchdog did not recover the render channel automatically");
                Check(fixture.Commit != frozen && recovery.IsPolling, "Automatic recovery did not advance the committed frame");
                Check(Log.Lines.Exists(line => line.Contains("RenderIntegration") && line.Contains("started.")), "Live watchdog was not running");
                Check(!Log.Lines.Exists(line => line.Contains("RenderIntegration") && line.Contains("heartbeat delayed")),
                    "The reproduced render stall must not be misdiagnosed as a blocked UI dispatcher");
            }
            finally { watchdog.Stop("integration complete"); fixture.Unblock(); }
            Check(!recovery.IsPolling, "Stopping watchdog left render recovery active");
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly HwndSource Source;
        internal readonly Border Root;
        internal readonly TextBlock Text = new TextBlock { Text = "Before" };
        internal readonly TextBox Editor = new TextBox { Text = "Keep these user settings" };
        internal readonly Button Command = new Button { Content = "No command replay", IsEnabled = false };
        internal int CommandClicks, Dropped;
        internal bool Block;
        private readonly object _context;
        private readonly IntPtr _notificationHwnd;
        private readonly uint _milMessage;
        private object Field(string name) => _context.GetType().GetField(name, All).GetValue(_context);
        internal long Commit => (long)Field("_lastCommitTime");
        internal bool Waiting => Field("_interlockState").ToString() == "WaitingForResponse";
        internal bool Pending => (bool)Field("_needToCommitChannel");
        internal Fixture(bool software, bool animated)
        {
            Source = new HwndSource(new HwndSourceParameters("Vanya isolated render regression") {
                Width = 80, Height = 80, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            if (software) Source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            var panel = new StackPanel(); panel.Children.Add(Text); panel.Children.Add(Editor); panel.Children.Add(Command);
            Root = new Border { Background = System.Windows.Media.Brushes.White, Child = panel };
            Command.Click += (_, __) => CommandClicks++;
            Source.RootVisual = Root;
            if (animated) Text.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.5, 1, TimeSpan.FromSeconds(1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            Pump(250);
            Editor.Select(5, 5);
            _context = typeof(Dispatcher).GetField("_reserved0", All).GetValue(Dispatcher.CurrentDispatcher);
            object notification = Field("_notificationWindow");
            object wrapper = notification.GetType().GetField("_hwndNotification", All).GetValue(notification);
            // .NET Framework protects this wrapper in SecurityCriticalDataClass.
            PropertyInfo value = wrapper.GetType().GetProperty("Value", All);
            if (value != null) wrapper = value.GetValue(wrapper);
            _notificationHwnd = (IntPtr)wrapper.GetType().GetProperty("Handle", All).GetValue(wrapper);
            _milMessage = RegisterWindowMessage("MilChannelNotify");
            ComponentDispatcher.ThreadFilterMessage += Filter;
        }
        private void Filter(ref MSG message, ref bool handled)
        {
            if (Block && message.hwnd == _notificationHwnd && message.message == _milMessage) { Dropped++; handled = true; }
        }
        internal void Unblock()
        {
            Block = false;
            _context.GetType().GetMethod("NotifyChannelMessage", All).Invoke(_context, null);
            Text.BeginAnimation(UIElement.OpacityProperty, null);
            Pump(30);
        }
        public void Dispose()
        {
            Unblock(); ComponentDispatcher.ThreadFilterMessage -= Filter;
            Source.RootVisual = null; Pump(30); Source.Dispose();
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string value);
}
