using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class DockerTheme
    {
        internal const string WindowBackground = "VanyaTools.Theme.WindowBackground";
        internal const string SurfaceBackground = "VanyaTools.Theme.SurfaceBackground";
        internal const string PanelBackground = "VanyaTools.Theme.PanelBackground";
        internal const string InputBackground = "VanyaTools.Theme.InputBackground";
        internal const string BorderBrush = "VanyaTools.Theme.BorderBrush";
        internal const string TextBrush = "VanyaTools.Theme.TextBrush";
        internal const string MutedTextBrush = "VanyaTools.Theme.MutedTextBrush";
        internal const string StatusBackground = "VanyaTools.Theme.StatusBackground";
        internal const string ErrorTextBrush = "VanyaTools.Theme.ErrorTextBrush";
        internal const string UrgentBackground = "VanyaTools.Theme.UrgentBackground";
        internal const string CompleteBackground = "VanyaTools.Theme.CompleteBackground";
        internal const string SelectedBackground = "VanyaTools.Theme.SelectedBackground";
        internal const string SelectedTextBrush = "VanyaTools.Theme.SelectedTextBrush";
        private const string ButtonBackground = "VanyaTools.Theme.ButtonBackground";
        private const string ButtonHover = "VanyaTools.Theme.ButtonHover";
        private const string ButtonPressed = "VanyaTools.Theme.ButtonPressed";
        private const string DisabledBackground = "VanyaTools.Theme.DisabledBackground";
        private const string DisabledText = "VanyaTools.Theme.DisabledText";
        private static readonly string ThemePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "theme.txt");
        private static readonly object RootsLock = new object();
        private static readonly List<WeakReference<FrameworkElement>> Roots = new List<WeakReference<FrameworkElement>>();
        private static readonly List<WeakReference<ComboBox>> Selectors = new List<WeakReference<ComboBox>>();
        private static bool _dark = LoadDarkTheme();

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
        private static readonly ResourceDictionary Styles = CreateStyles();

        public static void Apply(FrameworkElement root)
        {
            if (root == null) return;
            lock (RootsLock)
            {
                Roots.RemoveAll(reference => !reference.TryGetTarget(out _));
                if (!Roots.Exists(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, root)))
                    Roots.Add(new WeakReference<FrameworkElement>(root));
            }
            ApplyPalette(root, _dark);
            foreach (object key in Styles.Keys) root.Resources[key] = Styles[key];
            UseTheme(root, TextElement.ForegroundProperty, TextBrush);
            if (root is Panel) UseTheme(root, Panel.BackgroundProperty, WindowBackground);
            else if (root is Border) UseTheme(root, Border.BackgroundProperty, SurfaceBackground);
            else if (root is Control) UseTheme(root, Control.BackgroundProperty, WindowBackground);
        }

        internal static void UseTheme(FrameworkElement element, DependencyProperty property, string key)
        {
            element.SetResourceReference(property, key);
        }

        internal static Style ControlStyle(Type type) => Styles[type] as Style;

        internal static FrameworkElement CreateSettingsView(Action<string, bool> status)
        {
            var panel = new StackPanel { Margin = new Thickness(4, 4, 4, 6) };
            panel.Children.Add(new TextBlock { Text = "Тема интерфейса", FontSize = 11,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
            var selector = new ComboBox { ItemsSource = new[] { "Светлая", "Тёмная" },
                SelectedIndex = _dark ? 1 : 0, MinHeight = 27, FontSize = 11,
                ToolTip = "Меняется сразу. Выбор сохраняется на этом компьютере." };
            lock (RootsLock) Selectors.Add(new WeakReference<ComboBox>(selector));
            selector.SelectionChanged += (_, __) =>
            {
                bool dark = selector.SelectedIndex == 1;
                if (dark == _dark) return;
                SetDarkTheme(dark);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ThemePath));
                    File.WriteAllText(ThemePath, dark ? "dark" : "light");
                }
                catch (Exception error)
                {
                    Log.Error("Could not save docker theme.", error);
                    status?.Invoke("Тема изменена, но не сохранена: " + error.Message, true);
                }
            };
            panel.Children.Add(selector);
            return panel;
        }

        private static bool LoadDarkTheme()
        {
            try { return File.Exists(ThemePath) && String.Equals(File.ReadAllText(ThemePath).Trim(), "dark", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void SetDarkTheme(bool dark)
        {
            _dark = dark;
            var roots = new List<FrameworkElement>();
            var selectors = new List<ComboBox>();
            lock (RootsLock)
            {
                Roots.RemoveAll(reference => !reference.TryGetTarget(out _));
                Selectors.RemoveAll(reference => !reference.TryGetTarget(out _));
                foreach (var reference in Roots) if (reference.TryGetTarget(out var root)) roots.Add(root);
                foreach (var reference in Selectors) if (reference.TryGetTarget(out var selector)) selectors.Add(selector);
            }
            foreach (var root in roots) OnUi(root, () => { if (_dark == dark) ApplyPalette(root, dark); });
            foreach (var selector in selectors) OnUi(selector, () => { if (_dark == dark) selector.SelectedIndex = dark ? 1 : 0; });
        }

        private static void OnUi(FrameworkElement element, Action action)
        {
            if (element.Dispatcher.HasShutdownStarted) return;
            if (element.Dispatcher.CheckAccess()) action();
            else element.Dispatcher.BeginInvoke(action);
        }

        private static void ApplyPalette(FrameworkElement root, bool dark)
        {
            var values = new Dictionary<string, string>
            {
                [WindowBackground] = dark ? "#20262C" : "#ECEFF1",
                [SurfaceBackground] = dark ? "#252D34" : "#FFFFFF",
                [PanelBackground] = dark ? "#2D3842" : "#F6F8FA",
                [InputBackground] = dark ? "#1D252C" : "#FFFFFF",
                [BorderBrush] = dark ? "#586B7C" : "#A6BAC7",
                [TextBrush] = dark ? "#E5ECF2" : "#253C4C",
                [MutedTextBrush] = dark ? "#A7B6C4" : "#6E6E6E",
                [StatusBackground] = dark ? "#2F3D48" : "#DFE5E8",
                [ErrorTextBrush] = dark ? "#FFAAA4" : "#8A1F11",
                [UrgentBackground] = dark ? "#55383C" : "#FFEEEE",
                [CompleteBackground] = dark ? "#263E5C" : "#C9DAF8",
                [SelectedBackground] = dark ? "#315D7A" : "#CFE6F7",
                [SelectedTextBrush] = dark ? "#FFFFFF" : "#000000",
                [ButtonBackground] = dark ? "#344552" : "#EDF1F3",
                [ButtonHover] = dark ? "#405A6C" : "#DFE9EF",
                [ButtonPressed] = dark ? "#50738C" : "#C9DCE7",
                [DisabledBackground] = dark ? "#2B353D" : "#E4E7E9",
                [DisabledText] = dark ? "#82909B" : "#88949C"
            };
            foreach (var pair in values) root.Resources[pair.Key] = Brush(pair.Value);
            // Scope OS brush aliases to this docker/window; never change Corel or artwork resources.
            root.Resources[SystemColors.WindowBrushKey] = root.Resources[InputBackground];
            root.Resources[SystemColors.WindowTextBrushKey] = root.Resources[TextBrush];
            root.Resources[SystemColors.ControlBrushKey] = root.Resources[PanelBackground];
            root.Resources[SystemColors.ControlTextBrushKey] = root.Resources[TextBrush];
            root.Resources[SystemColors.ControlDarkBrushKey] = root.Resources[BorderBrush];
            root.Resources[SystemColors.ControlLightBrushKey] = root.Resources[PanelBackground];
            root.Resources[SystemColors.ControlLightLightBrushKey] = root.Resources[SurfaceBackground];
            root.Resources[SystemColors.GrayTextBrushKey] = root.Resources[DisabledText];
            root.Resources[SystemColors.HighlightBrushKey] = root.Resources[SelectedBackground];
            root.Resources[SystemColors.HighlightTextBrushKey] = root.Resources[SelectedTextBrush];
            root.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = root.Resources[SelectedBackground];
            root.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = root.Resources[SelectedTextBrush];
        }

        public static Button Primary(Button button)
        {
            button.Style = PrimaryButtonStyle;
            return button;
        }

        internal static Button Accent(Button button, Color color, bool darkText = false, bool preserveDisabledColor = false)
        {
            string normal = color.ToString();
            string hover = Color.FromRgb((byte)(color.R * .88), (byte)(color.G * .88), (byte)(color.B * .88)).ToString();
            string pressed = Color.FromRgb((byte)(color.R * .75), (byte)(color.G * .75), (byte)(color.B * .75)).ToString();
            string muted = Color.FromRgb((byte)(color.R * .55 + 255 * .45),
                (byte)(color.G * .55 + 255 * .45), (byte)(color.B * .55 + 255 * .45)).ToString();
            button.Style = ButtonStyle(normal, hover, pressed, darkText ? "#253C4C" : "#FFFFFF", pressed,
                preserveDisabledColor ? muted : null, preserveDisabledColor ? "#475569" : null);
            return button;
        }

        private static ResourceDictionary CreateStyles()
        {
            var resources = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='{x:Type TextBox}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.InputBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
    <Setter Property='CaretBrush' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='SelectionBrush' Value='{DynamicResource VanyaTools.Theme.SelectedBackground}'/>
    <Style.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.DisabledText}'/></Trigger></Style.Triggers>
  </Style>
  <Style TargetType='{x:Type PasswordBox}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.InputBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
    <Setter Property='CaretBrush' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
  </Style>
  <Style TargetType='{x:Type Label}'><Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/></Style>
  <Style TargetType='{x:Type GroupBox}'>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
  </Style>
  <Style TargetType='{x:Type Expander}'><Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/></Style>
  <Style TargetType='{x:Type ComboBox}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.InputBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
    <Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='5,2'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='ScrollViewer.CanContentScroll' Value='True'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBox}'>
      <Grid>
        <ToggleButton x:Name='DropDownToggle' Focusable='False' ClickMode='Press'
          IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
          <ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'>
            <Border x:Name='ToggleBorder' Background='{Binding Background, RelativeSource={RelativeSource AncestorType=ComboBox}}'
              BorderBrush='{Binding BorderBrush, RelativeSource={RelativeSource AncestorType=ComboBox}}' BorderThickness='1' CornerRadius='3'>
              <Path Data='M0,0 L4,4 L8,0' Stroke='{DynamicResource VanyaTools.Theme.TextBrush}' StrokeThickness='1.4'
                HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,8,0'/>
            </Border>
            <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ToggleBorder' Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.ButtonPressed}'/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate></ToggleButton.Template>
        </ToggleButton>
        <ContentPresenter x:Name='Selection' Margin='5,2,25,2' IsHitTestVisible='False'
          Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
          ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'
          HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
        <TextBox x:Name='PART_EditableTextBox' Margin='4,1,24,1' Visibility='Hidden' BorderThickness='0'
          Background='{TemplateBinding Background}' Foreground='{TemplateBinding Foreground}'
          IsReadOnly='{TemplateBinding IsReadOnly}' HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}'
          VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}'/>
        <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'>
          <Border MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
            MaxHeight='{TemplateBinding MaxDropDownHeight}' Background='{DynamicResource VanyaTools.Theme.InputBackground}'
            BorderBrush='{DynamicResource VanyaTools.Theme.BorderBrush}' BorderThickness='1' Padding='1'>
            <ScrollViewer CanContentScroll='True'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>
          </Border>
        </Popup>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsEditable' Value='True'><Setter TargetName='PART_EditableTextBox' Property='Visibility' Value='Visible'/><Setter TargetName='Selection' Property='Visibility' Value='Hidden'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.DisabledText}'/><Setter Property='Opacity' Value='0.65'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type ComboBoxItem}'>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.InputBackground}'/>
    <Setter Property='Padding' Value='5,3'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBoxItem}'>
      <Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border>
    </ControlTemplate></Setter.Value></Setter>
    <Style.Triggers>
      <Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SelectedBackground}'/><Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.SelectedTextBrush}'/></Trigger>
      <Trigger Property='IsHighlighted' Value='True'><Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SelectedBackground}'/><Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.SelectedTextBrush}'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.DisabledText}'/></Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='{x:Type DatePicker}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.InputBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
  </Style>
  <Style TargetType='{x:Type DatePickerTextBox}' BasedOn='{StaticResource {x:Type TextBox}}'/>
  <Style TargetType='{x:Type Calendar}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SurfaceBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
  </Style>
  <Style TargetType='{x:Type TabControl}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SurfaceBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
  </Style>
  <Style TargetType='{x:Type TabItem}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.PanelBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
    <Setter Property='Padding' Value='6,3'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TabItem}'>
      <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'
        CornerRadius='3' Padding='{TemplateBinding Padding}'>
        <ContentPresenter ContentSource='Header' RecognizesAccessKey='True' HorizontalAlignment='Center' VerticalAlignment='Center'/>
      </Border>
    </ControlTemplate></Setter.Value></Setter>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.ButtonHover}'/></Trigger>
      <Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SurfaceBackground}'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.DisabledText}'/></Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='{x:Type DataGrid}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SurfaceBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
    <Setter Property='RowBackground' Value='{DynamicResource VanyaTools.Theme.SurfaceBackground}'/>
    <Setter Property='AlternatingRowBackground' Value='{DynamicResource VanyaTools.Theme.PanelBackground}'/>
    <Setter Property='HorizontalGridLinesBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
    <Setter Property='VerticalGridLinesBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
  </Style>
  <Style TargetType='{x:Type DataGridColumnHeader}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.PanelBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
    <Setter Property='BorderThickness' Value='0,0,1,1'/><Setter Property='Padding' Value='4,3'/>
  </Style>
  <Style TargetType='{x:Type DataGridRowHeader}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.PanelBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
  </Style>
  <Style TargetType='{x:Type ListBox}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.InputBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
  </Style>
  <Style TargetType='{x:Type ListView}' BasedOn='{StaticResource {x:Type ListBox}}'/>
  <Style TargetType='{x:Type ToolTip}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SurfaceBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
  </Style>
  <Style TargetType='{x:Type ContextMenu}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SurfaceBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/>
  </Style>
  <Style TargetType='{x:Type MenuItem}'>
    <Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.SurfaceBackground}'/>
    <Setter Property='Foreground' Value='{DynamicResource VanyaTools.Theme.TextBrush}'/>
  </Style>
  <Style TargetType='{x:Type Separator}'><Setter Property='Background' Value='{DynamicResource VanyaTools.Theme.BorderBrush}'/></Style>
</ResourceDictionary>");
            resources[typeof(Button)] = ButtonStyle(ButtonBackground, ButtonHover, ButtonPressed,
                TextBrush, BorderBrush, DisabledBackground, DisabledText, true);
            foreach (var type in new[] { typeof(CheckBox), typeof(RadioButton) })
            {
                var style = new Style(type);
                style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TextBrush)));
                style.Setters.Add(new Setter(ContentControl.ContentTemplateSelectorProperty, ChoiceContent));
                style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
                var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
                disabled.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(DisabledText)));
                style.Triggers.Add(disabled);
                resources[type] = style;
            }
            return resources;
        }

        private static object StyleBrush(string value, bool theme) =>
            theme ? (object)new DynamicResourceExtension(value) : Brush(value);

        private static Style ButtonStyle(string normal, string hover, string pressed,
            string foreground, string outline, string disabledBackground = null, string disabledForeground = null,
            bool theme = false)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, StyleBrush(foreground, theme)));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 27.0));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(ContentControl.ContentTemplateSelectorProperty, WrappingContent));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            if (theme)
            {
                border.SetResourceReference(Border.BackgroundProperty, normal);
                border.SetResourceReference(Border.BorderBrushProperty, outline);
            }
            else
            {
                border.SetValue(Border.BackgroundProperty, Brush(normal));
                border.SetValue(Border.BorderBrushProperty, Brush(outline));
            }
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
            over.Setters.Add(new Setter(Border.BackgroundProperty, StyleBrush(hover, theme)) { TargetName = "Chrome" });
            template.Triggers.Add(over);
            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Border.BackgroundProperty, StyleBrush(pressed, theme)) { TargetName = "Chrome" });
            template.Triggers.Add(down);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, disabledBackground != null
                ? StyleBrush(disabledBackground, theme) : new DynamicResourceExtension(DisabledBackground)) { TargetName = "Chrome" });
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, disabledForeground != null
                ? StyleBrush(disabledForeground, theme) : new DynamicResourceExtension(DisabledText)));
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
