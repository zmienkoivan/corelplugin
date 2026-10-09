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

        // Version 1.0.128 inserted controls into the Standard bar with this tag.
        // Remove only those controls on request; leave all other customizations.
        internal static string RemoveV128AddedControls()
        {
            dynamic standard = GetStandardBar();
            if (standard == null)
                throw new InvalidOperationException("Стандартная панель Corel не найдена.");

            dynamic controls = standard.Controls;
            int removed = 0;
            for (int i = (int)controls.Count; i >= 1; i--)
            {
                dynamic control = controls.Item[i];
                try
                {
                    string tag = (string)control.Tag;
                    if (tag == null || !tag.StartsWith("VanyaTools:", StringComparison.Ordinal))
                        continue;
                    string id = tag.Substring("VanyaTools:".Length).Trim('{', '}');
                    if (!IsOurId(id)) continue;
                    controls.Remove(i);
                    removed++;
                }
                catch (Exception error) { Log.Error("Could not remove tagged toolbar control.", error); }
            }
            return removed == 0
                ? "Кнопок версии 1.0.128 на стандартной панели нет."
                : "Убрано кнопок версии 1.0.128: " + removed + ".";
        }

        internal static string RemoveOldHostedButtons()
        {
            dynamic standard = GetStandardBar();
            if (standard == null)
                throw new InvalidOperationException("Стандартная панель Corel не найдена.");

            dynamic controls = standard.Controls;
            int removed = 0;
            for (int i = (int)controls.Count; i >= 1; i--)
            {
                string id = GetOurControlId(controls.Item[i]);
                if (!String.Equals(id, TrimButton, StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(id, FitFrameButton, StringComparison.OrdinalIgnoreCase))
                    continue;
                controls.Remove(i);
                removed++;
            }
            return "Убрано старых кнопок: " + removed + ". Новые команды — в категории «Плагины».";
        }

        internal static void ApplyDockerIcon()
        {
            string iconPath = Path.Combine(Path.GetDirectoryName(typeof(VanyaToolsDocker).Assembly.Location),
                "VanyaToolsIcon.ico");
            if (!File.Exists(iconPath))
            {
                Log.Info("Docker icon is missing: " + iconPath);
                return;
            }
            try
            {
                int applied = 0;
                foreach (dynamic bar in CorelApp.Get().FrameWork.CommandBars)
                {
                    try
                    {
                        dynamic controls = bar.Controls;
                        for (int i = 1; i <= (int)controls.Count; i++)
                        {
                            dynamic control = controls.Item[i];
                            if (!IsDockerControl(control)) continue;
                            try { control.SetCustomIcon(iconPath); applied++; }
                            catch (Exception error) { Log.Error("Could not set Docker button icon.", error); }
                        }
                    }
                    catch (Exception error) { Log.Error("Could not inspect command bar for Docker icon.", error); }
                }
                Log.Info("Docker icon applied to " + applied + " controls.");
            }
            catch (Exception error) { Log.Error("Could not apply Docker icon.", error); }
        }

        private static dynamic GetStandardBar()
        {
            foreach (dynamic bar in CorelApp.Get().FrameWork.CommandBars)
                if (String.Equals((string)bar.Name, "Standard", StringComparison.OrdinalIgnoreCase))
                    return bar;
            return null;
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
                string id = (string)control.ID;
                if (id != null) id = id.Trim('{', '}');
                if (IsOurId(id)) return id;
            }
            catch { }
            try
            {
                string tag = (string)control.Tag;
                const string prefix = "VanyaTools:";
                if (tag != null && tag.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string id = tag.Substring(prefix.Length);
                    id = id.Trim('{', '}');
                    if (IsOurId(id)) return id;
                }
            }
            catch { }
            return null;
        }

        private static bool IsDockerControl(dynamic control)
        {
            if (String.Equals(GetOurControlId(control), DockerButton,
                StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                return String.Equals((string)control.Caption, "Vanya Tools",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
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
