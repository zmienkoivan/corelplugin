using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed class LaserBendTab : UserControl
    {
        private readonly Action<string,bool> _status;
        private readonly TextBlock _message;
        internal LaserBendTab(Action<string,bool> status)
        {
            _status=status;
            var panel=DockerLayout.ToolGroup("3D-гибка",
                "Подготовьте одну замкнутую векторную развёртку. Отверстия — замкнутые внутренние контуры, сгибы — прямые открытые линии от края до края участка. Линии сгиба не должны пересекаться внутри материала. " +
                "Выделите всё вместе и откройте просмотр. В окне выберите линию и сгибаемую сторону, затем меняйте угол. Толщина — 1,5 мм.");
            panel.Margin=new Thickness(8);Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            panel.Children.Add(new TextBlock{Text="1. Выделите контур и линии сгиба\n2. Выберите линию и сторону в окне\n3. Меняйте угол и вращайте модель",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
            var open=DockerTheme.Primary(new Button{Content="Открыть 3D-гибку…",MinHeight=36});panel.Children.Add(open);open.Click+=(_,__)=>Open();
            _message=new TextBlock{Text="Исходный CDR не изменяется.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,9,0,0)};panel.Children.Add(_message);
        }
        private void Open()
        {
            try
            {
                LaserBendModel model;
                using(DockerUiWatchdog.Trace(Dispatcher,"Read laser bend geometry"))model=LaserBendCapture.Capture();
                var window=new LaserBendWindow(model);var owner=PresentationSource.FromVisual(this) as HwndSource;
                if(owner!=null)new WindowInteropHelper(window).Owner=owner.Handle;
                window.ShowDialog();Report("Просмотр 3D-гибки закрыт. Исходная развёртка сохранена.",false);
            }
            catch(Exception error){Log.Error("Laser bend preview failed.",error);Report(error.GetBaseException().Message,true);}
        }
        private void Report(string text,bool error)
        {
            _message.Text=text;DockerTheme.UseTheme(_message,TextBlock.ForegroundProperty,error?DockerTheme.ErrorTextBrush:DockerTheme.MutedTextBrush);_status(text,error);
        }
    }
}
