using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VanyaTools.Native
{
    internal sealed class LaserBendWindow : Window
    {
        private readonly LaserBendModel _model;
        private readonly LaserBendFlatView _flat;
        private readonly LaserBendScene _scene;
        private readonly ComboBox _folds;
        private readonly Slider _angle;
        private readonly TextBox _number;
        private readonly TextBlock _side;
        private readonly StackPanel _controls;
        private bool _sync;
        internal int Selected => _folds.SelectedIndex;
        internal LaserBendWindow(LaserBendModel model)
        {
            _model=model;Title="3D-гибка · Vanya Tools";
            Width=Math.Min(1280,SystemParameters.WorkArea.Width-24);Height=Math.Min(850,SystemParameters.WorkArea.Height-24);
            MinWidth=820;MinHeight=540;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);
            var root=new DockPanel{Margin=new Thickness(12)};Content=root;
            DockerTheme.UseTheme(root,Panel.BackgroundProperty,DockerTheme.WindowBackground);
            var footer=new DockPanel{Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var close=Button("Закрыть",()=>Close());close.IsCancel=true;DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);
            var help=new TextBlock{Text="3D: мышь — вращение · колесо — масштаб · правая кнопка — перемещение",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,FontSize=12};
            DockerTheme.UseTheme(help,TextBlock.ForegroundProperty,DockerTheme.MutedTextBrush);footer.Children.Add(help);
            var header=new DockPanel{Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
            var info=new TextBlock{Text="Толщина 1,5 мм",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};
            DockPanel.SetDock(info,Dock.Right);header.Children.Add(info);
            var title=DockerLayout.ToolGroup("3D-гибка",
                "Выделите в Corel одну цельную развёртку с отверстиями и прямыми линиями сгиба. Выберите сгиб по номеру или нажатием на линию слева. Затем нажмите на сгибаемую часть слева или в 3D: она подсветится синим. " +
                "Углы всех сгибов сохраняются, пока открыто окно. Клапаны, соединённые со сгибаемой частью, двигаются вместе с ней. " +
                "Толщина — 1,5 мм. Это визуальная сборка с идеальной осью сгиба: без радиуса гибки, растяжения материала, расчёта развёртки и контроля столкновений. Исходный документ не меняется.");
            header.Children.Add(title);
            var bar=new WrapPanel{Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
            _folds=new ComboBox{Width=220,MinHeight=32,Margin=new Thickness(0,0,10,0),ItemsSource=model.Lines.Select((line,i)=>(i+1)+" · "+line.Name).ToArray()};bar.Children.Add(_folds);
            _controls=new StackPanel{Orientation=Orientation.Horizontal};bar.Children.Add(_controls);
            _controls.Children.Add(new TextBlock{Text="Угол",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,7,0)});
            _angle=new Slider{Minimum=-180,Maximum=180,Width=210,TickFrequency=1,IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};_controls.Children.Add(_angle);
            _number=new TextBox{Width=58,MinHeight=32,Text="0",VerticalContentAlignment=VerticalAlignment.Center};_controls.Children.Add(_number);
            _controls.Children.Add(new TextBlock{Text="°",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3,0,8,0)});
            foreach(int value in new[]{-90,0,90}){int angle=value;_controls.Children.Add(Button(value+"°",()=>SetAngle(angle)));}
            _controls.Children.Add(Button("Другая сторона",FlipSide));
            bar.Children.Add(Button("Сбросить сгибы",Reset));
            var split=new Grid();split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(2,GridUnitType.Star)});
            split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(8)});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(3,GridUnitType.Star)});root.Children.Add(split);
            _flat=new LaserBendFlatView(model);_scene=new LaserBendScene(model);
            var left=new DockPanel();var flatBar=new DockPanel{Margin=new Thickness(0,0,0,6)};
            var fit=Button("Вписать",()=>_flat.Fit());DockPanel.SetDock(fit,Dock.Right);flatBar.Children.Add(fit);
            flatBar.Children.Add(new TextBlock{Text="1 · Линия → 2 · Сторона",VerticalAlignment=VerticalAlignment.Center});DockPanel.SetDock(flatBar,Dock.Top);left.Children.Add(flatBar);
            _side=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0),FontSize=12};DockPanel.SetDock(_side,Dock.Bottom);left.Children.Add(_side);left.Children.Add(_flat);split.Children.Add(left);
            var right=new DockPanel();var sceneBar=new DockPanel{Margin=new Thickness(0,0,0,6)};
            var fit3=Button("Вписать",()=>_scene.Fit());DockPanel.SetDock(fit3,Dock.Right);sceneBar.Children.Add(fit3);
            var resetView=Button("Исходный вид",()=>_scene.Fit(true));DockPanel.SetDock(resetView,Dock.Right);sceneBar.Children.Add(resetView);
            sceneBar.Children.Add(new TextBlock{Text="3D",VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold});
            DockPanel.SetDock(sceneBar,Dock.Top);right.Children.Add(sceneBar);right.Children.Add(_scene);Grid.SetColumn(right,2);split.Children.Add(right);
            _folds.SelectionChanged+=(_,__)=>Sync();_flat.SelectFold+=index=>_folds.SelectedIndex=index;
            _flat.SelectPanel+=ChooseSide;_scene.SelectPanel+=ChooseSide;
            _angle.ValueChanged+=(_,__)=>{if(!_sync)SetAngle(_angle.Value);};
            _number.LostKeyboardFocus+=(_,__)=>ReadAngle();_number.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ReadAngle();e.Handled=true;}};
            _folds.SelectedIndex=model.Lines.Length>0?0:-1;Sync();
            Loaded+=(_,__)=>{_flat.Fit();_scene.Fit();};
        }
        private static Button Button(string text,Action action)
        {
            var button=new Button{Content=text,MinHeight=32,Padding=new Thickness(10,3,10,3),Margin=new Thickness(0,0,5,0)};
            button.Click+=(_,__)=>action();return button;
        }
        internal void SetAngle(double angle)
        {
            if(Selected<0)return;
            _model.Lines[Selected].Angle=Math.Max(-180,Math.Min(180,angle));Sync();
        }
        private void ReadAngle()
        {
            if(double.TryParse(_number.Text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out double angle) && !double.IsNaN(angle) && !double.IsInfinity(angle))SetAngle(angle);
            else Sync();
        }
        internal void ChooseSide(int panel) { if(Selected<0)return;_model.ChooseSide(Selected,panel);Sync(); }
        internal void FlipSide() { if(Selected<0)return;_model.Lines[Selected].MovingSide*=-1;Sync(); }
        internal void Reset() { foreach(var fold in _model.Lines)fold.Angle=0;Sync(); }
        private void Sync()
        {
            _sync=true;
            try
            {
                _controls.IsEnabled=Selected>=0;_flat.Selected=Selected;
                if(Selected>=0)
                {
                    var fold=_model.Lines[Selected];_angle.Value=fold.Angle;_number.Text=fold.Angle.ToString("0.#",CultureInfo.CurrentCulture);
                    _side.Text="Синим — сгибаемая сторона. Нажмите на другую часть, чтобы сменить её. Сгибов: "+_model.Lines.Length+" · граней: "+_model.Panels.Length;
                }
                else _side.Text="Линии сгиба не найдены. Добавьте в Corel прямые открытые линии от края до края участка и выделите их вместе с контуром.";
                _flat.InvalidateVisual();_scene.Update(Selected);
            }
            finally{_sync=false;}
        }
    }
}
