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

        // Called only by the explicit Settings button. Never rearrange controls
        // automatically: the user may have customized their Corel workspace.
        internal static string AddMissingToStandardToolbar()
        {
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
                throw new InvalidOperationException("Стандартная панель Corel не найдена.");

            dynamic controls = standard.Controls;
            int added = 0;
            foreach (string id in new[] { DockerButton, TrimButton, FitFrameButton })
            {
                bool exists = false;
                for (int i = 1; i <= (int)controls.Count; i++)
                {
                    if (String.Equals(GetOurControlId(controls.Item[i]), id,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists) continue;

                dynamic control = controls.Add(id, (int)controls.Count + 1, false);
                try { control.Tag = "VanyaTools:" + id; } catch { }
                added++;
            }
            return added == 0
                ? "Кнопки уже есть на стандартной панели."
                : "Добавлено кнопок: " + added + ". Их можно переместить в настройках Corel.";
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
