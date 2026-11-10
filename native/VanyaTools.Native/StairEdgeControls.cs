using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VanyaTools.Native
{
    internal sealed class StairEdgeControls : StackPanel
    {
        internal readonly CheckBox Audit, Repair;
        internal readonly Slider Strength;
        internal readonly TextBlock Note;

        internal StairEdgeControls(bool repair, bool audit, int strength, Action changed)
        {
            var toggles = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
            Audit = new CheckBox { Content = "Подсветить ступени", IsChecked = audit, FontSize = 11,
                ToolTip = "Отмечает оранжевым резкие ступени края. Подсветка видна только в предпросмотре." };
            Repair = new CheckBox { Content = "Сгладить лесенку", IsChecked = repair, FontSize = 11,
                ToolTip = "Сглаживает найденные ступени края, оставляя прямые участки и одиночные углы." };
            toggles.Children.Add(Audit); toggles.Children.Add(Repair); Children.Add(toggles);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
            var label = new TextBlock { Text = "Сила", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0) };
            row.Children.Add(label);
            var number = new TextBlock { Width = 18, TextAlignment = TextAlignment.Right, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Text = strength.ToString() };
            DockPanel.SetDock(number, Dock.Right); row.Children.Add(number);
            Strength = new Slider { Minimum = 1, Maximum = 3, Value = strength,
                TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Ищет короткие ступени по краю. Большая сила захватывает более длинные ступени." };
            System.Windows.Automation.AutomationProperties.SetName(Strength, "Сила сглаживания лесенки");
            row.Children.Add(Strength); Children.Add(row);
            Note = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
            DockerTheme.UseTheme(Note, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
            Children.Add(Note);
            foreach (var toggle in new[] { Audit, Repair })
            {
                toggle.Checked += (_, __) => Update();
                toggle.Unchecked += (_, __) => Update();
            }
            Strength.ValueChanged += (_, __) => { number.Text = ((int)Strength.Value).ToString(); changed(); };
            Update(false);

            void Update(bool notify = true)
            {
                Strength.IsEnabled = Audit.IsChecked == true || Repair.IsChecked == true;
                if (Audit.IsChecked != true) Note.Text = "";
                if (notify) changed();
            }
        }
    }
}
