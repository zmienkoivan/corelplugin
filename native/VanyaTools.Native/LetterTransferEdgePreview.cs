using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class LetterTransferEdgePreview
    {
        internal static Window Create(Window owner,string letter,BitmapSource before,BitmapSource after)
        {
            var window=new Window{Owner=owner,Title="Проверка края · "+letter+" · Vanya Tools",Width=1000,Height=750,MinWidth=650,MinHeight=450,WindowStartupLocation=WindowStartupLocation.CenterOwner};DockerTheme.Apply(window);
            var root=new DockPanel{Margin=new Thickness(12)};window.Content=root;var tools=new WrapPanel();DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);
            var background=new ComboBox{ItemsSource=new[]{"Серый фон","Белый фон","Тёмный фон","Шахматка"},SelectedIndex=0,Width=140,Margin=new Thickness(3)};tools.Children.Add(background);
            var zoom=new ComboBox{ItemsSource=new[]{"×1","×2","×4","×8"},SelectedIndex=0,Width=80,Margin=new Thickness(3)};tools.Children.Add(zoom);
            var note=new TextBlock{Text="Слева сохранённый PNG, справа новый край. Сравните окантовку и отверстия на разных фонах. Для применения ко всем буквам закройте просмотр и нажмите «Обновить край всех букв».",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,7,3,9)};DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
            var close=new Button{Content="Закрыть",HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(18,6,18,6),Margin=new Thickness(3),IsCancel=true};close.Click+=(_,__)=>window.Close();DockPanel.SetDock(close,Dock.Bottom);root.Children.Add(close);
            var panel=new StackPanel{Orientation=Orientation.Horizontal};var back=new Border{Child=panel,Background=Brushes.LightGray};var scroll=new ScrollViewer{Content=back,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};root.Children.Add(scroll);
            foreach(var item in new[]{Tuple.Create("Сохранённый PNG",before),Tuple.Create("Новый край",after)})
            {
                var column=new StackPanel{Margin=new Thickness(15)};column.Children.Add(new TextBlock{Text=item.Item1,Foreground=Brushes.Black,Background=Brushes.White,Margin=new Thickness(0,0,0,12)});
                var image=new Image{Source=item.Item2,Height=400,Width=400d*item.Item2.PixelWidth/item.Item2.PixelHeight,Stretch=Stretch.Fill};RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);column.Children.Add(image);panel.Children.Add(column);
            }
            background.SelectionChanged+=(_,__)=>back.Background=background.SelectedIndex==1?Brushes.White:background.SelectedIndex==2?new SolidColorBrush(Color.FromRgb(45,48,54)):background.SelectedIndex==3?LetterTransferImages.Checker():Brushes.LightGray;
            zoom.SelectionChanged+=(_,__)=>{double scale=Math.Pow(2,zoom.SelectedIndex);panel.LayoutTransform=new ScaleTransform(scale,scale);};return window;
        }
    }
}
