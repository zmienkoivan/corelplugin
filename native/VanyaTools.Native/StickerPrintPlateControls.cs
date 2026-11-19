using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
namespace VanyaTools.Native
{
    internal sealed class StickerPrintPlateControls:StackPanel
    {
        internal StickerPrintPlateControls(Action<StickerPrintPlateKind,double> create, Action showPalette)
        {
            var group=DockerLayout.ToolGroup("3. Подложка и лак","Выделите рисунок. Минус ужимает плашку, плюс расширяет. Треппинг — от −0,50 до +0,50 мм, шаг 0,05 мм.");Children.Add(group);
            var paletteButton = new Button { Content = "Палитра WHITE/CLEAR", MinHeight = 27,
                Margin = new Thickness(2, 0, 2, 5), FontSize = 11,
                ToolTip = "Показать плашечные цвета WHITE и CLEAR в палитре CorelDRAW, как CUT. Выделение не требуется." };
            paletteButton.Click += (_, __) => showPalette();
            group.Children.Add(paletteButton);
            bool showing = false;
            Loaded += (_, __) => {
                if (showing) return;
                showing = true;
                try { showPalette(); }
                finally { showing = false; }
            };
            var columns=new UniformGrid{Columns=2};group.Children.Add(columns);
            foreach(var kind in new[]{StickerPrintPlateKind.White,StickerPrintPlateKind.Clear})
            {
                var panel=new StackPanel{Margin=new Thickness(2)};columns.Children.Add(panel);
                var input=new ComboBox{ItemsSource=Enumerable.Range(-10,21).Select(i=>(i*.05).ToString("+0.00;-0.00;0.00",CultureInfo.CurrentCulture)).ToArray(),SelectedIndex=kind==StickerPrintPlateKind.White?8:12,MinHeight=27,Tag=kind,ToolTip="Треппинг, мм: − ужать, + расширить"};
                System.Windows.Automation.AutomationProperties.SetName(input,StickerPrintPlateModel.Ink(kind)+" · треппинг, мм");
                panel.Children.Add(new TextBlock{Text=StickerPrintPlateModel.Ink(kind)+" · треппинг, мм",FontSize=11,Margin=new Thickness(0,0,0,3)});panel.Children.Add(input);
                var button=new Button{Content=kind==StickerPrintPlateKind.White?"Создать подложку":"Создать лак",MinHeight=29,Margin=new Thickness(0,4,0,0),FontSize=11,ToolTip="Печатный силуэт "+StickerPrintPlateModel.Ink(kind)+" с сохранением отверстий и наложением заливки"};
                button.Click+=(_,__)=>create(kind,(input.SelectedIndex-10)*.05);panel.Children.Add(button);
            }
        }
    }
}
