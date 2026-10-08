using System;
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

        internal static void Ensure()
        {
            try
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
                {
                    Log.Info("Standard command bar was not found; toolbar buttons remain available through workspace customization.");
                    return;
                }

                TryAdd(standard.Controls, DockerButton);
                TryAdd(standard.Controls, TrimButton);
                TryAdd(standard.Controls, FitFrameButton);
            }
            catch (Exception error)
            {
                // A toolbar API failure must never prevent the docker from loading.
                Log.Error("Could not update the Standard toolbar.", error);
            }
        }

        private static void TryAdd(dynamic controls, string id)
        {
            try
            {
                string tag = "VanyaTools:" + id;
                for (int i = 1; i <= (int)controls.Count; i++)
                {
                    dynamic control = controls.Item[i];
                    try
                    {
                        if (String.Equals((string)control.ID, id, StringComparison.OrdinalIgnoreCase) ||
                            String.Equals((string)control.Tag, tag, StringComparison.Ordinal))
                            return;
                    }
                    catch { }
                }

                dynamic added = controls.Add(id, 0, false);
                try { added.Tag = tag; } catch { }
                if (String.Equals(id, DockerButton, StringComparison.OrdinalIgnoreCase))
                {
                    try { added.Caption = "Vanya Tools"; } catch { }
                }
                Log.Info("Added Standard toolbar control " + id);
            }
            catch (Exception error)
            {
                Log.Error("Could not add toolbar control " + id, error);
            }
        }
    }
}
