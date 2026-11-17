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
    internal sealed partial class LetterTransferWindow:Window
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
        private readonly TabControl _steps=new TabControl(),_views=new TabControl();
        private readonly ComboBox _edgeQuality=new ComboBox();
        private readonly Expander _edgeExpert=new Expander{Header="Ручная настройка края",IsExpanded=false};
        private readonly TextBlock _workflowNote=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(5)};
        private FrameworkElement _countsPanel,_sizesPanel;
        private Button _insert;
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
        private string _stage="",_timingKey,_recognitionSourceNote;
        private double _zoom=1;
        internal LetterTransferWindow(Func<string> token,Func<string> capture,Action<string,bool> status,string libraryRoot=null,Func<BitmapSource,string[],string,string,string,bool,bool> confirm=null,Func<string> captureReference=null,Func<BitmapSource,int,string,bool,bool> confirmRecognition=null,Func<BitmapSource,BitmapSource,LetterTransferSheet,List<LetterTransferApproved>> review=null)
        {
            _token=token;_capture=capture;_captureReference=captureReference??capture;_status=status;_root=libraryRoot??LetterTransferStore.DefaultRoot;_confirm=confirm??((reference,letters,model,resolution,prompt,retry)=>_batchPlan!=null?LetterTransferReviewWindow.ConfirmBatch(this,reference,_batchPlan):LetterTransferReviewWindow.Confirm(this,reference,letters,model,resolution,prompt,retry));
            _confirmRecognition=confirmRecognition??((preview,count,prompt,retry)=>_analysisBatchCount>0?LetterTransferReviewWindow.ConfirmRecognitionBatch(this,preview,_analysisBatchCount,count,prompt):LetterTransferReviewWindow.ConfirmRecognition(this,preview,count,prompt,retry,_recognitionSourceNote));
            _review=review??((reference,sheet,cells)=>LetterTransferReviewWindow.Review(this,reference,sheet,cells));
            Title="Буквы-трансферы · Vanya Tools";Width=Math.Min(1380,SystemParameters.WorkArea.Width-30);Height=Math.Min(900,SystemParameters.WorkArea.Height-30);MinWidth=1000;MinHeight=640;WindowStartupLocation=WindowStartupLocation.CenterOwner;DockerTheme.Apply(this);
            var root=new DockPanel{Margin=new Thickness(10)};Content=root;
            var bottom=new DockPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
            var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);bottom.Children.Add(actions);
            _insert=DockerTheme.Primary(Button("Создать листы в Corel",()=>Safe(Insert)));actions.Children.Add(_insert);
            _cancel=Button("Закрыть",()=>{if(_request!=null || _sequenceBusy){_sequenceCancellation?.Cancel();_request?.Cancel();Report("Останавливаю. Полученные ответы сохранятся.");}else Close();});actions.Children.Add(_cancel);
            var statuses=new StackPanel();statuses.Children.Add(_message);statuses.Children.Add(_timing);bottom.Children.Add(statuses);
            DockPanel.SetDock(_tools,Dock.Top);root.Children.Add(_tools);
            _tools.Children.Add(new TextBlock{Text="Набор",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3)});_tools.Children.Add(_library);
            _tools.Children.Add(Button("Новый",()=>Safe(NewSet)));_tools.Children.Add(Button("Открыть…",()=>Safe(OpenSet)));_tools.Children.Add(Button("Сохранить",()=>Safe(()=>{Save();RefreshLibrary();Report("Набор сохранён: "+_directory);})));_tools.Children.Add(Button("Папка набора",()=>Safe(()=>Process.Start(new ProcessStartInfo(_directory){UseShellExecute=true}))));
            _body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(285)});_body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(255)});_body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});root.Children.Add(_body);
            var leftTabs=_steps;leftTabs.Margin=new Thickness(0,8,10,0);_body.Children.Add(leftTabs);
            var sizes=new WrapPanel{Margin=new Thickness(0,4,0,0)};_sizesPanel=sizes;DockPanel.SetDock(sizes,Dock.Top);root.Children.Insert(root.Children.IndexOf(_body),sizes);
            sizes.Children.Add(new TextBlock{Text="Высота основы букв, мм",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});_height.Width=70;_height.MinHeight=29;sizes.Children.Add(_height);
            sizes.Children.Add(new TextBlock{Text="Ширина полотна, мм",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,4,0)});_sheetWidth.Width=80;_sheetWidth.MinHeight=29;sizes.Children.Add(_sheetWidth);
            var fields=new StackPanel{Margin=new Thickness(5)};var printFields=new StackPanel{Margin=new Thickness(5)};var edgeFields=new StackPanel{Margin=new Thickness(5)};var edgeAdvanced=new StackPanel();_edgeExpert.Content=edgeAdvanced;
            leftTabs.Items.Add(new TabItem{Header="1. Стиль",Content=new ScrollViewer{Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            leftTabs.Items.Add(new TabItem{Header="2. Буквы",Content=new ScrollViewer{Content=edgeFields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            leftTabs.Items.Add(new TabItem{Header="3. Печать",Content=new ScrollViewer{Content=printFields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            var projectFields=new StackPanel();Field(projectFields,"Название набора",_name);fields.Children.Add(new Expander{Header="Название набора",Content=projectFields});_name.MaxLength=120;
            Field(fields,"Референс · одна буква, несколько или набор",new Border{Child=_reference,Background=Brushes.LightGray,Padding=new Thickness(4)});var refButtons=new WrapPanel();refButtons.Children.Add(Button("Референс из файла…",()=>Safe(()=>Reference(false))));refButtons.Children.Add(Button("Из Corel",()=>Safe(()=>Reference(true))));fields.Children.Add(refButtons);
            _style.ItemsSource=new[]{"По референсу","Единый алфавит и фактура","Коллаж · разные оформления"};Field(fields,"Оформление",_style);
            _notes.MaxLength=4000;_notes.TextWrapping=TextWrapping.Wrap;_notes.AcceptsReturn=true;_notes.Height=55;_notes.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;Field(fields,"Уточнения для генерации",_notes);
            fields.Children.Add(new TextBlock{Text="Референс → буквы → полотно. Создание, удаление фона, распознавание и край идут одной серией.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,7)});
            printFields.Children.Add(new TextBlock{Text="Количество распределяется по частоте русских букв. Любое число копий можно исправить в таблице.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,6)});
            Field(printFields,"Всего копий (33–5000)",_target);var frequencyAdvanced=new StackPanel();frequencyAdvanced.Children.Add(Button("Восстановить распределение по частотности",()=>Safe(()=>{Read();LetterTransferRules.Allocate(_set.Letters,_set.TargetCopies);_table.Items.Refresh();Save();Preview();})));var weights=new CheckBox{Content="Показать частотные веса",Margin=new Thickness(4)};weights.Checked+=(_,__)=>_table.Columns[1].Visibility=Visibility.Visible;weights.Unchecked+=(_,__)=>_table.Columns[1].Visibility=Visibility.Collapsed;frequencyAdvanced.Children.Add(weights);printFields.Children.Add(new Expander{Header="Частотность",Content=frequencyAdvanced});
            Field(fields,"Оформлений на букву (1–4)",_variants);
            var aiAdvanced=new StackPanel();_model.ItemsSource=LetterTransferModels.Names;_model.SelectedIndex=0;Field(aiAdvanced,"Модель",_model);_resolution.ItemsSource=new[]{"2K","4K"};_resolution.SelectedIndex=1;Field(aiAdvanced,"Размер отдельных букв",_resolution);fields.Children.Add(new Expander{Header="Модель и размер ответа",Content=aiAdvanced});
            fields.Children.Add(DockerTheme.Primary(Button("Создать буквы",()=>Generate(false))));
            fields.Children.Add(new TextBlock{Text="Один лист 4K на оформление. Перед серией показывается общее число платных запросов. Проверка результата — после всей серии.",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,3,0,8)});
            edgeFields.Children.Add(_workflowNote);edgeFields.Children.Add(DockerTheme.Primary(Button("Проверить полученные листы",()=>ReviewPending())));
            _edgeQuality.ItemsSource=new[]{"Ровный край · сохранить окантовку","Исходный силуэт","Мои параметры"};Field(edgeFields,"Край букв",_edgeQuality);
            edgeFields.Children.Add(Button("Посмотреть край выбранной буквы",()=>PreviewEdge()));
            edgeFields.Children.Add(DockerTheme.Primary(Button("Обновить край всех букв",()=>Reprocess())));
            edgeFields.Children.Add(new TextBlock{Text="Изменения берутся из сохранённых исходников. Старые PNG остаются в наборе; обрезка не накапливается.",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,3,0,8)});
            edgeFields.Children.Add(_edgeExpert);
            edgeFields.Children.Add(Button("Догенерировать недостающие",()=>Generate(false)));
            edgeFields.Children.Add(Button("Дальше: полотно →",()=>SelectStep(2)));
            var formatAdvanced=new StackPanel();formatAdvanced.Children.Add(_autoLength);Field(formatAdvanced,"Высота фиксированного листа, мм",_sheetHeight);printFields.Children.Add(new Expander{Header="Формат листа",Content=formatAdvanced});
            Field(edgeAdvanced,"Порог удаления полупрозрачности (1–255)",_alphaThreshold);
            edgeAdvanced.Children.Add(_edgeTrimLabel);edgeAdvanced.Children.Add(_edgeTrim);edgeAdvanced.Children.Add(_smoothEdges);
            edgeAdvanced.Children.Add(_sharpContour);edgeAdvanced.Children.Add(_contourLabel);edgeAdvanced.Children.Add(_contourSmooth);
            _sharpContour.ToolTip="Выравнивает внешний силуэт и отверстия, сохраняет углы и окантовку. Текстура остаётся внутри чёткого контура; PNG выводится с разрешением до 600 DPI. Новые детали изображения не создаются.";
            _contourSmooth.ToolTip="Допустимое выравнивание края в пикселях исходной буквы. Меньше — ближе к исходнику, больше — ровнее силуэт.";
            _smoothEdges.ToolTip="Сглаживает маску без полупрозрачности и убирает отдельные частицы до 4 пикселей. Исходник сохраняется.";
            _edgeTrim.ToolTip="Пиксели исходной буквы после удаления фона. 0 — без подрезания. Для уже сохранённых букв нажмите кнопку ниже.";
            edgeAdvanced.Children.Add(Button("Только повторная обработка PNG",()=>Reclean()));
            var layoutAdvanced=new StackPanel();Field(layoutAdvanced,"Зазор, мм",_gap);Field(layoutAdvanced,"Поля, мм",_margin);layoutAdvanced.Children.Add(_readyOnly);printFields.Children.Add(new Expander{Header="Поля и состав листа",Content=layoutAdvanced});
            printFields.Children.Add(new TextBlock{Text="Прямая сетка для нарезки. Основа букв одинаковой высоты; Ё и Й получают место сверху.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,7)});
            printFields.Children.Add(Button("← Вернуться к буквам",()=>SelectStep(1)));
            var middle=new DockPanel{Margin=new Thickness(0,8,10,0)};_countsPanel=middle;Grid.SetColumn(middle,1);_body.Children.Add(middle);
            var tableNote=new TextBlock{Text="Копий каждой буквы\nМожно менять вручную",FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,5)};DockPanel.SetDock(tableNote,Dock.Top);middle.Children.Add(tableNote);
            _complete=Button("Перейти к буквам",()=>SelectStep(1));DockPanel.SetDock(_complete,Dock.Bottom);middle.Children.Add(_complete);
            _table.Columns.Add(new DataGridTextColumn{Header="Буква",Binding=new Binding("Letter"),IsReadOnly=true,Width=48});
            _table.Columns.Add(new DataGridTextColumn{Header="Вес",Binding=new Binding("Weight"),Width=53,Visibility=Visibility.Collapsed});_table.Columns.Add(new DataGridTextColumn{Header="Копий",Binding=new Binding("Copies"),Width=60});_table.Columns.Add(new DataGridTextColumn{Header="Вар.",Binding=new Binding("Ready"),IsReadOnly=true,Width=56});middle.Children.Add(_table);
            _table.CellEditEnding+=(_,__)=>Queue();_table.SelectionChanged+=(_,__)=>{var row=_table.SelectedItem as LetterTransferRow;if(row!=null)_letter.SelectedItem=row.Letter;};
            var tabs=_views;tabs.Margin=new Thickness(0,8,0,0);Grid.SetColumn(tabs,2);_body.Children.Add(tabs);
            var print=new DockPanel();var previewTools=new WrapPanel();DockPanel.SetDock(previewTools,Dock.Top);print.Children.Add(previewTools);
            previewTools.Children.Add(Button("−",()=>Zoom(_zoom/1.25)));previewTools.Children.Add(Button("+",()=>Zoom(_zoom*1.25)));previewTools.Children.Add(Button("Вписать",()=>Zoom(Math.Max(.15,(_scroll.ActualWidth-35)/(_set.SheetWidthMm*2)))));previewTools.Children.Add(Button("100%",()=>Zoom(1)));_previewBackground.ItemsSource=new[]{"Белый фон","Серый фон","Шахматка"};_previewBackground.SelectedIndex=1;_previewBackground.ToolTip="Только фон предпросмотра. На листы Corel не добавляется.";previewTools.Children.Add(_previewBackground);previewTools.Children.Add(_showGrid);_showGrid.Checked+=(_,__)=>Queue();_showGrid.Unchecked+=(_,__)=>Queue();_showGrid.ToolTip="Направляющие только в предпросмотре; на печатный лист не добавляются.";_previewBackground.SelectionChanged+=(_,__)=>Queue();
            DockPanel.SetDock(_summary,Dock.Bottom);print.Children.Add(_summary);_scroll.Content=_pages;print.Children.Add(_scroll);tabs.Items.Add(new TabItem{Header="Полотно",Content=print});
            _scroll.PreviewMouseWheel+=(_,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;Zoom(_zoom*(e.Delta>0?1.15:1/1.15));e.Handled=true;};
            var variantsPanel=new DockPanel();var variantTools=new WrapPanel();DockPanel.SetDock(variantTools,Dock.Top);variantsPanel.Children.Add(variantTools);
            _letter.ItemsSource=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();_letter.SelectedIndex=0;_letter.Width=60;variantTools.Children.Add(_letter);
            variantTools.Children.Add(Button("PNG / изображение…",()=>Safe(()=>Import(false))));variantTools.Children.Add(Button("Из Corel",()=>Safe(()=>Import(true))));variantTools.Children.Add(Button("Удалить вариант",()=>Safe(Remove)));variantTools.Children.Add(Button("Ещё вариант этой буквы",()=>Generate(true)));variantTools.Children.Add(Button("Предпросмотр края…",()=>PreviewEdge()));variantsPanel.Children.Add(_glyphs);tabs.Items.Add(new TabItem{Header="Буквы",Content=variantsPanel});
            var jobsPanel=new DockPanel();var jobTools=new WrapPanel();DockPanel.SetDock(jobTools,Dock.Top);jobsPanel.Children.Add(jobTools);
            jobTools.Children.Add(Button("История Replicate",()=>Safe(()=>Process.Start(new ProcessStartInfo("https://replicate.com/predictions"){UseShellExecute=true}))));jobTools.Children.Add(Button("Проверить готовый ответ",()=>ReviewJob()));jobTools.Children.Add(Button("Распознать буквы заново…",()=>ReviewJob(true)));jobTools.Children.Add(Button("Продолжить / повторить…",()=>Retry()));jobsPanel.Children.Add(_jobs);tabs.Items.Add(new TabItem{Header="История ИИ",Content=jobsPanel});
            var referenceView=new DockPanel();var referenceHint=new TextBlock{Text="1. Добавьте образец стиля.\n2. Выберите число оформлений.\n3. Нажмите «Создать буквы» — результат придёт одной проверкой.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12)};DockPanel.SetDock(referenceHint,Dock.Top);referenceView.Children.Add(referenceHint);var largeReference=new Image{Stretch=Stretch.Uniform,Margin=new Thickness(15)};largeReference.SetBinding(Image.SourceProperty,new Binding("Source"){Source=_reference});referenceView.Children.Add(largeReference);tabs.Items.Add(new TabItem{Header="Референс",Content=referenceView});
            _steps.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,_steps))SelectStep(_steps.SelectedIndex);};
            _edgeQuality.SelectionChanged+=(_,__)=>{if(!_loading)ChooseEdgePreset();};
            _letter.SelectionChanged+=(_,__)=>{if(!_loading && _steps.SelectedIndex==1)_table.SelectedItem=_set.Letters.FirstOrDefault(r=>r.Letter==(string)_letter.SelectedItem);};
            _glyphs.SelectionChanged+=(_,__)=>{if((_glyphs.SelectedItem as ListBoxItem)?.Tag is LetterTransferGlyph glyph)_letter.SelectedItem=glyph.Letter;};
            var factory=new FrameworkElementFactory(typeof(WrapPanel));_glyphs.ItemsPanel=new ItemsPanelTemplate(factory);ScrollViewer.SetHorizontalScrollBarVisibility(_glyphs,ScrollBarVisibility.Disabled);
            _library.SelectionChanged+=(_,__)=>{if(_loading || _library.SelectedItem==null)return;Safe(()=>{Save();Load(((LetterTransferLibraryItem)_library.SelectedItem).Directory);});};
            foreach(var input in new[]{_name,_notes,_height,_sheetWidth,_sheetHeight,_gap,_margin,_target,_variants,_alphaThreshold})input.TextChanged+=(_,__)=>Queue();_style.SelectionChanged+=(_,__)=>Queue();_model.SelectionChanged+=(_,__)=>Queue();_smoothEdges.Checked+=(_,__)=>Queue();_smoothEdges.Unchecked+=(_,__)=>Queue();_edgeTrim.ValueChanged+=(_,__)=>{_edgeTrimLabel.Text="Подрезать край: "+((int)_edgeTrim.Value)+" px";Queue();};_sharpContour.Checked+=(_,__)=>{_contourSmooth.IsEnabled=true;Queue();};_sharpContour.Unchecked+=(_,__)=>{_contourSmooth.IsEnabled=false;Queue();};_contourSmooth.ValueChanged+=(_,__)=>{_contourLabel.Text="Выравнивание контура: "+N(_contourSmooth.Value)+" px";Queue();};_readyOnly.Checked+=(_,__)=>Queue();_readyOnly.Unchecked+=(_,__)=>Queue();_autoLength.Checked+=(_,__)=>{_sheetHeight.IsEnabled=false;Queue();};_autoLength.Unchecked+=(_,__)=>{_sheetHeight.IsEnabled=true;Queue();};
            _target.TextChanged+=(_,__)=>{if(_loading || _set==null || !Int32.TryParse(_target.Text,out int value) || value<33 || value>5000 || value==_set.TargetCopies)return;_set.TargetCopies=value;LetterTransferRules.Allocate(_set.Letters,value);RefreshCounts();Queue();};
            _refresh.Tick+=(_,__)=>{_refresh.Stop();if(_loading || _request!=null)return;try{Save();Preview();RefreshWorkflow();}catch(Exception error){_summary.Text=error.GetBaseException().Message;}};
            _clock.Tick+=(_,__)=>_timing.Text=OperationTiming.Display(_stage,_elapsed.Elapsed,_timingKey);
            Closing+=(_,e)=>{if(_request!=null || _sequenceBusy){e.Cancel=true;_sequenceCancellation?.Cancel();_request?.Cancel();Report("Дождитесь завершения отмены. Результат и история останутся в наборе.");return;}try{Save();}catch(Exception error){e.Cancel=true;Report(error.GetBaseException().Message,true);}};
            Closed+=(_,__)=>{_refresh.Stop();_clock.Stop();};
            var saved=LetterTransferStore.List(_root);if(saved.Count>0)Load(saved[0].Directory);else NewSet();RefreshLibrary();
        }
        private void NewSet()
        {
            if(_set!=null)Save();var set=new LetterTransferSet{Name="Набор букв "+DateTime.Now.ToString("dd.MM HH-mm")};LetterTransferWorkflow.EdgePreset(set,0);string directory=LetterTransferStore.Create(_root,set);Load(directory);RefreshLibrary();
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
                _edgeQuality.SelectedIndex=LetterTransferWorkflow.EdgeMode(set);_edgeExpert.IsExpanded=false;SelectStep(set.Glyphs.Count>0 || set.Jobs.Any(j=>LetterTransferAi.HasResult(directory,j))?1:0);RefreshAssets();Preview();Report("Набор загружен. Параметры сохраняются автоматически.");
            }
            finally{_loading=false;}
        }
        private void Read()
        {
            if(_set==null)return;
            if(!_table.CommitEdit(DataGridEditingUnit.Cell,true) || !_table.CommitEdit(DataGridEditingUnit.Row,true))throw new ArgumentException("Завершите правку таблицы: нужны числовые значения.");
            var candidate=new LetterTransferSet{
                Id=_set.Id,Schema=_set.Schema,Reference=_set.Reference,Letters=_set.Letters,Glyphs=_set.Glyphs,Jobs=_set.Jobs,
                Name=_name.Text.Trim(),Style=(string)_style.SelectedItem,Notes=_notes.Text,AutoLength=_autoLength.IsChecked==true,
                AlphaThreshold=Integer(_alphaThreshold),EdgeTrimPx=(int)_edgeTrim.Value,SmoothEdges=_smoothEdges.IsChecked==true,SharpContour=_sharpContour.IsChecked==true,ContourSmoothPx=_contourSmooth.Value,
                GenerationModel=LetterTransferModels.At(_model.SelectedIndex),HeightMm=Number(_height),SheetWidthMm=Number(_sheetWidth),SheetHeightMm=Number(_sheetHeight),
                GapMm=Number(_gap),MarginMm=Number(_margin),TargetCopies=Integer(_target),VariantsPerLetter=Integer(_variants)};
            LetterTransferRules.Validate(candidate);
            // Preserve list/object identity used by the table and in-flight jobs.
            // Invalid intermediate input must not corrupt the active settings.
            foreach(var property in typeof(LetterTransferSet).GetProperties())if(property.CanWrite && (property.PropertyType.IsValueType || property.PropertyType==typeof(string)))property.SetValue(_set,property.GetValue(candidate));
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
                var panel=new StackPanel{Background=Brushes.LightGray,Width=150,Margin=new Thickness(3)};panel.Children.Add(new Image{Source=Thumb(glyph.File),Width=140,Height=130,Stretch=Stretch.Uniform,Margin=new Thickness(5)});
                panel.Children.Add(new TextBlock{Text=glyph.Letter+" · вариант "+(_set.Glyphs.Where(g=>g.Letter==glyph.Letter).ToList().IndexOf(glyph)+1),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});_glyphs.Items.Add(new ListBoxItem{Content=panel,Tag=glyph});
            }
            RefreshWorkflow();
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
            _complete.Content=missingVariants>0?"К буквам · не хватает "+missingVariants:"Посмотреть буквы";_complete.IsEnabled=true;
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
        private async void Generate(bool selected)=>await GenerateSequence(selected);
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
        private async Task<bool> Run(LetterTransferJob job,string token,bool review=true,bool resumeOnly=false)
        {
            _refresh.Stop();_request=CancellationTokenSource.CreateLinkedTokenSource(_sequenceCancellation?.Token??CancellationToken.None);_body.IsEnabled=_tools.IsEnabled=false;_cancel.Content="Остановить";_stage="Генерация букв";_timingKey="letters/"+job.Model+"/"+job.Resolution;_elapsed.Restart();_clock.Start();bool generated=false;Report("ИИ создаёт «"+String.Join(" ",job.Letters)+"». Ожидайте ответ.");
            try
            {
                await Task.Run(()=>LetterTransferAi.Generate(_directory,job,token,progress=>Dispatcher.BeginInvoke(new Action(()=>{_stage=progress=="sending"?"Отправка превью":progress=="recovering"?"Поиск созданного запроса · без новой генерации":progress=="creating"?"Запуск модели":progress=="processing"?"Создание букв":progress=="downloading"?"Скачивание ответа":progress=="auto-cancelling"?"Автоотмена · лимит 10 минут":progress=="cancelling"?"Отмена":"Ожидание ИИ";})),_request.Token,resumeOnly));
                generated=true;OperationTiming.Record(_timingKey,_elapsed.Elapsed);Report("Ответ ИИ сохранён. Проверьте буквы перед добавлением.");
            }
            catch(Exception error){Failure(error);}
            finally
            {
                _clock.Stop();_elapsed.Stop();_timing.Text=OperationTiming.Display(_stage,_elapsed.Elapsed,_timingKey);_request.Dispose();_request=null;_body.IsEnabled=_tools.IsEnabled=!_sequenceBusy;_cancel.Content=_sequenceBusy?"Остановить серию":"Закрыть";RefreshAssets();
            }
            if(review && LetterTransferAi.HasResult(_directory,job))await Review(job);
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
        private Task Review(LetterTransferJob job,bool forceRecognition=false,bool onlyMissing=false)=>ReviewPreparedJob(job,forceRecognition,onlyMissing);
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
        private async void RebuildSources()
        {
            if(_request!=null || _sequenceBusy)return;
            try
            {
                Save();if(_set.Glyphs.Count==0)throw new InvalidOperationException("В наборе пока нет готовых букв.");
                _refresh.Stop();_request=new CancellationTokenSource();_body.IsEnabled=_tools.IsEnabled=false;_cancel.Content="Отменить пересборку";Report("Восстанавливаю рисунки из сохранённых ответов ИИ · без новой генерации…");
                int count=await Task.Run(()=>LetterTransferSourceRecovery.Rebuild(_directory,_set,_request.Token));
                _cache.Clear();RefreshAssets();Preview();Report("Пересобрано из ответов ИИ: "+count+" из "+_set.Glyphs.Count+" рисунков. Остальные сохранены без изменений. Старые PNG остаются в папке набора; платных запросов не было.");
            }
            catch(OperationCanceledException){Report("Пересборка отменена. Прежние рисунки сохранены.");}
            catch(Exception error){Failure(error);}
            finally{if(_request!=null){_request.Dispose();_request=null;}_body.IsEnabled=_tools.IsEnabled=true;_cancel.Content="Закрыть";}
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
