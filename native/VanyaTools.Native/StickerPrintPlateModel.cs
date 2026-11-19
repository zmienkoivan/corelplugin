using System;
namespace VanyaTools.Native
{
    internal enum StickerPrintPlateKind { White, Clear }
    internal static class StickerPrintPlateModel
    {
        internal const int MaxPixels=64000000;
        internal static string Ink(StickerPrintPlateKind kind)=>kind==StickerPrintPlateKind.White?"WHITE":"CLEAR";
        internal static double Validate(StickerPrintPlateKind kind,double trapping)
        {
            if(kind!=StickerPrintPlateKind.White && kind!=StickerPrintPlateKind.Clear)throw new ArgumentException("Неизвестная печатная плашка.");
            double steps=trapping/.05;
            if(Double.IsNaN(trapping) || Double.IsInfinity(trapping) || trapping<-.500000001 || trapping>.500000001 || Math.Abs(steps-Math.Round(steps))>1e-7)
                throw new ArgumentException("Треппинг: от −0,50 до +0,50 мм с шагом 0,05 мм.");
            return Math.Round(steps)*.05;
        }
        internal static int CaptureDpi(double width,double height)
        {
            if(!(width>0) || !(height>0) || Double.IsInfinity(width+height))throw new InvalidOperationException("Не удалось определить размер рисунка.");
            int dpi=(int)Math.Min(600,Math.Min(Math.Sqrt(MaxPixels/(width*height))*25.4,20000/Math.Max(width,height)*25.4));
            while(dpi>=72 && Math.Ceiling(width*dpi/25.4)*Math.Ceiling(height*dpi/25.4)>MaxPixels)dpi--;
            if(dpi<72)throw new InvalidOperationException("Слишком большое выделение. Создавайте подложку и лак частями.");
            return dpi;
        }
    }
}
