using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferApproved
    {
        internal string Letter;
        internal BitmapSource Image,Original;
        internal Int32Rect Bounds;
    }
    internal static class LetterTransferReviewWindow
    {
        private static Window Dialog(Window owner,string title)
        {
            var window=new Window{Owner=owner,Title=title+" · Vanya Tools",Width=Math.Min(950,SystemParameters.WorkArea.Width-30),Height=Math.Min(760,SystemParameters.WorkArea.Height-30),MinWidth=650,MinHeight=450,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            DockerTheme.Apply(window);return window;
        }
        internal static bool Confirm(Window owner,BitmapSource reference,string[] letters,string model,string resolution,string prompt,bool retry=false,string sequenceNote=null)
        {
            return Confirmation(owner,reference,letters,model,resolution,prompt,retry,sequenceNote).ShowDialog()==true;
        }
        internal static Window Confirmation(Window owner,BitmapSource reference,string[] letters,string model,string resolution,string prompt,bool retry=false,string sequenceNote=null)
        {
            string note=(String.IsNullOrWhiteSpace(sequenceNote)?"":sequenceNote+"\n")+"Отправить это превью в "+model+" через Replicate?\nБуквы: "+String.Join(", ",letters)+" · "+resolution+" · один платный запрос.\nПревью референса: "+reference.PixelWidth+" × "+reference.PixelHeight+" px."+
                (retry?"\nСохранённый запрос будет продолжен. Если предыдущая генерация завершилась ошибкой или была отменена, повтор создаст новый платный запрос.":"");
            return ConfirmationDialog(owner,"Отправка референса в ИИ",reference,note,prompt);
        }
        internal static bool ConfirmRecognition(Window owner,BitmapSource preview,int count,string prompt,bool retry,string sourceNote=null)
            =>RecognitionConfirmation(owner,preview,count,prompt,retry,sourceNote).ShowDialog()==true;
        internal static Window RecognitionConfirmation(Window owner,BitmapSource preview,int count,string prompt,bool retry=false,string sourceNote=null)
        {
            string note=(String.IsNullOrWhiteSpace(sourceNote)?"":sourceNote+"\n")+"Отправить показанный коллаж из "+count+" ячеек в GPT-5 mini через Replicate для распознавания букв? Это отдельный платный запрос анализа. Рисунки не перерисовываются.\n"+
                "Повторы получат одинаковое обозначение и сохранятся как варианты. Размер превью: "+preview.PixelWidth+" × "+preview.PixelHeight+" px.\n"+
                (retry?"Есть сохранённая попытка. Продолжение активного запроса не создаёт новую генерацию; повтор после завершившейся ошибки оплачивается отдельно.":"Результат распознавания сохранится в истории; его повторное открытие не требует нового анализа.");
            return ConfirmationDialog(owner,"Распознавание букв · подтверждение",preview,note,prompt);
        }
        private static Window ConfirmationDialog(Window owner,string title,BitmapSource reference,string text,string prompt)
        {
            var window=Dialog(owner,title);var root=new DockPanel{Margin=new Thickness(12)};window.Content=root;
            var note=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
            var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var cancel=Button("Отмена",()=>window.DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);footer.Children.Add(DockerTheme.Primary(Button("Подтвердить отправку",()=>window.DialogResult=true)));
            var preview=new Image{Source=reference,Stretch=Stretch.Uniform,Width=330,Margin=new Thickness(0,0,12,0)};DockPanel.SetDock(preview,Dock.Left);root.Children.Add(preview);
            root.Children.Add(new TextBox{Text=prompt,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});return window;
        }
        internal static List<LetterTransferApproved> Review(Window owner,BitmapSource reference,BitmapSource sheet,LetterTransferSheet result)
        {
            var approved=new List<LetterTransferApproved>();var window=ReviewDialog(owner,reference,sheet,result,approved);
            return window.ShowDialog()==true?approved:new List<LetterTransferApproved>();
        }
        internal static Window ReviewDialog(Window owner,BitmapSource reference,BitmapSource sheet,LetterTransferSheet result,List<LetterTransferApproved> approved)
        {
            var cells=result.Cells;var window=Dialog(owner,"Проверка букв");var root=new DockPanel{Margin=new Thickness(12)};window.Content=root;
            var checks=new CheckBox[cells.Length];var choices=new ComboBox[cells.Length];var borders=new Border[cells.Length];int active=-1;var excluded=cells.Select(c=>c.Excluded).ToArray();bool manualLayout=result.LayoutChanged && !cells.Any(c=>c.Recognized);
            var note=new TextBlock{Text="Подготовлено ячеек: "+cells.Count(c=>c.Image!=null && c.Safe)+" из "+cells.Length+". "+(result.Notice??"")+" Проверьте целостность, кириллицу и стиль. Отметьте подходящие. Непринятые ячейки не добавляются; исходный лист останется в истории.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
            var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            footer.Children.Add(Button("Назад",()=>window.DialogResult=false));
            footer.Children.Add(Button("Отметить все целые",()=>{for(int i=0;i<checks.Length;i++)if(checks[i].IsEnabled && cells[i].Safe && !cells[i].RecognitionUncertain)checks[i].IsChecked=true;}));
            footer.Children.Add(Button("Назначить по порядку",()=>{
                var selected=Enumerable.Range(0,cells.Length).Where(i=>!excluded[i] && cells[i].Image!=null).ToArray();
                var assigned=LetterTransferOrder.Assign(result.Requested,selected.Select(i=>choices[i].SelectedItem as string).ToArray(),out string message);
                for(int n=0;n<selected.Length;n++)if(LetterTransferRules.ValidLetter(assigned[n]))choices[selected[n]].SelectedItem=assigned[n];
                note.Text=message;
            }));
            footer.Children.Add(DockerTheme.Primary(Button("Добавить отмеченные",()=>{
                for(int i=0;i<cells.Length;i++)if(checks[i].IsChecked==true && cells[i].Image!=null && LetterTransferRules.ValidLetter(choices[i].SelectedItem as string))approved.Add(new LetterTransferApproved{Letter=(string)choices[i].SelectedItem,Image=cells[i].Image,Original=cells[i].Original,Bounds=cells[i].Bounds});
                if(approved.Count==0){MessageBox.Show(window,"Назначьте обозначения и отметьте хотя бы одну проверенную букву.");return;}window.DialogResult=true;
            })));
            var palette=new StackPanel();DockPanel.SetDock(palette,Dock.Top);root.Children.Add(palette);
            var selection=new TextBlock{Text="Щёлкните ячейку, затем букву ниже или введите её с клавиатуры. Следующая ячейка выберется сама.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3)};palette.Children.Add(selection);
            var keys=new WrapPanel();palette.Children.Add(keys);
            void Select(int index)
            {
                if(index<0 || index>=cells.Length)return;
                if(active>=0){borders[active].BorderThickness=new Thickness(1);DockerTheme.UseTheme(borders[active],Border.BorderBrushProperty,DockerTheme.BorderBrush);}
                active=index;borders[index].BorderThickness=new Thickness(2);borders[index].BorderBrush=Brushes.DodgerBlue;
                selection.Text="Ячейка "+(index+1)+" · выберите букву. Повторное обозначение сохранится как ещё один вариант.";
            }
            void Assign(string letter)
            {
                if(active<0 || excluded[active] || cells[active].Image==null)return;
                choices[active].SelectedItem=letter;if(cells[active].Safe)checks[active].IsChecked=true;
                int next=Enumerable.Range(1,cells.Length).Select(d=>(active+d)%cells.Length).Where(i=>!excluded[i] && cells[i].Image!=null && !LetterTransferRules.ValidLetter(choices[i].SelectedItem as string)).DefaultIfEmpty(-1).First();
                if(next>=0){Select(next);borders[next].BringIntoView();}
            }
            foreach(char letter in LetterTransferRules.Alphabet)
            {
                string value=letter.ToString();var key=Button(value,()=>Assign(value));key.Tag="assign-"+value;key.Padding=new Thickness(7,3,7,3);key.MinWidth=32;keys.Children.Add(key);
            }
            var tabs=new TabControl();root.Children.Add(tabs);var grid=new UniformGrid{Columns=LetterTransferRules.Columns(cells.Length)};tabs.Items.Add(new TabItem{Header="Буквы",Content=new ScrollViewer{Content=grid,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            tabs.Items.Add(new TabItem{Header="Исходный лист ИИ",Content=new Image{Source=sheet,Stretch=Stretch.Uniform}});
            tabs.Items.Add(new TabItem{Header="Референс",Content=new Image{Source=reference,Stretch=Stretch.Uniform}});
            for(int i=0;i<cells.Length;i++)
            {
                int index=i;var cell=cells[i];var card=new DockPanel{Margin=new Thickness(9)};var header=new WrapPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(header,Dock.Top);card.Children.Add(header);
                header.Children.Add(new TextBlock{Text=(i+1)+" · ",VerticalAlignment=VerticalAlignment.Center});
                checks[i]=new CheckBox{Content="Принять",Tag="accept",IsEnabled=!excluded[i] && cell.Image!=null && LetterTransferRules.ValidLetter(cell.Letter),IsChecked=!excluded[i] && (cell.Manual || cell.Recognized) && !cell.RecognitionUncertain && cell.Safe && cell.Image!=null && LetterTransferRules.ValidLetter(cell.Letter),VerticalAlignment=VerticalAlignment.Center};header.Children.Add(checks[i]);
                choices[i]=new ComboBox{ItemsSource=new[]{"—"}.Concat(LetterTransferRules.Alphabet.Select(c=>c.ToString())).ToArray(),SelectedItem=cell.Letter??"—",IsEnabled=!excluded[i],Width=65,Margin=new Thickness(8,0,0,0)};
                choices[i].SelectionChanged+=(_,__)=>{cells[index].Manual=true;cells[index].Letter=LetterTransferRules.ValidLetter(choices[index].SelectedItem as string)?(string)choices[index].SelectedItem:null;checks[index].IsEnabled=!excluded[index] && cells[index].Image!=null && LetterTransferRules.ValidLetter(choices[index].SelectedItem as string);if(!checks[index].IsEnabled)checks[index].IsChecked=false;};header.Children.Add(choices[i]);
                if(manualLayout)
                {
                    var skip=new CheckBox{Content="Лишняя ячейка",Tag="skip",IsChecked=excluded[i],Margin=new Thickness(0,4,0,2)};DockPanel.SetDock(skip,Dock.Top);card.Children.Add(skip);
                    skip.Checked+=(_,__)=>{cells[index].Excluded=excluded[index]=true;checks[index].IsChecked=false;checks[index].IsEnabled=false;choices[index].IsEnabled=false;};
                    skip.Unchecked+=(_,__)=>{cells[index].Excluded=excluded[index]=false;choices[index].IsEnabled=true;checks[index].IsEnabled=cells[index].Image!=null && LetterTransferRules.ValidLetter(choices[index].SelectedItem as string);};
                }
                if(cell.Recognized)
                {
                    var recognition=new TextBlock{Text=cell.RecognitionUncertain?"ИИ не уверен — проверьте обозначение":"Распознано · GPT mini"+(cells.Count(c=>c.Letter!=null && c.Letter==cell.Letter)>1?" · вариант":""),TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,3,0,3)};
                    DockPanel.SetDock(recognition,Dock.Top);card.Children.Add(recognition);
                    choices[i].SelectionChanged+=(_,__)=>{cells[index].RecognitionUncertain=!LetterTransferRules.ValidLetter(choices[index].SelectedItem as string);recognition.Text="Обозначение изменено вручную";};
                }
                if(!String.IsNullOrEmpty(cell.Warning))
                {var warning=new TextBlock{Text=cell.Warning,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,3)};DockPanel.SetDock(warning,Dock.Bottom);card.Children.Add(warning);}
                card.Children.Add(new Image{Source=cell.Preview,Height=cells.Length>4?140:Double.NaN,Stretch=Stretch.Uniform,Margin=new Thickness(5)});
                var border=new Border{Child=card,Background=LetterTransferImages.Checker(),BorderThickness=new Thickness(1),Margin=new Thickness(3),Focusable=true};borders[i]=border;
                border.PreviewMouseLeftButtonDown+=(_,e)=>{Select(index);if(e.OriginalSource is Image){border.Focus();e.Handled=true;}};DockerTheme.UseTheme(border,Border.BorderBrushProperty,DockerTheme.BorderBrush);grid.Children.Add(border);
            }
            window.PreviewTextInput+=(_,e)=>{
                if(tabs.SelectedIndex!=0 || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is ComboBox)return;
                string letter=e.Text.ToUpperInvariant();if(!LetterTransferRules.ValidLetter(letter))return;Assign(letter);e.Handled=true;
            };
            int first=Enumerable.Range(0,cells.Length).Where(i=>cells[i].Image!=null).DefaultIfEmpty(-1).First();Select(first);
            return window;
        }
        private static Button Button(string text,Action action)
        {var b=new Button{Content=text,MinHeight=34,Margin=new Thickness(3),Padding=new Thickness(9,4,9,4)};b.Click+=(_,__)=>action();return b;}
    }
}
