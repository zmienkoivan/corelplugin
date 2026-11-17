using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed partial class LetterTransferWindow
    {
        private LetterTransferBatchPlan _batchPlan;
        private int _analysisBatchCount;
        private CancellationTokenSource _sequenceCancellation;
        private void Busy(string stage,string timing=null)
        {
            _refresh.Stop();_request=CancellationTokenSource.CreateLinkedTokenSource(_sequenceCancellation?.Token??CancellationToken.None);_body.IsEnabled=_tools.IsEnabled=false;
            _cancel.Content="Остановить";_stage=stage;_timingKey=timing;_elapsed.Restart();_clock.Start();
        }
        private void Idle()
        {
            _clock.Stop();_elapsed.Stop();_timing.Text=OperationTiming.Display(_stage,_elapsed.Elapsed,_timingKey);_request?.Dispose();_request=null;
            _body.IsEnabled=_tools.IsEnabled=!_sequenceBusy;_cancel.Content=_sequenceBusy?"Остановить серию":"Закрыть";
        }
        private async Task GenerateSequence(bool selected)
        {
            if(_request!=null || _sequenceBusy)return;var prepared=new List<LetterTransferPrepared>();bool canceled=false;string stopReason=null;
            try
            {
                Save();string token=_token();if(String.IsNullOrWhiteSpace(token))throw new InvalidOperationException("Укажите ключ Replicate в настройках ИИ.");
                var targets=selected?_table.SelectedItems.Cast<LetterTransferRow>().Select(r=>r.Letter).Distinct().ToArray():LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();
                if(targets.Length==0)throw new InvalidOperationException("Выберите букву.");
                var passes=LetterTransferRules.MissingPasses(_set,targets);if(selected && passes.Count==0)passes.Add(targets);
                if(passes.Count==0)throw new InvalidOperationException("Все выбранные варианты уже готовы. Переходите к печати.");
                if(String.IsNullOrWhiteSpace(_set.Reference))throw new InvalidOperationException("Добавьте референс стиля.");
                var reference=LetterTransferImages.RequestReference(LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,_set.Reference)));string model=_set.GenerationModel;
                if(!selected)
                {
                    string referenceHash=LetterTransferImages.Fingerprint(reference);
                    var waiting=_set.Jobs.Where(j=>!j.Reviewed && j.Model==model && LetterTransferAi.HasResult(_directory,j) && j.Prompt==LetterTransferAi.Prompt(_set,j.Letters,j.Key,j.Model) && LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,j.Reference)))==referenceHash).ToArray();
                    if(waiting.Length>0){await ReviewSaved(waiting);return;}
                    var active=_set.Jobs.LastOrDefault(j=>j.Model==model && LetterTransferAi.InFlight(_directory,j) && j.Prompt==LetterTransferAi.Prompt(_set,j.Letters,j.Key,j.Model) && LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,j.Reference)))==referenceHash);
                    if(active!=null){if(await Run(active,token,false,true))await ReviewSaved(new[]{active});return;}

                }
                if(_set.Jobs.Count+passes.Count>200 || targets.Any(l=>_set.Glyphs.Count(g=>g.Letter==l)+passes.Count(p=>p.Contains(l))>8))throw new InvalidOperationException("Слишком много сохранённых запросов или вариантов. Удалите ненужные варианты или создайте новый набор.");
                _batchPlan=new LetterTransferBatchPlan{Model=model,Passes=passes.Select(p=>p.ToArray()).ToArray(),Resolutions=passes.Select(p=>p.Length>4?"4K":(string)_resolution.SelectedItem).ToArray(),Prompts=passes.Select(p=>LetterTransferAi.Prompt(_set,p,LetterTransferImages.Key(reference),model)).ToArray()};
                foreach(string prompt in _batchPlan.Prompts)LetterTransferModels.ValidatePrompt(model,prompt);
                if(!_confirm(reference,passes[0],model,_batchPlan.Resolutions[0],String.Join("\n\n",_batchPlan.Prompts),false))return;
                _sequenceBusy=true;_sequenceCancellation=new CancellationTokenSource();_body.IsEnabled=_tools.IsEnabled=false;_cancel.Content="Остановить серию";
                for(int i=0;i<_batchPlan.Generations;i++)
                {
                    _sequenceCancellation.Token.ThrowIfCancellationRequested();Report("Лист "+(i+1)+" из "+_batchPlan.Generations+": создание → фон → обозначения → край.");
                    var job=LetterTransferAi.Create(_directory,_set,_batchPlan.Passes[i],model,_batchPlan.Resolutions[i],reference);
                    if(!await Run(job,token,false)){stopReason=_message.Text;canceled=_sequenceCancellation.IsCancellationRequested;break;}
                    var item=await PrepareReview(job,false,true);prepared.Add(item);
                    if(!item.Sheet.Cells.Any(c=>c.Safe && c.Image!=null)){Report("Не удалось подготовить целые буквы. Остальные запросы серии остановлены.",true);break;}
                }
            }
            catch(OperationCanceledException){canceled=true;Report("Серия остановлена. Полученные листы сохранены.");}
            catch(Exception error){Failure(error);}
            finally
            {
                _sequenceBusy=false;_batchPlan=null;_sequenceCancellation?.Dispose();_sequenceCancellation=null;_body.IsEnabled=_tools.IsEnabled=true;_cancel.Content="Закрыть";
            }
            if(prepared.Count>0)
            {
                try{SelectStep(1);ApprovePrepared(prepared);if(!String.IsNullOrEmpty(stopReason))Report("Серия остановлена. Полученные варианты сохранены. "+stopReason,true);else if(canceled)Report("Серия остановлена. Проверенные варианты сохранены.");else Report("Проверка завершена. Готовых букв: "+_set.Glyphs.Select(g=>g.Letter).Distinct().Count()+" из 33. Недостающие можно получить отдельным запуском.");}
                catch(Exception error){Failure(error);}
            }
            if(_set.Jobs.Any(j=>!j.Reviewed && LetterTransferAi.HasResult(_directory,j)))SelectStep(1);RefreshAssets();
        }
        private async Task<LetterTransferPrepared> PrepareReview(LetterTransferJob job,bool forceRecognition=false,bool recognitionApproved=false,bool skipRecognition=false,bool finish=true)
        {
            LetterTransferPrepared item;
            Busy("Разделяю лист и удаляю фон");
            try
            {
                var cancel=_request.Token;
                item=await LetterTransferWorkflow.Work(()=>{
                    var source=LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,job.Output));var cells=LetterTransferSheet.Read(source,job.Letters,job.Key,_set.AlphaThreshold,cancel);
                    foreach(var cell in cells.Cells)cell.SourceJobId=job.Id;
                    LetterTransferReviewDraft.Restore(LetterTransferStore.Resolve(_directory,job.Output),cells);
                    var result=new LetterTransferPrepared{Job=job,Source=source,Sheet=cells};
                    try
                    {
                        result.Recognition=LetterTransferRecognition.Prepare(_directory,job,cells);
                        if(!forceRecognition && LetterTransferRecognition.TryCached(result.Recognition,out var labels))LetterTransferRecognition.Apply(cells,labels,result.Recognition.CellIndices);
                        else result.NeedsRecognition=forceRecognition || !cells.Cells.Where(c=>c.Image!=null).All(c=>c.Manual && LetterTransferRules.ValidLetter(c.Letter));
                    }
                    catch(Exception error)when(error is InvalidDataException || error is ArgumentException){cells.Notice=(cells.Notice??"")+" "+error.Message;}
                    return result;
                },cancel);
            }
            finally{Idle();}
            if(!skipRecognition && item.NeedsRecognition && item.Recognition!=null)
            {
                var plan=item.Recognition;string token=_token();_recognitionSourceNote="Исходный ответ: "+item.Source.PixelWidth+" × "+item.Source.PixelHeight+" px.";
                if(!String.IsNullOrWhiteSpace(token) && (recognitionApproved || _confirmRecognition(plan.Preview,plan.Count,plan.Prompt,File.Exists(plan.Output+".prediction.json") || File.Exists(plan.Output+".submitted"))))
                {
                    await RecognizePrepared(item,token,forceRecognition);
                }
            }
            if(finish)await FinishPrepared(item);
            return item;
        }
        private async Task RecognizePrepared(LetterTransferPrepared item,string token,bool force=false)
        {
            var plan=item.Recognition;Busy("Распознаю буквы","letters/recognition/"+LetterTransferRecognition.Model);
            try
            {
                var cancel=_request.Token;var labels=await Task.Run(()=>LetterTransferRecognition.Run(plan,token,p=>Dispatcher.BeginInvoke(new Action(()=>_stage=p=="recovering"?"Ищу сохранённый анализ":"Распознаю буквы")),cancel,force));
                LetterTransferRecognition.Apply(item.Sheet,labels,plan.CellIndices);OperationTiming.Record(_timingKey,_elapsed.Elapsed);
            }
            catch(OperationCanceledException){throw;}
            catch(Exception error){item.Sheet.Notice=(item.Sheet.Notice??"")+" Обозначения нужно проверить вручную: "+error.GetBaseException().Message;}
            finally{Idle();}
        }
        private async Task FinishPrepared(LetterTransferPrepared item)
        {
            Busy("Выравниваю край и сохраняю исходники");
            try
            {
                var cancel=_request.Token;var job=item.Job;
                await LetterTransferWorkflow.Work(()=>{
                    LetterTransferReviewDraft.Restore(LetterTransferStore.Resolve(_directory,job.Output),item.Sheet);
                    if(job.Resolution=="4K" && Math.Max(item.Source.PixelWidth,item.Source.PixelHeight)<3000 || job.Resolution=="2K" && Math.Max(item.Source.PixelWidth,item.Source.PixelHeight)<1500)item.Sheet.Notice=(item.Sheet.Notice??"")+" Размер ответа ниже запрошенного: "+item.Source.PixelWidth+" × "+item.Source.PixelHeight+" px.";
                    LetterTransferEdges.Preview(item.Sheet,_set,cancel);return true;
                },cancel);
            }
            finally{Idle();}
        }
        private void ApprovePrepared(IReadOnlyList<LetterTransferPrepared> prepared,bool onlyMissing=false)
        {
            var combined=LetterTransferWorkflow.Combine(prepared);var approved=_review(LetterTransferImages.Load(LetterTransferStore.Resolve(_directory,prepared[0].Job.Reference)),LetterTransferWorkflow.ContactSheet(prepared.Select(p=>p.Source)),combined);
            foreach(var item in prepared)LetterTransferReviewDraft.Save(LetterTransferStore.Resolve(_directory,item.Job.Output),item.Sheet);
            foreach(var glyph in approved)if(String.IsNullOrEmpty(glyph.SourceJobId))glyph.SourceJobId=combined.Cells.FirstOrDefault(c=>ReferenceEquals(c.Image,glyph.Image) || c.Original!=null && ReferenceEquals(c.Original,glyph.Original))?.SourceJobId;
            if(onlyMissing){var existing=new HashSet<string>(_set.Glyphs.Select(g=>g.Letter));approved=approved.Where(g=>!existing.Contains(g.Letter)).ToList();}
            var pending=new List<LetterTransferApproved>();
            foreach(var item in prepared)
            {
                string source="ИИ · "+item.Job.Id.Substring(0,8);var imported=new Dictionary<string,int>();
                foreach(var glyph in _set.Glyphs.Where(g=>g.Source==source))
                {
                    string file=LetterTransferStore.Resolve(_directory,String.IsNullOrEmpty(glyph.OriginalFile)?glyph.File:glyph.OriginalFile);if(!File.Exists(file))continue;
                    string key=glyph.Letter+LetterTransferImages.Fingerprint(LetterTransferImages.Load(file));imported[key]=imported.TryGetValue(key,out int count)?count+1:1;
                }
                foreach(var glyph in approved.Where(g=>g.SourceJobId==item.Job.Id))
                {
                    string key=glyph.Letter+LetterTransferImages.Fingerprint(LetterTransferImages.Prepare(glyph.Original??glyph.Image,_set.AlphaThreshold,CancellationToken.None));
                    if(imported.TryGetValue(key,out int count) && count>0){imported[key]=count-1;continue;}pending.Add(glyph);
                }
            }
            if(pending.Count==0){Report("Изменения обозначений сохранены. Новых отмеченных букв нет.");return;}
            if(pending.Any(g=>prepared.All(p=>p.Job.Id!=g.SourceJobId)) || pending.GroupBy(g=>g.Letter).Any(g=>_set.Glyphs.Count(v=>v.Letter==g.Key)+g.Count()>8))throw new InvalidOperationException("Не удалось сопоставить варианты или превышено 8 вариантов на букву.");
            var previous=_set.Glyphs.ToList();var flags=prepared.ToDictionary(p=>p.Job.Id,p=>p.Job.Reviewed);
            try
            {
                foreach(var glyph in pending)
                {
                    var saved=LetterTransferImages.Add(_directory,_set,glyph.Letter,glyph.Original??glyph.Image,"ИИ · "+glyph.SourceJobId.Substring(0,8));
                    if(glyph.Bounds.Width>0 && glyph.Bounds.Height>0){saved.SourceJobId=glyph.SourceJobId;saved.SourceCell=new[]{glyph.Bounds.X,glyph.Bounds.Y,glyph.Bounds.Width,glyph.Bounds.Height};}
                }
                foreach(var item in prepared.Where(p=>pending.Any(g=>g.SourceJobId==p.Job.Id)))item.Job.Reviewed=true;
                Save();
            }
            catch
            {
                var added=_set.Glyphs.Where(g=>!previous.Any(p=>p.Id==g.Id)).ToArray();_set.Glyphs=previous;
                foreach(var item in prepared)item.Job.Reviewed=flags[item.Job.Id];
                foreach(var glyph in added)foreach(string file in new[]{glyph.File,glyph.OriginalFile})if(!String.IsNullOrEmpty(file))try{File.Delete(LetterTransferStore.Resolve(_directory,file));}catch(Exception error){Log.Error("Could not remove an uncommitted letter.",error);}throw;
            }
            _cache.Clear();RefreshAssets();Preview();SelectStep(1);Report("Добавлено вариантов: "+pending.Count+". Переходите к полотну.");
        }
        private async Task ReviewPreparedJob(LetterTransferJob job,bool forceRecognition,bool onlyMissing)
        {var prepared=await PrepareReview(job,forceRecognition);ApprovePrepared(new[]{prepared},onlyMissing);}
        private void SelectStep(int index)
        {
            if(index<0 || index>2)return;
            if(_steps.SelectedIndex!=index)_steps.SelectedIndex=index;
            if(_views.Items.Count>=4)_views.SelectedIndex=index==0?3:index==1?1:0;
            if(_countsPanel!=null)_countsPanel.Visibility=index==2?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
            if(_body.ColumnDefinitions.Count>1)_body.ColumnDefinitions[1].Width=new System.Windows.GridLength(index==2?245:0);
            if(_sizesPanel!=null)_sizesPanel.Visibility=index==2?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
            if(_insert!=null)_insert.Visibility=index==2?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
        }
        private void RefreshWorkflow()
        {
            if(_set==null)return;int ready=_set.Glyphs.Select(g=>g.Letter).Distinct().Count(),missing=LetterTransferRules.MissingVariants(_set);
            _workflowNote.Text="Готово букв: "+ready+" из 33 · вариантов: "+_set.Glyphs.Count+".\n"+(missing==0?"Выбранное число оформлений готово.":"До цели не хватает вариантов: "+missing+".");
            if(_insert!=null)_insert.IsEnabled=_set.Glyphs.Count>0;
            if(!_loading && _edgeQuality.SelectedIndex!=LetterTransferWorkflow.EdgeMode(_set))
            {bool was=_loading;_loading=true;try{_edgeQuality.SelectedIndex=LetterTransferWorkflow.EdgeMode(_set);}finally{_loading=was;}}
        }
        private void ChooseEdgePreset()
        {
            if(_edgeQuality.SelectedIndex==2){_edgeExpert.IsExpanded=true;return;}
            if(_edgeQuality.SelectedIndex<0)return;var profile=new LetterTransferSet();LetterTransferWorkflow.EdgePreset(profile,_edgeQuality.SelectedIndex);
            _loading=true;try{_alphaThreshold.Text=profile.AlphaThreshold.ToString();_edgeTrim.Value=profile.EdgeTrimPx;_smoothEdges.IsChecked=profile.SmoothEdges;_sharpContour.IsChecked=profile.SharpContour;_contourSmooth.Value=profile.ContourSmoothPx;_edgeExpert.IsExpanded=false;}finally{_loading=false;}Queue();
        }
        private async void Reprocess()
        {
            if(_request!=null || _sequenceBusy)return;
            try
            {
                Save();if(_set.Glyphs.Count==0)throw new InvalidOperationException("Сначала добавьте буквы.");Busy("Обновляю край из исходных листов");var cancel=_request.Token;
                int count=await LetterTransferWorkflow.Work(()=>LetterTransferSourceRecovery.Rebuild(_directory,_set,cancel,null,true),cancel);
                _cache.Clear();RefreshAssets();Preview();Report("Обновлён край вариантов: "+count+". Исходники и прежние PNG сохранены. Платных запросов нет.");
            }
            catch(OperationCanceledException){Report("Обработка остановлена. Прежние буквы сохранены.");}
            catch(Exception error){Failure(error);}
            finally{Idle();}
        }
        private async void ReviewPending()=>await ReviewSaved();
        private async Task ReviewSaved(LetterTransferJob[] selection=null)
        {
            if(_request!=null || _sequenceBusy)return;var prepared=new List<LetterTransferPrepared>();bool canceled=false;
            try
            {
                Save();var jobs=selection??_set.Jobs.Where(j=>!j.Reviewed && LetterTransferAi.HasResult(_directory,j)).ToArray();
                if(jobs.Length==0){SelectStep(1);Report("Новых листов нет. Сохранённые варианты показаны справа.");return;}
                _sequenceBusy=true;_sequenceCancellation=new CancellationTokenSource();_body.IsEnabled=_tools.IsEnabled=false;
                foreach(var job in jobs){_sequenceCancellation.Token.ThrowIfCancellationRequested();prepared.Add(await PrepareReview(job,false,false,true,false));}
                var pending=prepared.Where(p=>p.NeedsRecognition && p.Recognition!=null).ToArray();string token=_token();
                if(pending.Length>0 && !String.IsNullOrWhiteSpace(token))
                {
                    _analysisBatchCount=pending.Length;
                    bool approved;
                    try{approved=_confirmRecognition(LetterTransferWorkflow.ContactSheet(pending.Select(p=>p.Recognition.Preview)),pending.Sum(p=>p.Recognition.Count),String.Join("\n\n",pending.Select(p=>p.Recognition.Prompt)),false);}
                    finally{_analysisBatchCount=0;}
                    if(approved)foreach(var item in pending){_sequenceCancellation.Token.ThrowIfCancellationRequested();await RecognizePrepared(item,token);}
                }
                foreach(var item in prepared){_sequenceCancellation.Token.ThrowIfCancellationRequested();await FinishPrepared(item);}
            }
            catch(OperationCanceledException){canceled=true;Report("Проверка остановлена. Листы и полученные обозначения сохранены.");}
            catch(Exception error){canceled=true;Failure(error);}
            finally{_sequenceBusy=false;_sequenceCancellation?.Dispose();_sequenceCancellation=null;_body.IsEnabled=_tools.IsEnabled=true;_cancel.Content="Закрыть";}
            if(!canceled && prepared.Count>0)try{ApprovePrepared(prepared);SelectStep(1);}catch(Exception error){Failure(error);}
        }
    }
}
