using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class PrintRestorationReviewWindow : Window
    {
        private readonly Image[] _images;
        private readonly ScrollViewer[] _scrolls;
        private double _zoom=1;
        private bool _sync;
        private readonly int _width,_height;
        private PrintRestorationReviewWindow(string source,string output,PrintRestorationRequest request)
        {
            bool consent=request!=null;
            Title=consent?"Подтверждение отправки · Nano Banana 2":"Сверка восстановленного принта";
            Width=Math.Min(1180,SystemParameters.WorkArea.Width-40);Height=Math.Min(780,SystemParameters.WorkArea.Height-40);MinWidth=760;MinHeight=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);var root=new DockPanel{Margin=new Thickness(12)};Content=root;DockerTheme.UseTheme(root,Panel.BackgroundProperty,DockerTheme.WindowBackground);
            var actions=new DockPanel{LastChildFill=false,Margin=new Thickness(0,9,0,0)};DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
            var no=new Button{Content=consent?"Не отправлять":"Вернуться",MinHeight=34,MinWidth=100,IsCancel=true,Margin=new Thickness(7,0,0,0)};
            no.Click+=(_,__)=>DialogResult=false;DockPanel.SetDock(no,Dock.Right);actions.Children.Add(no);
            var yes=DockerTheme.Primary(new Button{Content=consent?"Отправить · платный запрос":"Рисунок сверен — принять",MinHeight=34,MinWidth=195});
            yes.Click+=(_,__)=>DialogResult=true;DockPanel.SetDock(yes,Dock.Right);actions.Children.Add(yes);
            var hint=new TextBlock{Text=consent?"Будет отправлено только показанное изображение в Replicate → Nano Banana 2 · "+request.Resolution+". Один платный запрос. Ничего не отправлено до нажатия кнопки.":
                "Сверьте буквы, снежинки и другие символы, потёртости, мелкие штрихи и расположение. ИИ может менять детали. В Corel пока ничего не вставляется.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,9)};
            DockPanel.SetDock(hint,Dock.Top);root.Children.Add(hint);
            var tools=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,7)};DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);
            foreach(string item in new[]{"−","+","1:1","Вписать"})
            {
                string caption=item;var button=new Button{Content=item,MinHeight=28,MinWidth=38,Margin=new Thickness(0,0,4,0)};
                button.Click+=(_,__)=>{if(caption=="Вписать")Fit();else SetZoom(caption=="1:1"?1:_zoom*(caption=="+"?1.25:.8));};tools.Children.Add(button);
            }
            tools.Children.Add(new TextBlock{Text="Колесо — масштаб · прокрутка синхронная",VerticalAlignment=VerticalAlignment.Center,FontSize=11,Margin=new Thickness(8,0,0,0)});
            var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());root.Children.Add(grid);
            var original=PrintRestorationImages.Load(source);_width=original.PixelWidth;_height=original.PixelHeight;
            _images=new Image[consent?1:2];_scrolls=new ScrollViewer[_images.Length];
            AddImage(0,"Исходник",original);
            if(!consent)
            {
                var restored=PrintRestorationImages.Load(output);
                if(Math.Abs((double)restored.PixelWidth/restored.PixelHeight/((double)_width/_height)-1)>.02)
                    hint.Text+="\nИИ изменил пропорции изображения. Сравнение показано в исходном размере — особенно внимательно проверьте геометрию.";
                AddImage(1,"Результат ИИ",restored);
            }
            else
            {
                var prompt=new DockPanel{Margin=new Thickness(10,0,0,0)};
                var label=new TextBlock{Text="Промпт · "+(request.KeyColor.HasValue?"фон "+PrintRestorationRequest.Hex(request.KeyColor.Value):"исходный фон"),Margin=new Thickness(0,0,0,5)};DockPanel.SetDock(label,Dock.Top);prompt.Children.Add(label);
                prompt.Children.Add(new TextBox{Text=request.Prompt,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(8)});
                Grid.SetColumn(prompt,1);grid.Children.Add(prompt);
            }
            Loaded+=(_,__)=>Fit();
            void AddImage(int index,string name,BitmapSource image)
            {
                var panel=new DockPanel{Margin=new Thickness(index==0?0:5,0,index==0?5:0,0)};
                var title=new TextBlock{Text=name,Margin=new Thickness(0,0,0,5)};DockPanel.SetDock(title,Dock.Top);panel.Children.Add(title);
                var picture=new Image{Source=image,Stretch=Stretch.Fill,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
                var scroll=new ScrollViewer{Content=picture,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Brushes.LightGray};
                _images[index]=picture;_scrolls[index]=scroll;panel.Children.Add(scroll);Grid.SetColumn(panel,index);grid.Children.Add(panel);
                scroll.PreviewMouseWheel+=(_,e)=>{SetZoom(_zoom*Math.Pow(1.25,e.Delta/120d));e.Handled=true;};
                scroll.ScrollChanged+=(_,e)=> {
                    if(_sync)return;_sync=true;try{foreach(var other in _scrolls)if(other!=null && other!=scroll){other.ScrollToHorizontalOffset(scroll.HorizontalOffset);other.ScrollToVerticalOffset(scroll.VerticalOffset);}}finally{_sync=false;}
                };
            }
        }
        private void Fit()
        {
            double scale=Double.MaxValue;foreach(var scroll in _scrolls)scale=Math.Min(scale,Math.Min(Math.Max(1,scroll.ViewportWidth)/_width,Math.Max(1,scroll.ViewportHeight)/_height));
            SetZoom(scale);
        }
        private void SetZoom(double zoom)
        {
            double previous=_zoom;_zoom=Math.Max(.01,Math.Min(16,zoom));
            foreach(var image in _images){image.Width=_width*_zoom;image.Height=_height*_zoom;}
            foreach(var scroll in _scrolls){double x=(scroll.HorizontalOffset+scroll.ViewportWidth/2)/previous*_zoom-scroll.ViewportWidth/2,y=(scroll.VerticalOffset+scroll.ViewportHeight/2)/previous*_zoom-scroll.ViewportHeight/2;scroll.ScrollToHorizontalOffset(x);scroll.ScrollToVerticalOffset(y);}
        }
        internal static bool Confirm(Window owner,PrintRestorationRequest request)=>new PrintRestorationReviewWindow(request.SourcePath,null,request){Owner=owner}.ShowDialog()==true;
        internal static bool Review(Window owner,string source,string output)=>new PrintRestorationReviewWindow(source,output,null){Owner=owner}.ShowDialog()==true;
    }
}
