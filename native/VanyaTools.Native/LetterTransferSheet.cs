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
        internal BitmapSource Image,Preview;
        internal string Letter,Warning;
        internal bool Safe;
    }
    internal sealed class LetterTransferSheet
    {
        internal LetterTransferCell[] Cells;
        internal string[] Requested;
        internal string Notice;
        internal bool LayoutChanged;
        private struct Band{internal int Start,End;internal Band(int start,int end){Start=start;End=end;}}
        internal static LetterTransferSheet Read(BitmapSource source,string[] letters,string key,int threshold,CancellationToken cancel)
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
            if(boxes.Count<Math.Max(1,letters.Length/2) || boxes.Count>64)return Fixed(source,letters,key,threshold,cancel);
            var cells=new List<LetterTransferCell>();
            foreach(var box in boxes)
            {
                cancel.ThrowIfCancellationRequested();var raw=Crop(pixels,w,box);
                var image=Clean(raw,color,threshold,cancel,out bool safe,out string warning);
                // Empty trailing slots do not shift labels of a complete sheet.
                if(image==null)continue;
                cells.Add(new LetterTransferCell{Image=image,Preview=image,Safe=safe,Warning=warning});
            }
            if(cells.Count==0)return Fixed(source,letters,key,threshold,cancel);
            bool changed=cells.Count!=letters.Length;
            for(int i=0;i<cells.Count;i++)cells[i].Letter=changed?null:letters[i];
            return new LetterTransferSheet{Cells=cells.ToArray(),Requested=letters.ToArray(),LayoutChanged=changed,Notice=changed?
                "ИИ изменил сетку: найдено ячеек "+cells.Count+", запрошено букв "+letters.Length+". Отметьте лишние ячейки и назначьте буквы по порядку либо выберите обозначения вручную; повторы можно сохранить как варианты.":null};
        }
        private static LetterTransferSheet Fixed(BitmapSource source,string[] letters,string key,int threshold,CancellationToken cancel)
        {
            var prepared=LetterTransferImages.Extract(source,letters.Length,key,cancel,threshold);var data=LetterTransferImages.Pixels(source);
            int cols=LetterTransferRules.Columns(letters.Length),rows=(letters.Length+cols-1)/cols,w=source.PixelWidth,h=source.PixelHeight;
            var cells=new LetterTransferCell[letters.Length];
            for(int n=0;n<cells.Length;n++)
            {
                int x=w*(n%cols)/cols,x1=w*(n%cols+1)/cols,y=h*(n/cols)/rows,y1=h*(n/cols+1)/rows;
                cells[n]=new LetterTransferCell{Image=prepared[n],Preview=prepared[n]??Crop(data,w,new Int32Rect(x,y,x1-x,y1-y)),Letter=letters[n],Safe=prepared[n]!=null,
                    Warning=prepared[n]!=null?null:"Не удалось безопасно отделить фон или рисунок касается границы. Показан исходный фрагмент; он не добавится в набор."};
            }
            return new LetterTransferSheet{Cells=cells,Requested=letters.ToArray()};
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
        private static BitmapSource Clean(BitmapSource source,Color key,int threshold,CancellationToken cancel,out bool safe,out string warning)
        {
            int w=source.PixelWidth,h=source.PixelHeight;var data=LetterTransferImages.Pixels(source);var matte=Background(data,w,h);
            // Use the observed local background, preserving genuine artwork in
            // the requested key hue when the model chose a different backdrop.
            PatternFillerMatte.Remove(data,w,h,cancel,matte,false,22);
            int Reflected(byte value,byte expected,int channel)=>channel==1?(expected==255?255-value:value):(expected==0?255-value:value);
            int mb=Reflected(matte[0],key.B,0),mg=Reflected(matte[1],key.G,1),mr=Reflected(matte[2],key.R,2);
            bool reserved=mg<=90 && Math.Min(mb,mr)-mg>=60;
            data=DtfRasterOperations.Correct(data,true,threshold,false,0,cancel);int visible=0;bool edge=false;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                int p=(y*w+x)*4;
                if(reserved && Math.Min(Reflected(data[p],key.B,0),Reflected(data[p+2],key.R,2))-Reflected(data[p+1],key.G,1)>=50)data[p+3]=0;
                if(data[p+3]==0){data[p]=data[p+1]=data[p+2]=0;continue;}
                visible++;if(x==0 || y==0 || x==w-1 || y==h-1)edge=true;
            }
            if(visible<16){safe=false;warning=null;return null;}
            safe=!edge && visible<w*h*.9;
            warning=edge?"Рисунок касается края ячейки — проверьте целостность перед принятием.":visible>=w*h*.9?"Фон мог остаться непрозрачным — проверьте ячейку перед принятием.":null;
            var result=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,data,w*4);result.Freeze();return LetterTransferImages.Prepare(result,threshold,cancel);
        }
    }
}
