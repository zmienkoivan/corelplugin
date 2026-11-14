using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed partial class DtfCorrectionWindow
    {
        private PrintRestorationHost _restorationHost;
        private Border _restorationPane;
        private ToggleButton _restoreToggle;
        private Button _restoreAcceptKnockout;
        private bool _restoreKnockoutPending;
        private string _restoreBackgroundMethod;
        private StackPanel _restorationFields;
        private readonly Border[] _restoreCards=new Border[5];
        private readonly TextBlock[] _restoreNotes=new TextBlock[5];
        private readonly Button[] _restoreActions=new Button[5];
        private ComboBox _restoreSize,_restoreBackground;
        private TextBox _restoreText,_restoreNotesInput;
        private Button _restoreReview,_restoreLast;
        private TextBlock _restoreStatus,_restoreTimingStatus;
        private ProgressBar _restoreProgress;
        private Button _restoreReset;
        private bool _restorationBusy,_restored,_restoreCropped,_restoreBackgroundDone;
        private bool _alphaChecked,_whiteChecked;
        private long _maskRevision;
        private string _captureSourcePath,_restoreReady,_restoreReference,_auditKey;
        private Color? _restoreKey;
        private double _restorationOffsetX,_restorationOffsetY,_restoreReadyOriginX,_restoreReadyOriginY;
        private PrintRestorationAudit _restoreAudit;
        private CancellationTokenSource _restorationCancellation;
        private readonly Stopwatch _restorationElapsed=new Stopwatch();
        private readonly DispatcherTimer _restorationClock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        private string _restorationStage,_restorationTiming;

        private void CreateRestorationPanel(Grid root,Panel toolbar,PrintRestorationHost host,bool show)
        {
            _restorationHost=host;_captureSourcePath=_sourcePath;
            root.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var toggle=_restoreToggle=new ToggleButton{Content="Мастер восстановления",IsChecked=show,MinHeight=27,FontSize=11,Margin=new Thickness(0,5,0,0),HorizontalAlignment=HorizontalAlignment.Right};
            toolbar.Children.Add(toggle);
            _restorationFields=new StackPanel();
            var paneContent=new DockPanel();var statusBar=new StackPanel{Margin=new Thickness(0,7,0,0)};
            DockPanel.SetDock(statusBar,Dock.Bottom);paneContent.Children.Add(statusBar);
            paneContent.Children.Add(new ScrollViewer{Content=_restorationFields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            _restorationPane=new Border{Width=238,Padding=new Thickness(10),Margin=new Thickness(10,0,0,0),CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1),
                Child=paneContent,Visibility=show?Visibility.Visible:Visibility.Collapsed};
            DockerTheme.UseTheme(_restorationPane,Border.BackgroundProperty,DockerTheme.SurfaceBackground);
            DockerTheme.UseTheme(_restorationPane,Border.BorderBrushProperty,DockerTheme.BorderBrush);
            Grid.SetColumn(_restorationPane,2);root.Children.Add(_restorationPane);
            toggle.Checked+=(_,__)=>{_restorationPane.Visibility=Visibility.Visible;Width=Math.Max(Width,Math.Min(1500,SystemParameters.WorkArea.Width-40));};
            toggle.Unchecked+=(_,__)=>_restorationPane.Visibility=Visibility.Collapsed;
            if(show)Width=Math.Min(1500,SystemParameters.WorkArea.Width-40);
            var hide=Button("Скрыть",(_,__)=>_restoreToggle.IsChecked=false);hide.FontSize=10;hide.MinHeight=24;hide.HorizontalAlignment=HorizontalAlignment.Right;
            _restorationFields.Children.Add(hide);
            _restorationFields.Children.Add(WithHelp(Text("Восстановление принта",13,true),
                "Каждый шаг работает с изображением внутри окна. В Corel вставляется только финальный принт. ИИ может ошибаться: сверяйте буквы, потёртости, символы и мелкие детали. Каждый запрос требует отдельного подтверждения. Локальные шаги работают без интернета. Обработка ИИ возвращает RGB; локальные операции сохраняют исходные каналы, когда они доступны."));
            var ai=RestorationStep(0,"1 · Восстановить рисунок","Низкое разрешение → Nano Banana 2","Восстановить · ИИ",RestoreAiAsync);
            _restoreSize=Combo(new[]{"4K · крупнее","2K · быстрее"});ai.Children.Add(_restoreSize);
            _restoreBackground=Combo(new[]{"Контрастный фон · авто","Ярко-розовый","Зелёный","Голубой","Оставить фон"});ai.Children.Add(_restoreBackground);
            _restoreText=Field("");_restoreText.AcceptsReturn=true;_restoreText.Height=48;_restoreText.TextWrapping=TextWrapping.Wrap;_restoreText.MaxLength=3000;
            _restoreNotesInput=Field("");_restoreNotesInput.AcceptsReturn=true;_restoreNotesInput.Height=48;_restoreNotesInput.TextWrapping=TextWrapping.Wrap;_restoreNotesInput.MaxLength=3000;
            var details=new StackPanel();details.Children.Add(Labeled("Точный текст · необязательно",_restoreText));details.Children.Add(Labeled("Уточнение",_restoreNotesInput));
            ai.Children.Add(new Expander{Header="Текст и уточнения",Content=details});
            _restoreReview=Button("Проверить готовый результат",async(_,__)=>await ReviewRestorationAsync());_restoreReview.Visibility=Visibility.Collapsed;ai.Children.Add(_restoreReview);
            _restoreLast=Button("Последний результат ИИ",async(_,__)=>await LoadLastRestorationAsync());ai.Children.Add(_restoreLast);
            RestorationStep(1,"2 · Обрезать поля","Рисунок целиком, с запасом 2 px","Обрезать поля",TrimRestorationAsync);
            var backgroundStep=RestorationStep(2,"3 · Удалить фон","Пипетка/палочка или нокаут","Фон…",RemoveRestorationBackgroundAsync);
            backgroundStep.Children.Remove(_restoreActions[2]);
            var backgroundModes=new UniformGrid{Columns=2};backgroundModes.Children.Add(_restoreActions[2]);
            backgroundModes.Children.Add(Button("Нокаут…",(_,__)=>SetupRestorationKnockout()));backgroundStep.Children.Add(backgroundModes);
            _restoreAcceptKnockout=Button("Принять нокаут",async(_,__)=>await AcceptRestorationKnockoutAsync());
            _restoreAcceptKnockout.Visibility=Visibility.Collapsed;backgroundStep.Children.Add(_restoreAcceptKnockout);
            var alpha=RestorationStep(3,"4 · Прозрачность","Проверка результата целиком","Проверить",()=>AuditRestorationAsync(false));
            var fixAlpha=Button("Убрать полупрозрачность",(_,__)=>{_remove.IsChecked=true;_remove.BringIntoView();});alpha.Children.Add(fixAlpha);
            var white=RestorationStep(4,"5 · Белое","Проверка почти белых пикселей","Проверить",()=>AuditRestorationAsync(true));
            white.Children.Add(Button("Подбелить",(_,__)=>{_whiten.IsChecked=true;_whiten.BringIntoView();}));
            _restoreProgress=new ProgressBar{IsIndeterminate=true,Height=3,Margin=new Thickness(0,0,0,5),Visibility=Visibility.Collapsed};statusBar.Children.Add(_restoreProgress);
            _restoreTimingStatus=Text("",11);_restoreTimingStatus.Visibility=Visibility.Collapsed;statusBar.Children.Add(_restoreTimingStatus);
            _restoreStatus=Text("Начните с восстановления или используйте локальные шаги.",11);statusBar.Children.Add(_restoreStatus);
            _restoreReset=Button("Вернуться к исходнику",async(_,__)=>await ResetRestorationAsync());statusBar.Children.Add(_restoreReset);
            _restorationClock.Tick+=(_,__)=>UpdateRestorationProgress();
            UpdateRestorationState();
        }
        private void UpdateRestorationProgress()
        {
            if(!String.IsNullOrEmpty(_restorationTiming))
            {
                _restoreTimingStatus.Text=OperationTiming.Display(_restorationStage,_restorationElapsed.Elapsed,_restorationTiming);
                _restoreTimingStatus.Visibility=Visibility.Visible;
            }
            else if(_restorationBusy)_restoreStatus.Text=OperationTiming.Display(_restorationStage,_restorationElapsed.Elapsed,null);
        }
        private StackPanel RestorationStep(int index,string title,string note,string action,Func<Task> run)
        {
            var panel=new StackPanel();panel.Children.Add(Text(title,12,true));
            _restoreNotes[index]=Text(note,10);panel.Children.Add(_restoreNotes[index]);
            _restoreActions[index]=Button(action,async(_,__)=>await run());panel.Children.Add(_restoreActions[index]);
            var card=new Border{Child=panel,Padding=new Thickness(7),BorderThickness=new Thickness(2,0,0,0),Margin=new Thickness(0,4,0,4)};
            _restoreCards[index]=card;_restorationFields.Children.Add(card);return panel;
        }
        private bool RestorationAvailable => _loaded && !_closed && !_applying && !_normalizing && !_presetBusy && !_insertingThinMask && !_qualityChanging && !_restorationBusy;
        private async Task BeginRestorationAsync(string stage,string timing=null)
        {
            _restorationBusy=true;_normalizing=true;_delay.Stop();_revision++;CancelDetail(true);
            _previewCancellation?.Cancel();_qualityCancellation?.Cancel();_qualityRevision++;_qualityChanging=true;_apply.IsEnabled=false;SetControlsEnabled(false);
            _restorationCancellation=new CancellationTokenSource();_restorationStage=stage;_restorationTiming=timing;_restorationElapsed.Restart();_restorationClock.Start();
            _cancel.Content="Отменить";_restoreStatus.Text=timing==null?stage:"Nano Banana 2 · "+(_restoreSize.SelectedIndex==0?"4K":"2K");
            _restorationFields.IsEnabled=false;_restoreReset.IsEnabled=false;_restoreProgress.Visibility=Visibility.Visible;UpdateRestorationProgress();
            foreach(var task in new[]{_previewTask,_detailTask,_qualityTask})if(task!=null)try{await task;}catch{}
            await Dispatcher.Yield(DispatcherPriority.Background);
            _restorationCancellation.Token.ThrowIfCancellationRequested();
        }
        private void EndRestoration()
        {
            _restorationClock.Stop();_restorationElapsed.Stop();if(_restorationTiming!=null)UpdateRestorationProgress();_restorationCancellation?.Dispose();_restorationCancellation=null;
            _restorationBusy=false;_normalizing=false;_qualityChanging=false;
            if(_closed){Cleanup();return;}
            _restoreReset.IsEnabled=true;_restoreProgress.Visibility=Visibility.Collapsed;_restorationFields.IsEnabled=true;_cancel.IsEnabled=true;_cancel.Content="Закрыть";SetControlsEnabled(true);UpdateResolutionInfo();UpdateRestorationState();QueuePreview();
        }
        private string RestorationPath(string name)
        {
            string folder=Path.Combine(Path.GetTempPath(),"Vanya-Restore-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);return Path.Combine(folder,name+".png");
        }
        private async Task RestoreAiAsync()
        {
            if(!RestorationAvailable || _restorationHost?.Generate==null)return;
            bool generated=false;
            try
            {
                _restorationHost.Validate?.Invoke();
                var input=_originalPixels.Bitmap();Color? key=_restoreBackground.SelectedIndex==4?(Color?)null:
                    _restoreBackground.SelectedIndex==1?Colors.Magenta:_restoreBackground.SelectedIndex==2?Colors.Lime:
                    _restoreBackground.SelectedIndex==3?Colors.Cyan:PrintRestorationImages.KeyColor(input);
                var request=new PrintRestorationRequest{SourcePath=RestorationPath("reference"),Resolution=_restoreSize.SelectedIndex==0?"4K":"2K",KeyColor=key,
                    ExactText=_restoreText.Text,Notes=_restoreNotesInput.Text,WidthMm=_originalPixels.Width*25.4/_originalPixels.DpiX,HeightMm=_originalPixels.Height*25.4/_originalPixels.DpiY};
                File.Copy(_workingSourcePath??_sourcePath,request.SourcePath);
                request.OutputPath=Path.Combine(Path.GetDirectoryName(request.SourcePath),"restored.png");
                if(_restorationHost.Confirm?.Invoke(this,request)!=true)return;
                await BeginRestorationAsync("Восстанавливаю рисунок…","restore:google/nano-banana-2:"+request.Resolution);
                var runCancellation=_restorationCancellation;var token=runCancellation.Token;
                var result=await _restorationHost.Generate(request,stage=>Dispatcher.BeginInvoke(new Action(()=> {
                    if(_closed || !_restorationBusy || _restorationCancellation!=runCancellation)return;
                    _restorationStage=stage=="sending"?"Отправляю исходник…":stage=="cancelling"?"Отменяю запрос…":
                        stage=="downloading"?"Загружаю результат…":stage!=null && stage.StartsWith("retrying",StringComparison.Ordinal)?"Восстанавливаю связь…":"Восстанавливаю рисунок…";
                    UpdateRestorationProgress();
                })),token);
                OperationTiming.Record(_restorationTiming,_restorationElapsed.Elapsed);
                if(_closed)return;
                _restoreReady=result;_restoreReference=request.SourcePath;_restoreKey=request.KeyColor;
                _restoreReadyOriginX=_restorationOffsetX;_restoreReadyOriginY=_restorationOffsetY;generated=true;
                _restorationStage="ИИ завершён";_restoreStatus.Text="Результат сохранён. Сверьте рисунок перед продолжением.";
            }
            catch(OperationCanceledException){_restorationStage="ИИ отменён";if(!_closed)_restoreStatus.Text="Запрос отменён. Исходник сохранён; готовый файл можно открыть через «Последний результат ИИ».";}
            catch(Exception error){_restorationStage="Ошибка ИИ";if(!_closed){_restoreStatus.Text=error.GetBaseException().Message;ShowMessage(_restoreStatus.Text,true);}}
            finally{if(_restorationBusy)EndRestoration();}
            if(generated && !_closed && _restoreReady!=null)await ReviewRestorationAsync();
        }
        private async Task ReviewRestorationAsync()
        {
            if(!RestorationAvailable || _restoreReady==null || _restorationHost?.Review==null)return;
            try
            {
                if(!File.Exists(_restoreReady))throw new FileNotFoundException("Не найден сохранённый результат ИИ.",_restoreReady);
                string reference=RestorationReference();
                if(!_restorationHost.Review(this,reference,_restoreReady))return;
                await BeginRestorationAsync("Загружаю принятый рисунок…");
                await AdoptRestorationSourceAsync(_restoreReady,_restorationOffsetX-_restoreReadyOriginX,_restorationOffsetY-_restoreReadyOriginY,_restorationCancellation.Token);
                _restored=true;_restoreCropped=false;_restoreBackgroundDone=false;_restoreKnockoutPending=false;
                _restoreStatus.Text="Рисунок принят. Дальше — обрезка полей и удаление фона.";
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!_closed)ShowMessage(error.GetBaseException().Message,true);}
            finally{if(_restorationBusy)EndRestoration();}
        }
        private string RestorationReference()
        {
            if(!String.IsNullOrEmpty(_restoreReference) && File.Exists(_restoreReference))return _restoreReference;
            // Recover sessions created by 1.2.42: its worker consumed reference.png.
            // Rebuild comparison locally; never issue another paid request.
            string path=RestorationPath("reference-recovered"),source=_workingSourcePath??_sourcePath;
            if(String.Equals(source,_restoreReady,StringComparison.OrdinalIgnoreCase))source=_captureSourcePath;
            if(File.Exists(source))File.Copy(source,path);else PrintRestorationImages.Save(_originalPixels.Bitmap(),path);
            return _restoreReference=path;
        }
        private async Task LoadLastRestorationAsync()
        {
            if(!RestorationAvailable)return;
            bool recovered=false;
            try
            {
                string path=_restorationHost?.LastResult?.Invoke();
                if(String.IsNullOrEmpty(path) || !File.Exists(path))throw new InvalidOperationException("Нет сохранённого результата ИИ.");
                // A previous result can belong to a different capture: user reviews it,
                // then its physical dimensions are matched to this selected artwork.
                string adjusted=RestorationPath("recovered");
                await BeginRestorationAsync("Открываю сохранённый результат…");
                await Task.Run(()=>PrintRestorationImages.SetPhysicalSize(path,adjusted,_originalPixels.Width*25.4/_originalPixels.DpiX,_originalPixels.Height*25.4/_originalPixels.DpiY,_restorationCancellation.Token));
                if(!_closed){_restoreReady=adjusted;_restoreReference=_workingSourcePath??_sourcePath;_restoreKey=null;
                    _restoreReadyOriginX=_restorationOffsetX;_restoreReadyOriginY=_restorationOffsetY;recovered=true;}
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!_closed)ShowMessage(error.GetBaseException().Message,true);}
            finally{if(_restorationBusy)EndRestoration();}
            if(recovered && !_closed && _restoreReady!=null)await ReviewRestorationAsync();
        }
        private async Task TrimRestorationAsync()
        {
            if(!RestorationAvailable)return;
            try
            {
                string source=_workingSourcePath??_sourcePath,output=RestorationPath("trimmed");var image=_originalPixels.Bitmap();
                await BeginRestorationAsync("Обрезаю поля…");var cancel=_restorationCancellation.Token;
                var bounds=await Task.Run(()=>PrintRestorationImages.TrimBounds(image,_restoreKey??PrintRestorationImages.BorderColor(image),10,cancel),cancel);
                if(bounds.X!=0 || bounds.Y!=0 || bounds.Width!=image.PixelWidth || bounds.Height!=image.PixelHeight)
                {
                    await Task.Run(()=>{if(_restorationHost?.Crop!=null)_restorationHost.Crop(source,output,bounds,cancel);else PrintRestorationImages.Crop(source,output,bounds,cancel);},cancel);
                    await AdoptRestorationSourceAsync(output,bounds.X*25.4/image.DpiX,bounds.Y*25.4/image.DpiY,cancel);
                }
                if(!_closed){_restoreCropped=true;_restoreStatus.Text="Поля обрезаны. Масштаб и положение рисунка сохранены.";}
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!_closed)ShowMessage(error.GetBaseException().Message,true);}
            finally{if(_restorationBusy)EndRestoration();}
        }
        private async Task RemoveRestorationBackgroundAsync()
        {
            if(!RestorationAvailable || _restorationHost?.RemoveBackground==null)return;
            try
            {
                string source=_workingSourcePath??_sourcePath;Color key=_restoreKey??PrintRestorationImages.BorderColor(_originalPixels.Bitmap());
                await BeginRestorationAsync("Удаление фона…");
                string path=_restorationHost.RemoveBackground(this,source,key);
                if(path==null)return;
                await AdoptRestorationSourceAsync(path,0,0,_restorationCancellation.Token);
                if(!_closed){_restoreBackgroundDone=true;_restoreBackgroundMethod="Удаление фона";_restoreKnockoutPending=false;_restoreStatus.Text="Фон принят. Проверьте прозрачность и белое.";}
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!_closed)ShowMessage(error.GetBaseException().Message,true);}
            finally{if(_restorationBusy)EndRestoration();}
        }
        private void SetupRestorationKnockout()
        {
            if(!RestorationAvailable)return;
            Color key=_restoreKey??PrintRestorationImages.BorderColor(_originalPixels.Bitmap());
            _knockoutHex.Text=Hex(key);_knockoutSoft.IsChecked=true;_knockoutTolerance.Value=0;_knockout.IsChecked=true;
            _maskEditor.ShowPreview();_knockout.BringIntoView();_restoreKnockoutPending=true;
            _restoreStatus.Text="Нокаут убирает цвет и в закрытых областях. Подберите допуск и маску защиты слева, затем нажмите «Принять нокаут».";
            UpdateRestorationState();QueuePreview();
        }
        private async Task AcceptRestorationKnockoutAsync()
        {
            if(!RestorationAvailable || !_restoreKnockoutPending || !ReadOptions(out DtfCorrectionOptions current))return;
            if(!current.Knockout){ShowMessage("Включите нокаут и выберите его цвет.",true);return;}
            var options=new DtfCorrectionOptions{Knockout=true,KnockoutSoft=current.KnockoutSoft,KnockoutColor=current.KnockoutColor,KnockoutTolerance=current.KnockoutTolerance,KnockoutMask=current.KnockoutMask};
            string source=_workingSourcePath??_sourcePath,output=RestorationPath("knockout");var original=_originalPixels.Bitmap();
            try
            {
                await BeginRestorationAsync("Применяю нокаут фона…");var cancel=_restorationCancellation.Token;
                bool changed=await Task.Run(()=> {
                    var before=PrintRestorationImages.Audit(original,current.NearWhiteTolerance,cancel);
                    _processor(source,output,options,cancel);
                    var after=PrintRestorationImages.Audit(Load(output),current.NearWhiteTolerance,cancel);
                    if(after.Visible==0)throw new InvalidOperationException("Нокаут убрал весь рисунок. Проверьте цвет, допуск и маску защиты.");
                    return before.AlphaSum!=after.AlphaSum;
                },cancel);
                if(!changed){_restoreStatus.Text="Нокаут не изменил прозрачность. Проверьте цвет фона и допуск.";return;}
                await AdoptRestorationSourceAsync(output,0,0,cancel);
                if(_closed)return;
                // The accepted knockout is now part of the input. Do not apply
                // the soft matte removal a second time in the final pipeline.
                _syncing=true;try{_knockout.IsChecked=false;}finally{_syncing=false;}
                _restoreKnockoutPending=false;_restoreBackgroundDone=true;_restoreBackgroundMethod="Нокаут";
                _restoreStatus.Text="Нокаут принят. Дальше — проверка прозрачности и белого.";
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!_closed)ShowMessage(error.GetBaseException().Message,true);}
            finally{if(_restorationBusy)EndRestoration();}
        }
        private async Task AuditRestorationAsync(bool white)
        {
            if(!RestorationAvailable || !ReadOptions(out DtfCorrectionOptions options))return;
            _highlight.IsChecked=!white;_highlightWhite.IsChecked=white;
            try
            {
                string source=_workingSourcePath??_sourcePath,output=RestorationPath("audit");string key=RestorationAuditKey(options);
                await BeginRestorationAsync("Проверяю полный результат…");var cancel=_restorationCancellation.Token;
                var audit=await Task.Run(()=> {
                    try{_processor(source,output,options,cancel);return PrintRestorationImages.Audit(Load(output),options.NearWhiteTolerance,cancel);}
                    finally{DeleteFile(output);}
                },cancel);
                if(_closed)return;
                if(audit.Visible==0)throw new InvalidOperationException("Результат полностью прозрачный. Проверьте удаление фона и порог.");
                _restoreAudit=audit;_auditKey=key;_alphaChecked=true;_whiteChecked=true;
                _restoreStatus.Text="Проверен результат в полном разрешении. Подсветка видна в предпросмотре.";
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!_closed)ShowMessage(error.GetBaseException().Message,true);}
            finally{if(_restorationBusy)EndRestoration();}
        }
        private async Task ResetRestorationAsync()
        {
            if(!RestorationAvailable)return;
            try
            {
                await BeginRestorationAsync("Возвращаю исходник…");
                await AdoptRestorationSourceAsync(_captureSourcePath,_restorationOffsetX,_restorationOffsetY,_restorationCancellation.Token);
                _restored=_restoreCropped=_restoreBackgroundDone=false;_restoreKey=null;_restoreKnockoutPending=false;
                _syncing=true;try{foreach(var toggle in new[]{_remove,_whiten,_stairs,_outline,_knockout,_halftone})toggle.IsChecked=false;}finally{_syncing=false;}_restoreStatus.Text="Исходник восстановлен. Готовый результат ИИ сохранён отдельно.";
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!_closed)ShowMessage(error.GetBaseException().Message,true);}
            finally{if(_restorationBusy)EndRestoration();}
        }
        private async Task AdoptRestorationSourceAsync(string path,double leftMm,double topMm,CancellationToken cancel)
        {
            int quality=SelectedPreviewQuality();var old=_originalPixels;Point? anchor=PreviewCenter();ReadOptions(out DtfCorrectionOptions previousSettings);
            var loaded=await Task.Run(()=>{var image=Load(path);cancel.ThrowIfCancellationRequested();return Tuple.Create(DtfCorrectionPixels.Capture(image),BuildPreviewOverview(image,quality,_workspace,cancel));},cancel);
            cancel.ThrowIfCancellationRequested();if(_closed){DeleteFile(loaded.Item2.Path);return;}
            foreach(var cached in _overviewCache.Values)DeleteFile(cached.Path);_overviewCache.Clear();
            _originalPixels=loaded.Item1;UsePreviewOverview(loaded.Item2);
            if(!_zoomFit)
            {
                _zoomScale=Math.Max(.001,Math.Min(32,_zoomScale*old.DpiX/_originalPixels.DpiX));
                if(anchor.HasValue)_resampledPreviewCenter=new Point((anchor.Value.X*25.4/old.DpiX-leftMm)*_originalPixels.DpiX/25.4,(anchor.Value.Y*25.4/old.DpiY-topMm)*_originalPixels.DpiY/25.4);
            }
            _sourcePath=_workingSourcePath=path;_inputWidth=_originalPixels.Width;_inputHeight=_originalPixels.Height;_inputDpiX=_originalPixels.DpiX;_inputDpiY=_originalPixels.DpiY;
            _resolutionNormalized=_normalizedSourceReady=false;_normalizedSourcePath=null;
            if(previousSettings!=null)
            {
                _syncing=true;
                try{_units.SelectedIndex=0;_previousUnit=0;_radius.Text=DtfOutlineUnits.Format(previousSettings.RadiusMm);_expansion.Text=DtfOutlineUnits.Format(previousSettings.ExpansionMm);}
                finally{_syncing=false;}
            }
            _restorationOffsetX-=leftMm;_restorationOffsetY-=topMm;_maskEditor.MoveOrigin(leftMm,topMm);_maskRevision++;
            _previewScene=Rect.Empty;_baseRevision=-1;_before.Source=_overview;_after.Source=null;
            _restoreAudit=null;_alphaChecked=_whiteChecked=false;_auditKey=null;
            UpdateResolutionInfo();
        }
        private string RestorationAuditKey(DtfCorrectionOptions options)=>new JavaScriptSerializer().Serialize(DtfCorrectionPreset.FromOptions(options))+"/"+_maskRevision+"/"+_workingSourcePath+"/"+_originalPixels.Width+"/"+_originalPixels.Height;
        private void ValidateRestorationAudit(DtfCorrectionOptions options)
        {
            if(_auditKey==null || _restoreAudit==null || _auditKey==RestorationAuditKey(options))return;
            _alphaChecked=_whiteChecked=false;_restoreAudit=null;_auditKey=null;UpdateRestorationState();
        }
        private void RestorationMaskChanged(){_maskRevision++;_alphaChecked=_whiteChecked=false;_restoreAudit=null;_auditKey=null;UpdateRestorationState();QueuePreview();}
        private void UpdateRestorationState()
        {
            if(_restorationPane==null)return;
            bool[] done={_restored,_restoreCropped,_restoreBackgroundDone,_alphaChecked&&_restoreAudit?.SemiTransparent==0,_whiteChecked&&_restoreAudit?.NearWhite==0};
            int next=Array.FindIndex(done,s=>!s);
            for(int i=0;i<5;i++)_restoreCards[i].BorderBrush=done[i]?Brushes.SeaGreen:i==next?Brushes.DarkOrange:Brushes.LightSlateGray;
            _restoreActions[0].IsEnabled=_restorationHost?.Generate!=null;_restoreActions[2].IsEnabled=_restorationHost?.RemoveBackground!=null;
            _restoreReview.Visibility=_restoreReady==null?Visibility.Collapsed:Visibility.Visible;
            bool hasLast=false;
            try{string last=_restorationHost?.LastResult?.Invoke();hasLast=last!=null && File.Exists(last);}catch{}
            _restoreLast.Visibility=_restoreReady==null && hasLast?Visibility.Visible:Visibility.Collapsed;
            _restoreNotes[0].Text=_restored?"✓ Рисунок сверен и принят":_restoreReady!=null?"Результат готов — нужна проверка":"Низкое разрешение → Nano Banana 2";
            _restoreNotes[1].Text=_restoreCropped?"✓ Поля обработаны":"Рисунок целиком, с запасом 2 px";
            _restoreNotes[2].Text=_restoreBackgroundDone?"✓ "+(_restoreBackgroundMethod??"Удаление фона")+" принято":_restoreKnockoutPending?"Настройте нокаут слева, затем примите":"Пипетка/палочка или нокаут";
            _restoreAcceptKnockout.Visibility=_restoreKnockoutPending?Visibility.Visible:Visibility.Collapsed;
            _restoreNotes[3].Text=_alphaChecked?(_restoreAudit.SemiTransparent==0?"✓ Полупрозрачных пикселей нет":"Полупрозрачных: "+_restoreAudit.SemiTransparent.ToString("N0")):"Проверка результата целиком";
            _restoreNotes[4].Text=_whiteChecked?(_restoreAudit.NearWhite==0?"✓ Почти белых пикселей нет":"Почти белых: "+_restoreAudit.NearWhite.ToString("N0")):"Проверка почти белых пикселей";
        }
        private void CancelCorrectionOrClose()
        {if(_restorationBusy){_restorationCancellation?.Cancel();_cancel.IsEnabled=false;}else if(_applying)_applyCancellation?.Cancel();else Close();}
    }
}
