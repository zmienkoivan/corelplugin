using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class DockerTheme
    {
        private sealed class WrappingTextTemplate : DataTemplateSelector
        {
            private readonly DataTemplate _text;
            internal WrappingTextTemplate(TextAlignment alignment = TextAlignment.Center)
            {
                var text = new FrameworkElementFactory(typeof(TextBlock));
                text.SetBinding(TextBlock.TextProperty, new Binding());
                text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
                text.SetValue(TextBlock.TextAlignmentProperty, alignment);
                _text = new DataTemplate { VisualTree = text };
            }
            public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
                item is string ? _text : null;
        }

        private static readonly DataTemplateSelector WrappingContent = new WrappingTextTemplate();
        private static readonly DataTemplateSelector ChoiceContent = new WrappingTextTemplate(TextAlignment.Left);
        private static readonly Style PrimaryButtonStyle = ButtonStyle(
            "#376A8B", "#2B5876", "#244B66", "#FFFFFF", "#2B5876");

        public static void Apply(FrameworkElement root)
        {
            root.Resources[typeof(Button)] = ButtonStyle(
                "#EDF1F3", "#DFE9EF", "#C9DCE7", "#253C4C", "#A6BAC7");
            foreach (var type in new[] { typeof(CheckBox), typeof(RadioButton) })
            {
                var style = new Style(type);
                style.Setters.Add(new Setter(ContentControl.ContentTemplateSelectorProperty, ChoiceContent));
                style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
                root.Resources[type] = style;
            }
        }

        public static Button Primary(Button button)
        {
            button.Style = PrimaryButtonStyle;
            return button;
        }

        private static Style ButtonStyle(string normal, string hover, string pressed,
            string foreground, string outline)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(foreground)));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 27.0));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(ContentControl.ContentTemplateSelectorProperty, WrappingContent));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(Border.BackgroundProperty, Brush(normal));
            border.SetValue(Border.BorderBrushProperty, Brush(outline));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(FrameworkElement.MarginProperty,
                new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hover)) { TargetName = "Chrome" });
            template.Triggers.Add(over);
            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Border.BackgroundProperty, Brush(pressed)) { TargetName = "Chrome" });
            template.Triggers.Add(down);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#E4E7E9")) { TargetName = "Chrome" });
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#88949C")));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private static Brush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
