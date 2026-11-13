using System;
using System.Windows;
using System.Windows.Controls;
namespace VanyaTools.Native
{
    internal static class CorelApp { internal static object Get() => throw new InvalidOperationException("No live Corel in checks."); }
    internal static class DockerTheme
    {
        internal const string MutedTextBrush="Muted",WindowBackground="Window";
        internal static void Apply(FrameworkElement element) { }
        internal static void UseTheme(FrameworkElement element,DependencyProperty property,string key) { }
    }
    internal static class DockerLayout
    {
        internal static StackPanel ToolGroup(string title,string help=null)
        { var panel=new StackPanel();panel.Children.Add(new TextBlock{Text=title});return panel; }
    }
}
