using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class Log { public static void Info(string value) { } public static void Error(string value, Exception error) { Console.Error.WriteLine(value + " " + error.GetType().Name); } }
}

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static HwndSource HiddenSource(string name) => new HwndSource(new HwndSourceParameters(name) { Width = 16, Height = 16, WindowStyle = 0 });
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vanya-RenderingChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string preference = Path.Combine(root, "mode.txt");
        RenderMode processMode = RenderOptions.ProcessRenderMode;
        try
        {
            Preferences(preference); Leases(); Lifecycle(preference);
            Check(RenderOptions.ProcessRenderMode == processMode, "Process-wide render mode was changed");
            Console.WriteLine("Rendering compatibility checks passed: " + _checks + ". Owned hidden WPF HWNDs only; no Corel or graphics registry changes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            DockerRenderingCompatibility.SetEnabled(false, preference);
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root, false);
        }
    }
    private static void Preferences(string path)
    {
        Check(!DockerRenderingCompatibility.LoadPreference(path), "Missing preference must default to ordinary rendering");
        DockerRenderingCompatibility.SavePreference(path, true);
        Check(DockerRenderingCompatibility.LoadPreference(path), "Software preference not restored");
        DockerRenderingCompatibility.SavePreference(path, false);
        Check(!DockerRenderingCompatibility.LoadPreference(path), "Default preference not restored");
        File.WriteAllText(path, "corrupt");
        Check(!DockerRenderingCompatibility.LoadPreference(path), "Invalid preference must not force software rendering");
    }
    private static void Leases()
    {
        using (HwndSource first = HiddenSource("Vanya rendering check A"))
        using (HwndSource other = HiddenSource("Vanya rendering check B"))
        {
            HwndTarget target = first.CompositionTarget, independent = other.CompositionTarget;
            RenderMode original = target.RenderMode, otherMode = independent.RenderMode;
            var one = SoftwareRenderLease.Acquire(target);
            Check(target.RenderMode == RenderMode.SoftwareOnly && independent.RenderMode == otherMode, "Lease changed an unrelated HWND");
            var two = SoftwareRenderLease.Acquire(target); one.Dispose(); one.Dispose();
            Check(target.RenderMode == RenderMode.SoftwareOnly, "One root disabled software mode while another root still used the same HWND");
            two.Dispose();
            Check(target.RenderMode == original, "Last lease did not restore original rendering");
            target.RenderMode = RenderMode.SoftwareOnly;
            using (SoftwareRenderLease.Acquire(target)) { }
            Check(target.RenderMode == RenderMode.SoftwareOnly, "Pre-existing software mode was changed");
            target.RenderMode = original;
            var foreign = SoftwareRenderLease.Acquire(target); target.RenderMode = RenderMode.Default; foreign.Dispose();
            Check(target.RenderMode == RenderMode.Default, "Lease overrode the host's subsequent mode change");
            bool rejected = false;
            var thread = new Thread(() => { try { using (SoftwareRenderLease.Acquire(target)) { } } catch (InvalidOperationException) { rejected = true; } });
            thread.Start(); Check(thread.Join(5000) && rejected, "Cross-thread HWND mutation was not rejected");
        }
    }
    private static void Lifecycle(string preference)
    {
        DockerRenderingCompatibility.SetEnabled(false, preference);
        using (HwndSource first = HiddenSource("Vanya controller A"))
        using (HwndSource second = HiddenSource("Vanya controller B"))
        {
            var one = new Border(); var two = new Border();
            var content = new StackPanel(); content.Children.Add(one); content.Children.Add(two);
            DockerRenderingCompatibility.Attach(one); DockerRenderingCompatibility.Attach(two); DockerRenderingCompatibility.Attach(one);
            RenderMode modeA = first.CompositionTarget.RenderMode, modeB = second.CompositionTarget.RenderMode;
            first.RootVisual = content; Pump();
            Check(one.IsLoaded && two.IsLoaded && first.CompositionTarget.RenderMode == modeA, "Default mode changed or hidden source did not load");
            DockerRenderingCompatibility.SetEnabled(true, preference);
            Check(first.CompositionTarget.RenderMode == RenderMode.SoftwareOnly && second.CompositionTarget.RenderMode == modeB, "Preference was not applied only to attached source");
            content.Children.Remove(one); Pump();
            Check(first.CompositionTarget.RenderMode == RenderMode.SoftwareOnly, "Unloading one attached root released another's HWND lease");
            first.RootVisual = null; Pump();
            Check(first.CompositionTarget.RenderMode == modeA, "Unloading last root did not restore host source");
            second.RootVisual = content; Pump();
            Check(second.CompositionTarget.RenderMode == RenderMode.SoftwareOnly, "Reparented root did not apply software preference to new source");
            bool failed = false;
            try { DockerRenderingCompatibility.SetEnabled(false, Path.Combine(preference, "invalid.txt")); }
            catch (IOException) { failed = true; }
            Check(failed && DockerRenderingCompatibility.Enabled && second.CompositionTarget.RenderMode == RenderMode.SoftwareOnly,
                "Failed preference save changed live rendering");
            DockerRenderingCompatibility.SetEnabled(false, preference);
            Check(second.CompositionTarget.RenderMode == modeB && first.CompositionTarget.RenderMode == modeA, "Disabling compatibility did not restore modes");
            second.RootVisual = null; Pump();
        }
    }
}
