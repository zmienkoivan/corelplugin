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
        private readonly Func<BitmapSource,int,string,bool,bool> _confirmRecognition;
        private readonly Func<BitmapSource,BitmapSource,LetterTransferSheet,List<LetterTransferApproved>> _review;
        private LetterTransferSet _set;
        private string _directory;
        private readonly string _root;
        private bool _loading,_saving,_sequenceBusy;
        private readonly ComboBox _library=new ComboBox{Width=225},_style=new ComboBox(),_model=new ComboBox(),_resolution=new ComboBox(),_letter=new ComboBox();
        private readonly TextBox _name=new TextBox(),_notes=new TextBox(),_height=new TextBox(),_sheetWidth=new TextBox(),_sheetHeight=new TextBox(),_gap=new TextBox(),_margin=new TextBox(),_target=new TextBox(),_variants=new TextBox();
        private readonly ComboBox _previewBackground=new ComboBox{Width=110,Margin=new Thickness(3)};
        private readonly CheckBox _autoLength=new CheckBox{Content="Полотно · длина по заполнению",IsChecked=true};
        private readonly TextBox _alphaThreshold=new TextBox();
        private readonly CheckBox _smoothEdges=new CheckBox{Content="Сгладить край и убрать мелкий мусор",IsChecked=true,Margin=new Thickness(0,3,0,4)};
        private readonly CheckBox _sharpContour=new CheckBox{Content="Чёткий ровный контур",Margin=new Thickness(0,7,0,3)};
        private readonly Slider _contourSmooth=new Slider{Minimum=.25,Maximum=3,Value=1.5,TickFrequency=.25,IsSnapToTickEnabled=true,Margin=new Thickness(0,3,0,4)};
        private readonly TextBlock _contourLabel=new TextBlock{Text="Выравнивание контура: 1,5 px"};
        private readonly Slider _edgeTrim=new Slider{Minimum=0,Maximum=6,Value=1,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,4,0,4)};
        private readonly TextBlock _edgeTrimLabel=new TextBlock{Text="Подрезать край: 1 px",Margin=new Thickness(0,6,0,0)};
        private readonly Button _complete;
        private readonly CheckBox _showGrid=new CheckBox{Content="Сетка нарезки",IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5)};
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
        private string _stage="",_timingKey,_sequenceNote,_recognitionSourceNote;
        private double _zoom=1;
        internal LetterTransferWindow(Func<string> token,Func<string> capture,Action<string,bool> status,string libraryRoot=null,Func<BitmapSource,string[],string,string,string,bool,bool> confirm=null,Func<string> captureReference=null,Func<BitmapSource,int,string,bool,bool> confirmRecognition=null,Func<BitmapSource,BitmapSource,LetterTransferSheet,List<LetterTransferApproved>> review=null)
        {
            _token=token;_capture=capture;_captureReference=captureReference??capture;_status=status;_root=libraryRoot??LetterTransferStore.DefaultRoot;_confirm=confirm??((reference,letters,model,resolution,prompt,retry)=>LetterTransferReviewWindow.Confirm(this,reference,letters,model,resolution,prompt,retry,_sequenceNote));
            _confirmRecognition=confirmRecognition??((preview,count,prompt,retry)=>LetterTransferReviewWindow.ConfirmRecognition(this,preview,count,prompt,retry,_recognitionSourceNote));
            _review=review??((reference,sheet,cells)=>LetterTransferReviewWindow.Review(this,reference,sheet,cells));
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
            sizes.Children.Add(new TextBlock{Text="Высота основы букв, мм",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});_height.Width=70;_height.MinHeight=29;sizes.Children.Add(_height);
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
            _model.ItemsSource=LetterTransferModels.Names;_model.SelectedIndex=0;Field(fields,"Генерация через Replicate",_model);_model.ToolTip="Модель для нового запроса. Продолжение сохранённого задания использует модель из его истории.";_resolution.ItemsSource=new[]{"2K","4K"};_resolution.SelectedIndex=1;Field(fields,"Размер ответа ИИ",_resolution);
            fields.Children.Add(Button("Создать весь алфавит / недостающие…",()=>Generate(false)));
            fields.Children.Add(Button("Догенерировать выбранные буквы…",()=>Generate(true)));
            fields.Children.Add(new TextBlock{Text="Весь алфавит — один лист 4K на оформление. Ошибки можно догенерировать по выбранным строкам. Перед каждым запросом нужно подтверждение.",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,3,0,8)});
            printFields.Children.Add(_autoLength);Field(printFields,"Высота фиксированного листа, мм",_sheetHeight);
            Field(printFields,"Порог удаления полупрозрачности (1–255)",_alphaThreshold);
            printFields.Children.Add(_edgeTrimLabel);printFields.Children.Add(_edgeTrim);printFields.Children.Add(_smoothEdges);
            printFields.Children.Add(_sharpContour);printFields.Children.Add(_contourLabel);printFields.Children.Add(_contourSmooth);
            _sharpContour.ToolTip="Выравнивает внешний силуэт и отверстия, сохраняет углы и окантовку. Текстура остаётся внутри чёткого контура; PNG выводится с разрешением до 600 DPI. Новые детали изображения не создаются.";
            _contourSmooth.ToolTip="Допустимое выравнивание края в пикселях исходной буквы. Меньше — ближе к исходнику, больше — ровнее силуэт.";
            _smoothEdges.ToolTip="Сглаживает маску без полупрозрачности и убирает отдельные частицы до 4 пикселей. Исходник сохраняется.";
            _edgeTrim.ToolTip="Пиксели исходной буквы после удаления фона. 0 — без подрезания. Для уже сохранённых букв нажмите кнопку ниже.";
            printFields.Children.Add(Button("Очистить края готовых букв",()=>Reclean()));
            printFields.Children.Add(new TextBlock{Text="Исходники сохраняются. Для возврата исходного края: 0 px, отключить сглаживание и ровный контур, повторить очистку.",TextWrapping=TextWrapping.Wrap,FontSize=11});
            printFields.Children.Add(new TextBlock{Text="Каждая полученная буква автоматически очищается от фона и полупрозрачности. PNG сохраняются отдельно; высота задаётся сверху. Длина полотна рассчитывается по заполнению, до 7 м на страницу.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,7)});Field(printFields,"Зазор, мм",_gap);Field(printFields,"Поля, мм",_margin);printFields.Children.Add(_readyOnly);
            printFields.Children.Add(new TextBlock{Text="Прямая сетка: одинаковые столбцы и ряды для нарезки. Ё и Й имеют дополнительное место над основной буквой.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,7)});
            var middle=new DockPanel{Margin=new Thickness(0,8,10,0)};Grid.SetColumn(middle,1);_body.Children.Add(middle);
            var tableNote=new TextBlock{Text="Буква · частотный вес · копий · вариантов\nCtrl / Shift — несколько строк",FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,5)};DockPanel.SetDock(tableNote,Dock.Top);middle.Children.Add(tableNote);
            _complete=Button("Получить все буквы…",()=>CompleteAlphabet());DockPanel.SetDock(_complete,Dock.Bottom);middle.Children.Add(_complete);
            _table.Columns.Add(new DataGridTextColumn{Header="Буква",Binding=new Binding("Letter"),IsReadOnly=true,Width=48});
            _table.Columns.Add(new DataGridTextColumn{Header="Вес",Binding=new Binding("Weight"),Width=53});_table.Columns.Add(new DataGridTextColumn{Header="Копий",Binding=new Binding("Copies"),Width=60});_table.Columns.Add(new DataGridTextColumn{Header="Вар.",Binding=new Binding("Ready"),IsReadOnly=true,Width=56});middle.Children.Add(_table);
            _table.CellEditEnding+=(_,__)=>Queue();_table.SelectionChanged+=(_,__)=>{var row=_table.SelectedItem as LetterTransferRow;if(row!=null)_letter.SelectedItem=row.Letter;};
            var tabs=new TabControl{Margin=new Thickness(0,8,0,0)};Grid.SetColumn(tabs,2);_body.Children.Add(tabs);
            var print=new DockPanel();var previewTools=new WrapPanel();DockPanel.SetDock(previewTools,Dock.Top);print.Children.Add(previewTools);
            previewTools.Children.Add(Button("−",()=>Zoom(_zoom/1.25)));previewTools.Children.Add(Button("+",()=>Zoom(_zoom*1.25)));previewTools.Children.Add(Button("Вписать",()=>Zoom(Math.Max(.15,(_scroll.ActualWidth-35)/(_set.SheetWidthMm*2)))));previewTools.Children.Add(Button("100%",()=>Zoom(1)));_previewBackground.ItemsSource=new[]{"Белый фон","Серый фон","Шахматка"};_previewBackground.SelectedIndex=1;_previewBackground.ToolTip="Только фон предпросмотра. На листы Corel не добавляется.";previewTools.Children.Add(_previewBackground);previewTools.Children.Add(_showGrid);_showGrid.Checked+=(_,__)=>Queue();_showGrid.Unchecked+=(_,__)=>Queue();_showGrid.ToolTip="Направляющие только в предпросмотре; на печатный лист не добавляются.";_previewBackground.SelectionChanged+=(_,__)=>Queue();
            DockPanel.SetDock(_summary,Dock.Bottom);print.Children.Add(_summary);_scroll.Content=_pages;print.Children.Add(_scroll);tabs.Items.Add(new TabItem{Header="Печатные листы",Content=print});
            _scroll.PreviewMouseWheel+=(_,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;Zoom(_zoom*(e.Delta>0?1.15:1/1.15));e.Handled=true;};
            var variantsPanel=new DockPanel();var variantTools=new WrapPanel();DockPanel.SetDock(variantTools,Dock.Top);variantsPanel.Children.Add(variantTools);
            _letter.ItemsSource=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();_letter.SelectedIndex=0;_letter.Width=60;variantTools.Children.Add(_letter);
            variantTools.Children.Add(Button("PNG / изображение…",()=>Safe(()=>Import(false))));variantTools.Children.Add(Button("Из Corel",()=>Safe(()=>Import(true))));variantTools.Children.Add(Button("Удалить вариант",()=>Safe(Remove)));variantTools.Children.Add(Button("Предпросмотр края…",()=>PreviewEdge()));variantsPanel.Children.Add(_glyphs);tabs.Items.Add(new TabItem{Header="Варианты букв",Content=variantsPanel});
            var jobsPanel=new DockPanel();var jobTools=new WrapPanel();DockPanel.SetDock(jobTools,Dock.Top);jobsPanel.Children.Add(jobTools);
            jobTools.Children.Add(Button("История Replicate",()=>Safe(()=>Process.Start(new ProcessStartInfo("https://replicate.com/predictions"){UseShellExecute=true}))));jobTools.Children.Add(Button("Проверить готовый ответ",()=>ReviewJob()));jobTools.Children.Add(Button("Распознать буквы заново…",()=>ReviewJob(true)));jobTools.Children.Add(Button("Продолжить / повторить…",()=>Retry()));jobsPanel.Children.Add(_jobs);tabs.Items.Add(new TabItem{Header="История ИИ",Content=jobsPanel});
            _library.SelectionChanged+=(_,__)=>{if(_loading || _library.SelectedItem==null)return;Safe(()=>{Save();Load(((LetterTransferLibraryItem)_library.SelectedItem).Directory);});};
            foreach(var input in new[]{_name,_notes,_height,_sheetWidth,_sheetHeight,_gap,_margin,_target,_variants,_alphaThreshold})input.TextChanged+=(_,__)=>Queue();_style.SelectionChanged+=(_,__)=>Queue();_model.SelectionChanged+=(_,__)=>Queue();_smoothEdges.Checked+=(_,__)=>Queue();_smoothEdges.Unchecked+=(_,__)=>Queue();_edgeTrim.ValueChanged+=(_,__)=>{_edgeTrimLabel.Text="Подрезать край: "+((int)_edgeTrim.Value)+" px";Queue();};_sharpContour.Checked+=(_,__)=>{_contourSmooth.IsEnabled=true;Queue();};_sharpContour.Unchecked+=(_,__)=>{_contourSmooth.IsEnabled=false;Queue();};_contourSmooth.ValueChanged+=(_,__)=>{_contourLabel.Text="Выравнивание контура: "+N(_contourSmooth.Value)+" px";Queue();};_readyOnly.Checked+=(_,__)=>Queue();_readyOnly.Unchecked+=(_,__)=>Queue();_autoLength.Checked+=(_,__)=>{_sheetHeight.IsEnabled=false;Queue();};_autoLength.Unchecked+=(_,__)=>{_sheetHeight.IsEnabled=true;Queue();};
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
                _autoLength.IsChecked=set.AutoLength;_sheetHeight.IsEnabled=!set.AutoLength;_alphaThreshold.Text=set.AlphaThreshold.ToString();_edgeTrim.Value=set.EdgeTrimPx;_smoothEdges.IsChecked=set.SmoothEdges;_sharpContour.IsChecked=set.SharpContour;_contourSmooth.Value=set.ContourSmoothPx;_contourSmooth.IsEnabled=set.SharpContour;_model.SelectedIndex=Array.IndexOf(LetterTransferModels.Ids,set.GenerationModel);_height.Text=N(set.HeightMm);_sheetWidth.Text=N(set.SheetWidthMm);_sheetHeight.Text=N(set.SheetHeightMm);_gap.Text=N(set.GapMm);_margin.Text=N(set.MarginMm);_target.Text=set.TargetCopies.ToString();_variants.Text=set.VariantsPerLetter.ToString();
                _table.ItemsSource=set.Letters;_table.SelectedIndex=0;_reference.Source=String.IsNullOrEmpty(set.Reference)?null:Thumb(set.Reference);
                RefreshAssets();Preview();Report("Набор загружен. Параметры сохраняются автоматически.");
            }
            finally{_loading=false;}
        }
        private void Read()
        {
            if(_set==null)return;
            if(!_table.CommitEdit(DataGridEditingUnit.Cell,true) || !_table.CommitEdit(DataGridEditingUnit.Row,true))throw new ArgumentException("Завершите правку таблицы: нужны числовые значения.");
            _set.Name=_name.Text.Trim();_set.Style=(string)_style.SelectedItem;_set.Notes=_notes.Text;_set.AutoLength=_autoLength.IsChecked==true;_set.AlphaThreshold=Integer(_alphaThreshold);_set.EdgeTrimPx=(int)_edgeTrim.Value;_set.SmoothEdges=_smoothEdges.IsChecked==true;_set.SharpContour=_sharpContour.IsChecked==true;_set.ContourSmoothPx=_contourSmooth.Value;_set.GenerationModel=LetterTransferModels.At(_model.SelectedIndex);_set.HeightMm=Number(_height);_set.SheetWidthMm=Number(_sheetWidth);_set.SheetHeightMm=Number(_sheetHeight);_set.GapMm=Number(_gap);_set.MarginMm=Number(_margin);_set.TargetCopies=Integer(_target);_set.VariantsPerLetter=Integer(_variants);LetterTransferRules.Validate(_set);
        }
        private void Save()
        {
            if(_set==null || _saving)return;_saving=true;try{Read();LetterTransferStore.Save(_directory,_set);}finally{_saving=false;}
        }
        private void Queue(){if(_loading || _set==null)return;_refresh.Stop();_refresh.Start();}
        private void RefreshCounts()
        {foreach(var row in _set.Letters)row.Ready=_set.Glyphs.Count(g=>g.Letter==row.Letter)+" / "+_set.VariantsPerLetter;_table.Items.Refresh();}
        private void RefreshAssets()
        {
            RefreshCounts();_glyphs.Items.Clear();
            foreach(var glyph in _set.Glyphs.OrderBy(g=>LetterTransferRules.Alphabet.IndexOf(g.Letter,StringComparison.Ordinal)))
            {
                var panel=new StackPanel{Orientation=Orientation.Horizontal,Background=Brushes.LightGray};panel.Children.Add(new Image{Source=Thumb(glyph.File),Width=72,Height=65,Stretch=Stretch.Uniform,Margin=new Thickness(5)});
                panel.Children.Add(new TextBlock{Text=glyph.Letter+" · "+glyph.Width+" × "+glyph.Height+" px\n"+(glyph.Source??""),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});_glyphs.Items.Add(new ListBoxItem{Content=panel,Tag=glyph});
            }
            _jobs.Items.Clear();foreach(var job in _set.Jobs.AsEnumerable().Reverse())_jobs.Items.Add(new ListBoxItem{Content=LetterTransferModels.Name(job.Model)+" · "+String.Join(" ",job.Letters)+" · "+"заказано "+job.Resolution+(File.Exists(LetterTransferStore.Resolve(_directory,job.Output))?" · получено "+LetterTransferImages.ActualSize(LetterTransferStore.Resolve(_directory,job.Output)):"")+" · "+(job.Reviewed?"проверено":File.Exists(LetterTransferStore.Resolve(_directory,job.Output))?"ответ готов":LetterTransferAi.UncertainSubmission(_directory,job)?"нет номера от Replicate":"запрос сохранён"),Tag=job});
        }
        private BitmapSource Thumb(string relative,int maximumHeight=250)
        {
            string cacheKey=relative+"|"+maximumHeight;
            if(_cache.TryGetValue(cacheKey,out var image))return image;string path=LetterTransferStore.Resolve(_directory,relative);if(!File.Exists(path))return null;
            try{using(var stream=File.OpenRead(path)){var thumbnail=new BitmapImage();thumbnail.BeginInit();thumbnail.CacheOption=BitmapCacheOption.OnLoad;thumbnail.StreamSource=stream;thumbnail.DecodePixelHeight=maximumHeight;thumbnail.EndInit();thumbnail.Freeze();image=thumbnail;}}
            catch(Exception error)when(error is IOException || error is NotSupportedException || error is FileFormatException || error is ArgumentException){Log.Error("Could not load saved letter thumbnail.",error);image=null;}
            _cache[cacheKey]=image;return image;
        }
        private void Preview()
        {
            RefreshCounts();_pages.Children.Clear();var placements=LetterTransferLayout.Build(_set,_readyOnly.IsChecked==true);int pages=placements.Count==0?0:placements.Max(p=>p.Page)+1;
            if(pages>200)throw new InvalidOperationException("Получается больше 200 листов. Увеличьте лист или уменьшите количество / размер букв.");
            foreach(var group in placements.GroupBy(p=>p.Page))
            {
                double pageHeight=LetterTransferLayout.PageHeight(_set,group);var drawing=new DrawingGroup();using(var context=drawing.Open())
                {
                    context.DrawRectangle(_previewBackground.SelectedIndex==2?LetterTransferImages.Checker():_previewBackground.SelectedIndex==1?Brushes.LightGray:Brushes.White,new Pen(Brushes.Gray,.3),new Rect(0,0,_set.SheetWidthMm,pageHeight));
                    if(_showGrid.IsChecked==true)
                    {
                        var pen=new Pen(new SolidColorBrush(Color.FromArgb(100,75,90,110)),.15){DashStyle=DashStyles.Dash};
                        foreach(var col in group.Select(p=>p.CellX+p.CellWidth+_set.GapMm/2).Distinct().Where(x=>x<_set.SheetWidthMm-_set.MarginMm))context.DrawLine(pen,new Point(col,_set.MarginMm),new Point(col,pageHeight-_set.MarginMm));
                        foreach(var row in group.Select(p=>p.CellY+p.CellHeight+_set.GapMm/2).Distinct().Where(y=>y<pageHeight-_set.MarginMm))context.DrawLine(pen,new Point(_set.MarginMm,row),new Point(_set.SheetWidthMm-_set.MarginMm,row));
                    }
                    foreach(var p in group)
                    {
                        var image=Thumb(p.Glyph.File,Math.Min(1024,p.Glyph.Height));if(image==null)throw new FileNotFoundException("Не найден PNG буквы «"+p.Glyph.Letter+"». Восстановите файл или импортируйте новый вариант.");
                        context.DrawImage(image,new Rect(p.X,p.Y,p.Width,p.Height));
                    }
                }
                drawing.Freeze();var border=new Border{Child=new Image{Source=new DrawingImage(drawing),Width=_set.SheetWidthMm*2,Height=pageHeight*2},Margin=new Thickness(8)};_pages.Children.Add(border);
            }
            _pages.LayoutTransform=new ScaleTransform(_zoom,_zoom);int requested=_set.Letters.Sum(r=>r.Copies);int missing=_set.Letters.Count(r=>!_set.Glyphs.Any(g=>g.Letter==r.Letter));
            int missingVariants=LetterTransferRules.MissingVariants(_set);
            _complete.Content=missingVariants>0?"Довести до "+_set.VariantsPerLetter+" · не хватает "+missingVariants+"…":"Готово · по "+_set.VariantsPerLetter+" на букву";_complete.IsEnabled=missingVariants>0;
            _summary.Text="На листах: "+placements.Count+" из "+requested+" копий · листов: "+pages+" · нет букв: "+missing+" · длина "+N(placements.GroupBy(p=>p.Page).Sum(g=>LetterTransferLayout.PageHeight(_set,g)))+" мм\nВариантов не хватает: "+missingVariants+" · цель: "+_set.VariantsPerLetter+" на каждую букву.\nПрямая сетка · основа "+N(_set.HeightMm)+" мм; Ё/Й выше · Ctrl+колесо — зум. Исходные PNG сохраняются без масштабирования.";
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
            if(_request!=null || _sequenceBusy)return;
            try
            {
                Save();string token=_token();if(String.IsNullOrWhiteSpace(token))throw new InvalidOperationException("Укажите ключ Replicate в настройках ИИ.");
                var targets=selected?_table.SelectedItems.Cast<LetterTransferRow>().Select(r=>r.Letter).Distinct().ToArray():LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();
                if(targets.Length==0)throw new InvalidOperationException("Выберите строки букв в таблице.");
                var passes=LetterTransferRules.MissingPasses(_set,targets);bool extra=selected && passes.Count==0;
                if(extra)passes.Add(targets);
                if(passes.Count==0)throw new InvalidOperationException("Весь алфавит уже сохранён в выбранном числе оформлений.");
                var limits=targets.ToDictionary(l=>l,l=>extra?_set.Glyphs.Count(g=>g.Letter==l)+1:_set.VariantsPerLetter);
                if(String.IsNullOrWhiteSpace(_set.Reference))throw new InvalidOperationException("Сначала добавьте референс стиля из файла или выделения Corel.");
                var reference=LetterTransferImages.RequestReference(LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,_set.Reference)));
                string model=_set.GenerationModel;
                _sequenceBusy=true;
                for(int pass=0;pass<passes.Count;pass++)
                {
                    var letters=passes[pass].Where(l=>_set.Glyphs.Count(g=>g.Letter==l)<limits[l]).ToArray();if(letters.Length==0)continue;
                    _sequenceNote="Запрос "+(pass+1)+" из "+passes.Count+" · "+(extra?"ещё один вариант выбранных букв":"цель: "+_set.VariantsPerLetter+" оформления каждой буквы")+". Каждый запрос подтверждается отдельно.";
                    if(_set.Jobs.Count>=200)throw new InvalidOperationException("В наборе уже 200 запросов. Создайте новый набор.");
                    if(letters.Any(l=>_set.Glyphs.Count(g=>g.Letter==l)>=8))throw new InvalidOperationException("На букву можно сохранить до 8 вариантов. Удалите ненужные.");
                    string resolution=letters.Length>4?"4K":(string)_resolution.SelectedItem;
                    if(letters.Length>4)_resolution.SelectedItem="4K";
                    if(!_confirm(reference,letters,model,resolution,LetterTransferAi.Prompt(_set,letters,LetterTransferImages.Key(reference),model),false))return;
                    var before=letters.ToDictionary(l=>l,l=>_set.Glyphs.Count(g=>g.Letter==l));
                    var job=LetterTransferAi.Create(_directory,_set,letters,model,resolution,reference);
                    if(!await Run(job,token))return;
                    if(letters.All(l=>_set.Glyphs.Count(g=>g.Letter==l)<=before[l])){Report("Новых вариантов не принято. Серия остановлена; оставшиеся запросы не отправлены.");return;}
                }
                int missing=targets.Sum(l=>Math.Max(0,limits[l]-_set.Glyphs.Count(g=>g.Letter==l)));
                Report(missing>0?"Серия проверена. До выбранной цели не хватает вариантов: "+missing+". Нажмите «Довести до "+_set.VariantsPerLetter+"» для оставшихся букв; новые запросы потребуют подтверждения.":selected?"Выбранные буквы сохранены.":"Полный алфавит сохранён: 33 буквы, оформлений на букву — "+_set.VariantsPerLetter+". Можно компоновать полотно.");
            }
            catch(Exception error){Failure(error);}
            finally{_sequenceBusy=false;_sequenceNote=null;}
        }
        private LetterTransferJob SelectedJob()=>((_jobs.SelectedItem as ListBoxItem)?.Tag as LetterTransferJob)??_set.Jobs.LastOrDefault(j=>!j.Reviewed);
        private async void Retry()
        {
            if(_request!=null)return;try
            {
                Save();var job=SelectedJob();if(job==null)throw new InvalidOperationException("Выберите запрос в истории.");
                if(LetterTransferAi.HasResult(_directory,job)){await Review(job);return;}
                if(LetterTransferAi.UncertainSubmission(_directory,job))
                {await Run(job,_token());return;}
                string token=_token();if(String.IsNullOrWhiteSpace(token))throw new InvalidOperationException("Укажите ключ Replicate в настройках ИИ.");
                var reference=LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,job.Reference));
                if(!_confirm(reference,job.Letters,job.Model,job.Resolution,job.Prompt,true))return;await Run(job,token);
            }
            catch(Exception error){Failure(error);}
        }
        private async Task<bool> Run(LetterTransferJob job,string token)
        {
            _refresh.Stop();_request=new CancellationTokenSource();_body.IsEnabled=_tools.IsEnabled=false;_cancel.Content="Отменить запрос";_stage="Генерация букв";_timingKey="letters/"+job.Model+"/"+job.Resolution;_elapsed.Restart();_clock.Start();bool generated=false;Report("ИИ создаёт «"+String.Join(" ",job.Letters)+"». Ожидайте ответ.");
            try
            {
                await Task.Run(()=>LetterTransferAi.Generate(_directory,job,token,progress=>Dispatcher.BeginInvoke(new Action(()=>{_stage=progress=="sending"?"Отправка превью":progress=="recovering"?"Поиск созданного запроса · без новой генерации":progress=="creating"?"Запуск модели":progress=="processing"?"Создание букв":progress=="downloading"?"Скачивание ответа":progress=="auto-cancelling"?"Автоотмена · лимит 10 минут":progress=="cancelling"?"Отмена":"Ожидание ИИ";})),_request.Token));
                generated=true;OperationTiming.Record(_timingKey,_elapsed.Elapsed);Report("Ответ ИИ сохранён. Проверьте буквы перед добавлением.");
            }
            catch(Exception error){Failure(error);}
            finally
            {
                _clock.Stop();_elapsed.Stop();_timing.Text=OperationTiming.Display(_stage,_elapsed.Elapsed,_timingKey);_request.Dispose();_request=null;_body.IsEnabled=_tools.IsEnabled=true;_cancel.Content="Закрыть";RefreshAssets();
            }
            if(LetterTransferAi.HasResult(_directory,job))await Review(job);
            return generated;
        }
        private async void ReviewJob(bool forceRecognition=false)
        {
            if(_request!=null || _sequenceBusy)return;
            try{var job=SelectedJob();if(job==null)throw new InvalidOperationException("В истории пока нет запросов.");await Review(job,forceRecognition);}catch(Exception error){Failure(error);}
        }
        private async void CompleteAlphabet()
        {
            if(_request!=null || _sequenceBusy)return;
            try
            {
                Save();var missing=new HashSet<string>(_set.Letters.Where(r=>!_set.Glyphs.Any(g=>g.Letter==r.Letter)).Select(r=>r.Letter));
                if(missing.Count==0){Generate(false);return;}
                var job=_set.Jobs.AsEnumerable().Reverse().FirstOrDefault(j=>j.Letters.Any(missing.Contains) && LetterTransferAi.HasResult(_directory,j));
                if(job==null){Generate(false);return;}
                await Review(job,false,true);
            }
            catch(Exception error){Failure(error);}
        }
        private async Task Review(LetterTransferJob job,bool forceRecognition=false,bool onlyMissing=false)
        {
            var sheet=LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,job.Output));_recognitionSourceNote="Исходный ответ: "+sheet.PixelWidth+" × "+sheet.PixelHeight+" px · заказано "+job.Resolution+".";var cells=LetterTransferSheet.Read(sheet,job.Letters,job.Key,_set.AlphaThreshold,CancellationToken.None);
            string output=LetterTransferStore.Resolve(_directory,job.Output);LetterTransferReviewDraft.Restore(output,cells);
            bool manuallyComplete=cells.Cells.Where(c=>c.Image!=null).All(c=>c.Manual && LetterTransferRules.ValidLetter(c.Letter));
            try
            {
                var plan=LetterTransferRecognition.Prepare(_directory,job,cells);
                if(!forceRecognition && LetterTransferRecognition.TryCached(plan,out var cached))LetterTransferRecognition.Apply(cells,cached,plan.CellIndices);
                else if(forceRecognition || !manuallyComplete)
                {
                    string token=_token();
                    if(String.IsNullOrWhiteSpace(token))Report("Для распознавания нужен ключ Replicate. Можно проверить и назначить обозначения вручную.",true);
                    else if(_confirmRecognition(plan.Preview,plan.Count,plan.Prompt,!forceRecognition && (File.Exists(plan.Output+".prediction.json") || File.Exists(plan.Output+".submitted"))))
                    {
                        _refresh.Stop();_request=new CancellationTokenSource();_body.IsEnabled=_tools.IsEnabled=false;_cancel.Content="Отменить распознавание";_stage="Распознавание букв · GPT mini";_timingKey="letters/recognition/"+LetterTransferRecognition.Model;_elapsed.Restart();_clock.Start();
                        try
                        {
                            var labels=await Task.Run(()=>LetterTransferRecognition.Run(plan,token,progress=>Dispatcher.BeginInvoke(new Action(()=>{_stage=progress=="sending"?"Отправка коллажа букв":progress=="processing"?"Распознавание букв · GPT mini":progress=="recovering"?"Поиск сохранённого анализа":progress=="downloading"?"Получение обозначений":progress=="auto-cancelling"?"Автоотмена · лимит 10 минут":progress=="cancelling"?"Отмена распознавания":"Ожидание распознавания";})),_request.Token,forceRecognition));
                            LetterTransferRecognition.Apply(cells,labels,plan.CellIndices);OperationTiming.Record(_timingKey,_elapsed.Elapsed);
                        }
                        finally{_clock.Stop();_elapsed.Stop();_timing.Text=OperationTiming.Display(_stage,_elapsed.Elapsed,_timingKey);_request.Dispose();_request=null;_body.IsEnabled=_tools.IsEnabled=true;_cancel.Content="Закрыть";}
                    }
                }
            }
            catch(Exception error){Failure(error);}
            LetterTransferReviewDraft.Restore(output,cells);
            if(job.Resolution=="4K" && Math.Max(sheet.PixelWidth,sheet.PixelHeight)<3000 || job.Resolution=="2K" && Math.Max(sheet.PixelWidth,sheet.PixelHeight)<1500)
                cells.Notice=(cells.Notice??"")+" Внимание: запрошено "+job.Resolution+", получено "+sheet.PixelWidth+" × "+sheet.PixelHeight+" px. Размер ответа ниже запрошенного.";
            LetterTransferEdges.Preview(cells,_set,CancellationToken.None);
            var approved=_review(LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,job.Reference)),sheet,cells);
            LetterTransferReviewDraft.Save(output,cells);
            if(onlyMissing){var existing=new HashSet<string>(_set.Glyphs.Select(g=>g.Letter));approved=approved.Where(g=>!existing.Contains(g.Letter)).ToList();}
            // Consume previously imported cells as a multiset. Reopening a
            // partial review can add remaining variants without importing the
            // accepted cells again, including visually identical duplicates.
            string source="ИИ · "+job.Id.Substring(0,8);var imported=new Dictionary<string,int>();
            foreach(var glyph in _set.Glyphs.Where(g=>g.Source==source))
            {
                string file=LetterTransferStore.Resolve(_directory,String.IsNullOrEmpty(glyph.OriginalFile)?glyph.File:glyph.OriginalFile);if(!File.Exists(file))continue;
                string key=glyph.Letter+LetterTransferImages.Fingerprint(LetterTransferImages.Load(file));imported[key]=imported.TryGetValue(key,out int count)?count+1:1;
            }
            approved=approved.Where(g=>{
                string key=g.Letter+LetterTransferImages.Fingerprint(LetterTransferImages.Prepare(g.Original??g.Image,_set.AlphaThreshold,CancellationToken.None));
                if(imported.TryGetValue(key,out int count) && count>0){imported[key]=count-1;return false;}return true;
            }).ToList();
            if(approved.Count==0){Report("Новых отмеченных букв нет. Уже добавленные рисунки сохранены в наборе.");return;}
            foreach(var group in approved.GroupBy(a=>a.Letter))if(_set.Glyphs.Count(g=>g.Letter==group.Key)+group.Count()>8)throw new InvalidOperationException("Слишком много вариантов буквы «"+group.Key+"». Удалите ненужные.");
            foreach(var glyph in approved)LetterTransferImages.Add(_directory,_set,glyph.Letter,glyph.Original??glyph.Image,"ИИ · "+job.Id.Substring(0,8));job.Reviewed=true;Save();RefreshAssets();Preview();Report("Добавлено проверенных рисунков: "+approved.Count+". Букв в наборе: "+_set.Glyphs.Select(g=>g.Letter).Distinct().Count()+"; повторы сохранены как варианты.");
        }
        private async void PreviewEdge()
        {
            if(_request!=null || _sequenceBusy)return;BitmapSource before=null,after=null;string letter=null;
            try
            {
                Save();var glyph=(_glyphs.SelectedItem as ListBoxItem)?.Tag as LetterTransferGlyph??_set.Glyphs.FirstOrDefault();if(glyph==null)throw new InvalidOperationException("В наборе пока нет готовых букв.");
                letter=glyph.Letter;before=LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,glyph.File));
                var original=LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,String.IsNullOrEmpty(glyph.OriginalFile)?glyph.File:glyph.OriginalFile));
                _refresh.Stop();_request=new CancellationTokenSource();_body.IsEnabled=_tools.IsEnabled=false;_cancel.Content="Отменить просмотр";Report("Готовлю новый край «"+letter+"»…");
                after=await Task.Run(()=>LetterTransferEdges.Finish(original,_set,letter,_request.Token));
            }
            catch(OperationCanceledException){Report("Подготовка просмотра отменена.");}
            catch(Exception error){Failure(error);}
            finally{if(_request!=null){_request.Dispose();_request=null;}_body.IsEnabled=_tools.IsEnabled=true;_cancel.Content="Закрыть";}
            if(after!=null)LetterTransferEdgePreview.Create(this,letter,before,after).ShowDialog();
        }
        private async void Reclean()
        {
            if(_request!=null || _sequenceBusy)return;
            try
            {
                Save();if(_set.Glyphs.Count==0)throw new InvalidOperationException("В наборе пока нет готовых букв.");
                _refresh.Stop();_request=new CancellationTokenSource();_body.IsEnabled=_tools.IsEnabled=false;_cancel.Content="Отменить очистку";Report("Очищаю края готовых букв · "+_set.EdgeTrimPx+" px…");
                int count=await Task.Run(()=>LetterTransferEdges.Reclean(_directory,_set,_request.Token));
                _cache.Clear();RefreshAssets();Preview();Report("Края очищены: "+count+" рисунков. Исходные PNG сохранены; 0 px, отключённое сглаживание и ровный контур возвращают исходный край.");
            }
            catch(OperationCanceledException){Report("Очистка отменена. Буквы и исходные PNG сохранены.");}
            catch(Exception error){Failure(error);}
            finally{if(_request!=null){_request.Dispose();_request=null;}_body.IsEnabled=_tools.IsEnabled=true;_cancel.Content="Закрыть";}
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
