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
        internal static BitmapSource LoadReference(string path)
        {
            int width,height;
            // Read dimensions before requesting any full-resolution pixels.
            // A large source is valid for a style reference; decode its preview.
            using(var stream=File.OpenRead(path))
            {
                var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.None);
                width=decoder.Frames[0].PixelWidth;height=decoder.Frames[0].PixelHeight;
                if(width<=0 || height<=0 || width>100000 || height>100000 || (long)width*height>250000000)
                    throw new InvalidDataException("Не удалось подготовить превью референса: проверьте размер исходного файла.");
            }
            using(var stream=File.OpenRead(path))
            {
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;
                if(Math.Max(width,height)>2048){if(width>=height)image.DecodePixelWidth=2048;else image.DecodePixelHeight=2048;}
                image.EndInit();image.Freeze();return image;
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
        internal static BitmapSource Compose(IReadOnlyList<BitmapSource> sources)
        {
            if(sources==null || sources.Count<1 || sources.Count>16)throw new ArgumentException("Выберите от 1 до 16 изображений референса.");
            if(sources.Count==1)return Reference(sources[0]);
            int columns=(int)Math.Ceiling(Math.Sqrt(sources.Count)),rows=(sources.Count+columns-1)/columns;
            int cell=2048/columns,width=columns*cell,height=rows*cell;var visual=new DrawingVisual();
            using(var context=visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.LightGray,null,new Rect(0,0,width,height));
                for(int i=0;i<sources.Count;i++)
                {
                    var image=sources[i];double scale=Math.Min((cell-20.0)/image.PixelWidth,(cell-20.0)/image.PixelHeight);
                    double w=image.PixelWidth*scale,h=image.PixelHeight*scale;
                    context.DrawImage(image,new Rect((i%columns)*cell+(cell-w)/2,(i/columns)*cell+(cell-h)/2,w,h));
                }
            }
            var result=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();return result;
        }
        internal static BitmapSource Prepare(BitmapSource image,int threshold,CancellationToken cancel)
        {
            if(threshold<1 || threshold>255)throw new ArgumentOutOfRangeException(nameof(threshold));
            var pixels=DtfRasterOperations.Correct(Pixels(image),true,threshold,false,0,cancel);
            for(int p=0;p<pixels.Length;p+=4)if(pixels[p+3]==0)pixels[p]=pixels[p+1]=pixels[p+2]=0;
            int left=image.PixelWidth,top=image.PixelHeight,right=-1,bottom=-1;
            for(int y=0;y<image.PixelHeight;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<image.PixelWidth;x++)if(pixels[(y*image.PixelWidth+x)*4+3]!=0)
                {left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
            }
            if(right<left)return null;
            int w=right-left+1,h=bottom-top+1;var cropped=new byte[w*h*4];
            for(int y=0;y<h;y++)Buffer.BlockCopy(pixels,((y+top)*image.PixelWidth+left)*4,cropped,y*w*4,w*4);
            var result=BitmapSource.Create(w,h,image.DpiX,image.DpiY,PixelFormats.Bgra32,null,cropped,w*4);result.Freeze();return result;
        }
        internal static BitmapSource[] PrepareBatch(BitmapSource[] images,int threshold,CancellationToken cancel)
        {
            var result=new BitmapSource[images.Length];for(int i=0;i<images.Length;i++){cancel.ThrowIfCancellationRequested();if(images[i]!=null)result[i]=Prepare(images[i],threshold,cancel);}return result;
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
        internal static BitmapSource[] Extract(BitmapSource sheet,int count,string key,CancellationToken cancel,int alphaThreshold=130)
        {
            if(count<1 || count>33 || !ValidKey(key))throw new ArgumentException("Некорректная сетка букв.");
            int width=sheet.PixelWidth,height=sheet.PixelHeight;
            if(width<32 || height<32 || (long)width*height>25000000)throw new InvalidDataException("Лист ИИ слишком мал.");
            byte[] pixels=Pixels(sheet);int columns=LetterTransferRules.Columns(count),rows=(count+columns-1)/columns;RemoveSeparators(pixels,width,height,columns,rows);var color=(Color)ColorConverter.ConvertFromString(key);
            // Channel reflections preserve RGB distances and map the chosen technical
            // color to the tested magenta unmatting algorithm. White ink stays ink.
            Transform(pixels,color);PatternFillerMatte.Remove(pixels,width,height,cancel,MatteAtBorder(pixels,width,height));
            // The reserved hue must not survive as an opaque cast shadow or rim.
            for(int p=0;p<pixels.Length;p+=4)if(pixels[p+3]>0 && Math.Min(pixels[p],pixels[p+2])-pixels[p+1]>=50)pixels[p]=pixels[p+1]=pixels[p+2]=pixels[p+3]=0;
            Transform(pixels,color);
            pixels=DtfRasterOperations.Correct(pixels,true,alphaThreshold,false,0,cancel);
            for(int p=0;p<pixels.Length;p+=4)if(pixels[p+3]==0)pixels[p]=pixels[p+1]=pixels[p+2]=0;
            var result=new BitmapSource[count];
            for(int n=0;n<count;n++)
            {
                cancel.ThrowIfCancellationRequested();int x0=width*(n%columns)/columns,x1=width*(n%columns+1)/columns;
                int y0=height*(n/columns)/rows,y1=height*(n/columns+1)/rows;
                RemoveBoundarySpecks(pixels,width,x0,y0,x1,y1);
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
        private static void RemoveBoundarySpecks(byte[] pixels,int stride,int x0,int y0,int x1,int y1)
        {
            void Check(int x,int y)
            {
                int start=y*stride+x;if(pixels[start*4+3]<=8)return;
                var component=new List<int>{start};var seen=new HashSet<int>{start};
                for(int cursor=0;cursor<component.Count && component.Count<8;cursor++)
                {
                    int p=component[cursor],px=p%stride,py=p/stride;
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        int nx=px+dx,ny=py+dy,q=ny*stride+nx;
                        if(nx<x0 || nx>=x1 || ny<y0 || ny>=y1 || pixels[q*4+3]<=8 || !seen.Add(q))continue;
                        component.Add(q);
                    }
                }
                // A one-pixel speck at the border must not discard a whole
                // glyph. Anything connected to real artwork still rejects cuts.
                if(component.Count<8)foreach(int p in component)pixels[p*4+3]=0;
            }
            for(int x=x0;x<x1;x++){Check(x,y0);Check(x,y1-1);}
            for(int y=y0;y<y1;y++){Check(x0,y);Check(x1-1,y);}
        }
        private static byte[] MatteAtBorder(byte[] pixels,int width,int height)
        {
            var channels=new[]{new List<byte>(),new List<byte>(),new List<byte>()};int checkedPixels=0;
            void Sample(int x,int y)
            {
                int p=(y*width+x)*4;if(pixels[p+3]<250)return;checkedPixels++;
                // The reserved key is reflected to magenta. Estimate its actual
                // tone from the border; generated backgrounds may be shaded.
                if(Math.Min(pixels[p],pixels[p+2])-pixels[p+1]<60 || Math.Min(pixels[p],pixels[p+2])<100)return;
                for(int c=0;c<3;c++)channels[c].Add(pixels[p+c]);
            }
            int step=Math.Max(1,Math.Max(width,height)/512);
            for(int x=0;x<width;x+=step){Sample(x,0);Sample(x,height-1);}
            for(int y=0;y<height;y+=step){Sample(0,y);Sample(width-1,y);}
            if(channels[0].Count<16 || channels[0].Count<checkedPixels*.5)return null;
            var color=new byte[3];for(int c=0;c<3;c++){channels[c].Sort();color[c]=channels[c][channels[c].Count/2];}return color;
        }
        private static void RemoveSeparators(byte[] pixels,int width,int height,int columns,int rows)
        {
            // Only a neutral dark line spanning essentially the entire sheet at
            // a known grid boundary qualifies. A clipped glyph remains invalid.
            bool Dark(int x,int y)
            {
                int p=(y*width+x)*4;int min=Math.Min(pixels[p],Math.Min(pixels[p+1],pixels[p+2])),max=Math.Max(pixels[p],Math.Max(pixels[p+1],pixels[p+2]));
                return pixels[p+3]>=250 && max<=96 && max-min<=16;
            }
            var xs=new HashSet<int>();var ys=new HashSet<int>();
            for(int n=1;n<columns;n++)
            {
                int cut=width*n/columns;
                for(int x=Math.Max(0,cut-8);x<Math.Min(width,cut+8);x++)
                {int dark=0;for(int y=0;y<height;y++)if(Dark(x,y))dark++;if(dark>=height*.985)for(int dx=-2;dx<=2;dx++)if(x+dx>=0 && x+dx<width)xs.Add(x+dx);}
            }
            for(int n=1;n<rows;n++)
            {
                int cut=height*n/rows;
                for(int y=Math.Max(0,cut-8);y<Math.Min(height,cut+8);y++)
                {int dark=0;for(int x=0;x<width;x++)if(Dark(x,y))dark++;if(dark>=width*.985)for(int dy=-2;dy<=2;dy++)if(y+dy>=0 && y+dy<height)ys.Add(y+dy);}
            }
            foreach(int x in xs)for(int y=0;y<height;y++)pixels[(y*width+x)*4+3]=0;
            foreach(int y in ys)for(int x=0;x<width;x++)pixels[(y*width+x)*4+3]=0;
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
            image=Prepare(image,set.AlphaThreshold,CancellationToken.None);
            if(image==null)throw new InvalidDataException("После удаления полупрозрачности в букве не осталось видимого рисунка.");
            // Preserve detail while writing the chosen physical cap height into
            // the separate PNG. Corel placement still uses current layout size.
            double dpi=image.PixelHeight*25.4/set.HeightMm;
            image=BitmapSource.Create(image.PixelWidth,image.PixelHeight,dpi,dpi,PixelFormats.Bgra32,null,Pixels(image),image.PixelWidth*4);image.Freeze();
            var glyph=new LetterTransferGlyph{Letter=letter,Width=image.PixelWidth,Height=image.PixelHeight,Source=source};
            glyph.File="glyphs/"+glyph.Id+".png";Save(image,LetterTransferStore.Resolve(directory,glyph.File));set.Glyphs.Add(glyph);return glyph;
        }
    }
}
