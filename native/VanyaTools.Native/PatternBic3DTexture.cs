using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class PatternBic3DTexture
    {
        internal static BitmapSource Create(PatternDraft draft,PatternAsset hero,Func<PatternAsset,BitmapSource> image=null,int maximumSide=1600)
        {
            var bottle=draft?.Frame?.Bottle;
            if(bottle==null || Math.Abs(bottle.TopDiameter-bottle.BottomDiameter)>.0001)
                throw new InvalidOperationException("Для 3D BIC выберите заготовку с одинаковым диаметром сверху и снизу.");
            image=image??(a=>a.Preview);
            double width=bottle.Width,height=bottle.Height,pixels=Math.Min(32,Math.Max(64,Math.Min(2048,maximumSide))/Math.Max(width,height));
            int w=Math.Max(1,(int)Math.Ceiling(width*pixels)),h=Math.Max(1,(int)Math.Ceiling(height*pixels));
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(w/width,h/height));
                dc.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
                dc.PushClip(new RectangleGeometry(new Rect(0,0,width,height)));
                foreach(var item in draft.Items)Draw(draft.Motifs[item.Motif],item);
                if(draft.Hero!=null && hero!=null)Draw(hero,draft.Hero);
                dc.Pop();dc.Pop();
                void Draw(PatternAsset asset,PatternPlacement item)
                {
                    var fit=PatternTransform.Fit(asset.Size,item);var bitmap=image(asset);
                    for(int turn=-1;turn<=1;turn++)
                    {
                        double x=item.Center.X+turn*width,y=draft.Frame.Height-item.Center.Y;
                        dc.PushTransform(new RotateTransform(-item.Angle,x,y));
                        if(asset.Mirrored)dc.PushTransform(new ScaleTransform(-1,1,x,y));
                        var area=new Rect(x-fit.Width/2,y-fit.Height/2,fit.Width,fit.Height);
                        if(bitmap!=null)dc.DrawImage(bitmap,area);else dc.DrawRectangle(Brushes.LightSteelBlue,null,area);
                        if(asset.Mirrored)dc.Pop();dc.Pop();
                    }
                }
            }
            var result=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();return result;
        }
    }
}
