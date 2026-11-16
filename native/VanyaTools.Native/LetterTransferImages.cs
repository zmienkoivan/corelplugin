using System;
using System.Security.Cryptography;
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
        // Keep a single approved style preview below the worker's 1 MiB
        // inline budget. File-host resolution can stall upstream model runs.
        internal const int RequestReferenceByteLimit=900*1024;
        internal static BitmapSource RequestReference(BitmapSource source)
        {
            var preview=Reference(source);var original=preview;
            while(true)
            {
                long bytes=PngSize(preview);if(bytes<=RequestReferenceByteLimit)return preview;
                double factor=Math.Min(.85,Math.Sqrt(RequestReferenceByteLimit/(double)bytes)*.95);
                int width=Math.Max(1,(int)(preview.PixelWidth*factor)),height=Math.Max(1,(int)(preview.PixelHeight*factor));
                // Always sample from the original preview, avoiding repeated
                // resampling and preserving the source file and its alpha.
                var resized=new TransformedBitmap(original,new ScaleTransform(width/(double)original.PixelWidth,height/(double)original.PixelHeight));
                resized.Freeze();preview=resized;
            }
        }
        private static long PngSize(BitmapSource image)
        {
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using(var stream=new MemoryStream()){encoder.Save(stream);return stream.Length;}
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
        internal static string ActualSize(string file)
        {
            try{using(var stream=File.OpenRead(file)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames[0];return frame.PixelWidth+" × "+frame.PixelHeight+" px";}}
            catch(Exception error)when(error is IOException || error is NotSupportedException || error is ArgumentException || error is FileFormatException){return "размер не прочитан";}
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
            byte[] pixels=Pixels(sheet);int columns=LetterTransferRules.Columns(count),rows=(count+columns-1)/columns;var bounds=RemoveSeparators(pixels,width,height,columns,rows,cancel);var color=(Color)ColorConverter.ConvertFromString(key);
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
                cancel.ThrowIfCancellationRequested();var box=bounds[n];if(box.Width<4 || box.Height<4)continue;
                int x0=box.X,x1=box.X+box.Width,y0=box.Y,y1=box.Y+box.Height;
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
        private static Int32Rect[] RemoveSeparators(byte[] pixels,int width,int height,int columns,int rows,CancellationToken cancel)
        {
            bool Dark(int x,int y)
            {
                int p=(y*width+x)*4;int min=Math.Min(pixels[p],Math.Min(pixels[p+1],pixels[p+2])),max=Math.Max(pixels[p],Math.Max(pixels[p+1],pixels[p+2]));
                return pixels[p+3]>=250 && max<=96 && max-min<=64;
            }
            int[] Find(bool vertical,int cut)
            {
                int across=vertical?width:height,length=vertical?height:width;
                int radius=Math.Max(8,Math.Min(24,across/250));
                int first=Math.Max(0,cut-radius),last=Math.Min(across-1,cut+radius),covered=0;
                var counts=new int[last-first+1];
                // Generated lines drift across neighboring pixels. Require a dark
                // divider somewhere in this narrow band along the entire sheet,
                // rather than an unrealistically perfect single pixel column.
                for(int along=0;along<length;along++)
                {
                    cancel.ThrowIfCancellationRequested();bool hit=false;
                    for(int at=first;at<=last;at++)if(vertical?Dark(at,along):Dark(along,at)){counts[at-first]++;hit=true;}
                    if(hit)covered++;
                }
                if(covered<length*.95)return null;
                // Ignore sparse crossings of perpendicular dividers when
                // measuring this line, so its removal cannot widen into ink.
                int lo=last+1,hi=first-1;
                for(int at=first;at<=last;at++)if(counts[at-first]>=Math.Max(2,length*.10)){lo=Math.Min(lo,at);hi=Math.Max(hi,at);}
                if(hi<lo)return null;
                return new[]{Math.Max(0,lo-2),Math.Min(across,hi+3)};
            }
            var xs=new int[columns+1][];var ys=new int[rows+1][];
            // Inspect both axes before clearing crossings, including the frame.
            for(int n=0;n<=columns;n++)xs[n]=Find(true,Math.Min(width-1,width*n/columns));
            for(int n=0;n<=rows;n++)ys[n]=Find(false,Math.Min(height-1,height*n/rows));
            foreach(var strip in xs.Where(s=>s!=null))for(int x=strip[0];x<strip[1];x++)for(int y=0;y<height;y++)pixels[(y*width+x)*4+3]=0;
            foreach(var strip in ys.Where(s=>s!=null))for(int y=strip[0];y<strip[1];y++)for(int x=0;x<width;x++)pixels[(y*width+x)*4+3]=0;
            var bounds=new Int32Rect[columns*rows];
            for(int n=0;n<bounds.Length;n++)
            {
                int col=n%columns,row=n/columns;
                int left=xs[col]?[1]??width*col/columns,right=xs[col+1]?[0]??width*(col+1)/columns;
                int top=ys[row]?[1]??height*row/rows,bottom=ys[row+1]?[0]??height*(row+1)/rows;
                // Check clipping at the artwork side of each removed divider.
                // Otherwise clearing a grid line could conceal a crossing glyph.
                bounds[n]=new Int32Rect(left,top,Math.Max(0,right-left),Math.Max(0,bottom-top));
            }
            return bounds;
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
        internal static string Fingerprint(BitmapSource image)
        {using(var sha=SHA256.Create())return image.PixelWidth+"x"+image.PixelHeight+":"+Convert.ToBase64String(sha.ComputeHash(Pixels(image)));}
        internal static int BodyHeight(string letter,BitmapSource image)
        {
            if(letter!="Ё" && letter!="Й")return image.PixelHeight;
            byte[] pixels=Pixels(image);int w=image.PixelWidth,h=image.PixelHeight;var occupied=new bool[h];
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)if(pixels[(y*w+x)*4+3]>=130){occupied[y]=true;break;}
            int first=Array.FindIndex(occupied,b=>b),last=Array.FindLastIndex(occupied,b=>b);
            if(first<0)return 0;
            for(int y=first+1;y<h*.4;y++)if(!occupied[y] && occupied[y-1])
            {
                int end=y;while(end<h && !occupied[end])end++;
                if(end<h*.45 && last-end+1>=h*.55)return last-end+1;
            }
            // A curved breve can overlap the top of И vertically while
            // remaining a separate shape. Measure connected components when
            // there is no completely empty horizontal row between them.
            var seen=new bool[w*h];var queue=new int[w*h];var parts=new List<int[]>();int total=0;
            for(int start=0;start<w*h;start++)
            {
                if(seen[start] || pixels[start*4+3]<130)continue;
                int head=0,tail=1;queue[0]=start;seen[start]=true;int top=h,bottom=-1;
                while(head<tail)
                {
                    int at=queue[head++],x=at%w,y=at/w;top=Math.Min(top,y);bottom=Math.Max(bottom,y);
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        int nx=x+dx,ny=y+dy;if(nx<0 || ny<0 || nx>=w || ny>=h)continue;int next=ny*w+nx;
                        if(seen[next] || pixels[next*4+3]<130)continue;seen[next]=true;queue[tail++]=next;
                    }
                }
                parts.Add(new[]{tail,top,bottom});total+=tail;
            }
            if(parts.Count<2)return 0;var body=parts.OrderByDescending(part=>part[0]).First();
            bool accent=parts.Any(part=>!ReferenceEquals(part,body) && part[0]>=Math.Max(8,total*.003) && (part[1]+part[2])*.5<body[1] && part[2]<=body[1]+h*.12);
            return accent && body[2]-body[1]+1>=h*.55 && body[1]>=h*.05?body[2]-body[1]+1:0;
        }
        internal static BitmapSource Physical(LetterTransferSet set,string letter,BitmapSource image)
        {
            int body=BodyHeight(letter,image);
            double physicalHeight=(letter=="Ё" || letter=="Й")?(body>0?set.HeightMm*image.PixelHeight/body:set.HeightMm*1.2):set.HeightMm;
            double dpi=image.PixelHeight*25.4/physicalHeight;
            var result=BitmapSource.Create(image.PixelWidth,image.PixelHeight,dpi,dpi,PixelFormats.Bgra32,null,Pixels(image),image.PixelWidth*4);result.Freeze();return result;
        }
        internal static LetterTransferGlyph Add(string directory,LetterTransferSet set,string letter,BitmapSource image,string source)
        {
            if(!LetterTransferRules.ValidLetter(letter))throw new ArgumentException("Выберите русскую заглавную букву.");
            if(set.Glyphs.Count>=264 || set.Glyphs.Count(g=>g.Letter==letter)>=8)throw new InvalidOperationException("До 8 сохранённых вариантов на букву. Удалите ненужный вариант.");
            var original=Prepare(image,set.AlphaThreshold,CancellationToken.None);
            if(original==null)throw new InvalidDataException("После удаления полупрозрачности в букве не осталось видимого рисунка.");
            image=Physical(set,letter,LetterTransferEdges.Finish(original,set,letter,CancellationToken.None));
            var glyph=new LetterTransferGlyph{Letter=letter,Width=image.PixelWidth,Height=image.PixelHeight,BodyHeight=BodyHeight(letter,image),Source=source,EdgeTrimPx=set.EdgeTrimPx,Smoothed=set.SmoothEdges,SharpContour=set.SharpContour,ContourSmoothPx=set.SharpContour?set.ContourSmoothPx:0};
            glyph.OriginalFile="glyphs/"+glyph.Id+"-source.png";glyph.File="glyphs/"+glyph.Id+".png";
            Save(original,LetterTransferStore.Resolve(directory,glyph.OriginalFile));Save(image,LetterTransferStore.Resolve(directory,glyph.File));set.Glyphs.Add(glyph);return glyph;
        }
    }
}
