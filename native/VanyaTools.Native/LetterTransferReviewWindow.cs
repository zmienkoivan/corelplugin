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
            var note=new TextBlock{Text="Отправить это превью в "+model+" через Replicate?\nБуквы: "+String.Join(", ",letters)+" · "+resolution+" · один платный запрос.\nПревью референса: "+reference.PixelWidth+" × "+reference.PixelHeight+" px."+
                (retry?"\nСохранённый запрос будет продолжен. Если предыдущая генерация завершилась ошибкой или была отменена, повтор создаст новый платный запрос.":""),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
            DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
            var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var cancel=Button("Отмена",()=>window.DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);footer.Children.Add(DockerTheme.Primary(Button("Подтвердить отправку",()=>window.DialogResult=true)));
            var preview=new Image{Source=reference,Stretch=Stretch.Uniform,Width=330,Margin=new Thickness(0,0,12,0)};DockPanel.SetDock(preview,Dock.Left);root.Children.Add(preview);
            root.Children.Add(new TextBox{Text=prompt,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            return window;
        }
        internal static List<LetterTransferApproved> Review(Window owner,BitmapSource reference,BitmapSource sheet,LetterTransferSheet result)
        {
            var approved=new List<LetterTransferApproved>();var window=ReviewDialog(owner,reference,sheet,result,approved);
            return window.ShowDialog()==true?approved:new List<LetterTransferApproved>();
        }
        internal static Window ReviewDialog(Window owner,BitmapSource reference,BitmapSource sheet,LetterTransferSheet result,List<LetterTransferApproved> approved)
        {
            var cells=result.Cells;var window=Dialog(owner,"Проверка букв");var root=new DockPanel{Margin=new Thickness(12)};window.Content=root;
            var checks=new CheckBox[cells.Length];var choices=new ComboBox[cells.Length];var excluded=new bool[cells.Length];
            var note=new TextBlock{Text="Подготовлено ячеек: "+cells.Count(c=>c.Image!=null)+" из "+cells.Length+". "+(result.Notice??"")+" Проверьте целостность, кириллицу и стиль. Отметьте подходящие. Непринятые ячейки не добавляются; исходный лист останется в истории.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
            var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            footer.Children.Add(Button("Назад",()=>window.DialogResult=false));
            footer.Children.Add(Button("Отметить все целые",()=>{for(int i=0;i<checks.Length;i++)if(checks[i].IsEnabled && cells[i].Safe)checks[i].IsChecked=true;}));
            if(result.LayoutChanged)footer.Children.Add(Button("Назначить по порядку",()=>{
                var selected=Enumerable.Range(0,cells.Length).Where(i=>!excluded[i] && cells[i].Image!=null).ToArray();
                if(selected.Length!=result.Requested.Length){MessageBox.Show(window,"Оставьте "+result.Requested.Length+" ячеек: сейчас "+selected.Length+". Отметьте лишние ячейки или назначьте обозначения вручную.");return;}
                for(int n=0;n<selected.Length;n++)choices[selected[n]].SelectedItem=result.Requested[n];
            }));
            footer.Children.Add(DockerTheme.Primary(Button("Добавить отмеченные",()=>{
                for(int i=0;i<cells.Length;i++)if(checks[i].IsChecked==true && cells[i].Image!=null && LetterTransferRules.ValidLetter(choices[i].SelectedItem as string))approved.Add(new LetterTransferApproved{Letter=(string)choices[i].SelectedItem,Image=cells[i].Image});
                if(approved.Count==0){MessageBox.Show(window,"Назначьте обозначения и отметьте хотя бы одну проверенную букву.");return;}window.DialogResult=true;
            })));
            var tabs=new TabControl();root.Children.Add(tabs);var grid=new UniformGrid{Columns=LetterTransferRules.Columns(cells.Length)};tabs.Items.Add(new TabItem{Header="Буквы",Content=new ScrollViewer{Content=grid,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            tabs.Items.Add(new TabItem{Header="Исходный лист ИИ",Content=new Image{Source=sheet,Stretch=Stretch.Uniform}});
            tabs.Items.Add(new TabItem{Header="Референс",Content=new Image{Source=reference,Stretch=Stretch.Uniform}});
            for(int i=0;i<cells.Length;i++)
            {
                int index=i;var cell=cells[i];var card=new DockPanel{Margin=new Thickness(9)};var header=new WrapPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(header,Dock.Top);card.Children.Add(header);
                header.Children.Add(new TextBlock{Text=(i+1)+" · ",VerticalAlignment=VerticalAlignment.Center});
                checks[i]=new CheckBox{Content="Принять",Tag="accept",IsEnabled=cell.Image!=null && LetterTransferRules.ValidLetter(cell.Letter),IsChecked=false,VerticalAlignment=VerticalAlignment.Center};header.Children.Add(checks[i]);
                choices[i]=new ComboBox{ItemsSource=new[]{"—"}.Concat(LetterTransferRules.Alphabet.Select(c=>c.ToString())).ToArray(),SelectedItem=cell.Letter??"—",Width=65,Margin=new Thickness(8,0,0,0)};
                choices[i].SelectionChanged+=(_,__)=>{checks[index].IsEnabled=!excluded[index] && cells[index].Image!=null && LetterTransferRules.ValidLetter(choices[index].SelectedItem as string);if(!checks[index].IsEnabled)checks[index].IsChecked=false;};header.Children.Add(choices[i]);
                if(result.LayoutChanged)
                {
                    var skip=new CheckBox{Content="Лишняя ячейка",Tag="skip",Margin=new Thickness(0,4,0,2)};DockPanel.SetDock(skip,Dock.Top);card.Children.Add(skip);
                    skip.Checked+=(_,__)=>{excluded[index]=true;checks[index].IsChecked=false;checks[index].IsEnabled=false;choices[index].IsEnabled=false;};
                    skip.Unchecked+=(_,__)=>{excluded[index]=false;choices[index].IsEnabled=true;checks[index].IsEnabled=cells[index].Image!=null && LetterTransferRules.ValidLetter(choices[index].SelectedItem as string);};
                }
                if(!String.IsNullOrEmpty(cell.Warning))
                {var warning=new TextBlock{Text=cell.Warning,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,3)};DockPanel.SetDock(warning,Dock.Bottom);card.Children.Add(warning);}
                card.Children.Add(new Image{Source=cell.Preview,Height=cells.Length>4?140:Double.NaN,Stretch=Stretch.Uniform,Margin=new Thickness(5)});
                var border=new Border{Child=card,Background=LetterTransferImages.Checker(),BorderThickness=new Thickness(1),Margin=new Thickness(3)};DockerTheme.UseTheme(border,Border.BorderBrushProperty,DockerTheme.BorderBrush);grid.Children.Add(border);
            }
            return window;
        }
        private static Button Button(string text,Action action)
        {var b=new Button{Content=text,MinHeight=34,Margin=new Thickness(3),Padding=new Thickness(9,4,9,4)};b.Click+=(_,__)=>action();return b;}
    }
}
