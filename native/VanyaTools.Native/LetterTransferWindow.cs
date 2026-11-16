using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferWindow:Window
    {
        private readonly Func<string> _token,_capture,_captureReference;
        private readonly Func<BitmapSource,string[],string,string,string,bool,bool> _confirm;
        private readonly Action<string,bool> _status;
        private LetterTransferSet _set;
        private string _directory;
        private readonly string _root;
        private bool _loading,_saving;
        private readonly ComboBox _library=new ComboBox{Width=225},_style=new ComboBox(),_model=new ComboBox(),_resolution=new ComboBox(),_letter=new ComboBox();
        private readonly TextBox _name=new TextBox(),_notes=new TextBox(),_height=new TextBox(),_sheetWidth=new TextBox(),_sheetHeight=new TextBox(),_gap=new TextBox(),_margin=new TextBox(),_target=new TextBox(),_variants=new TextBox();
        private readonly ComboBox _previewBackground=new ComboBox{Width=110,Margin=new Thickness(3)};
        private readonly CheckBox _autoLength=new CheckBox{Content="Полотно · длина по заполнению",IsChecked=true};
        private readonly TextBox _alphaThreshold=new TextBox();
        private readonly CheckBox _readyOnly=new CheckBox{Content="Только готовые буквы",IsChecked=true};
        private readonly DataGrid _table=new DataGrid{AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Extended,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeaderWidth=0,SelectionUnit=DataGridSelectionUnit.FullRow};
        private readonly Image _reference=new Image{Height=105,Stretch=Stretch.Uniform};
        private readonly ListBox _glyphs=new ListBox(),_jobs=new ListBox();
        private readonly StackPanel _pages=new StackPanel();
        private readonly TextBlock _message=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,5,3,2)},_timing=new TextBlock{Margin=new Thickness(3,2,3,5)},_summary=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4)};
        private readonly Grid _body=new Grid();
        private readonly WrapPanel _tools=new WrapPanel();
        private readonly ScrollViewer _scroll=new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        private readonly Dictionary<string,BitmapSource> _cache=new Dictionary<string,BitmapSource>();
        private readonly DispatcherTimer _refresh=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(350)},_clock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        private readonly Stopwatch _elapsed=new Stopwatch();
        private readonly Button _cancel;
        private CancellationTokenSource _request;
        private string _stage="",_timingKey;
        private double _zoom=1;
        internal LetterTransferWindow(Func<string> token,Func<string> capture,Action<string,bool> status,string libraryRoot=null,Func<BitmapSource,string[],string,string,string,bool,bool> confirm=null,Func<string> captureReference=null)
        {
            _token=token;_capture=capture;_captureReference=captureReference??capture;_status=status;_root=libraryRoot??LetterTransferStore.DefaultRoot;_confirm=confirm??((reference,letters,model,resolution,prompt,retry)=>LetterTransferReviewWindow.Confirm(this,reference,letters,model,resolution,prompt,retry));
            Title="Буквы-трансферы · Vanya Tools";Width=Math.Min(1380,SystemParameters.WorkArea.Width-30);Height=Math.Min(900,SystemParameters.WorkArea.Height-30);MinWidth=1000;MinHeight=640;WindowStartupLocation=WindowStartupLocation.CenterOwner;DockerTheme.Apply(this);
            var root=new DockPanel{Margin=new Thickness(10)};Content=root;
            var bottom=new DockPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
            var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);bottom.Children.Add(actions);
            actions.Children.Add(DockerTheme.Primary(Button("Создать листы в Corel",()=>Safe(Insert))));
            _cancel=Button("Закрыть",()=>{if(_request!=null){_request.Cancel();Report("Отмена запроса…");}else Close();});actions.Children.Add(_cancel);
            var statuses=new StackPanel();statuses.Children.Add(_message);statuses.Children.Add(_timing);bottom.Children.Add(statuses);
            DockPanel.SetDock(_tools,Dock.Top);root.Children.Add(_tools);
            _tools.Children.Add(new TextBlock{Text="Набор",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3)});_tools.Children.Add(_library);
            _tools.Children.Add(Button("Новый",()=>Safe(NewSet)));_tools.Children.Add(Button("Открыть…",()=>Safe(OpenSet)));_tools.Children.Add(Button("Сохранить",()=>Safe(()=>{Save();RefreshLibrary();Report("Набор сохранён: "+_directory);})));_tools.Children.Add(Button("Папка набора",()=>Safe(()=>Process.Start(new ProcessStartInfo(_directory){UseShellExecute=true}))));
            _body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(285)});_body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(255)});_body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});root.Children.Add(_body);
            var leftTabs=new TabControl{Margin=new Thickness(0,8,10,0)};_body.Children.Add(leftTabs);
            var sizes=new WrapPanel{Margin=new Thickness(0,4,0,0)};DockPanel.SetDock(sizes,Dock.Top);root.Children.Insert(root.Children.IndexOf(_body),sizes);
            sizes.Children.Add(new TextBlock{Text="Высота букв, мм",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});_height.Width=70;_height.MinHeight=29;sizes.Children.Add(_height);
            sizes.Children.Add(new TextBlock{Text="Ширина полотна, мм",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,4,0)});_sheetWidth.Width=80;_sheetWidth.MinHeight=29;sizes.Children.Add(_sheetWidth);
            var fields=new StackPanel{Margin=new Thickness(5)};var printFields=new StackPanel{Margin=new Thickness(5)};
            leftTabs.Items.Add(new TabItem{Header="Стиль и ИИ",Content=new ScrollViewer{Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            leftTabs.Items.Add(new TabItem{Header="Печать",Content=new ScrollViewer{Content=printFields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            Field(fields,"Название набора",_name);_name.MaxLength=120;
            Field(fields,"Референс · одна буква, несколько или набор",new Border{Child=_reference,Background=Brushes.LightGray,Padding=new Thickness(4)});var refButtons=new WrapPanel();refButtons.Children.Add(Button("Референс из файла…",()=>Safe(()=>Reference(false))));refButtons.Children.Add(Button("Из Corel",()=>Safe(()=>Reference(true))));fields.Children.Add(refButtons);
            _style.ItemsSource=new[]{"По референсу","Единый алфавит и фактура","Коллаж · разные оформления"};Field(fields,"Оформление",_style);
            _notes.MaxLength=4000;_notes.TextWrapping=TextWrapping.Wrap;_notes.AcceptsReturn=true;_notes.Height=55;_notes.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;Field(fields,"Уточнения для генерации",_notes);
            printFields.Children.Add(new TextBlock{Text="Русские заглавные · 33 буквы\nВеса — начальное приближение для русского текста. Для имён и заказов корректируйте вручную.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,6)});
            Field(printFields,"Всего копий по частотности (33–5000)",_target);printFields.Children.Add(Button("Распределить по частотности",()=>Safe(()=>{Read();LetterTransferRules.Allocate(_set.Letters,_set.TargetCopies);_table.Items.Refresh();Save();Preview();Report("Количество распределено. Любую строку можно изменить вручную.");})));
            Field(fields,"Оформлений на букву (1–4)",_variants);
            _model.ItemsSource=new[]{"Nano Banana 2","Nano Banana Pro"};_model.SelectedIndex=0;Field(fields,"Генерация через Replicate",_model);_resolution.ItemsSource=new[]{"2K","4K"};_resolution.SelectedIndex=1;Field(fields,"Размер ответа ИИ",_resolution);
            fields.Children.Add(Button("Создать недостающие · до 4 букв…",()=>Generate(false)));
            fields.Children.Add(Button("Новые варианты выбранных строк…",()=>Generate(true)));
            fields.Children.Add(new TextBlock{Text="Каждый запрос подтверждается отдельно. Буквы из ответа ИИ добавляются только после проверки.",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,3,0,8)});
            printFields.Children.Add(_autoLength);Field(printFields,"Высота фиксированного листа, мм",_sheetHeight);
            Field(printFields,"Порог удаления полупрозрачности (1–255)",_alphaThreshold);
            printFields.Children.Add(new TextBlock{Text="Каждая полученная буква автоматически очищается от фона и полупрозрачности. PNG сохраняются отдельно; высота задаётся сверху. Длина полотна рассчитывается по заполнению, до 7 м на страницу.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,7)});Field(printFields,"Зазор, мм",_gap);Field(printFields,"Поля, мм",_margin);printFields.Children.Add(_readyOnly);
            var middle=new DockPanel{Margin=new Thickness(0,8,10,0)};Grid.SetColumn(middle,1);_body.Children.Add(middle);
            var tableNote=new TextBlock{Text="Буква · частотный вес · копий · вариантов\nCtrl / Shift — несколько строк",FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,5)};DockPanel.SetDock(tableNote,Dock.Top);middle.Children.Add(tableNote);
            _table.Columns.Add(new DataGridTextColumn{Header="Буква",Binding=new Binding("Letter"),IsReadOnly=true,Width=48});
            _table.Columns.Add(new DataGridTextColumn{Header="Вес",Binding=new Binding("Weight"),Width=53});_table.Columns.Add(new DataGridTextColumn{Header="Копий",Binding=new Binding("Copies"),Width=60});_table.Columns.Add(new DataGridTextColumn{Header="Вар.",Binding=new Binding("Ready"),IsReadOnly=true,Width=56});middle.Children.Add(_table);
            _table.CellEditEnding+=(_,__)=>Queue();_table.SelectionChanged+=(_,__)=>{var row=_table.SelectedItem as LetterTransferRow;if(row!=null)_letter.SelectedItem=row.Letter;};
            var tabs=new TabControl{Margin=new Thickness(0,8,0,0)};Grid.SetColumn(tabs,2);_body.Children.Add(tabs);
            var print=new DockPanel();var previewTools=new WrapPanel();DockPanel.SetDock(previewTools,Dock.Top);print.Children.Add(previewTools);
            previewTools.Children.Add(Button("−",()=>Zoom(_zoom/1.25)));previewTools.Children.Add(Button("+",()=>Zoom(_zoom*1.25)));previewTools.Children.Add(Button("Вписать",()=>Zoom(Math.Max(.15,(_scroll.ActualWidth-35)/(_set.SheetWidthMm*2)))));previewTools.Children.Add(Button("100%",()=>Zoom(1)));_previewBackground.ItemsSource=new[]{"Белый фон","Серый фон","Шахматка"};_previewBackground.SelectedIndex=1;_previewBackground.ToolTip="Только фон предпросмотра. На листы Corel не добавляется.";previewTools.Children.Add(_previewBackground);_previewBackground.SelectionChanged+=(_,__)=>Queue();
            DockPanel.SetDock(_summary,Dock.Bottom);print.Children.Add(_summary);_scroll.Content=_pages;print.Children.Add(_scroll);tabs.Items.Add(new TabItem{Header="Печатные листы",Content=print});
            _scroll.PreviewMouseWheel+=(_,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;Zoom(_zoom*(e.Delta>0?1.15:1/1.15));e.Handled=true;};
            var variantsPanel=new DockPanel();var variantTools=new WrapPanel();DockPanel.SetDock(variantTools,Dock.Top);variantsPanel.Children.Add(variantTools);
            _letter.ItemsSource=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();_letter.SelectedIndex=0;_letter.Width=60;variantTools.Children.Add(_letter);
            variantTools.Children.Add(Button("PNG / изображение…",()=>Safe(()=>Import(false))));variantTools.Children.Add(Button("Из Corel",()=>Safe(()=>Import(true))));variantTools.Children.Add(Button("Удалить вариант",()=>Safe(Remove)));variantsPanel.Children.Add(_glyphs);tabs.Items.Add(new TabItem{Header="Варианты букв",Content=variantsPanel});
            var jobsPanel=new DockPanel();var jobTools=new WrapPanel();DockPanel.SetDock(jobTools,Dock.Top);jobsPanel.Children.Add(jobTools);
            jobTools.Children.Add(Button("История Replicate",()=>Safe(()=>Process.Start(new ProcessStartInfo("https://replicate.com/predictions"){UseShellExecute=true}))));jobTools.Children.Add(Button("Проверить готовый ответ",()=>ReviewJob()));jobTools.Children.Add(Button("Продолжить / повторить…",()=>Retry()));jobsPanel.Children.Add(_jobs);tabs.Items.Add(new TabItem{Header="История ИИ",Content=jobsPanel});
            _library.SelectionChanged+=(_,__)=>{if(_loading || _library.SelectedItem==null)return;Safe(()=>{Save();Load(((LetterTransferLibraryItem)_library.SelectedItem).Directory);});};
            foreach(var input in new[]{_name,_notes,_height,_sheetWidth,_sheetHeight,_gap,_margin,_target,_variants,_alphaThreshold})input.TextChanged+=(_,__)=>Queue();_style.SelectionChanged+=(_,__)=>Queue();_readyOnly.Checked+=(_,__)=>Queue();_readyOnly.Unchecked+=(_,__)=>Queue();_autoLength.Checked+=(_,__)=>{_sheetHeight.IsEnabled=false;Queue();};_autoLength.Unchecked+=(_,__)=>{_sheetHeight.IsEnabled=true;Queue();};
            _refresh.Tick+=(_,__)=>{_refresh.Stop();if(_loading || _request!=null)return;try{Save();Preview();}catch(Exception error){_pages.Children.Clear();_summary.Text=error.GetBaseException().Message;}};
            _clock.Tick+=(_,__)=>_timing.Text=OperationTiming.Display(_stage,_elapsed.Elapsed,_timingKey);
            Closing+=(_,e)=>{if(_request!=null){e.Cancel=true;_request.Cancel();Report("Дождитесь завершения отмены. Результат и история останутся в наборе.");return;}try{Save();}catch(Exception error){e.Cancel=true;Report(error.GetBaseException().Message,true);}};
            Closed+=(_,__)=>{_refresh.Stop();_clock.Stop();};
            var saved=LetterTransferStore.List(_root);if(saved.Count>0)Load(saved[0].Directory);else NewSet();RefreshLibrary();
        }
        private void NewSet()
        {
            if(_set!=null)Save();var set=new LetterTransferSet{Name="Набор букв "+DateTime.Now.ToString("dd.MM HH-mm")};string directory=LetterTransferStore.Create(_root,set);Load(directory);RefreshLibrary();
        }
        private void OpenSet()
        {
            var dialog=new Microsoft.Win32.OpenFileDialog{Title="Открыть сохранённый набор",Filter="Набор букв (set.json)|set.json|JSON|*.json"};if(dialog.ShowDialog(this)!=true)return;
            var directory=Path.GetDirectoryName(dialog.FileName);LetterTransferStore.Load(directory);Save();Load(directory);RefreshLibrary();
        }
        private void RefreshLibrary()
        {
            _loading=true;try{var items=LetterTransferStore.List(_root);if(!items.Any(i=>String.Equals(i.Directory,_directory,StringComparison.OrdinalIgnoreCase)))items.Add(new LetterTransferLibraryItem{Directory=_directory,Name=_set.Name});_library.ItemsSource=items;_library.SelectedItem=items.FirstOrDefault(i=>String.Equals(i.Directory,_directory,StringComparison.OrdinalIgnoreCase));}finally{_loading=false;}
        }
        private void Load(string directory)
        {
            var set=LetterTransferStore.Load(directory);_loading=true;_refresh.Stop();
            try
            {
                _set=set;_directory=directory;_cache.Clear();_name.Text=set.Name;_notes.Text=set.Notes;_style.SelectedItem=set.Style;if(_style.SelectedIndex<0)_style.SelectedIndex=0;
                _autoLength.IsChecked=set.AutoLength;_sheetHeight.IsEnabled=!set.AutoLength;_alphaThreshold.Text=set.AlphaThreshold.ToString();_height.Text=N(set.HeightMm);_sheetWidth.Text=N(set.SheetWidthMm);_sheetHeight.Text=N(set.SheetHeightMm);_gap.Text=N(set.GapMm);_margin.Text=N(set.MarginMm);_target.Text=set.TargetCopies.ToString();_variants.Text=set.VariantsPerLetter.ToString();
                _table.ItemsSource=set.Letters;_table.SelectedIndex=0;_reference.Source=String.IsNullOrEmpty(set.Reference)?null:Thumb(set.Reference);
                RefreshAssets();Preview();Report("Набор загружен. Параметры сохраняются автоматически.");
            }
            finally{_loading=false;}
        }
        private void Read()
        {
            if(_set==null)return;
            if(!_table.CommitEdit(DataGridEditingUnit.Cell,true) || !_table.CommitEdit(DataGridEditingUnit.Row,true))throw new ArgumentException("Завершите правку таблицы: нужны числовые значения.");
            _set.Name=_name.Text.Trim();_set.Style=(string)_style.SelectedItem;_set.Notes=_notes.Text;_set.AutoLength=_autoLength.IsChecked==true;_set.AlphaThreshold=Integer(_alphaThreshold);_set.HeightMm=Number(_height);_set.SheetWidthMm=Number(_sheetWidth);_set.SheetHeightMm=Number(_sheetHeight);_set.GapMm=Number(_gap);_set.MarginMm=Number(_margin);_set.TargetCopies=Integer(_target);_set.VariantsPerLetter=Integer(_variants);LetterTransferRules.Validate(_set);
        }
        private void Save()
        {
            if(_set==null || _saving)return;_saving=true;try{Read();LetterTransferStore.Save(_directory,_set);}finally{_saving=false;}
        }
        private void Queue(){if(_loading || _set==null)return;_refresh.Stop();_refresh.Start();}
        private void RefreshAssets()
        {
            foreach(var row in _set.Letters)row.Ready=_set.Glyphs.Count(g=>g.Letter==row.Letter).ToString();_table.Items.Refresh();_glyphs.Items.Clear();
            foreach(var glyph in _set.Glyphs.OrderBy(g=>LetterTransferRules.Alphabet.IndexOf(g.Letter,StringComparison.Ordinal)))
            {
                var panel=new StackPanel{Orientation=Orientation.Horizontal,Background=Brushes.LightGray};panel.Children.Add(new Image{Source=Thumb(glyph.File),Width=72,Height=65,Stretch=Stretch.Uniform,Margin=new Thickness(5)});
                panel.Children.Add(new TextBlock{Text=glyph.Letter+" · "+glyph.Width+" × "+glyph.Height+" px\n"+(glyph.Source??""),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});_glyphs.Items.Add(new ListBoxItem{Content=panel,Tag=glyph});
            }
            _jobs.Items.Clear();foreach(var job in _set.Jobs.AsEnumerable().Reverse())_jobs.Items.Add(new ListBoxItem{Content=String.Join(" ",job.Letters)+" · "+job.Resolution+" · "+(job.Reviewed?"проверено":File.Exists(LetterTransferStore.Resolve(_directory,job.Output))?"ответ готов":LetterTransferAi.UncertainSubmission(_directory,job)?"нет номера от Replicate":"запрос сохранён"),Tag=job});
        }
        private BitmapSource Thumb(string relative)
        {
            if(_cache.TryGetValue(relative,out var image))return image;string path=LetterTransferStore.Resolve(_directory,relative);if(!File.Exists(path))return null;
            try{using(var stream=File.OpenRead(path)){var thumbnail=new BitmapImage();thumbnail.BeginInit();thumbnail.CacheOption=BitmapCacheOption.OnLoad;thumbnail.StreamSource=stream;thumbnail.DecodePixelHeight=250;thumbnail.EndInit();thumbnail.Freeze();image=thumbnail;}}
            catch(Exception error)when(error is IOException || error is NotSupportedException || error is FileFormatException || error is ArgumentException){Log.Error("Could not load saved letter thumbnail.",error);image=null;}
            _cache[relative]=image;return image;
        }
        private void Preview()
        {
            _pages.Children.Clear();var placements=LetterTransferLayout.Build(_set,_readyOnly.IsChecked==true);int pages=placements.Count==0?0:placements.Max(p=>p.Page)+1;
            if(pages>200)throw new InvalidOperationException("Получается больше 200 листов. Увеличьте лист или уменьшите количество / размер букв.");
            foreach(var group in placements.GroupBy(p=>p.Page))
            {
                double pageHeight=LetterTransferLayout.PageHeight(_set,group);var drawing=new DrawingGroup();using(var context=drawing.Open())
                {
                    context.DrawRectangle(_previewBackground.SelectedIndex==2?LetterTransferImages.Checker():_previewBackground.SelectedIndex==1?Brushes.LightGray:Brushes.White,new Pen(Brushes.Gray,.3),new Rect(0,0,_set.SheetWidthMm,pageHeight));
                    foreach(var p in group)
                    {
                        var image=Thumb(p.Glyph.File);if(image==null)throw new FileNotFoundException("Не найден PNG буквы «"+p.Glyph.Letter+"». Восстановите файл или импортируйте новый вариант.");
                        context.DrawImage(image,new Rect(p.X,p.Y,p.Width,p.Height));
                    }
                }
                drawing.Freeze();var border=new Border{Child=new Image{Source=new DrawingImage(drawing),Width=_set.SheetWidthMm*2,Height=pageHeight*2},Margin=new Thickness(8)};_pages.Children.Add(border);
            }
            _pages.LayoutTransform=new ScaleTransform(_zoom,_zoom);int requested=_set.Letters.Sum(r=>r.Copies);int missing=_set.Letters.Count(r=>r.Copies>0 && !_set.Glyphs.Any(g=>g.Letter==r.Letter));
            _summary.Text="На листах: "+placements.Count+" из "+requested+" копий · листов: "+pages+" · нет букв: "+missing+" · длина "+N(placements.GroupBy(p=>p.Page).Sum(g=>LetterTransferLayout.PageHeight(_set,g)))+" мм\nВысота "+N(_set.HeightMm)+" мм · Ctrl+колесо — зум. Исходные PNG сохраняются без масштабирования.";
        }
        private void Zoom(double value){_zoom=Math.Max(.1,Math.Min(5,value));_pages.LayoutTransform=new ScaleTransform(_zoom,_zoom);}
        private void Reference(bool corel)
        {
            Save();string[] sources;
            if(corel)sources=new[]{_captureReference()};
            else{var dialog=new Microsoft.Win32.OpenFileDialog{Title="Референс: одна буква, несколько или набор",Filter="Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",Multiselect=true};if(dialog.ShowDialog(this)!=true)return;sources=dialog.FileNames;}
            if(sources.Length>16)throw new ArgumentException("Для референса выберите до 16 изображений.");
            var image=LetterTransferImages.Compose(sources.Select(path=>LetterTransferImages.LoadReference(path)).ToArray());string relative="references/"+Guid.NewGuid().ToString("N")+".png";LetterTransferImages.Save(image,LetterTransferStore.Resolve(_directory,relative));_set.Reference=relative;Save();_reference.Source=Thumb(relative);Report("Референс сохранён локально. Отправка в ИИ потребует подтверждения.");
        }
        private void Import(bool corel)
        {
            Save();string letter=(string)_letter.SelectedItem,source=SelectImage(corel);if(source==null)return;var image=LetterTransferImages.Load(source);
            LetterTransferImages.Add(_directory,_set,letter,image,"Импорт вручную");Save();RefreshAssets();Preview();Report("Добавлен вариант «"+letter+"». Проверьте прозрачность фона перед печатью.");
        }
        private string SelectImage(bool corel)
        {
            if(corel)return _capture();var dialog=new Microsoft.Win32.OpenFileDialog{Filter="Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff"};return dialog.ShowDialog(this)==true?dialog.FileName:null;
        }
        private void Remove()
        {
            var glyph=(_glyphs.SelectedItem as ListBoxItem)?.Tag as LetterTransferGlyph;if(glyph==null)throw new InvalidOperationException("Выберите вариант во вкладке «Варианты букв».");
            _set.Glyphs.Remove(glyph);Save();RefreshAssets();Preview();Report("Вариант исключён из набора. Исходный PNG остаётся в папке glyphs.");
        }
        private async void Generate(bool selected)
        {
            if(_request!=null)return;
            try
            {
                Save();string token=_token();if(String.IsNullOrWhiteSpace(token))throw new InvalidOperationException("Укажите ключ Replicate в настройках ИИ.");
                string[] letters=(selected?_table.SelectedItems.Cast<LetterTransferRow>().Select(r=>r.Letter):_set.Letters.Where(r=>r.Copies>0 && _set.Glyphs.Count(g=>g.Letter==r.Letter)<_set.VariantsPerLetter).Select(r=>r.Letter)).Distinct().Take(4).ToArray();
                if(letters.Length==0)throw new InvalidOperationException(selected?"Выберите строки букв в таблице.":"Для всех нужных букв уже сохранено выбранное число оформлений.");
                if(letters.Any(l=>_set.Glyphs.Count(g=>g.Letter==l)>=8))throw new InvalidOperationException("На букву можно сохранить до 8 вариантов. Удалите ненужные.");
                if(String.IsNullOrWhiteSpace(_set.Reference))throw new InvalidOperationException("Сначала добавьте референс стиля из файла или выделения Corel.");
                if(_set.Jobs.Count>=200)throw new InvalidOperationException("В наборе уже 200 запросов. Создайте новый набор.");
                var reference=LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,_set.Reference));string model=_model.SelectedIndex==1?"google/nano-banana-pro":"google/nano-banana-2",resolution=(string)_resolution.SelectedItem;
                if(!_confirm(reference,letters,model,resolution,LetterTransferAi.Prompt(_set,letters,LetterTransferImages.Key(reference)),false))return;
                var job=LetterTransferAi.Create(_directory,_set,letters,model,resolution,reference);await Run(job,token);
            }
            catch(Exception error){Failure(error);}
        }
        private LetterTransferJob SelectedJob()=>((_jobs.SelectedItem as ListBoxItem)?.Tag as LetterTransferJob)??_set.Jobs.LastOrDefault(j=>!j.Reviewed);
        private async void Retry()
        {
            if(_request!=null)return;try
            {
                Save();var job=SelectedJob();if(job==null)throw new InvalidOperationException("Выберите запрос в истории.");
                if(LetterTransferAi.HasResult(_directory,job)){Review(job);return;}
                if(LetterTransferAi.UncertainSubmission(_directory,job))
                {await Run(job,_token());return;}
                string token=_token();if(String.IsNullOrWhiteSpace(token))throw new InvalidOperationException("Укажите ключ Replicate в настройках ИИ.");
                var reference=LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,job.Reference));
                if(!_confirm(reference,job.Letters,job.Model,job.Resolution,job.Prompt,true))return;await Run(job,token);
            }
            catch(Exception error){Failure(error);}
        }
        private async Task Run(LetterTransferJob job,string token)
        {
            _refresh.Stop();_request=new CancellationTokenSource();_body.IsEnabled=_tools.IsEnabled=false;_cancel.Content="Отменить запрос";_stage="Генерация букв";_timingKey="letters/"+job.Model+"/"+job.Resolution;_elapsed.Restart();_clock.Start();Report("ИИ создаёт «"+String.Join(" ",job.Letters)+"». Ожидайте ответ.");
            try
            {
                await Task.Run(()=>LetterTransferAi.Generate(_directory,job,token,progress=>Dispatcher.BeginInvoke(new Action(()=>{_stage=progress=="sending"?"Отправка превью":progress=="recovering"?"Поиск созданного запроса · без новой генерации":progress=="creating"?"Запуск модели":progress=="processing"?"Создание букв":progress=="downloading"?"Скачивание ответа":progress=="cancelling"?"Отмена":"Ожидание ИИ";})),_request.Token));
                OperationTiming.Record(_timingKey,_elapsed.Elapsed);Report("Ответ ИИ сохранён. Проверьте буквы перед добавлением.");
            }
            catch(Exception error){Failure(error);}
            finally
            {
                _clock.Stop();_elapsed.Stop();_timing.Text=OperationTiming.Display(_stage,_elapsed.Elapsed,_timingKey);_request.Dispose();_request=null;_body.IsEnabled=_tools.IsEnabled=true;_cancel.Content="Закрыть";RefreshAssets();
            }
            if(LetterTransferAi.HasResult(_directory,job))Review(job);
        }
        private void ReviewJob(){Safe(()=>{var job=SelectedJob();if(job==null)throw new InvalidOperationException("В истории пока нет запросов.");Review(job);});}
        private void Review(LetterTransferJob job)
        {
            if(job.Reviewed){Report("Этот ответ уже проверен. Сохранённые буквы доступны во вкладке «Варианты букв».");return;}
            var sheet=LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,job.Output));var cells=LetterTransferImages.PrepareBatch(LetterTransferImages.Extract(sheet,job.Letters.Length,job.Key,CancellationToken.None),_set.AlphaThreshold,CancellationToken.None);
            var approved=LetterTransferReviewWindow.Review(this,LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,job.Reference)),sheet,cells,job.Letters);
            if(approved.Count==0)return;
            foreach(var group in approved.GroupBy(a=>a.Letter))if(_set.Glyphs.Count(g=>g.Letter==group.Key)+group.Count()>8)throw new InvalidOperationException("Слишком много вариантов буквы «"+group.Key+"». Удалите ненужные.");
            foreach(var glyph in approved)LetterTransferImages.Add(_directory,_set,glyph.Letter,glyph.Image,"ИИ · "+job.Id.Substring(0,8));job.Reviewed=true;Save();RefreshAssets();Preview();Report("Добавлено проверенных букв: "+approved.Count+" из "+job.Letters.Length+". Остальные можно догенерировать.");
        }
        private void Insert()
        {
            if(_request!=null)return;Save();Preview();int count;using(DockerUiWatchdog.Trace(Dispatcher,"Insert letter transfers"))count=LetterTransferService.Insert(_directory,_set,_readyOnly.IsChecked==true);Report("Созданы новые листы Corel · букв: "+count+". Набор сохранён для допечатки.");
        }
        private void Failure(Exception error){Log.Error("Letter transfer operation failed.",error);Report(error is OperationCanceledException?"Запрос отменён. История и полученные файлы сохранены.":error.GetBaseException().Message,true);}
        private void Safe(Action action){try{action();}catch(Exception error){Failure(error);}}
        private void Report(string text,bool error=false){_message.Text=text;DockerTheme.UseTheme(_message,TextBlock.ForegroundProperty,error?DockerTheme.ErrorTextBrush:DockerTheme.MutedTextBrush);_status?.Invoke(text,error);}
        private static void Field(Panel panel,string title,FrameworkElement input){panel.Children.Add(new TextBlock{Text=title,Margin=new Thickness(0,5,0,3),TextWrapping=TextWrapping.Wrap});input.MinHeight=29;panel.Children.Add(input);}
        private static Button Button(string title,Action action){var b=new Button{Content=title,MinHeight=30,Margin=new Thickness(2),Padding=new Thickness(7,3,7,3)};b.Click+=(_,__)=>action();return b;}
        private static string N(double value)=>value.ToString("0.###",CultureInfo.CurrentCulture);
        private static double Number(TextBox input){if(!Double.TryParse(input.Text.Replace(',', '.'),NumberStyles.Float,CultureInfo.InvariantCulture,out double value) || !LetterTransferRules.Finite(value))throw new ArgumentException("Введите число: «"+input.Text+"».");return value;}
        private static int Integer(TextBox input){double value=Number(input);if(value!=Math.Floor(value) || value<Int32.MinValue || value>Int32.MaxValue)throw new ArgumentException("Нужно целое количество.");return (int)value;}
    }
}
