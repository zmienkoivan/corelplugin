using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VanyaTools.Native
{
    public sealed class VanyaToolsTrimButton : UserControl
    {
        public VanyaToolsTrimButton() : this(null) { }

        public VanyaToolsTrimButton(object app)
        {
            CorelApp.SetHostApplication(app);
            Content = ToolbarButton.Create("✂", "Обрезать растр", () =>
            {
                if (!VanyaToolsDocker.TryRunToolbarTrim())
                    new BitmapTrimService().TrimSelected(TrimMode.TransparentPixels, TrimSides.All, 2, true);
            });
            CorelToolbarInstaller.RegisterHostedButton(this);
        }
    }

    public sealed class VanyaToolsFitFrameButton : UserControl
    {
        public VanyaToolsFitFrameButton() : this(null) { }

        public VanyaToolsFitFrameButton(object app)
        {
            CorelApp.SetHostApplication(app);
            Content = ToolbarButton.Create("▣", "Подогнать рамку под принт", () =>
            {
                if (!VanyaToolsDocker.TryRunToolbarFitFrame())
                    PrintFrameFitService.FitSelected(PrintFrameAnchor.TopCenter);
            });
            CorelToolbarInstaller.RegisterHostedButton(this);
        }
    }

    internal static class ToolbarButton
    {
        internal static Button Create(string symbol, string tooltip, Action action)
        {
            var button = new Button
            {
                Width = 29,
                Height = 27,
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(0),
                ToolTip = tooltip,
                Content = new TextBlock
                {
                    Text = symbol,
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    FontSize = 17,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            button.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception error)
                {
                    Log.Error("Toolbar action failed: " + tooltip, error);
                    MessageBox.Show(error.Message, "Vanya Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            return button;
        }
    }

    internal static class CorelToolbarInstaller
    {
        private const string DockerButton = "60cc0805-a158-44eb-a286-49061c3d963d";
        private const string TrimButton = "a624e7f3-8a23-4b1e-b2e6-58cf6725b9e4";
        private const string FitFrameButton = "96b63d8e-86b4-433f-a315-126381e35e11";
        private const string RecoveryToolbarName = "Vanya Tools";
        private const int CuiBarTop = 1;
        private static readonly List<WeakReference<UserControl>> HostedButtons =
            new List<WeakReference<UserControl>>();

        private static string VisibilityPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "toolbar-buttons-hidden.txt");

        internal static string ToggleButtons()
        {
            // The XSLT toolbar insertion is workspace-specific. A visibility
            // toggle cannot show WPF hosts which have never been created.
            if (CountHostedButtons() == 0) return RestoreButtons();
            bool show = File.Exists(VisibilityPath);
            if (show)
            {
                File.Delete(VisibilityPath);
                int shown = SetHostedButtonsVisible(true);
                Log.Info("Shown Vanya Tools action button hosts: " + shown);
                return "Кнопки действий показаны.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(VisibilityPath));
            File.WriteAllText(VisibilityPath, "hidden");
            int hidden = SetHostedButtonsVisible(false);
            Log.Info("Hidden Vanya Tools action button hosts: " + hidden);
            return "Кнопки действий скрыты.";
        }

        internal static void ApplySavedVisibility()
        {
            try
            {
                int hosts = SetHostedButtonsVisible(!File.Exists(VisibilityPath));
                Log.Info("Vanya Tools toolbar hosts: " + hosts + ", hidden=" + File.Exists(VisibilityPath));
                if (hosts == 0)
                    Log.Info("Vanya Tools toolbar: no action hosts in this workspace; use Restore buttons in settings.");
            }
            catch (Exception error) { Log.Error("Could not restore toolbar button visibility.", error); }
        }

        internal static string RestoreButtons()
        {
            if (File.Exists(VisibilityPath)) File.Delete(VisibilityPath);
            dynamic app = CorelApp.Get();
            dynamic bar = FindRecoveryToolbar((object)app);
            bool created = bar == null;
            if (created)
            {
                // A dedicated persistent toolbar avoids depending on the GUID
                // or localized name of the user's standard command bar.
                bar = app.FrameWork.CommandBars.Add(RecoveryToolbarName, CuiBarTop, false);
                if (bar == null) throw new InvalidOperationException("Corel не создал панель Vanya Tools.");
            }
            int added = 0;
            foreach (string id in new[] { DockerButton, TrimButton, FitFrameButton })
                if (EnsureButton((object)bar.Controls, id)) added++;
            bar.Visible = true;
            if (!Convert.ToBoolean(bar.Visible))
                throw new InvalidOperationException("Corel не показал панель Vanya Tools. Проверьте её в меню «Окно → Панели инструментов».");
            SetHostedButtonsVisible(true);
            Log.Info("Vanya Tools toolbar restored: created=" + created + ", added=" + added);
            return "Панель Vanya Tools показана: открытие докера, обрезка и рамка. Её можно переместить в удобное место.";
        }

        private static object FindRecoveryToolbar(object appObject)
        {
            dynamic app = appObject;
            foreach (dynamic bar in app.FrameWork.CommandBars)
                if (String.Equals(Convert.ToString(bar.Name), RecoveryToolbarName, StringComparison.OrdinalIgnoreCase) &&
                    !Convert.ToBoolean(bar.BuiltIn)) return (object)bar;
            return null;
        }

        private static bool EnsureButton(object controlsObject, string id)
        {
            dynamic controls = controlsObject;
            for (int i = 1; i <= Convert.ToInt32(controls.Count); i++)
                if (String.Equals(GetOurControlId(controls.Item[i]), id, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                // Index=0 is the SDK's default insertion position. Only our
                // recovery toolbar is modified by this operation.
                dynamic added = controls.Add(id, 0, false);
                if (added == null || !String.Equals(GetOurControlId(added), id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Corel не вернул зарегистрированную команду.");
                try { added.Tag = "VanyaTools:" + id; } catch { }
                Log.Info("Vanya Tools recovery toolbar: added " + id);
                return true;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Не удалось добавить кнопку Vanya Tools. " +
                    "Закройте Corel и установите полный пакет обновления через INSTALL.bat, затем повторите восстановление.", error);
            }
        }

        private static int CountHostedButtons()
        {
            int count = 0;
            for (int i = HostedButtons.Count - 1; i >= 0; i--)
            {
                if (!HostedButtons[i].TryGetTarget(out UserControl host)) { HostedButtons.RemoveAt(i); continue; }
                if (host.IsLoaded) count++;
            }
            return count;
        }

        internal static void RegisterHostedButton(UserControl host)
        {
            HostedButtons.Add(new WeakReference<UserControl>(host));
            host.Visibility = File.Exists(VisibilityPath) ? Visibility.Collapsed : Visibility.Visible;
            host.Loaded += (_, __) =>
                host.Visibility = File.Exists(VisibilityPath) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static int SetHostedButtonsVisible(bool visible)
        {
            int changed = 0;
            for (int i = HostedButtons.Count - 1; i >= 0; i--)
            {
                UserControl host;
                if (!HostedButtons[i].TryGetTarget(out host)) { HostedButtons.RemoveAt(i); continue; }
                var state = visible ? Visibility.Visible : Visibility.Collapsed;
                if (host.Dispatcher.CheckAccess()) host.Visibility = state;
                else host.Dispatcher.BeginInvoke(new Action(() => host.Visibility = state));
                changed++;
            }
            return changed;
        }

        // v1.0.117 inserted controls through COM as well as UserUI.xslt. On some
        // workspaces COM inserted one over the New Document button. Remove only
        // duplicate Vanya Tools controls, keeping the rightmost toolbar group.
        internal static void RemoveLegacyDuplicates()
        {
            try
            {
                string markerPath = GetMigrationMarkerPath();
                if (File.Exists(markerPath)) return;

                dynamic app = CorelApp.Get();
                dynamic standard = null;
                foreach (dynamic bar in app.FrameWork.CommandBars)
                {
                    if (String.Equals((string)bar.Name, "Standard", StringComparison.OrdinalIgnoreCase))
                    {
                        standard = bar;
                        break;
                    }
                }
                if (standard == null)
                {
                    Log.Info("Standard command bar was not found; legacy toolbar cleanup skipped.");
                    return;
                }

                dynamic controls = standard.Controls;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int found = 0;
                int removed = 0;
                for (int i = (int)controls.Count; i >= 1; i--)
                {
                    dynamic control = controls.Item[i];
                    string id = GetOurControlId(control);
                    if (id == null) continue;
                    found++;
                    if (seen.Add(id)) continue;
                    controls.Remove(i);
                    removed++;
                }

                if (found == 0) return;
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath));
                File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
                Log.Info("Legacy Standard toolbar duplicates removed: " + removed);
            }
            catch (Exception error)
            {
                Log.Error("Could not remove legacy Standard toolbar duplicates.", error);
            }
        }

        private static string GetOurControlId(dynamic control)
        {
            try
            {
                string id = NormalizeId((string)control.ID);
                if (IsOurId(id)) return id;
            }
            catch { }
            try
            {
                string tag = (string)control.Tag;
                const string prefix = "VanyaTools:";
                if (tag != null && tag.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string id = NormalizeId(tag.Substring(prefix.Length));
                    if (IsOurId(id)) return id;
                }
            }
            catch { }
            try
            {
                string caption = (string)control.Caption;
                if (String.Equals(caption, "Vanya Tools", StringComparison.OrdinalIgnoreCase)) return DockerButton;
                if (String.Equals(caption, "Vanya Tools: обрезать растр", StringComparison.OrdinalIgnoreCase)) return TrimButton;
                if (String.Equals(caption, "Vanya Tools: подогнать рамку", StringComparison.OrdinalIgnoreCase)) return FitFrameButton;
            }
            catch { }
            return null;
        }

        private static string NormalizeId(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return null;
            if (id.StartsWith("guid://", StringComparison.OrdinalIgnoreCase)) id = id.Substring(7);
            return id.Trim('{', '}', ' ');
        }

        private static bool IsOurId(string id)
        {
            return String.Equals(id, DockerButton, StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(id, TrimButton, StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(id, FitFrameButton, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMigrationMarkerPath()
        {
            string corelVersion = Process.GetCurrentProcess().MainModule.FileVersionInfo.FileMajorPart.ToString();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "toolbar-117-cleaned-" + corelVersion + ".txt");
        }
    }
}
