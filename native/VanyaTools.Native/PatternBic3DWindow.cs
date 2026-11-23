using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class PatternBic3DWindow : Window
    {
        internal readonly PatternBic3DScene Scene=new PatternBic3DScene();
        private readonly TextBlock _status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,5,3,3)};
        private readonly DispatcherTimer _refresh=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(160)};
        private readonly PatternAsset _hero;
        private readonly Func<PatternAsset,BitmapSource> _image;
        private PatternDraft _pending;
        internal PatternBic3DWindow(PatternAsset hero,Func<PatternAsset,BitmapSource> image)
        {
            _hero=hero;_image=image;Title="BIC · 3D-просмотр паттерна · Vanya Tools";
            Width=Math.Min(700,SystemParameters.WorkArea.Width-40);Height=Math.Min(780,SystemParameters.WorkArea.Height-40);
            MinWidth=380;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;DockerTheme.Apply(this);
            var root=new DockPanel{Margin=new Thickness(10)};Content=root;
            var tools=new WrapPanel{Margin=new Thickness(0,0,0,6)};DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);
            Add("Спереди",()=>Scene.View(0));Add("Сзади / стык",()=>Scene.View(180));Add("Сбоку",()=>Scene.View(90));Add("Вписать",()=>Scene.Fit());
            var seam=new CheckBox{Content="Показать стык",Margin=new Thickness(6),VerticalAlignment=VerticalAlignment.Center};
            seam.Checked+=(_,__)=>Scene.ShowSeam=true;seam.Unchecked+=(_,__)=>Scene.ShowSeam=false;tools.Children.Add(seam);
            var hint=new TextBlock{Text="Перетаскивание мышью — вращение; колесо — масштаб. Вид обновляется вместе с паттерном.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,3,3,5)};
            DockPanel.SetDock(hint,Dock.Bottom);root.Children.Add(hint);DockPanel.SetDock(_status,Dock.Bottom);root.Children.Add(_status);root.Children.Add(Scene);
            _refresh.Tick+=(_,__)=>{_refresh.Stop();var draft=_pending;_pending=null;if(draft==null)return;try{Scene.Set(draft.Frame.Bottle,PatternBic3DTexture.Create(draft,_hero,_image));_status.Text="Текущий паттерн · высота печати "+draft.Frame.Bottle.Height.ToString("0.#")+" мм · условная форма корпуса BIC";}catch(Exception error){_status.Text="3D не обновлён: "+error.Message;Log.Error("BIC 3D preview failed.",error);}};
            Closed+=(_,__)=>{_refresh.Stop();_pending=null;};
            void Add(string title,Action action){var button=new Button{Content=title,Margin=new Thickness(2),Padding=new Thickness(9,4,9,4)};button.Click+=(_,__)=>action();tools.Children.Add(button);}
        }
        internal void Queue(PatternDraft draft){_pending=draft;_refresh.Stop();_refresh.Start();}
        internal void Waiting(){_refresh.Stop();_pending=null;_status.Text="Обновляю паттерн… Пока показан предыдущий вид.";}
        internal void Failed(string message){_refresh.Stop();_pending=null;_status.Text="Паттерн не обновлён: "+message+". Показан предыдущий вид.";}
    }
}
