using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace VanyaTools.Native
{
    internal static class DockerSettings
    {
        // Sections keep the same controls alive while collapsed. Editing a
        // field and folding it must never reload or save its value implicitly.
        internal static Expander Section(string title, FrameworkElement content) =>
            Format(new Expander { Content = content }, title);

        internal static Expander Format(FrameworkElement view, string title)
        {
            var section = view as Expander;
            if (section == null) return Section(title, view);
            section.Header = new TextBlock { Text = title, FontSize = 12,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            section.IsExpanded = false;
            section.Margin = new Thickness(0, 0, 0, 6);
            section.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            if (section.Content is FrameworkElement content) content.Margin = new Thickness(4, 5, 4, 8);
            AutomationProperties.SetName(section, title);
            return section;
        }
    }
}
