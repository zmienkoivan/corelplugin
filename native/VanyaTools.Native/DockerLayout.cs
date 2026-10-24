using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VanyaTools.Native
{
    internal static class DockerLayout
    {
        internal static StackPanel ToolGroup(string title, string help = null)
        {
            var group = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            group.Children.Add(new TextBlock { Text = title, FontSize = 11,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(1, 2, 1, 4) });
            if (!String.IsNullOrWhiteSpace(help)) DockerHelp.Add(group, help);
            return group;
        }

        internal static StackPanel LabeledField(string label, FrameworkElement editor)
        {
            var field = new StackPanel();
            field.Children.Add(new TextBlock { Text = label, FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.Wrap });
            field.Children.Add(editor);
            return field;
        }

        // A label and its editor share one row even in a narrow docker.
        internal static Grid FieldRow(string label, FrameworkElement editor, double editorWidth = 80)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = editorWidth > 0
                ? new GridLength(editorWidth) : new GridLength(1.4, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0) });
            Grid.SetColumn(editor, 1); row.Children.Add(editor);
            return row;
        }

        internal static Grid Trailing(UIElement main, UIElement trailing)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(main); Grid.SetColumn(trailing, 1); row.Children.Add(trailing);
            return row;
        }

        internal static UniformGrid Cards(double minimumCellWidth, int maximumColumns = 3)
        {
            var cards = new UniformGrid { Columns = Math.Min(2, maximumColumns) };
            cards.SizeChanged += (_, __) =>
            {
                int columns = Math.Max(1, Math.Min(maximumColumns,
                    (int)(cards.ActualWidth / minimumCellWidth)));
                if (cards.Columns != columns) cards.Columns = columns;
            };
            return cards;
        }
    }
}
