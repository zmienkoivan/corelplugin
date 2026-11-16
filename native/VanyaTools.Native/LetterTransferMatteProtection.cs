using System;
using System.Threading;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class LetterTransferMatteProtection
    {
        internal static bool[] Create(byte[] pixels,int width,int height,byte[] matte,Color key,CancellationToken cancel,bool wideReservedHue=false)
        {
            int count=width*height;var candidate=new bool[count];var near=new bool[count];var protectedInk=new bool[count];var outside=new bool[count];var seen=new bool[count];var queue=new int[count];
            double norm=0;for(int c=0;c<3;c++)norm+=matte[c]*matte[c];
            int Reflect(byte value,byte expected,int channel)=>channel==1?(expected==255?255-value:value):(expected==0?255-value:value);
            int mb=Reflect(matte[0],key.B,0),mg=Reflect(matte[1],key.G,1),mr=Reflect(matte[2],key.R,2);
            double Residual(int p)
            {
                double brightness=0;for(int c=0;c<3;c++)brightness+=pixels[p+c]*matte[c];brightness/=Math.Max(1,norm);
                if(brightness<.25 || brightness>1.1)return Double.MaxValue;
                double result=0;for(int c=0;c<3;c++){double d=pixels[p+c]-brightness*matte[c];result+=d*d;}return result;
            }
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<width;x++)
                {
                    int at=y*width+x,p=at*4;if(pixels[p+3]<130)continue;int distance=0;for(int c=0;c<3;c++){int d=pixels[p+c]-matte[c];distance+=d*d;}
                    int b=Reflect(pixels[p],key.B,0),g=Reflect(pixels[p+1],key.G,1),r=Reflect(pixels[p+2],key.R,2);
                    near[at]=distance<=18*18;
                    candidate[at]=distance<=45*45 || Residual(p)<=24*24 || b>=mb-75 && r>=mr-75 && g<=mg+60 && Math.Min(b,r)-g>=60 || g<=90 && Math.Min(b,r)-g>=90 || wideReservedHue && Math.Min(b,r)-g>=50;
                }
            }
            // Find the outside before protecting enclosed material. A uniformly
            // shaded empty slot is still one connected background component.
            int head=0,tail=0;
            void Outside(int x,int y){int at=y*width+x;if(!candidate[at] || outside[at])return;outside[at]=true;queue[tail++]=at;}
            for(int x=0;x<width;x++){Outside(x,0);Outside(x,height-1);}for(int y=0;y<height;y++){Outside(0,y);Outside(width-1,y);}
            while(head<tail)
            {
                if((head&4095)==0)cancel.ThrowIfCancellationRequested();int at=queue[head++],x=at%width,y=at/width;
                if(x>0)Outside(x-1,y);if(x+1<width)Outside(x+1,y);if(y>0)Outside(x,y-1);if(y+1<height)Outside(x,y+1);
            }
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<width;x++)
                {
                    int at=y*width+x,p=at*4;if(!candidate[at] || outside[at] || near[at])continue;
                    // Chromatic or textured enclosed material is an ink seed,
                    // even when its hue is the reserved technical background.
                    bool textured=false;
                    for(int radius=1;radius<=2 && !textured;radius++)for(int direction=0;direction<4;direction++)
                    {
                        int dx=direction==1?0:radius,dy=direction==0?0:direction==3?-radius:radius;
                        int ax=x-dx,ay=y-dy,bx=x+dx,by=y+dy;if(ax<0 || ay<0 || bx<0 || by<0 || ax>=width || bx>=width || ay>=height || by>=height)continue;
                        int ia=ay*width+ax,ib=by*width+bx,a=ia*4,b=ib*4;if(!candidate[ia] || !candidate[ib] || pixels[a+3]<130 || pixels[b+3]<130)continue;
                        int dot=0;for(int c=0;c<3;c++)dot+=(pixels[a+c]-pixels[p+c])*(pixels[b+c]-pixels[p+c]);
                        if(dot>18*18){textured=true;break;}
                    }
                    // Grain oscillates on both sides of a pixel; a smooth
                    // shadow-to-ink transition does not create a texture seed.
                    protectedInk[at]=textured || Residual(p)>18*18;
                }
            }
            bool FlatCore(int at)
            {
                int x=at%width,y=at/width,p=at*4;if(!near[at] || x==0 || y==0 || x==width-1 || y==height-1)return false;
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    int n=(y+dy)*width+x+dx;if(!near[n] || protectedInk[n])return false;
                    for(int c=0;c<3;c++)if(Math.Abs(pixels[n*4+c]-pixels[p+c])>10)return false;
                }
                return true;
            }
            // A counter has a flat patch of actual background. Dark gaps inside
            // fabric have no such core and must remain opaque, not Swiss cheese.
            for(int start=0;start<count;start++)
            {
                if(seen[start] || !candidate[start] || protectedInk[start])continue;cancel.ThrowIfCancellationRequested();head=0;tail=1;queue[0]=start;seen[start]=true;bool border=false,flat=false,allNear=true;
                void Add(int next){if(seen[next] || !candidate[next] || protectedInk[next])return;seen[next]=true;queue[tail++]=next;}
                while(head<tail)
                {
                    int at=queue[head++],x=at%width,y=at/width;border|=x==0 || y==0 || x==width-1 || y==height-1;allNear&=near[at];if(!border && !flat)flat=FlatCore(at);
                    if(x>0)Add(at-1);if(x+1<width)Add(at+1);if(y>0)Add(at-width);if(y+1<height)Add(at+width);
                }
                if(!border && !flat && !(tail<=16 && allNear))for(int i=0;i<tail;i++)protectedInk[queue[i]]=true;
            }
            return protectedInk;
        }
    }
}
