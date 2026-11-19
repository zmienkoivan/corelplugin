using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace VanyaTools.Native
{
    internal static class StickerPrintPlateService
    {
        internal static string Create(StickerPrintPlateKind kind,double trapping)=>Create((object)CorelApp.Get(),kind,trapping);
        internal static string Create(object application,StickerPrintPlateKind kind,double trapping)
        {
            trapping=StickerPrintPlateModel.Validate(kind,trapping);dynamic app=application,doc=app.ActiveDocument;
            if(doc==null)throw new InvalidOperationException("Откройте документ и выделите рисунок.");
            dynamic selected=app.ActiveSelectionRange;
            if(selected==null || Convert.ToInt32(selected.Count)==0)throw new InvalidOperationException("Выделите рисунок для подложки или лака.");
            var originals=Items((object)selected.Shapes);dynamic input=app.CreateShapeRange();
            foreach(dynamic shape in originals)if(Convert.ToBoolean(shape.Layer.Printable) && !Technical((object)shape))input.Add(shape);
            if(Convert.ToInt32(input.Count)==0)throw new InvalidOperationException("Выделите сам рисунок. Контуры CUT и плашки WHITE/CLEAR не используются как исходник.");
            object color=StickerPrintSpotColor.Create(application,kind),page=(object)doc.ActivePage,oldLayer=(object)doc.ActiveLayer;
            int oldUnit=Convert.ToInt32(doc.Unit),oldRef=Convert.ToInt32(doc.ReferencePoint);bool optimized=Convert.ToBoolean(app.Optimization),dirty=Convert.ToBoolean(doc.Dirty);
            var temporary=new List<object>();object layer=null,plate=null;bool started=false,success=false;
            try
            {
                doc.BeginCommandGroup("Vanya Tools · "+StickerPrintPlateModel.Ink(kind));started=true;doc.Unit=CorelConstants.CdrMillimeter;doc.ReferencePoint=CorelConstants.CdrBottomLeft;app.Optimization=true;
                double x,y,w,h;input.GetBoundingBox(out x,out y,out w,out h,true);int dpi=StickerPrintPlateModel.CaptureDpi(w,h);
                RasterCaptureMemory.EnsureAvailable((long)Math.Ceiling(w*dpi/25.4)*(long)Math.Ceiling(h*dpi/25.4));
                dynamic copies=input.Duplicate(0,0);temporary.AddRange(Items((object)copies.Shapes));
                if(Convert.ToInt32(copies.Count)!=Convert.ToInt32(input.Count))throw new InvalidOperationException("Corel не скопировал всё выделение для печатной плашки.");
                dynamic surviving=app.CreateShapeRange();
                foreach(object copy in temporary.ToArray())
                    if(Prune(copy))surviving.Add((dynamic)copy);else temporary.Remove(copy);
                if(Convert.ToInt32(surviving.Count)==0)throw new InvalidOperationException("В выделении нет печатного рисунка после исключения технических контуров.");
                dynamic working=Convert.ToInt32(surviving.Count)==1?surviving.Shapes[1]:surviving.Group();
                temporary.Clear();temporary.Add((object)working);
                dynamic raster=working.ConvertToBitmapEx(CorelConstants.CdrRgbColorImage,true,true,dpi,CorelConstants.CdrNormalAntiAliasing,true);
                temporary.Clear();temporary.Add((object)raster);
                var bitmap=NativeBitmapPixelReader.Read((object)raster.Bitmap,dpi,StickerPrintPlateModel.MaxPixels,false);
                double left=Convert.ToDouble(raster.LeftX),top=Convert.ToDouble(raster.TopY),width=Convert.ToDouble(raster.SizeWidth),height=Convert.ToDouble(raster.SizeHeight);
                raster.Delete();temporary.Clear();
                var geometry=StickerPrintPlateGeometry.Build(bitmap,width,height,trapping);
                object curve=StickerPrintPlateGeometry.Curve((object)doc,geometry,left,top);
                // Named printing planes sit above artwork with overprint. This
                // preserves their independent PDF separations without changing
                // the original artwork's knockout/overprint attributes. The RIP
                // maps WHITE to the underbase and CLEAR to the varnish pass.
                layer=(object)((dynamic)page).CreateLayer(StickerPrintPlateModel.Ink(kind)+(kind==StickerPrintPlateKind.White?" · подложка":" · лак"));
                dynamic target=layer;target.Printable=true;target.Visible=true;target.Editable=true;
                if(!Convert.ToBoolean(target.Printable))throw new InvalidOperationException("Corel не включил печать слоя плашки.");
                plate=(object)target.CreateCurve((dynamic)curve);dynamic output=plate;
                output.Fill.ApplyUniformFill((dynamic)color);output.Outline.SetNoOutline();output.OverprintFill=true;
                StickerPrintSpotColor.Validate((object)output.Fill.UniformColor,kind);
                if(!Convert.ToBoolean(output.OverprintFill))throw new InvalidOperationException("Corel не включил наложение плашечной заливки.");
                output.Name=StickerPrintPlateModel.Ink(kind)+" · "+trapping.ToString("+0.00;-0.00;0.00",CultureInfo.CurrentCulture)+" мм";
                RestoreSelection(originals);output.AddToSelection();
                try{app.ActiveWindow.ActiveView.SimulateOverprints=true;}catch(Exception error){Log.Error("Could not enable overprint preview for WHITE/CLEAR.",error);}
                success=true;Log.Info("Created "+StickerPrintPlateModel.Ink(kind)+" printing plate; trapping="+trapping.ToString(CultureInfo.InvariantCulture)+" mm; capture="+dpi+" DPI; nodes="+geometry.Figures.Count+" loops.");
                return "Создана "+StickerPrintPlateModel.Ink(kind)+" · треппинг "+trapping.ToString("+0.00;-0.00;0.00",CultureInfo.CurrentCulture)+" мм";
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
                if(cleanup!=null)throw new InvalidOperationException("Не удалось полностью убрать временные объекты. Проверьте документ; изменения не скрыты.",cleanup);
            }
        }
        private static List<object> Items(object collection)
        {var result=new List<object>();foreach(object item in (System.Collections.IEnumerable)collection)result.Add(item);return result;}
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
