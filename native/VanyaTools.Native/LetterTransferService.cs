using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal static class LetterTransferService
    {
        internal static int Insert(string directory,LetterTransferSet set,bool readyOnly)=>Insert((object)CorelApp.Get(),directory,set,readyOnly);
        internal static int Insert(object application,string directory,LetterTransferSet set,bool readyOnly)
        {
            var placements=LetterTransferLayout.Build(set,readyOnly);if(placements.Count==0)throw new InvalidOperationException("Нет готовых букв для печати.");
            // Validate every source before creating a page; do not silently skip
            // a missing file and produce the wrong inventory.
            foreach(var glyph in placements.Select(p=>p.Glyph).Distinct())
            {
                string path=LetterTransferStore.Resolve(directory,glyph.File);
                var image=LetterTransferImages.Load(path);
                if(image.PixelWidth!=glyph.Width || image.PixelHeight!=glyph.Height)throw new InvalidDataException("Файл буквы «"+glyph.Letter+"» изменён. Импортируйте его как новый вариант.");
            }
            dynamic app=application,doc=app.ActiveDocument;
            if(doc==null)throw new InvalidOperationException("Откройте документ CorelDRAW.");
            int oldUnit=Convert.ToInt32(doc.Unit);bool optimized=Convert.ToBoolean(app.Optimization),dirty=Convert.ToBoolean(doc.Dirty);
            object previous=doc.ActivePage;bool begun=false,success=false,cleaned=true;var pages=new List<object>();
            try
            {
                doc.Unit=CorelConstants.CdrMillimeter;app.Optimization=true;doc.BeginCommandGroup("Vanya Tools · буквы-трансферы");begun=true;
                foreach(var group in placements.GroupBy(p=>p.Page))
                {
                    dynamic page=doc.AddPages(1);pages.Add((object)page);page.Activate();page.SetSize(set.SheetWidthMm,LetterTransferLayout.PageHeight(set,group));
                    page.Name=set.Name+" · "+LetterTransferPrintSizes.Label(group.First().PrintHeightMm)+" мм · лист "+(group.Key+1);dynamic layer=page.CreateLayer("Буквы-трансферы"),range=app.CreateShapeRange();
                    var sources=new Dictionary<string,object>();
                    foreach(var item in group)
                    {
                        dynamic shape;
                        if(sources.TryGetValue(item.Glyph.Id,out object source))shape=((dynamic)source).Duplicate(0,0);
                        else
                        {
                            var ids=new HashSet<int>();foreach(dynamic existing in layer.Shapes)ids.Add(Convert.ToInt32(existing.StaticID));
                            object nativeLayer=layer;
                            dynamic filter=nativeLayer.GetType().InvokeMember("ImportEx",BindingFlags.InvokeMethod,null,nativeLayer,
                                new object[]{new BStrWrapper(LetterTransferStore.Resolve(directory,item.Glyph.File)),0,app.CreateStructImportOptions()});
                            filter.Finish();source=null;
                            foreach(dynamic candidate in layer.Shapes)if(!ids.Contains(Convert.ToInt32(candidate.StaticID)))
                            {
                                if(source!=null || Convert.ToInt32(candidate.Type)!=CorelConstants.CdrBitmapShape)throw new InvalidOperationException("Corel не вернул одну букву после импорта.");
                                source=(object)candidate;
                            }
                            if(source==null)throw new InvalidOperationException("Corel не вставил букву «"+item.Glyph.Letter+"».");
                            sources.Add(item.Glyph.Id,source);shape=(dynamic)source;
                        }
                        shape.SetSize(item.Width,item.Height);
                        shape.Move(Convert.ToDouble(page.LeftX)+item.X-Convert.ToDouble(shape.LeftX),Convert.ToDouble(page.TopY)-item.Y-Convert.ToDouble(shape.TopY));
                        shape.Name="Буква "+item.Glyph.Letter+" · "+item.Glyph.Id.Substring(0,8);range.Add(shape);
                    }
                    range.CreateSelection();
                }
                success=true;return placements.Count;
            }
            catch
            {
                for(int i=pages.Count-1;i>=0;i--)try{((dynamic)pages[i]).Delete();}catch(Exception error){cleaned=false;Log.Error("Could not roll back letter page.",error);}
                try{((dynamic)previous).Activate();}catch(Exception error){Log.Error("Could not restore original page.",error);}throw;
            }
            finally
            {
                try{if(begun)doc.EndCommandGroup();}catch(Exception error){Log.Error("Could not finish letters command.",error);}
                try{doc.Unit=oldUnit;}catch(Exception error){Log.Error("Could not restore document units.",error);}
                try{app.Optimization=optimized;}catch(Exception error){Log.Error("Could not restore optimization.",error);}
                try{doc.Dirty=success || !cleaned || dirty;if(!optimized)app.ActiveWindow.Refresh();}catch(Exception error){Log.Error("Could not refresh letter pages.",error);}
            }
        }
    }
}
