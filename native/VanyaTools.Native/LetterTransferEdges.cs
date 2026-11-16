using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class LetterTransferEdges
    {
        internal static BitmapSource Trim(BitmapSource source,int pixels,int threshold,CancellationToken cancel)
        {
            if(pixels<0 || pixels>6)throw new ArgumentOutOfRangeException(nameof(pixels));
            var image=LetterTransferImages.Prepare(source,threshold,cancel);
            if(image==null)throw new InvalidDataException("В букве не осталось видимого рисунка.");
            if(pixels==0)return image;
            int w=image.PixelWidth,h=image.PixelHeight;byte[] data=LetterTransferImages.Pixels(image);var distance=new byte[w*h];
            // Distance to transparency (including the image boundary), capped to
            // the requested inset. Two passes keep 4K batch processing linear.
            for(int y=0;y<h;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<w;x++)
                {
                    int p=y*w+x;if(data[p*4+3]==0)continue;
                    int d=x==0 || y==0 || x==w-1 || y==h-1?1:pixels+2;
                    if(x>0)d=Math.Min(d,distance[p-1]+1);
                    if(y>0){d=Math.Min(d,distance[p-w]+1);if(x>0)d=Math.Min(d,distance[p-w-1]+1);if(x+1<w)d=Math.Min(d,distance[p-w+1]+1);}
                    distance[p]=(byte)d;
                }
            }
            for(int y=h-1;y>=0;y--)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=w-1;x>=0;x--)
                {
                    int p=y*w+x;if(distance[p]==0)continue;int d=distance[p];
                    if(x+1<w)d=Math.Min(d,distance[p+1]+1);
                    if(y+1<h){d=Math.Min(d,distance[p+w]+1);if(x>0)d=Math.Min(d,distance[p+w-1]+1);if(x+1<w)d=Math.Min(d,distance[p+w+1]+1);}
                    distance[p]=(byte)d;
                }
            }
            var thin=ThinComponents(data,distance,w,h,pixels,cancel);
            for(int y=0;y<h;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<w;x++)
                {
                    int p=y*w+x,d=distance[p];if(d==0 || d>pixels)continue;
                    // Preserve a genuinely thin separate stroke or accent.
                    // A narrow background ridge attached to a thick letter is
                    // part of the requested inset and must not survive as hair.
                    bool ridge=thin[p];
                    for(int dy=-1;dy<=1 && ridge;dy++)for(int dx=-1;dx<=1;dx++)
                    {int nx=x+dx,ny=y+dy;if(nx>=0 && ny>=0 && nx<w && ny<h && distance[ny*w+nx]>d){ridge=false;break;}}
                    if(!ridge){int i=p*4;data[i]=data[i+1]=data[i+2]=data[i+3]=0;}
                }
            }
            var result=BitmapSource.Create(w,h,image.DpiX,image.DpiY,PixelFormats.Bgra32,null,data,w*4);result.Freeze();
            return LetterTransferImages.Prepare(result,threshold,cancel);
        }
        private static bool[] ThinComponents(byte[] data,byte[] distance,int width,int height,int inset,CancellationToken cancel)
        {
            int count=width*height;var thin=new bool[count];var seen=new bool[count];var queue=new int[count];
            for(int start=0;start<count;start++)
            {
                if(seen[start] || data[start*4+3]==0)continue;cancel.ThrowIfCancellationRequested();
                int head=0,tail=1,peak=0;queue[0]=start;seen[start]=true;
                while(head<tail)
                {
                    int at=queue[head++],x=at%width,y=at/width;peak=Math.Max(peak,distance[at]);
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        int nx=x+dx,ny=y+dy;if(nx<0 || ny<0 || nx>=width || ny>=height)continue;int next=ny*width+nx;
                        if(seen[next] || data[next*4+3]==0)continue;seen[next]=true;queue[tail++]=next;
                    }
                }
                if(peak<=inset+1)for(int i=0;i<tail;i++)thin[queue[i]]=true;
            }
            return thin;
        }
        internal static BitmapSource Process(BitmapSource source,int pixels,int threshold,bool smooth,CancellationToken cancel)
        {
            var image=Trim(source,pixels,threshold,cancel);if(!smooth)return image;
            int w=image.PixelWidth,h=image.PixelHeight;var input=LetterTransferImages.Pixels(image);var output=new byte[input.Length];var weights=new[]{1,2,1};int visible=0;
            for(int y=0;y<h;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<w;x++)
                {
                    int alpha=0,b=0,g=0,r=0;
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        int nx=x+dx,ny=y+dy;if(nx<0 || ny<0 || nx>=w || ny>=h)continue;int i=(ny*w+nx)*4;
                        if(input[i+3]==0)continue;int weight=weights[dx+1]*weights[dy+1];alpha+=weight;
                        b+=input[i]*weight;g+=input[i+1]*weight;r+=input[i+2]*weight;
                    }
                    int p=(y*w+x)*4;
                    bool Ink(int nx,int ny)=>nx>=0 && ny>=0 && nx<w && ny<h && input[(ny*w+nx)*4+3]>0;
                    bool thin=false;
                    if(alpha<9 && input[p+3]>0)
                    {
                        for(int ay=-1;ay<=1 && !thin;ay++)for(int ax=-1;ax<=1 && !thin;ax++)
                        {
                            if(ax==0 && ay==0 || !Ink(x+ax,y+ay))continue;
                            for(int by=-1;by<=1 && !thin;by++)for(int bx=-1;bx<=1;bx++)
                                if(ax*bx+ay*by<0 && Ink(x+bx,y+by)){thin=true;break;}
                        }
                    }
                    if(alpha<9 && !thin)continue;visible++;
                    // Smooth only the mask. Existing artwork colors and texture
                    // are untouched; new corner pixels use opaque ink neighbors.
                    output[p]=input[p+3]>0?input[p]:(byte)(b/alpha);output[p+1]=input[p+3]>0?input[p+1]:(byte)(g/alpha);output[p+2]=input[p+3]>0?input[p+2]:(byte)(r/alpha);output[p+3]=255;
                }
            }
            if(visible==0)return image;
            RemoveSpecks(output,w,h,cancel);
            var result=BitmapSource.Create(w,h,image.DpiX,image.DpiY,PixelFormats.Bgra32,null,output,w*4);result.Freeze();return LetterTransferImages.Prepare(result,threshold,cancel);
        }
        private static void RemoveSpecks(byte[] data,int width,int height,CancellationToken cancel)
        {
            int count=width*height;var seen=new bool[count];var queue=new int[count];var tiny=new List<int>();int biggest=0;int[] largest=null;
            for(int start=0;start<count;start++)
            {
                if(seen[start] || data[start*4+3]==0)continue;cancel.ThrowIfCancellationRequested();
                int head=0,tail=1;queue[0]=start;seen[start]=true;
                while(head<tail)
                {
                    int at=queue[head++],x=at%width,y=at/width;
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        int nx=x+dx,ny=y+dy;if(nx<0 || ny<0 || nx>=width || ny>=height)continue;int next=ny*width+nx;
                        if(seen[next] || data[next*4+3]==0)continue;seen[next]=true;queue[tail++]=next;
                    }
                }
                if(tail<=4)for(int i=0;i<tail;i++)tiny.Add(queue[i]);
                if(tail>biggest){biggest=tail;if(tail<=4){largest=new int[tail];Array.Copy(queue,largest,tail);}else largest=null;}
            }
            foreach(int pixel in tiny)
            {
                if(biggest<=4 && largest!=null && Array.IndexOf(largest,pixel)>=0)continue;
                int p=pixel*4;data[p]=data[p+1]=data[p+2]=data[p+3]=0;
            }
        }
        internal static void Preview(LetterTransferSheet sheet,int pixels,int threshold,CancellationToken cancel,bool smooth=false)
        {
            foreach(var cell in sheet.Cells)if(cell.Image!=null)
            {cell.Original=cell.Image;cell.Image=Process(cell.Original,pixels,threshold,smooth,cancel);cell.Preview=cell.Image;}
        }
        internal static int Reclean(string directory,LetterTransferSet set,CancellationToken cancel,Action<string,LetterTransferSet> save=null)
        {
            LetterTransferRules.Validate(set);var original=set.Glyphs;var updated=new List<LetterTransferGlyph>();var files=new List<string>();
            try
            {
                foreach(var glyph in original)
                {
                    cancel.ThrowIfCancellationRequested();string source=String.IsNullOrEmpty(glyph.OriginalFile)?glyph.File:glyph.OriginalFile;
                    var image=Process(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,source)),set.EdgeTrimPx,set.AlphaThreshold,set.SmoothEdges,cancel);
                    image=LetterTransferImages.Physical(set,glyph.Letter,image);
                    var next=new LetterTransferGlyph{Id=glyph.Id,Letter=glyph.Letter,Source=glyph.Source,OriginalFile=source,EdgeTrimPx=set.EdgeTrimPx,Smoothed=set.SmoothEdges,File="glyphs/"+glyph.Id+"-edge-"+Guid.NewGuid().ToString("N")+".png",Width=image.PixelWidth,Height=image.PixelHeight,BodyHeight=LetterTransferImages.BodyHeight(glyph.Letter,image)};
                    string output=LetterTransferStore.Resolve(directory,next.File);files.Add(output);LetterTransferImages.Save(image,output);updated.Add(next);
                }
                cancel.ThrowIfCancellationRequested();set.Glyphs=updated;
                (save??LetterTransferStore.Save)(directory,set);return updated.Count;
            }
            catch
            {
                set.Glyphs=original;
                foreach(string file in files)try{File.Delete(file);}catch(Exception error){Log.Error("Could not clean uncommitted letter edge output.",error);}
                throw;
            }
        }
    }
}
