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
        internal StickerPrintPlateControls(Action<StickerPrintPlateKind,double,int> create, Action showPalette,
            StickerPrintPlateSettings settings=null)
        {
            settings=settings??new StickerPrintPlateSettings();
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
                panel.Children.Add(new TextBlock{Text="Плотность заливки, %",FontSize=11,Margin=new Thickness(0,5,0,3)});
                var density=new TextBox{Text=settings.Tint(kind).ToString(CultureInfo.CurrentCulture),MinHeight=27,
                    Tag=kind,ToolTip="От 10 до 100%. Значение запоминается отдельно для каждого цвета."};
                System.Windows.Automation.AutomationProperties.SetName(density,StickerPrintPlateModel.Ink(kind)+" · плотность заливки, %");
                panel.Children.Add(density);
                var button=new Button{Content=kind==StickerPrintPlateKind.White?"Создать подложку":"Создать лак",MinHeight=29,Margin=new Thickness(0,4,0,0),FontSize=11,ToolTip="Печатный силуэт "+StickerPrintPlateModel.Ink(kind)+" с сохранением отверстий и наложением заливки"};
                var errorText=new TextBlock{FontSize=10,TextWrapping=TextWrapping.Wrap,
                    Foreground=System.Windows.Media.Brushes.Firebrick,Visibility=Visibility.Collapsed};
                density.TextChanged+=(_,__)=>{
                    int value;
                    bool valid=Int32.TryParse(density.Text,NumberStyles.Integer,CultureInfo.CurrentCulture,out value) && value>=10 && value<=100;
                    button.IsEnabled=valid;
                    errorText.Text=valid?"":"Введите число от 10 до 100%.";
                    if(valid)try{settings.SetTint(kind,value);}catch(Exception error){
                        Log.Error("Could not save WHITE/CLEAR fill density.",error);
                        errorText.Text="Не удалось запомнить значение. "+error.Message;
                    }
                    errorText.Visibility=errorText.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
                };
                button.Click+=(_,__)=>create(kind,(input.SelectedIndex-10)*.05,
                    StickerPrintPlateModel.ValidateTint(Int32.Parse(density.Text,CultureInfo.CurrentCulture)));
                panel.Children.Add(button);panel.Children.Add(errorText);
            }
        }
    }
}
