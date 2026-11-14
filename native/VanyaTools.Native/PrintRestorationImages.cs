using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class PrintRestorationImages
    {
        internal static BitmapSource Load(string path)
        {
            using(var stream=File.OpenRead(path)) {var image=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);image.Freeze();return image;}
        }
        internal static void Save(BitmapSource image,string path)
        {var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(path))encoder.Save(stream);}
        internal static void SetPhysicalSize(string source,string output,double widthMm,double heightMm,CancellationToken cancel)
        {
            if(widthMm<=0 || heightMm<=0 || Double.IsNaN(widthMm+heightMm) || Double.IsInfinity(widthMm+heightMm))throw new ArgumentException("Некорректный размер принта.");
            var image=Load(source);cancel.ThrowIfCancellationRequested();
            var pixels=DtfCorrectionPixels.Capture(image,image.PixelWidth*25.4/widthMm,image.PixelHeight*25.4/heightMm);
            using(var stream=File.Create(output))pixels.WritePng(stream,cancel);
        }
        private static byte[] Pixels(BitmapSource image)
        {
            BitmapSource bgra=image.Format==PixelFormats.Bgra32?image:new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
            var data=new byte[checked(image.PixelWidth*image.PixelHeight*4)];bgra.CopyPixels(data,image.PixelWidth*4,0);return data;
        }
        internal static PrintRestorationAudit Audit(BitmapSource image,int tolerance,CancellationToken cancel)
        {
            BitmapSource bgra=image.Format==PixelFormats.Bgra32?image:new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
            int stride=checked(image.PixelWidth*4);var data=new byte[checked(stride*Math.Min(64,image.PixelHeight))];var result=new PrintRestorationAudit();
            for(int y=0;y<image.PixelHeight;y+=64)
            {
                cancel.ThrowIfCancellationRequested();int rows=Math.Min(64,image.PixelHeight-y);
                bgra.CopyPixels(new Int32Rect(0,y,image.PixelWidth,rows),data,stride,0);
                for(int p=0;p<rows*stride;p+=4)
                {
                    byte a=data[p+3];if(a==0)continue;result.Visible++;result.AlphaSum+=a;
                    if(a<255)result.SemiTransparent++;
                    int min=Math.Min(data[p],Math.Min(data[p+1],data[p+2]));
                    if(min>=255-tolerance && (data[p]!=255 || data[p+1]!=255 || data[p+2]!=255))result.NearWhite++;
                }
            }
            cancel.ThrowIfCancellationRequested();return result;
        }

        internal static Color KeyColor(BitmapSource image)
        {
            var data=Pixels(image);var candidates=new[]{Colors.Magenta,Colors.Lime,Colors.Cyan,Color.FromRgb(255,128,0)};
            double best=-1;Color selected=candidates[0];int step=Math.Max(1,image.PixelWidth*image.PixelHeight/65536);
            foreach(var c in candidates)
            {
                double nearest=Double.MaxValue;
                for(int pixel=0;pixel<image.PixelWidth*image.PixelHeight;pixel+=step)
                {
                    int p=pixel*4;if(data[p+3]==0)continue;
                    double r=data[p+2]-c.R,g=data[p+1]-c.G,b=data[p]-c.B;nearest=Math.Min(nearest,r*r+g*g+b*b);
                }
                if(nearest>best){best=nearest;selected=c;}
            }
            return selected;
        }
        internal static Color BorderColor(BitmapSource image)
        {
            var data=Pixels(image);var counts=new System.Collections.Generic.Dictionary<int,int>();int width=image.PixelWidth,height=image.PixelHeight;
            for(int i=0;i<256;i++)foreach(var point in new[]{new Point((width-1)*i/255,0),new Point((width-1)*i/255,height-1),new Point(0,(height-1)*i/255),new Point(width-1,(height-1)*i/255)})
            {
                int p=((int)point.Y*width+(int)point.X)*4;if(data[p+3]<128)continue;
                int key=((data[p+2]>>3)<<10)|((data[p+1]>>3)<<5)|(data[p]>>3);counts.TryGetValue(key,out int count);counts[key]=count+1;
            }
            int best=0,max=0;foreach(var pair in counts)if(pair.Value>max){max=pair.Value;best=pair.Key;}
            long red=0,green=0,blue=0,total=0;
            for(int p=0;p<data.Length;p+=4)if(data[p+3]>=128 && (((data[p+2]>>3)<<10)|((data[p+1]>>3)<<5)|(data[p]>>3))==best)
            {red+=data[p+2];green+=data[p+1];blue+=data[p];total++;}
            return total==0?Colors.White:Color.FromRgb((byte)(red/total),(byte)(green/total),(byte)(blue/total));
        }
        internal static Int32Rect TrimBounds(BitmapSource image,Color background,int tolerance,CancellationToken cancel)
        {
            var data=Pixels(image);int width=image.PixelWidth,height=image.PixelHeight;bool hasTransparency=false;
            for(int p=3;p<data.Length;p+=4)if(data[p]==0){hasTransparency=true;break;}
            int left=width,top=height,right=-1,bottom=-1;
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<width;x++)
                {
                    int p=(y*width+x)*4;if(data[p+3]==0)continue;
                    if(!hasTransparency && Math.Max(Math.Abs(data[p]-background.B),Math.Max(Math.Abs(data[p+1]-background.G),Math.Abs(data[p+2]-background.R)))<=tolerance)continue;
                    left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
                }
            }
            if(right<left)throw new InvalidOperationException("Не найден рисунок для обрезки. Проверьте цвет фона.");
            left=Math.Max(0,left-2);top=Math.Max(0,top-2);right=Math.Min(width-1,right+2);bottom=Math.Min(height-1,bottom+2);
            return new Int32Rect(left,top,right-left+1,bottom-top+1);
        }
        internal static void Crop(string source,string output,Int32Rect bounds,CancellationToken cancel)
        {cancel.ThrowIfCancellationRequested();var image=Load(source);var cropped=new CroppedBitmap(image,bounds);cropped.Freeze();Save(cropped,output);cancel.ThrowIfCancellationRequested();}
    }
}
