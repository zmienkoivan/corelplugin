using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class WindowChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Flags).GetValue(value);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Rebuild(FrameBagWindow window) => Call(window, "Rebuild");
    private static object Fields(FrameBagWindow window) => window.GetType().GetField("_fields", Flags).GetValue(window);
    private static object Number(FrameBagWindow window, string key) => Fields(window).GetType().GetProperty("Item").GetValue(Fields(window), new object[] { key });
    private static void Set(FrameBagWindow window, string key, double value) =>
        Field<TextBox>(Number(window, key), "_input").Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static BagPoint[] Anchors(FrameBagWindow window) => Enumerable.Range(0, 4).Select(n => FrameBagClosures.Anchor(window.Plan, n / 2, n % 2)).ToArray();
    private static void SameAnchors(BagPoint[] a, BagPoint[] b) => Require(a.Zip(b, (x, y) => BagPoint.Sub(x, y).Length).All(d => d < 1e-8), "Window moved zipper endpoints while editing bag geometry.");

    internal static void Run()
    {
        var window = new FrameBagWindow(new FrameBagSetup { LeftZipper = BagZipper.Slot, RightZipper = BagZipper.Split });
        try
        {
            Require(window.Plan != null, "Initial window geometry failed.");
            var initial = Anchors(window);
            Require(Field<StackPanel>(Number(window, "FrontHeight"), "View").Visibility == Visibility.Visible,
                "Front height is hidden behind an optional switch.");
            Set(window, "Height", 230); Set(window, "FrontHeight", 210); Rebuild(window);
            Require(window.Plan != null && window.Plan.Setup.Height == 230 && window.Plan.Setup.FrontHeight == 210 && window.Plan.Setup.SplitHeights,
                "Window ignored independent front/rear heights.");
            SameAnchors(initial, Anchors(window));
            Set(window, "Radius0", 8); Set(window, "Radius1", 12); Rebuild(window);
            Require(window.Plan.Setup.CornerRadii[0] == 8 && window.Plan.Setup.CornerRadii[1] == 12,
                "Window ignored individual corner radii.");
            SameAnchors(initial, Anchors(window));
            Field<Slider>(Number(window, "CornerRadius"), "_slider").Value = 18; Rebuild(window);
            Require(window.Plan.Setup.CornerRadii.All(r => r == 18), "Shared radius failed to update all corners.");
            SameAnchors(initial, Anchors(window));

            var side = Field<FrameBagPreview>(window, "_side");
            var left = Field<ToggleButton>(window, "_leftView"); var right = Field<ToggleButton>(window, "_rightView");
            right.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(right.IsChecked == true && left.IsChecked == false && Field<int>(side, "_editSide") == 1,
                "Visible right-side switch did not select the corresponding zipper.");
            right.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(right.IsChecked == true && left.IsChecked == false, "Repeated side click cleared the selection.");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1180, 760)); content.Arrange(new Rect(0, 0, 1180, 760)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1180, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var p = new BagPoint(100, 50); var mapped = (Point)Call(side, "Point", p);
            double mirror = window.Plan.Frame.Min(a => a.X) + window.Plan.Frame.Max(a => a.X);
            Require(Math.Abs(mapped.X - (mirror - p.X)) < 1e-8 && mapped.Y == p.Y, "Right view does not mirror the frame.");
            var viewport = Field<PatternViewport>(side, "_viewport");
            var screen = new Point(viewport.Origin.X + mapped.X * viewport.Scale, viewport.Origin.Y + mapped.Y * viewport.Scale);
            var mouse = (Point)Call(side, "World", screen);
            Require(Math.Abs(mouse.X - p.X) < 1e-8 && Math.Abs(mouse.Y - p.Y) < 1e-8,
                "Mouse drag does not invert the mirrored view.");
            side.Zoom(2); var previousScale = viewport.Scale; var previousCenter = viewport.Center;
            left.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); bitmap.Render(content);
            Require(Math.Abs(viewport.Scale - previousScale) < 1e-8 && Math.Abs(viewport.Center.X - (mirror - previousCenter.X)) < 1e-8,
                "Side switch lost zoom or the physical viewing region.");
            var rightMode = Field<ComboBox>(window, "_rightZipper"); rightMode.SelectedIndex = (int)BagZipper.Slot; Rebuild(window);
            Require(right.IsChecked == true && Field<int>(side, "_editSide") == 1, "Zipper settings did not reveal the selected side.");
            var beforeDrag = Anchors(window);
            Call(window, "SetAnchor", 1, 0, beforeDrag[2].X + 3, beforeDrag[2].Y + 2); Rebuild(window);
            var afterDrag = Anchors(window);
            Require(BagPoint.Sub(afterDrag[0], beforeDrag[0]).Length < 1e-8 && Math.Abs(afterDrag[2].X - beforeDrag[2].X - 3) < 1e-8,
                "Right-side drag modified the wrong zipper or treated millimetres as percentages.");

            Set(window, "Height", 30); Set(window, "FrontHeight", 30); Rebuild(window);
            Require(window.Plan == null && Field<FrameBagPlan>(side, "_plan") != null && !Field<Button>(window, "_apply").IsEnabled,
                "Shrinking below a fixed zipper should block export and leave anchors editable.");
            Set(window, "Height", 230); Set(window, "FrontHeight", 210); Rebuild(window);
            SameAnchors(afterDrag, Anchors(window));
            var notches = Field<CheckBox>(window, "_notches"); notches.IsChecked = false; Rebuild(window);
            Require(window.Plan.Parts.All(part => part.Notches.Length == 0), "Notch checkbox did not affect export geometry.");
            notches.IsChecked = true; Rebuild(window);
            Require(window.Plan.Parts.Any(part => part.Notches.Length != 0), "Notches did not return after enabling.");

            Field<ComboBox>(window, "_leftZipper").SelectedIndex = 0; rightMode.SelectedIndex = 0;
            Field<ComboBox>(window, "_frameShape").SelectedIndex = 0;
            Field<TextBox>(Number(window, "Head"), "_input").Text = "not used";
            Field<TextBox>(Number(window, "Diagonal"), "_input").Text = "not used";
            Rebuild(window);
            Require(window.Plan != null && window.Plan.Frame.Length == 3, "Triangle window reads hidden head/diagonal values.");
            Require(Field<StackPanel>(Number(window, "Radius4"), "View").Visibility == Visibility.Collapsed,
                "Window displays a nonexistent fifth bag corner.");
            Field<CheckBox>(window, "_divideBags").IsChecked = true; Rebuild(window);
            Require(window.Plan != null && window.Plan.Sections.Length == 2, "Division checkbox did not produce two complete bags.");
            var divider = window.Plan.DivisionPath;
            var at = BagPoint.Mul(BagPoint.Add(divider[0], divider[divider.Length - 1]), .5);
            int added = side.AddDivisionPoint(at, 10); Rebuild(window);
            Require(added == 1 && window.Plan != null && window.Plan.Setup.DivisionLine.Length == 3,
                "Clicking the division line did not add an editable point.");
            side.MoveDivisionPoint(1, new BagPoint(at.X, at.Y + 8)); Rebuild(window);
            Require(window.Plan != null && Math.Abs(window.Plan.Setup.DivisionLine[1][1] - at.Y - 8) < 1e-8,
                "Dragging the divider point ignored millimetres.");
            Require(side.CanDeleteDivisionPoint, "Added division point cannot be removed.");
            side.DeleteDivisionPoint(); Rebuild(window);
            Require(window.Plan != null && window.Plan.Setup.DivisionLine.Length == 2, "Deleting a division point damaged the two bags.");
            side.MoveDivisionPoint(0, new BagPoint(100, 100)); Rebuild(window);
            Require(window.Plan == null && Field<FrameBagPlan>(side, "_plan") != null && !Field<Button>(window, "_apply").IsEnabled,
                "Invalid divider remains exportable or disappears from editing.");
            Field<FrameBagSetup>(window, "_anchors").DivisionLine = null; Rebuild(window);
            Require(window.Plan != null && window.Plan.Sections.Length == 2, "Reset divider did not restore valid independent bags.");
            Field<CheckBox>(window, "_divideBags").IsChecked = false;
            Field<CheckBox>(window, "_fullLength").IsChecked = false;
            Set(window, "BagLength", 200); Set(window, "Height", 60); Set(window, "FrontHeight", 60);
            Field<ComboBox>(window, "_attachment").SelectedIndex = 1; Rebuild(window);
            Require(window.Plan != null && window.Plan.Setup.BagLength == 200 && window.Plan.Setup.AttachToFront,
                "Length and front attachment controls did not change the outline.");
            Field<CheckBox>(window, "_fullLength").IsChecked = true; Rebuild(window);
            Require(window.Plan != null && window.Plan.Setup.BagLength == 0, "Full frame length did not restore.");
            var project = FrameBagGeometry.Build(new FrameBagSetup { Name = "Гаврила · модуль", BagLength = 300.5,
                Height = 140.25, FrontHeight = 145.75, SplitHeights = true, TopDepth = 63.25, BottomDepth = 38.75,
                Clearance = 4.75, Allowance = 9.25, CornerRadius = 12.75, CornerRadii = new[] { 7.25, 9.75, 11.25, 13.5 },
                DivideBags = true, Notches = true, NotchDepth = 1.25, LeftZipper = BagZipper.Slot, RightZipper = BagZipper.Flap,
                DivisionLine = new[] { new[] { -50d, 90 }, new[] { 150.125, 85.75 }, new[] { 250.25, 110.125 }, new[] { 700d, 100 } } });
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            string saved = serializer.Serialize(project.Setup);
            var restored = serializer.Deserialize<FrameBagSetup>(saved);
            Call(window, "LoadSetup", restored);
            Require(window.Plan != null && serializer.Serialize(window.Plan.Setup) == saved,
                "Loading the project did not restore every saved parameter, decimal radius and division node.");
            var upperBefore = window.Plan.Setup.UpperZippers.Copy();
            var lowerBefore = window.Plan.Setup.LowerZippers.Copy();
            Call(window, "SelectBag", 1);
            Require(Field<ToggleButton>(window, "_lowerView").IsChecked == true && Field<ToggleButton>(window, "_upperView").IsChecked == false &&
                side.ZipperPlan == window.Plan.Sections[1], "Lower module switch edits the wrong zipper plan.");
            Field<ComboBox>(window, "_leftZipper").SelectedIndex = (int)BagZipper.Flap;
            Set(window, "ZipperWidth", 3.5); Set(window, "FlapDepth", 8); Set(window, "FlapExtra", 3);
            Call(window, "SelectBag", 0); Rebuild(window); // switching before the queued refresh must retain edits
            Require(window.Plan != null && window.Plan.Setup.LowerZippers.Left == BagZipper.Flap && window.Plan.Setup.LowerZippers.Width == 3.5 &&
                window.Plan.Setup.LowerZippers.FlapDepth == 8 && window.Plan.Setup.LowerZippers.FlapExtra == 3,
                "Pending lower settings were lost when switching back to the upper bag.");
            Require(serializer.Serialize(window.Plan.Setup.UpperZippers) == serializer.Serialize(upperBefore), "Lower controls overwrote upper zipper settings.");
            Field<ComboBox>(window, "_leftZipper").SelectedIndex = (int)BagZipper.None;
            Field<ComboBox>(window, "_rightZipper").SelectedIndex = (int)BagZipper.Slot;
            Set(window, "ZipperWidth", 5.25); Rebuild(window);
            Require(window.Plan != null && window.Plan.Sections[0].Setup.LeftZipper == BagZipper.None && window.Plan.Sections[0].Setup.RightZipper == BagZipper.Slot &&
                window.Plan.Sections[1].Setup.ZipperWidth == 3.5, "Upper controls changed the lower zipper width or side.");
            Call(window, "SelectBag", 1);
            Field<ComboBox>(window, "_rightZipper").SelectedIndex = (int)BagZipper.Slot; Rebuild(window);
            Require(window.Plan != null && Field<int>(side,"_editSide") == 1, "Lower right zipper did not become active.");
            var upperSaved = serializer.Serialize(window.Plan.Setup.UpperZippers);
            var leftSaved = (double[])window.Plan.Setup.LowerZippers.LeftStart.Clone();
            var rightSaved = (double[])window.Plan.Setup.LowerZippers.RightStart.Clone();
            Call(window,"SetAnchor",1,0,rightSaved[0]+1,rightSaved[1]+1); Rebuild(window);
            Require(window.Plan != null && window.Plan.Setup.LowerZippers.RightStart[0] == rightSaved[0]+1 &&
                window.Plan.Setup.LowerZippers.LeftStart.SequenceEqual(leftSaved) && serializer.Serialize(window.Plan.Setup.UpperZippers) == upperSaved,
                "Dragging the lower-right anchor changed another zipper.");
            Call(window,"SetAnchor",1,0,rightSaved[0],-10d); Rebuild(window);
            Require(window.Plan == null && side.ZipperPlan != null && side.ZipperPlan.Setup.RightZipStartYmm == -10 &&
                !Field<Button>(window,"_apply").IsEnabled, "Invalid lower zipper cannot be corrected in preview or remains exportable.");
            Call(window,"SetAnchor",1,0,rightSaved[0],rightSaved[1]); Rebuild(window);
            Require(window.Plan != null, "Correcting the lower anchor did not restore the cutting kit.");
            saved = serializer.Serialize(window.Plan.Setup);
            Call(window,"LoadSetup",serializer.Deserialize<FrameBagSetup>(saved));
            Require(window.Plan != null && serializer.Serialize(window.Plan.Setup) == saved,
                "Project reload loses independent module modes, flap values or coordinates.");
            Call(window,"SelectBag",1);
            Field<ComboBox>(window,"_leftZipper").SelectedIndex=0; Field<ComboBox>(window,"_rightZipper").SelectedIndex=0; Rebuild(window);
            Require(window.Plan != null && window.Plan.Setup.LowerZippers.Left == BagZipper.None && window.Plan.Setup.LowerZippers.Right == BagZipper.None,
                "Explicitly disabled lower zippers were re-added automatically.");
            Field<CheckBox>(window,"_divideBags").IsChecked=false; Rebuild(window);
            Field<CheckBox>(window,"_divideBags").IsChecked=true; Rebuild(window);
            Require(window.Plan != null && window.Plan.Setup.LowerZippers.Left == BagZipper.None && window.Plan.Setup.LowerZippers.Right == BagZipper.None,
                "Toggling modules discarded their saved zipper choices.");
            var oldProject = project.Setup.Copy(); oldProject.UpperZippers = oldProject.LowerZippers = null;
            Call(window,"LoadSetup",oldProject);
            Require(window.Plan != null && window.Plan.Setup.UpperZippers != null && window.Plan.Setup.LowerZippers != null &&
                window.Plan.Sections.All(section => section.Parts.Sum(part => part.Cutouts.Length) > 0), "Older modular project did not acquire its missing lower zipper.");
            Require(window.Title.Contains("GAVRILABAGS"), "Constructor title does not match its branded tab.");
            Console.WriteLine("Frame bag window: side switches, mirrored mouse/zoom, fixed zippers, front/rear heights, corner radii and notch controls passed.");
        }
        finally { window.Close(); }
    }
}

namespace VanyaTools.Native
{
    internal static class DockerTheme
    {
        internal const string WindowBackground = "window", ErrorTextBrush = "error", MutedTextBrush = "muted", BorderBrush = "border";
        internal static void Apply(Window window) { }
        internal static void UseTheme(DependencyObject target, DependencyProperty property, object key) { }
        internal static Button Primary(Button button) => button;
    }
    internal static class DockerLayout
    {
        internal static StackPanel ToolGroup(string title, string help) => new StackPanel();
        internal static StackPanel LabeledField(string title, FrameworkElement field) => new StackPanel { Children = { new TextBlock { Text = title }, field } };
        internal static Grid FieldRow(string title, FrameworkElement field, double width)
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            grid.Children.Add(new TextBlock { Text = title }); Grid.SetColumn(field, 1); grid.Children.Add(field); return grid;
        }
    }
}
