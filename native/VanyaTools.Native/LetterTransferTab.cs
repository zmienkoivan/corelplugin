using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferTab:UserControl
    {
        internal LetterTransferTab(Action<string,bool> status,Func<string> token)
        {
            var panel=DockerLayout.ToolGroup("Буквы-трансферы","Русские заглавные А–Я, включая Ё. Сохранённые варианты букв, частотное количество копий и листы для печати. Референс можно загрузить из файла или взять из выделения Corel. Перед каждым ИИ-запросом показывается превью для подтверждения.");
            panel.Margin=new Thickness(8);Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            var open=DockerTheme.Primary(new Button{Content="Открыть наборы букв…",MinHeight=36});panel.Children.Add(open);
            open.Click+=(_,__)=>{
                try
                {
                    var window=new LetterTransferWindow(token,()=>AiSelectionCapture.Capture(maxDimension:4096,maxPixels:16777216),status,
                        captureReference:()=>AiSelectionCapture.Capture(maxDimension:2048,maxPixels:4194304));var owner=PresentationSource.FromVisual(this) as HwndSource;
                    if(owner!=null)new WindowInteropHelper(window).Owner=owner.Handle;window.ShowDialog();
                }
                catch(Exception error){Log.Error("Letter transfers window failed.",error);status(error.GetBaseException().Message,true);}
            };
        }
    }
}
