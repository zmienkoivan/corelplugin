using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace VanyaTools.Native
{
    internal sealed partial class SportLayoutTab
    {
        private static StackPanel UiGroup(Panel parent, string title, string help = null)
        {
            var group = DockerLayout.ToolGroup(title, help); parent.Children.Add(group); return group;
        }

        private static StackPanel UiFold(Panel parent, string title, string help = null)
        {
            var content = new StackPanel { Margin = new Thickness(4, 4, 4, 5) };
            var section = new Expander { Header = new TextBlock { Text = title, FontSize = 11,
                TextWrapping = TextWrapping.Wrap }, Content = content, IsExpanded = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(1, 2, 1, 5) };
            content.Tag = section;
            UiFoldHelp(content, help);
            AutomationProperties.SetName(section, title); parent.Children.Add(section); return content;
        }

        private static void UiFoldHelp(Panel content, string help)
        {
            if (String.IsNullOrWhiteSpace(help) || !(content.Tag is Expander section)) return;
            string old = AutomationProperties.GetHelpText(section);
            string text = String.IsNullOrWhiteSpace(old) ? help : old + "\n\n" + help;
            section.ToolTip = new TextBlock { Text = text, MaxWidth = 380, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetHelpText(section, text);
        }

        private static Button UiAction(string title, RoutedEventHandler action, bool primary = false, string help = null)
        {
            var button = Button(title, action); button.Margin = new Thickness(1, 1, 1, 3);
            button.Padding = new Thickness(4, 2, 4, 2);
            if (!String.IsNullOrWhiteSpace(help)) { button.ToolTip = help; AutomationProperties.SetName(button, help); }
            return primary ? DockerTheme.Primary(button) : button;
        }

        private static UniformGrid UiActions(Panel parent, params Button[] buttons)
        {
            var row = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 2) };
            foreach (var button in buttons) { button.Margin = new Thickness(2, 1, 2, 3); row.Children.Add(button); }
            parent.Children.Add(row); return row;
        }

        private static UniformGrid UiFields(Panel parent, int columns = 2)
        {
            var fields = new UniformGrid { Columns = columns }; parent.Children.Add(fields); return fields;
        }

        private static TextBox UiNumber(Panel parent, string title, string initial, string help = null)
        {
            var field = Field(initial); field.TextAlignment = TextAlignment.Right;
            UiLabeled(parent, title, field, help); return field;
        }

        private static void UiLabeled(Panel parent, string title, FrameworkElement editor, string help = null)
        {
            editor.Width = Double.NaN; editor.MinWidth = 0; editor.Margin = new Thickness(0);
            if (editor is Control control) { control.FontSize = 11; control.MinHeight = 27; }
            AutomationProperties.SetName(editor, help ?? title);
            if (help != null) editor.ToolTip = help;
            var field = DockerLayout.LabeledField(title, editor); field.Margin = new Thickness(2, 0, 2, 5);
            parent.Children.Add(field);
        }

        private static void UiHideEmpty(TextBlock note)
        {
            var style = new Style(typeof(TextBlock));
            var trigger = new DataTrigger { Binding = new Binding("Text") { Source = note }, Value = "" };
            trigger.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
            style.Triggers.Add(trigger); note.Style = style;
            DockerTheme.UseTheme(note, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
        }
    }
}
