using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace VanyaTools.Native
{
    // One lease per connected Vanya root. Multiple roots can share an HWND;
    // releasing one must not restore hardware rendering while another uses it.
    internal sealed class SoftwareRenderLease : IDisposable
    {
        private sealed class State { internal RenderMode Previous; internal int Users; }
        private static readonly ConditionalWeakTable<HwndTarget, State> States = new ConditionalWeakTable<HwndTarget, State>();
        private HwndTarget _target;
        private readonly State _state;
        internal HwndTarget Target => _target;
        private SoftwareRenderLease(HwndTarget target, State state) { _target = target; _state = state; }

        internal static SoftwareRenderLease Acquire(HwndTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Dispatcher.VerifyAccess();
            var state = States.GetValue(target, t => new State { Previous = t.RenderMode });
            try { target.RenderMode = RenderMode.SoftwareOnly; }
            catch { if (state.Users == 0) States.Remove(target); throw; }
            state.Users++;
            return new SoftwareRenderLease(target, state);
        }
        public void Dispose()
        {
            HwndTarget target = _target;
            if (target == null) return;
            target.Dispatcher.VerifyAccess(); _target = null;
            if (--_state.Users != 0) return;
            States.Remove(target);
            try { if (target.RenderMode == RenderMode.SoftwareOnly) target.RenderMode = _state.Previous; }
            catch (ObjectDisposedException) { /* The Corel host may already have destroyed this target. */ }
        }
    }

    internal sealed class DockerRenderingCompatibility
    {
        private static readonly string PreferencePath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "software-rendering.txt");
        private static readonly object Gate = new object();
        private static readonly ConditionalWeakTable<FrameworkElement, DockerRenderingCompatibility> Attached =
            new ConditionalWeakTable<FrameworkElement, DockerRenderingCompatibility>();
        private static readonly List<WeakReference<DockerRenderingCompatibility>> Roots = new List<WeakReference<DockerRenderingCompatibility>>();
        private static readonly List<WeakReference<CheckBox>> Selectors = new List<WeakReference<CheckBox>>();
        private static bool _enabled = LoadPreference(PreferencePath);
        private readonly FrameworkElement _owner;
        private SoftwareRenderLease _lease;
        internal static bool Enabled => Volatile.Read(ref _enabled);

        private DockerRenderingCompatibility(FrameworkElement owner)
        {
            _owner = owner;
            owner.Loaded += (_, __) => Refresh();
            owner.Unloaded += (_, __) => Release();
            PresentationSource.AddSourceChangedHandler(owner, (_, __) => Refresh());
            lock (Gate)
            {
                Roots.RemoveAll(item => !item.TryGetTarget(out _));
                Roots.Add(new WeakReference<DockerRenderingCompatibility>(this));
            }
            Refresh();
        }
        internal static void Attach(FrameworkElement owner)
        { if (owner != null) Attached.GetValue(owner, element => new DockerRenderingCompatibility(element)); }

        private void Refresh()
        {
            _owner.Dispatcher.VerifyAccess();
            try
            {
                var source = PresentationSource.FromVisual(_owner) as HwndSource;
                HwndTarget target = Enabled && _owner.IsLoaded && source != null && !source.IsDisposed ? source.CompositionTarget : null;
                if (ReferenceEquals(target, _lease?.Target)) return;
                Release();
                if (target != null)
                {
                    _lease = SoftwareRenderLease.Acquire(target);
                    Log.Info("Vanya WPF software rendering enabled: root=" + _owner.GetType().Name +
                        ", hwnd=" + source.Handle.ToInt64() + ", tier=" + (RenderCapability.Tier >> 16) +
                        ", processMode=" + RenderOptions.ProcessRenderMode + ".");
                }
            }
            catch (Exception error) { Log.Error("Could not apply Vanya WPF rendering compatibility.", error); }
        }
        private void Release()
        {
            var previous = _lease; _lease = null;
            if (previous == null) return;
            try { previous.Dispose(); Log.Info("Vanya WPF software rendering lease released: root=" + _owner.GetType().Name + "."); }
            catch (Exception error) { Log.Error("Could not restore Vanya WPF window rendering.", error); }
        }

        internal static FrameworkElement CreateSettingsView(Action<string, bool> status)
        {
            var panel = new StackPanel { Margin = new Thickness(4, 4, 4, 6) };
            var check = new CheckBox { Content = new TextBlock { Text = "Программная отрисовка Vanya Tools", TextWrapping = TextWrapping.Wrap },
                IsChecked = Enabled, FontSize = 11, Margin = new Thickness(0, 4, 0, 2),
                ToolTip = "Режим совместимости для проверки зависания интерфейса. Меняет только WPF-окна Vanya Tools, не настройки графики Corel или Windows." };
            lock (Gate) Selectors.Add(new WeakReference<CheckBox>(check));
            RoutedEventHandler changed = (_, __) => {
                bool next = check.IsChecked == true;
                if (next == Enabled) return;
                try
                {
                    SetEnabled(next, PreferencePath);
                    status?.Invoke(next ? "Программная отрисовка Vanya Tools включена. Для проверки зависания полностью перезапустите CorelDRAW."
                        : "Обычная отрисовка Vanya Tools восстановлена.", false);
                }
                catch (Exception error)
                { check.IsChecked = Enabled; Log.Error("Could not save rendering compatibility.", error); status?.Invoke("Отрисовка: " + error.Message, true); }
            };
            check.Checked += changed; check.Unchecked += changed;
            panel.Children.Add(check);
            panel.Children.Add(new TextBlock { Text = "Для ПК, где панель замирает целиком. Выбор сохраняется на этом компьютере. Может увеличить нагрузку на процессор. После включения перезапустите Corel для проверки.",
                FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
            return panel;
        }
        private static void RefreshAll()
        {
            var roots = new List<DockerRenderingCompatibility>(); var selectors = new List<CheckBox>();
            lock (Gate)
            {
                Roots.RemoveAll(item => !item.TryGetTarget(out _)); Selectors.RemoveAll(item => !item.TryGetTarget(out _));
                foreach (var item in Roots) if (item.TryGetTarget(out var root)) roots.Add(root);
                foreach (var item in Selectors) if (item.TryGetTarget(out var selector)) selectors.Add(selector);
            }
            foreach (var root in roots) OnUi(root._owner, root.Refresh);
            foreach (var selector in selectors) OnUi(selector, () => selector.IsChecked = Enabled);
        }
        internal static void SetEnabled(bool enabled, string preferencePath)
        {
            SavePreference(preferencePath, enabled);
            Volatile.Write(ref _enabled, enabled); RefreshAll();
        }
        private static void OnUi(FrameworkElement owner, Action work)
        {
            if (owner.Dispatcher.HasShutdownStarted) return;
            if (owner.Dispatcher.CheckAccess()) work(); else owner.Dispatcher.BeginInvoke(work);
        }
        internal static bool LoadPreference(string path)
        {
            try { return File.Exists(path) && String.Equals(File.ReadAllText(path).Trim(), "software", StringComparison.Ordinal); }
            catch (Exception error) { Log.Error("Could not read WPF rendering compatibility.", error); return false; }
        }
        internal static void SavePreference(string path, bool enabled)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, enabled ? "software" : "default"); }
    }
}
