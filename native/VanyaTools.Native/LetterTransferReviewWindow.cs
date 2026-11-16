using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferApproved
    {
        internal string Letter;
        internal BitmapSource Image;
    }
    internal static class LetterTransferReviewWindow
    {
        private static Window Dialog(Window owner,string title)
        {
            var window=new Window{Owner=owner,Title=title+" · Vanya Tools",Width=Math.Min(950,SystemParameters.WorkArea.Width-30),Height=Math.Min(760,SystemParameters.WorkArea.Height-30),MinWidth=650,MinHeight=450,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            DockerTheme.Apply(window);return window;
        }
        internal static bool Confirm(Window owner,BitmapSource reference,string[] letters,string model,string resolution,string prompt,bool retry=false)
        {
            return Confirmation(owner,reference,letters,model,resolution,prompt,retry).ShowDialog()==true;
        }
        internal static Window Confirmation(Window owner,BitmapSource reference,string[] letters,string model,string resolution,string prompt,bool retry=false)
        {
            var window=Dialog(owner,"Отправка референса в ИИ");var root=new DockPanel{Margin=new Thickness(12)};window.Content=root;
            var note=new TextBlock{Text="Отправить это превью в "+model+" через Replicate?\nБуквы: "+String.Join(", ",letters)+" · "+resolution+" · один платный запрос."+
                (retry?"\nСохранённый запрос будет продолжен. Если предыдущая генерация завершилась ошибкой или была отменена, повтор создаст новый платный запрос.":""),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
            DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
            var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var cancel=Button("Отмена",()=>window.DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);footer.Children.Add(DockerTheme.Primary(Button("Подтвердить отправку",()=>window.DialogResult=true)));
            var preview=new Image{Source=reference,Stretch=Stretch.Uniform,Width=330,Margin=new Thickness(0,0,12,0)};DockPanel.SetDock(preview,Dock.Left);root.Children.Add(preview);
            root.Children.Add(new TextBox{Text=prompt,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            return window;
        }
        internal static List<LetterTransferApproved> Review(Window owner,BitmapSource reference,BitmapSource sheet,BitmapSource[] cells,string[] letters)
        {
            var window=Dialog(owner,"Проверка букв");var root=new DockPanel{Margin=new Thickness(12)};window.Content=root;
            var checks=new CheckBox[cells.Length];var choices=new ComboBox[cells.Length];var approved=new List<LetterTransferApproved>();
            var note=new TextBlock{Text="Фон и полупрозрачность удалены пакетно. Проверьте, что в каждой ячейке ровно одна целая буква, верная кириллица и нужный стиль. Отметьте подходящие. Можно исправить обозначение буквы. Непринятые ячейки не добавляются; исходный лист останется в истории.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
            var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            footer.Children.Add(Button("Назад",()=>window.DialogResult=false));
            footer.Children.Add(DockerTheme.Primary(Button("Добавить отмеченные",()=>{
                for(int i=0;i<cells.Length;i++)if(checks[i].IsChecked==true && cells[i]!=null)approved.Add(new LetterTransferApproved{Letter=(string)choices[i].SelectedItem,Image=cells[i]});
                if(approved.Count==0){MessageBox.Show(window,"Отметьте хотя бы одну проверенную букву.");return;}window.DialogResult=true;
            })));
            var tabs=new TabControl();root.Children.Add(tabs);var grid=new UniformGrid{Columns=cells.Length==1?1:2};tabs.Items.Add(new TabItem{Header="Буквы",Content=grid});
            tabs.Items.Add(new TabItem{Header="Исходный лист ИИ",Content=new Image{Source=sheet,Stretch=Stretch.Uniform}});
            tabs.Items.Add(new TabItem{Header="Референс",Content=new Image{Source=reference,Stretch=Stretch.Uniform}});
            for(int i=0;i<cells.Length;i++)
            {
                var card=new DockPanel{Margin=new Thickness(9)};var header=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(header,Dock.Top);card.Children.Add(header);
                checks[i]=new CheckBox{Content="Принять",IsEnabled=cells[i]!=null,IsChecked=false,VerticalAlignment=VerticalAlignment.Center};header.Children.Add(checks[i]);
                choices[i]=new ComboBox{ItemsSource=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray(),SelectedItem=letters[i],Width=65,Margin=new Thickness(8,0,0,0)};header.Children.Add(choices[i]);
                if(cells[i]!=null)card.Children.Add(new Image{Source=cells[i],Stretch=Stretch.Uniform,Margin=new Thickness(5)});
                else card.Children.Add(new TextBlock{Text="Ячейка пустая, обрезана или фон не отделился. Откройте исходный лист, обработайте букву вручную и импортируйте PNG.",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center});
                var border=new Border{Child=card,Background=LetterTransferImages.Checker(),BorderThickness=new Thickness(1),Margin=new Thickness(3)};DockerTheme.UseTheme(border,Border.BorderBrushProperty,DockerTheme.BorderBrush);grid.Children.Add(border);
            }
            return window.ShowDialog()==true?approved:new List<LetterTransferApproved>();
        }
        private static Button Button(string text,Action action)
        {var b=new Button{Content=text,MinHeight=34,Margin=new Thickness(3),Padding=new Thickness(9,4,9,4)};b.Click+=(_,__)=>action();return b;}
    }
}
