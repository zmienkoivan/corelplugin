using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VanyaTools.Native
{
    internal sealed partial class DtfCorrectionWindow
    {
        private static FrameworkElement WithHelp(FrameworkElement title,object description)
        {
            var content=description is string text ? new TextBlock { Text=text,TextWrapping=TextWrapping.Wrap,MaxWidth=360 } : description;
            var help=new ToggleButton {Content="?",Width=21,Height=21,MinWidth=0,Padding=new Thickness(0),Margin=new Thickness(4,0,0,0),VerticalAlignment=VerticalAlignment.Center};
            var tip=new ToolTip {Content=content,MaxWidth=400,Placement=PlacementMode.Right};help.ToolTip=tip;
            help.Checked+=(_,__)=>{tip.PlacementTarget=help;tip.IsOpen=true;};help.Unchecked+=(_,__)=>tip.IsOpen=false;
            tip.Closed+=(_,__)=>help.IsChecked=false;
            System.Windows.Automation.AutomationProperties.SetName(help,"Справка");ToolTipService.SetShowDuration(help,60000);
            var row=new DockPanel {LastChildFill=true};DockPanel.SetDock(help,Dock.Right);row.Children.Add(help);row.Children.Add(title);return row;
        }
    }
}
