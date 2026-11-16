using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class LetterTransferImages
    {
        private static readonly string[] Keys={"#FF00FF","#00FF00","#00FFFF","#FFFF00","#0000FF","#FF0000"};
        internal static bool ValidKey(string key)=>Keys.Contains(key);
        internal static Brush Checker()
        {
            var drawing=new DrawingGroup();using(var context=drawing.Open())
            {
                context.DrawRectangle(Brushes.White,null,new Rect(0,0,16,16));context.DrawRectangle(Brushes.LightGray,null,new Rect(0,0,8,8));context.DrawRectangle(Brushes.LightGray,null,new Rect(8,8,8,8));
            }
            var brush=new DrawingBrush(drawing){Viewport=new Rect(0,0,16,16),ViewportUnits=BrushMappingMode.Absolute,TileMode=TileMode.Tile};brush.Freeze();return brush;
        }
        internal static BitmapSource Load(string path)
        {
            using(var stream=File.OpenRead(path))
            {
                var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var image=decoder.Frames[0];
                if(image.PixelWidth>10000 || image.PixelHeight>10000 || (long)image.PixelWidth*image.PixelHeight>25000000)
                    throw new InvalidDataException("Рисунок слишком велик (не больше 25 Мп и 10000 px по стороне).");
                image.Freeze();return image;
            }
        }
        internal static void Save(BitmapSource image,string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using(var stream=File.Create(path))encoder.Save(stream);
        }
        internal static BitmapSource Reference(BitmapSource source)
        {
            double scale=Math.Min(1,2048.0/Math.Max(source.PixelWidth,source.PixelHeight));
            if(scale==1)return source;
            var image=new TransformedBitmap(source,new ScaleTransform(scale,scale));image.Freeze();return image;
        }
        internal static byte[] Pixels(BitmapSource image)
        {
            var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
            var pixels=new byte[checked(image.PixelWidth*image.PixelHeight*4)];converted.CopyPixels(pixels,image.PixelWidth*4,0);return pixels;
        }
        internal static string Key(BitmapSource reference)
        {
            byte[] data=Pixels(reference);long best=-1;string selected=Keys[0];int step=Math.Max(1,data.Length/4/65536);
            foreach(string key in Keys)
            {
                var c=(Color)ColorConverter.ConvertFromString(key);long nearest=long.MaxValue;
                for(int p=0;p<data.Length;p+=step*4)
                {
                    if(data[p+3]<32)continue;
                    int b=data[p]-c.B,g=data[p+1]-c.G,r=data[p+2]-c.R;nearest=Math.Min(nearest,b*b+g*g+r*r);
                }
                if(nearest>best){best=nearest;selected=key;}
            }
            return selected;
        }
        internal static BitmapSource[] Extract(BitmapSource sheet,int count,string key,CancellationToken cancel)
        {
            if(count<1 || count>4 || !ValidKey(key))throw new ArgumentException("Некорректная сетка букв.");
            int width=sheet.PixelWidth,height=sheet.PixelHeight;
            if(width<32 || height<32 || (long)width*height>25000000)throw new InvalidDataException("Лист ИИ слишком мал.");
            byte[] pixels=Pixels(sheet);var color=(Color)ColorConverter.ConvertFromString(key);
            // Channel reflections preserve RGB distances and map the chosen technical
            // color to the tested magenta unmatting algorithm. White ink stays ink.
            Transform(pixels,color);PatternFillerMatte.Remove(pixels,width,height,cancel);Transform(pixels,color);
            int columns=count==1?1:2,rows=(count+columns-1)/columns;var result=new BitmapSource[count];
            for(int n=0;n<count;n++)
            {
                cancel.ThrowIfCancellationRequested();int x0=width*(n%columns)/columns,x1=width*(n%columns+1)/columns;
                int y0=height*(n/columns)/rows,y1=height*(n/columns+1)/rows;
                int left=x1,top=y1,right=-1,bottom=-1,visible=0;bool clipped=false;
                for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)
                {
                    if(pixels[(y*width+x)*4+3]<=8)continue;
                    visible++;left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
                    if(x==x0 || x==x1-1 || y==y0 || y==y1-1)clipped=true;
                }
                // Do not silently turn a clipped sheet or a remaining background
                // into a glyph. All disconnected parts (Ё, Й, decoration) are kept.
                if(clipped || visible<16 || visible>(x1-x0)*(y1-y0)*.85 || right-left<3 || bottom-top<3)continue;
                left=Math.Max(x0,left-2);top=Math.Max(y0,top-2);right=Math.Min(x1-1,right+2);bottom=Math.Min(y1-1,bottom+2);
                int w=right-left+1,h=bottom-top+1;var cell=new byte[w*h*4];
                for(int y=0;y<h;y++)Buffer.BlockCopy(pixels,((top+y)*width+left)*4,cell,y*w*4,w*4);
                result[n]=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,cell,w*4);result[n].Freeze();
            }
            return result;
        }
        private static void Transform(byte[] pixels,Color color)
        {
            for(int i=0;i<pixels.Length;i+=4)
            {
                if(pixels[i+3]==0)continue;
                if(color.B==0)pixels[i]=(byte)(255-pixels[i]);
                if(color.G==255)pixels[i+1]=(byte)(255-pixels[i+1]);
                if(color.R==0)pixels[i+2]=(byte)(255-pixels[i+2]);
            }
        }
        internal static LetterTransferGlyph Add(string directory,LetterTransferSet set,string letter,BitmapSource image,string source)
        {
            if(!LetterTransferRules.ValidLetter(letter))throw new ArgumentException("Выберите русскую заглавную букву.");
            if(set.Glyphs.Count>=264 || set.Glyphs.Count(g=>g.Letter==letter)>=8)throw new InvalidOperationException("До 8 сохранённых вариантов на букву. Удалите ненужный вариант.");
            if(!Pixels(image).Where((value,index)=>index%4==3).Any(alpha=>alpha>8))throw new InvalidDataException("В изображении нет видимого рисунка буквы.");
            var glyph=new LetterTransferGlyph{Letter=letter,Width=image.PixelWidth,Height=image.PixelHeight,Source=source};
            glyph.File="glyphs/"+glyph.Id+".png";Save(image,LetterTransferStore.Resolve(directory,glyph.File));set.Glyphs.Add(glyph);return glyph;
        }
    }
}
