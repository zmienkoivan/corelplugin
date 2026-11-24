using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace VanyaTools.Native
{
    internal static class StickerPrintPlateService
    {
        internal static string Create(StickerPrintPlateKind kind,double trapping,int tint=100)=>Create((object)CorelApp.Get(),kind,trapping,tint);
        internal static string Create(object application,StickerPrintPlateKind kind,double trapping,int tint=100)
        {
            trapping=StickerPrintPlateModel.Validate(kind,trapping);StickerPrintPlateModel.ValidateTint(tint);dynamic app=application,doc=app.ActiveDocument;
            if(doc==null)throw new InvalidOperationException("Откройте документ и выделите рисунок.");
            dynamic selected=app.ActiveSelectionRange;
            if(selected==null || Convert.ToInt32(selected.Count)==0)throw new InvalidOperationException("Выделите рисунок для подложки или лака.");
            var originals=Items((object)selected.Shapes);dynamic input=app.CreateShapeRange();
            foreach(dynamic shape in originals)if(Convert.ToBoolean(shape.Layer.Printable) && !Technical((object)shape))input.Add(shape);
            if(Convert.ToInt32(input.Count)==0)throw new InvalidOperationException("Выделите сам рисунок. Контуры CUT и плашки WHITE/CLEAR не используются как исходник.");
            object color=StickerPrintSpotColor.Create(application,kind,tint),page=(object)doc.ActivePage,oldLayer=(object)doc.ActiveLayer;
            int oldUnit=Convert.ToInt32(doc.Unit),oldRef=Convert.ToInt32(doc.ReferencePoint);bool optimized=Convert.ToBoolean(app.Optimization),dirty=Convert.ToBoolean(doc.Dirty);
            var temporary=new List<object>();object layer=null,plate=null;bool started=false,success=false;
            var memory=new StickerPrintPlateMemory(kind);
            try
            {
                doc.BeginCommandGroup("Vanya Tools · "+StickerPrintPlateModel.Ink(kind));started=true;doc.Unit=CorelConstants.CdrMillimeter;doc.ReferencePoint=CorelConstants.CdrBottomLeft;app.Optimization=true;
                double x,y,w,h;input.GetBoundingBox(out x,out y,out w,out h,true);int dpi=StickerPrintPlateModel.CaptureDpi(w,h);
                RasterCaptureMemory.EnsureAvailable((long)Math.Ceiling(w*dpi/25.4)*(long)Math.Ceiling(h*dpi/25.4));
                dynamic copies=input.Duplicate(0,0);temporary.AddRange(Items((object)copies.Shapes));
                memory.Mark("selection duplicated");
                if(Convert.ToInt32(copies.Count)!=Convert.ToInt32(input.Count))throw new InvalidOperationException("Corel не скопировал всё выделение для печатной плашки.");
                dynamic surviving=app.CreateShapeRange();
                foreach(object copy in temporary.ToArray())
                    if(Prune(copy))surviving.Add((dynamic)copy);else temporary.Remove(copy);
                if(Convert.ToInt32(surviving.Count)==0)throw new InvalidOperationException("В выделении нет печатного рисунка после исключения технических контуров.");
                dynamic working=Convert.ToInt32(surviving.Count)==1?surviving.Shapes[1]:surviving.Group();
                temporary.Clear();temporary.Add((object)working);
                dynamic raster=working.ConvertToBitmapEx(CorelConstants.CdrRgbColorImage,true,true,dpi,CorelConstants.CdrNormalAntiAliasing,true);
                temporary.Clear();temporary.Add((object)raster);
                memory.Mark("raster created");
                var bitmap=NativeBitmapPixelReader.Read((object)raster.Bitmap,dpi,StickerPrintPlateModel.MaxPixels,false);
                memory.Mark("pixels read");
                double left=Convert.ToDouble(raster.LeftX),top=Convert.ToDouble(raster.TopY),width=Convert.ToDouble(raster.SizeWidth),height=Convert.ToDouble(raster.SizeHeight);
                raster.Delete();temporary.Clear();
                memory.Mark("temporary raster deleted");
                var geometry=StickerPrintPlateGeometry.Build(bitmap,width,height);
                memory.Mark("plate geometry built");
                object curve=StickerPrintPlateGeometry.Curve((object)doc,geometry,left,top,trapping,memory);
                memory.Mark("native trapping applied");
                // The editable WHITE plane sits below page artwork; CLEAR stays
                // above it. Both retain named spot inks and explicit overprint.
                layer=(object)((dynamic)page).CreateLayer(StickerPrintPlateModel.Ink(kind)+(kind==StickerPrintPlateKind.White?" · подложка":" · лак"));
                dynamic target=layer;target.Printable=true;target.Visible=true;target.Editable=true;
                if(!Convert.ToBoolean(target.Printable))throw new InvalidOperationException("Corel не включил печать слоя плашки.");
                if(kind==StickerPrintPlateKind.White)PlaceUnderArtwork(layer);
                plate=(object)target.CreateCurve((dynamic)curve);dynamic output=plate;
                output.Fill.ApplyUniformFill((dynamic)color);output.Outline.SetNoOutline();output.OverprintFill=true;
                StickerPrintSpotColor.Validate((object)output.Fill.UniformColor,kind,tint);
                if(!Convert.ToBoolean(output.OverprintFill))throw new InvalidOperationException("Corel не включил наложение плашечной заливки.");
                output.Name=StickerPrintPlateModel.Ink(kind)+" · "+tint+"% · "+trapping.ToString("+0.00;-0.00;0.00",CultureInfo.CurrentCulture)+" мм";
                RestoreSelection(originals);output.AddToSelection();
                memory.Mark("spot plate created");
                // Preserve the user's display mode. OverprintFill controls the
                // print separation independently; enabling simulation here can
                // rasterize/cache every bitmap on a crowded document page.
                memory.Mark("overprint preview preserved");
                success=true;Log.Info("Created "+StickerPrintPlateModel.Ink(kind)+" printing plate; trapping="+trapping.ToString(CultureInfo.InvariantCulture)+" mm; capture="+dpi+" DPI; paths="+geometry.Figures.Count+"; nodes="+Convert.ToInt32(((dynamic)curve).Nodes.Count)+".");
                return "Создана "+StickerPrintPlateModel.Ink(kind)+" · заливка "+tint+"% · треппинг "+trapping.ToString("+0.00;-0.00;0.00",CultureInfo.CurrentCulture)+" мм";
            }
            finally
            {
                Exception cleanup=null;
                foreach(dynamic shape in temporary)try{shape.Delete();}catch(Exception error){cleanup=error;Log.Error("Could not remove a printing-plate capture copy.",error);}
                if(!success && layer!=null)try{((dynamic)layer).Delete();}catch(Exception error){cleanup=error;Log.Error("Could not roll back a printing plate layer.",error);}
                if(!success)try{RestoreSelection(originals);}catch(Exception error){cleanup=error;Log.Error("Could not restore selection after printing plate failure.",error);}
                try{((dynamic)oldLayer).Activate();}finally
                {
                    try{doc.ReferencePoint=oldRef;doc.Unit=oldUnit;}finally
                    {
                        try{if(started)doc.EndCommandGroup();}finally{app.Optimization=optimized;}
                    }
                }
                if(!success && cleanup==null)doc.Dirty=dirty;
                if(!optimized)try{app.ActiveWindow.Refresh();}catch(Exception error){Log.Error("Could not refresh the printing plate preview.",error);}
                if(!optimized)try{app.Refresh();}catch(Exception error){Log.Error("Could not refresh the printing plate object list.",error);}
                memory.Mark(success?"completed":"failed");
                if(cleanup!=null)throw new InvalidOperationException("Не удалось полностью убрать временные объекты. Проверьте документ; изменения не скрыты.",cleanup);
            }
        }
        private static List<object> Items(object collection)
        {var result=new List<object>();foreach(object item in (System.Collections.IEnumerable)collection)result.Add(item);return result;}
        private static void PlaceUnderArtwork(object layer)
        {
            object lowest=null,current=(object)((dynamic)layer).Below;
            int count=0;
            while(current!=null)
            {
                if(++count>10000)throw new InvalidOperationException("Не удалось определить нижний слой страницы.");
                dynamic candidate=current;
                if(!Convert.ToBoolean(candidate.IsSpecialLayer) && !Convert.ToBoolean(candidate.Master))lowest=current;
                current=(object)candidate.Below;
            }
            if(lowest!=null)((dynamic)layer).MoveBelow((dynamic)lowest);
        }
        private static void RestoreSelection(List<object> shapes)
        {for(int i=0;i<shapes.Count;i++)if(i==0)((dynamic)shapes[i]).CreateSelection();else ((dynamic)shapes[i]).AddToSelection();}
        private static bool Technical(object value)
        {
            dynamic shape=value;
            if(Convert.ToInt32(shape.Type)==CorelConstants.CdrGroupShape)return false;
            int fill=Convert.ToInt32(shape.Fill.Type);
            if(fill==1 && Spot((object)shape.Fill.UniformColor,new[]{"WHITE","CLEAR"}))return true;
            return fill==0 && Convert.ToInt32(shape.Outline.Type)!=0 && Spot((object)shape.Outline.Color,new[]{"CUT","CutContour"});
        }
        private static bool Spot(object value,string[] names)
        {dynamic color=value;return Convert.ToBoolean(color.IsSpot) && names.Contains((string)Convert.ToString(color.SpotColorName),StringComparer.OrdinalIgnoreCase);}
        private static bool Prune(object value)
        {
            dynamic shape=value;if(Technical(value)){shape.Delete();return false;}
            if(Convert.ToInt32(shape.Type)==CorelConstants.CdrGroupShape)
            {
                bool remaining=false;foreach(object child in Items((object)shape.Shapes))remaining|=Prune(child);
                if(!remaining){try{shape.Delete();}catch{}return false;}
            }
            else
            {
                object clip=null;try{clip=(object)shape.PowerClip;}catch{}
                if(clip!=null)foreach(object child in Items((object)((dynamic)clip).Shapes))Prune(child);
            }
            return true;
        }
    }
}
