using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    // Static instructions are attached explicitly. Live messages and labels
    // keep their original controls and are never classified by their text.
    internal static class DockerHelp
    {
        private sealed class HelpRow : Grid
        {
            private readonly Button _button;
            private readonly Popup _popup;
            private readonly TextBox _text;
            private string _help;

            internal HelpRow(FrameworkElement anchor, string help)
            {
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (anchor != null)
                {
                    Margin = anchor.Margin;
                    anchor.Margin = new Thickness(0);
                    Children.Add(anchor);
                }
                else Margin = new Thickness(0, 0, 0, 3);
                _button = new Button { Content = "?", Width = 22, Height = 22,
                    MinWidth = 22, MinHeight = 22, Padding = new Thickness(0),
                    FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(_button, "Справка");
                Grid.SetColumn(_button, 1);
                Children.Add(_button);
                _text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                    Foreground = Brushes.Black, FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12, MaxHeight = 360,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                _popup = new Popup { PlacementTarget = _button, Placement = PlacementMode.Bottom,
                    StaysOpen = false, AllowsTransparency = true,
                    Child = new Border { Width = 380, Padding = new Thickness(12),
                        Background = Brushes.White, BorderBrush = Brushes.SlateGray,
                        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _text } };
                _button.Click += (_, __) => _popup.IsOpen = !_popup.IsOpen;
                _popup.Opened += (_, __) => _text.Focus();
                _text.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape) { _popup.IsOpen = false; _button.Focus(); e.Handled = true; }
                };
                Unloaded += (_, __) => _popup.IsOpen = false;
                Append(help);
            }

            internal void Append(string help)
            {
                _help = String.IsNullOrEmpty(_help) ? help : _help + "\n\n" + help;
                _text.Text = _help;
                _button.ToolTip = new TextBlock { Text = _help, TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 380, FontSize = 12 };
                AutomationProperties.SetHelpText(_button, _help);
            }
        }

        internal static void Add(Panel panel, string help)
        {
            if (String.IsNullOrWhiteSpace(help)) return;
            int index = panel.Children.Count - 1;
            if (index >= 0 && panel.Children[index] is HelpRow existing)
            { existing.Append(help); return; }
            var anchor = index >= 0 ? panel.Children[index] as FrameworkElement : null;
            if (anchor != null) panel.Children.RemoveAt(index);
            var row = new HelpRow(anchor, help);
            if (anchor != null)
            {
                Grid.SetRow(row, Grid.GetRow(anchor)); Grid.SetColumn(row, Grid.GetColumn(anchor));
                Grid.SetRowSpan(row, Grid.GetRowSpan(anchor)); Grid.SetColumnSpan(row, Grid.GetColumnSpan(anchor));
                DockPanel.SetDock(row, DockPanel.GetDock(anchor));
            }
            panel.Children.Add(row);
        }
    }
}
