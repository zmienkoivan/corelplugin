using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferCell
    {
        internal BitmapSource Image,Preview,Original;
        internal Int32Rect Bounds;
        internal string Letter,Warning,SourceJobId;
        internal bool Safe,Recognized,RecognitionUncertain,Manual,Excluded;
    }
    internal sealed class LetterTransferSheet
    {
        internal LetterTransferCell[] Cells;
        internal string[] Requested;
        internal string Notice;
        internal bool LayoutChanged,ContentLayout;
        private struct Band{internal int Start,End;internal Band(int start,int end){Start=start;End=end;}}
        internal static LetterTransferSheet Read(BitmapSource source,string[] letters,string key,int threshold,CancellationToken cancel)
        {
            var sheet=ReadCore(source,letters,key,threshold,cancel,true);
            int ready=sheet.Cells.Count(c=>c.Safe && c.Image!=null);
            bool punctuation=letters.Any(l=>LetterTransferRules.Punctuation.Contains(l));
            if(ready>=letters.Length && (!punctuation || sheet.Cells.Length==letters.Length))return sheet;
            // A model can omit dividers, move rows or let a descender overlap
            // the next row. Recover separate silhouettes before guessing cuts.
            var color=(Color)ColorConverter.ConvertFromString(key);
            var clean=Clean(source,color,threshold,cancel,true,true,out _,out _,false);
            if(clean==null)return sheet;
            var recovered=LetterTransferComponentGrid.Read(clean,letters,cancel);
            if(recovered==null)return sheet;
            int recoveredReady=recovered.Cells.Count(c=>c.Safe && c.Image!=null);
            if(recoveredReady<=ready && !(punctuation && recoveredReady>=letters.Length && recovered.Cells.Length<sheet.Cells.Length))return sheet;
            Log.Info("Letter sheet silhouette recovery: "+ready+" -> "+recovered.Cells.Count(c=>c.Safe && c.Image!=null)+" complete drawings; candidates="+recovered.Cells.Length+"; requested="+letters.Length+".");
            return recovered;
        }
        internal static LetterTransferSheet ReadLegacy(BitmapSource source,string[] letters,string key,int threshold,CancellationToken cancel)=>ReadCore(source,letters,key,threshold,cancel,false,false);
        internal static LetterTransferSheet ReadBeforeSpill(BitmapSource source,string[] letters,string key,int threshold,CancellationToken cancel)=>ReadCore(source,letters,key,threshold,cancel,true,false);
        private static LetterTransferSheet ReadCore(BitmapSource source,string[] letters,string key,int threshold,CancellationToken cancel,bool protectArtwork,bool cleanSpill=true)
        {
            int w=source.PixelWidth,h=source.PixelHeight;
            if(w<32 || h<32 || (long)w*h>25000000)throw new ArgumentException("Некорректный размер листа.");
            byte[] pixels=LetterTransferImages.Pixels(source);var color=(Color)ColorConverter.ConvertFromString(key);
            var pageMatte=Background(pixels,w,h);int delta=0;
            for(int c=0;c<3;c++){int d=pageMatte[c]-(c==0?color.B:c==1?color.G:color.R);delta+=d*d;}
            bool coloredDividers=delta>80*80;var strokes=new bool[w*h];
            for(int y=0;y<h;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<w;x++)
                {
                    int p=(y*w+x)*4,min=Math.Min(pixels[p],Math.Min(pixels[p+1],pixels[p+2])),max=Math.Max(pixels[p],Math.Max(pixels[p+1],pixels[p+2]));
                    int b=pixels[p]-color.B,g=pixels[p+1]-color.G,r=pixels[p+2]-color.R;
                    strokes[y*w+x]=pixels[p+3]>=250 && (max<=96 && max-min<=64 || coloredDividers && b*b+g*g+r*r<90*90);
                }
            }
            var horizontal=Bands(strokes,w,h,new Int32Rect(0,0,w,h),false,cancel);
            var boxes=new List<Int32Rect>();
            for(int row=0;row+1<horizontal.Count;row++)
            {
                int top=horizontal[row].End,bottom=horizontal[row+1].Start;
                if(bottom-top<16)continue;
                var vertical=Bands(strokes,w,h,new Int32Rect(0,top,w,bottom-top),true,cancel);
                for(int col=0;col+1<vertical.Count;col++)
                {
                    int left=vertical[col].End,right=vertical[col+1].Start;
                    if(right-left>=16)boxes.Add(new Int32Rect(left,top,right-left,bottom-top));
                }
            }
            bool contentLayout=false;
            if(boxes.Count<Math.Max(1,letters.Length/2) || boxes.Count>64)
            {
                var light=LightGrid(pixels,w,h,pageMatte,cancel);
                if(light!=null)boxes=light;
                else
                {
                    var content=LetterTransferContentGrid.Find(pixels,w,h,pageMatte,cancel);
                    if(content==null)return Fixed(source,letters,key,threshold,cancel,protectArtwork);
                    boxes=content;contentLayout=true;
                }
            }
            var cells=new List<LetterTransferCell>();
            foreach(var box in boxes)
            {
                cancel.ThrowIfCancellationRequested();var raw=Crop(pixels,w,box);
                var image=Clean(raw,color,threshold,cancel,protectArtwork,cleanSpill,out bool safe,out string warning);
                // Empty trailing slots do not shift labels of a complete sheet.
                if(image==null)continue;
                cells.Add(new LetterTransferCell{Image=image,Preview=image,Safe=safe,Warning=warning,Bounds=box});
            }
            if(cells.Count==0)return Fixed(source,letters,key,threshold,cancel,protectArtwork);
            bool changed=cells.Count!=letters.Length;
            for(int i=0;i<cells.Count;i++)cells[i].Letter=changed?null:letters[i];
            return new LetterTransferSheet{Cells=cells.ToArray(),Requested=letters.ToArray(),LayoutChanged=changed,ContentLayout=contentLayout,Notice=changed?
                "На листе найдено рисунков: "+cells.Count+", запрошено букв: "+letters.Length+". Проверьте обозначения. Повторы сохраняются как варианты; отсутствующие буквы можно догенерировать отдельно.":null};
        }
        private static LetterTransferSheet Fixed(BitmapSource source,string[] letters,string key,int threshold,CancellationToken cancel,bool protectArtwork)
        {
            var prepared=LetterTransferImages.Extract(source,letters.Length,key,cancel,threshold,protectArtwork);var data=LetterTransferImages.Pixels(source);
            int cols=LetterTransferRules.Columns(letters.Length),rows=(letters.Length+cols-1)/cols,w=source.PixelWidth,h=source.PixelHeight;
            var cells=new LetterTransferCell[letters.Length];
            for(int n=0;n<cells.Length;n++)
            {
                int x=w*(n%cols)/cols,x1=w*(n%cols+1)/cols,y=h*(n/cols)/rows,y1=h*(n/cols+1)/rows;
                cells[n]=new LetterTransferCell{Bounds=new Int32Rect(x,y,x1-x,y1-y),Image=prepared[n],Preview=prepared[n]??Crop(data,w,new Int32Rect(x,y,x1-x,y1-y)),Letter=letters[n],Safe=prepared[n]!=null,
                    Warning=prepared[n]!=null?null:"Не удалось безопасно отделить фон или рисунок касается границы. Показан исходный фрагмент; он не добавится в набор."};
            }
            int ready=cells.Count(c=>c.Image!=null);
            return new LetterTransferSheet{Cells=cells,Requested=letters.ToArray(),Notice=ready<cells.Length?"Не удалось определить границы всех букв. Фрагменты с предупреждением не отправляются на распознавание.":null};
        }
        private static List<Int32Rect> LightGrid(byte[] pixels,int width,int height,byte[] matte,CancellationToken cancel)
        {
            var strokes=new bool[width*height];
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<width;x++)
                {
                    int at=y*width+x,p=at*4,min=Math.Min(pixels[p],Math.Min(pixels[p+1],pixels[p+2])),max=Math.Max(pixels[p],Math.Max(pixels[p+1],pixels[p+2]));
                    int b=pixels[p]-matte[0],g=pixels[p+1]-matte[1],r=pixels[p+2]-matte[2];
                    // Generated white lines can be tinted by the technical
                    // backdrop. Require a continuous line, not a white glyph.
                    strokes[at]=pixels[p+3]>=250 && min>=128 && max-min<=128 && b*b+g*g+r*r>=90*90;
                }
            }
            List<Band> Bounds(List<Band> lines,int size)
            {
                if(lines.Count==0)return null;
                var result=new List<Band>(lines);
                if(result[0].Start>=16)result.Insert(0,new Band(0,0));
                if(size-result[result.Count-1].End>=16)result.Add(new Band(size,size));
                return result;
            }
            var horizontal=Bounds(Bands(strokes,width,height,new Int32Rect(0,0,width,height),false,cancel),height);
            if(horizontal==null || horizontal.Count<3 || horizontal.Count>17)return null;
            var globalVertical=Bounds(Bands(strokes,width,height,new Int32Rect(0,0,width,height),true,cancel),width);
            var boxes=new List<Int32Rect>();
            for(int row=0;row+1<horizontal.Count;row++)
            {
                int top=horizontal[row].End,bottom=horizontal[row+1].Start;
                if(bottom-top<16)return null;
                var vertical=Bounds(Bands(strokes,width,height,new Int32Rect(0,top,width,bottom-top),true,cancel),width);
                // A glyph crossing one divider must be flagged by Clean,
                // rather than causing its two neighboring cells to be merged.
                if(globalVertical!=null && (vertical==null || vertical.Count<globalVertical.Count))vertical=globalVertical;
                if(vertical==null || vertical.Count<3 || vertical.Count>17)return null;
                for(int col=0;col+1<vertical.Count;col++)
                {
                    int left=vertical[col].End,right=vertical[col+1].Start;
                    if(right-left<16)return null;
                    boxes.Add(new Int32Rect(left,top,right-left,bottom-top));
                }
            }
            return boxes.Count>=2 && boxes.Count<=64?boxes:null;
        }
        private static List<Band> Bands(bool[] ink,int width,int height,Int32Rect region,bool vertical,CancellationToken cancel)
        {
            int size=vertical?region.Width:region.Height,length=vertical?region.Height:region.Width;
            var counts=new int[length];int covered=0;var hits=new bool[size];
            void Change(int at,int direction)
            {
                if(at<0 || at>=size)return;
                for(int n=0;n<length;n++)
                {
                    int x=region.X+(vertical?at:n),y=region.Y+(vertical?n:at);
                    if(!ink[y*width+x])continue;
                    int before=counts[n];counts[n]+=direction;if(before==0 && counts[n]>0)covered++;if(before>0 && counts[n]==0)covered--;
                }
            }
            for(int at=0;at<3;at++)Change(at,1);
            for(int at=0;at<size;at++)
            {cancel.ThrowIfCancellationRequested();Change(at+3,1);Change(at-4,-1);hits[at]=covered>=length*.94;}
            var result=new List<Band>();int offset=vertical?region.X:region.Y;
            for(int at=0;at<size;at++)
            {
                if(!hits[at])continue;int start=at;while(at+1<size && hits[at+1])at++;int end=at+1;
                // A wide stripe at an outside edge may be unused grid slots.
                if(end-start<=Math.Max(20,size/100) || vertical && (start==0 || end==size))result.Add(new Band(offset+start,offset+end));
            }
            return result;
        }
        private static BitmapSource Crop(byte[] pixels,int stride,Int32Rect box)
        {
            var data=new byte[box.Width*box.Height*4];for(int y=0;y<box.Height;y++)Buffer.BlockCopy(pixels,((box.Y+y)*stride+box.X)*4,data,y*box.Width*4,box.Width*4);
            var image=BitmapSource.Create(box.Width,box.Height,96,96,PixelFormats.Bgra32,null,data,box.Width*4);image.Freeze();return image;
        }
        private static byte[] Background(byte[] data,int width,int height)
        {
            var colors=new Dictionary<int,int>();var sums=new Dictionary<int,long[]>();int step=Math.Max(1,Math.Max(width,height)/256),inset=Math.Min(12,Math.Min(width,height)/8);
            void Sample(int x,int y)
            {
                int p=(y*width+x)*4;if(data[p+3]<250)return;int code=(data[p]>>4)|((data[p+1]>>4)<<4)|((data[p+2]>>4)<<8);
                if(!colors.ContainsKey(code)){colors[code]=0;sums[code]=new long[3];}colors[code]++;for(int c=0;c<3;c++)sums[code][c]+=data[p+c];
            }
            for(int x=inset;x<width-inset;x+=step){Sample(x,inset);Sample(x,height-inset-1);}
            for(int y=inset;y<height-inset;y+=step){Sample(inset,y);Sample(width-inset-1,y);}
            if(colors.Count==0)return new byte[]{255,0,255};int best=colors.OrderByDescending(p=>p.Value).First().Key;
            return sums[best].Select(s=>(byte)(s/colors[best])).ToArray();
        }
        private static void RemoveShadows(byte[] pixels,int width,int height,byte[] matte,CancellationToken cancel,bool[] protection)
        {
            double norm=matte.Sum(c=>(int)c*c);if(norm<10000)return;
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<width;x++)
                {
                    int p=(y*width+x)*4;if(pixels[p+3]==0 || protection!=null && protection[p/4])continue;
                    double brightness=0;for(int c=0;c<3;c++)brightness+=pixels[p+c]*matte[c];brightness/=norm;
                    // A cast shadow darkens the reserved backdrop while keeping
                    // its chromatic direction. Do not treat neutral black or a
                    // genuine green fabric with other color channels as matte.
                    if(brightness<.25 || brightness>1.1)continue;
                    double residual=0;for(int c=0;c<3;c++){double d=pixels[p+c]-brightness*matte[c];residual+=d*d;}
                    if(residual<=24*24)pixels[p]=pixels[p+1]=pixels[p+2]=pixels[p+3]=0;
                }
            }
        }
        private static void ShadowEdges(byte[] pixels,int width,int height,byte[] matte,CancellationToken cancel,bool[] protection,bool unmatteProtected=false)
        {
            var distance=new byte[width*height];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int at=y*width+x;if(pixels[at*4+3]==0)continue;int value=x==0 || y==0 || x==width-1 || y==height-1?1:4;
                if(x>0)value=Math.Min(value,distance[at-1]+1);
                if(y>0){value=Math.Min(value,distance[at-width]+1);if(x>0)value=Math.Min(value,distance[at-width-1]+1);if(x+1<width)value=Math.Min(value,distance[at-width+1]+1);}
                distance[at]=(byte)value;
            }
            for(int y=height-1;y>=0;y--)for(int x=width-1;x>=0;x--)
            {
                int at=y*width+x,value=distance[at];if(value==0)continue;
                if(x+1<width)value=Math.Min(value,distance[at+1]+1);
                if(y+1<height){value=Math.Min(value,distance[at+width]+1);if(x>0)value=Math.Min(value,distance[at+width-1]+1);if(x+1<width)value=Math.Min(value,distance[at+width+1]+1);}
                distance[at]=(byte)value;
            }
            var original=(byte[])pixels.Clone();
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<width;x++)
                {
                    int at=y*width+x,p=at*4;if(protection!=null && protection[at] && !unmatteProtected || distance[at]==0 || distance[at]>=4 || original[p+3]<250)continue;
                    int core=-1,best=Int32.MaxValue;
                    for(int dy=-6;dy<=6;dy++)for(int dx=-6;dx<=6;dx++)
                    {
                        int nx=x+dx,ny=y+dy,d=dx*dx+dy*dy;if(nx<0 || ny<0 || nx>=width || ny>=height || d>=best)continue;
                        int n=ny*width+nx;if(distance[n]<4 || original[n*4+3]<250)continue;core=n*4;best=d;
                    }
                    if(core<0)continue;
                    double ff=0,fm=0,mm=0,cf=0,cm=0;
                    for(int c=0;c<3;c++){ff+=original[core+c]*original[core+c];fm+=original[core+c]*matte[c];mm+=matte[c]*matte[c];cf+=original[p+c]*original[core+c];cm+=original[p+c]*matte[c];}
                    // A protected edge may still contain a reflected technical hue.
                    // Refit it only when the nearest opaque core has a different
                    // hue; actual green/magenta fabric remains protected.
                    if(unmatteProtected && protection!=null && protection[at] && fm*fm>=ff*mm*.81)continue;
                    double determinant=ff*mm-fm*fm;if(determinant<1000000)continue;
                    double ink=(cf*mm-cm*fm)/determinant,backdrop=(cm*ff-cf*fm)/determinant;
                    if(ink<0 || ink>1 || backdrop<.03 || backdrop>1.1 || ink+backdrop>1.2)continue;
                    double residual=0;for(int c=0;c<3;c++){double d=original[p+c]-ink*original[core+c]-backdrop*matte[c];residual+=d*d;}
                    if(residual>3*12*12)continue;
                    if(ink<.02){pixels[p]=pixels[p+1]=pixels[p+2]=pixels[p+3]=0;continue;}
                    // Fit foreground coverage and a separately darkened matte.
                    // Interior texture is immutable; only a three-pixel edge
                    // band with a matching local color mixture is unmatted.
                    for(int c=0;c<3;c++)pixels[p+c]=(byte)Math.Max(0,Math.Min(255,Math.Round((original[p+c]-backdrop*matte[c])/ink)));
                    pixels[p+3]=(byte)Math.Max(1,Math.Round(ink*original[p+3]));
                }
            }
        }
        private static BitmapSource Clean(BitmapSource source,Color key,int threshold,CancellationToken cancel,bool protectArtwork,bool cleanSpill,out bool safe,out string warning,bool trim=true)
        {
            int w=source.PixelWidth,h=source.PixelHeight;var data=LetterTransferImages.Pixels(source);var matte=Background(data,w,h);
            // Use the observed local background, preserving genuine artwork in
            // the requested key hue when the model chose a different backdrop.
            int Reflect(byte value,byte expected,int channel)=>channel==1?(expected==255?255-value:value):(expected==0?255-value:value);
            int mb=Reflect(matte[0],key.B,0),mg=Reflect(matte[1],key.G,1),mr=Reflect(matte[2],key.R,2);
            bool pureMatte=mg<=70 && Math.Min(mb,mr)-mg>=100;
            var protection=protectArtwork && pureMatte?LetterTransferMatteProtection.Create(data,w,h,matte,key,cancel,nearTolerance:cleanSpill?36:18):null;
            if(pureMatte)RemoveShadows(data,w,h,matte,cancel,protection);
            PatternFillerMatte.Remove(data,w,h,cancel,matte,false,45,protection);
            if(pureMatte)ShadowEdges(data,w,h,matte,cancel,protection);
            data=DtfRasterOperations.Correct(data,true,threshold,false,0,cancel);
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                int p=(y*w+x)*4;if(!pureMatte || protection!=null && protection[p/4])continue;
                int b=Reflect(data[p],key.B,0),g=Reflect(data[p+1],key.G,1),r=Reflect(data[p+2],key.R,2);
                if(b>=mb-75 && r>=mr-75 && g<=mg+60 && Math.Min(b,r)-g>=60)data[p+3]=0;
            }
            if(protectArtwork && cleanSpill && pureMatte)
            {
                // The protection mask separates actual enclosed fabric from
                // the reserved backdrop and its tinted fringe, including counters.
                // Remove that fringe before contour fitting so smoothing cannot
                // stretch the key color into the preserved border.
                double norm=matte.Sum(c=>(int)c*c);
                for(int y=0;y<h;y++)
                {
                    cancel.ThrowIfCancellationRequested();
                    for(int x=0;x<w;x++)
                    {
                        int at=y*w+x,p=at*4;if(data[p+3]==0 || protection[at])continue;
                        int b=Reflect(data[p],key.B,0),g=Reflect(data[p+1],key.G,1),r=Reflect(data[p+2],key.R,2);if(g>90 || Math.Min(b,r)-g<90)continue;
                        double brightness=0;for(int c=0;c<3;c++)brightness+=data[p+c]*matte[c];if(brightness<norm*.8)continue;
                        data[p+3]=0;
                    }
                }
                ShadowEdges(data,w,h,matte,cancel,protection,true);
                data=DtfRasterOperations.Correct(data,true,threshold,false,0,cancel);
            }
            int visible=0;bool edge=false;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                int p=(y*w+x)*4;if(data[p+3]==0){data[p]=data[p+1]=data[p+2]=0;continue;}
                visible++;if(x==0 || y==0 || x==w-1 || y==h-1)edge=true;
            }
            if(visible<16){safe=false;warning=null;return null;}
            safe=!edge && visible<w*h*.9;
            warning=edge?"Рисунок касается края ячейки — проверьте целостность перед принятием.":visible>=w*h*.9?"Фон мог остаться непрозрачным — проверьте ячейку перед принятием.":null;
            var result=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,data,w*4);result.Freeze();return trim?LetterTransferImages.Prepare(result,threshold,cancel):result;
        }
    }
}
