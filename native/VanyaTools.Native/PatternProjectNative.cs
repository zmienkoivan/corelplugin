using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class PatternProjectNative
    {
        private static string Tag(int i)=>"VanyaPatternSource-"+i;
        private const string FrameTag="VanyaPatternFrame";
        internal static string Capture(PatternEditorSession session,PatternAsset[] sources,string directory)
        {
            dynamic app=session.Application,doc=session.Document;
            if(!Object.Equals((object)app.ActiveDocument,session.Document))throw new InvalidOperationException("Вернитесь в документ паттерна перед сохранением проекта.");
            object oldPage=(object)doc.ActivePage,oldLayer=(object)doc.ActiveLayer,layer=null,output=null;
            dynamic selection=app.CreateShapeRange();foreach(dynamic shape in app.ActiveSelectionRange)selection.Add(shape);
            int unit=Convert.ToInt32(doc.Unit);bool dirty=Convert.ToBoolean(doc.Dirty),optimization=Convert.ToBoolean(app.Optimization),started=false;
            string path=Path.Combine(directory,"sources.cdr");Exception cleanup=null;
            try
            {
                doc.Unit=CorelConstants.CdrMillimeter;doc.BeginCommandGroup("Vanya Tools · сохранение исходников паттерна");started=true;app.Optimization=true;
                layer=(object)((dynamic)oldPage).CreateLayer("Vanya · временная копия проекта");dynamic copied=app.CreateShapeRange();
                for(int i=0;i<sources.Length;i++)
                {
                    var asset=sources[i];object shape;
                    if(asset.Shape!=null)shape=(object)((dynamic)asset.Shape).CopyToLayer((dynamic)layer);
                    else
                    {
                        if(asset.Preview==null)throw new InvalidOperationException("Не найден исходный рисунок «"+asset.Label+"».");
                        string png=Path.Combine(directory,"source-"+i+".png");
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(asset.Preview));using(var file=File.Create(png))encoder.Save(file);
                        var before=new HashSet<int>(Items((object)((dynamic)layer).Shapes).Select(Id));
                        dynamic filter=((dynamic)layer).ImportEx(png,0,app.CreateStructImportOptions());filter.Finish();
                        var added=Items((object)((dynamic)layer).Shapes).Where(s=>!before.Contains(Id(s))).ToArray();
                        if(added.Length!=1)throw new InvalidOperationException("Corel не добавил один исходный рисунок проекта.");
                        shape=added[0];((dynamic)shape).SetSize(asset.Size.X,asset.Size.Y);
                    }
                    ((dynamic)shape).Name=Tag(i);copied.Add((dynamic)shape);
                }
                if(!session.BottleMode)
                {
                    dynamic frame=((dynamic)layer).CreateCurve((dynamic)PatternFillService.GetOutline(session.Target));
                    frame.Name=FrameTag;copied.Add(frame);
                }
                if(Convert.ToInt32(copied.Count)==0)throw new InvalidOperationException("В проекте нет исходников для сохранения.");
                CorelClipboardTransfer.Copy((object)copied,"исходники проекта паттерна");
                output=(object)app.CreateDocument();WorkTimeRuntime.IgnoreTemporaryDocument(output);dynamic native=output;native.Unit=CorelConstants.CdrMillimeter;
                native.AssignColorContext(doc.ColorContext);
                dynamic target=native.ActiveLayer;if(Convert.ToInt32(target.Shapes.Count)>0)target.Shapes.All().Delete();
                CorelClipboardTransfer.WaitAvailable("исходники проекта паттерна");
                dynamic imported=target.PasteEx(app.CreateStructPasteOptions());
                var names=Items((object)imported).Select(s=>(string)Convert.ToString(((dynamic)s).Name)).ToArray();
                var expected=Enumerable.Range(0,sources.Length).Select(Tag).Concat(session.BottleMode?Array.Empty<string>():new[]{FrameTag}).ToArray();
                if(names.Length!=expected.Length || names.Distinct().Count()!=names.Length || !new HashSet<string>(names).SetEquals(expected))
                    throw new InvalidOperationException("Corel не сохранил все исходные рисунки проекта. Файл не заменён.");
                object saveOptions=app.CreateStructSaveAsOptions();
                saveOptions.GetType().InvokeMember("Version",BindingFlags.SetProperty,null,saveOptions,new object[]{0});
                output.GetType().InvokeMember("SaveAsCopy",BindingFlags.InvokeMethod,null,output,new object[]{path,saveOptions});
                return path;
            }
            finally
            {
                if(output!=null)try{dynamic owned=output;owned.Dirty=false;owned.Close();}catch(Exception error){cleanup=error;Log.Error("Could not close owned pattern source document.",error);}
                try{doc.Activate();((dynamic)oldPage).Activate();if(layer!=null)((dynamic)layer).Delete();}catch(Exception error){cleanup=error;Log.Error("Could not remove pattern project source copies.",error);}
                try{((dynamic)oldLayer).Activate();if(Convert.ToInt32(selection.Count)>0)selection.CreateSelection();else doc.ClearSelection();}
                catch(Exception error){cleanup=error;Log.Error("Could not restore pattern project selection.",error);}
                try{doc.Unit=unit;if(started)doc.EndCommandGroup();doc.Dirty=dirty || cleanup!=null;}
                finally{app.Optimization=optimization;}
                if(cleanup!=null)throw new InvalidOperationException("Не удалось полностью восстановить документ после сохранения. Проверьте исходный CDR.",cleanup);
            }
        }

        internal static PatternProjectOpened Open(object application,PatternProjectPackage package,string filePath)
        {
            PatternProjectData data=package.Data;PatternProjectArchive.Validate(data);
            var settings=new PatternEditorSettings();PatternProjectData.ReadValues(settings,data.Settings);PatternProjectData.ReadValues(settings.Layout,data.Layout);
            dynamic app=application,doc=null;bool success=false;
            try
            {
                doc=app.OpenDocument(package.NativePath);doc.Unit=CorelConstants.CdrMillimeter;
                var shapes=Items((object)doc.ActivePage.Shapes).ToDictionary(s=>(string)Convert.ToString(((dynamic)s).Name),s=>s);
                var assets=new PatternAsset[data.Sources.Length];var ids=new Dictionary<int,int>();
                for(int i=0;i<assets.Length;i++)
                {
                    if(!shapes.TryGetValue(Tag(i),out var shape))throw new InvalidOperationException("В проекте отсутствует исходный рисунок "+(i+1)+".");
                    var source=data.Sources[i];var size=new PatternPoint(source.Width,source.Height);int id=Id(shape);ids.Add(source.Id,id);
                    assets[i]=new PatternAsset{Shape=shape,Id=id,Label=source.Label,IsFiller=source.Filler,Size=size,Preview=package.Previews[i],
                        Footprint=new PatternFootprint(size,source.Hull.Select(p=>new PatternPoint(p[0],p[1]))),Silhouette=source.Silhouette?.Read(),OutlineFallback=source.OutlineFallback};
                }
                object sourcesLayer=(object)doc.ActiveLayer;dynamic page=doc.ActivePage;
                page.SetSize(Math.Max(50,data.Width+20),Math.Max(50,data.Height+20));page.Name="Паттерн · "+Path.GetFileNameWithoutExtension(filePath);
                dynamic layer=page.CreateLayer("Паттерн · варианты");layer.Activate();
                var session=new PatternEditorSession{Application=application,Document=(object)doc,Motifs=data.Motifs.Select(i=>assets[i]).ToArray(),
                    Hero=data.Hero<0?null:assets[data.Hero],MirroredIds=data.Mirrors.Where(ids.ContainsKey).Select(id=>ids[id]).ToArray()};
                PatternFrame frame;
                if(data.Bottle)
                {
                    session.BottlePageIndex=Convert.ToInt32(page.Index);session.BottleSelection=(object)app.CreateShapeRange();
                    session.BottleCenter=new PatternPoint(Convert.ToDouble(page.LeftX)+Convert.ToDouble(page.SizeWidth)/2,Convert.ToDouble(page.BottomY)+Convert.ToDouble(page.SizeHeight)/2);
                    frame=PatternBottleLayout.Frame(new PatternBottle(settings.BottleTop,settings.BottleBottom,settings.BottleHeight),session.BottleCenter);
                }
                else
                {
                    if(!shapes.TryGetValue(FrameTag,out var sourceFrame))throw new InvalidOperationException("Не найден контур детали проекта.");
                    dynamic target=layer.CreateCurve((dynamic)PatternFillService.GetOutline(sourceFrame));target.Name="Контур проекта паттерна";target.Fill.ApplyNoFill();target.Outline.SetNoOutline();
                    var seam=PatternFillService.ReadFrame(PatternFillService.GetOutline((object)target));
                    target.Move(Convert.ToDouble(page.LeftX)+Convert.ToDouble(page.SizeWidth)/2-seam.Left-seam.Width/2,Convert.ToDouble(page.BottomY)+Convert.ToDouble(page.SizeHeight)/2-seam.Bottom-seam.Height/2);
                    session.Target=(object)target;session.Seam=PatternFillService.ReadFrame(PatternFillService.GetOutline((object)target));target.CreateSelection();
                    frame=PatternFillService.FrameFor(session,settings.Allowance);
                }
                if(Math.Abs(frame.Width-data.Width)>.02 || Math.Abs(frame.Height-data.Height)>.02)throw new InvalidOperationException("Размеры исходного контура не соответствуют проекту.");
                ((dynamic)sourcesLayer).Printable=false;((dynamic)sourcesLayer).Visible=false;
                var variants=data.DraftSources.Select((index,i)=>data.DraftMirrors[i]?assets[index].MirrorHorizontally():assets[index]).ToArray();
                var draft=new PatternDraft{Frame=frame,Options=settings.Layout,Motifs=variants,MirroredIds=new HashSet<int>(session.MirroredIds),Items=data.Items.Select(p=>p.Read(variants.Length)).ToList(),
                    Hero=data.Main?.Read(1),Allowance=settings.Allowance,BottleWhole=data.Whole,Reserve=data.Reserve?.Read(),CellGuides=data.CellGuides?.Read(),RepeatGuides=data.RepeatGuides?.Read(),
                    CenterMarks=data.CenterMarks?.Read(),BottleCutView=data.CutView?.Read(),BottleSeamGuide=data.SeamGuide?.Read()};
                success=true;return new PatternProjectOpened{Session=session,Settings=settings,Draft=draft,Edits=data.Edits,Ids=ids,FilePath=filePath};
            }
            finally{if(!success && doc!=null)try{doc.Dirty=false;doc.Close();}catch(Exception error){Log.Error("Could not close invalid pattern project document.",error);}}
        }
        private static int Id(object shape)=>Convert.ToInt32(((dynamic)shape).StaticID);
        private static List<object> Items(object collection){var result=new List<object>();foreach(object item in (System.Collections.IEnumerable)collection)result.Add(item);return result;}
    }
}
