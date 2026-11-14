using System;
using System.Threading;
using System.Windows;

namespace VanyaTools.Native
{
    internal static class PrintRestorationNativeCrop
    {
        internal static bool TryCrop(string source,string output,Int32Rect bounds,CancellationToken cancellation)
        {
            if(!NativeRasterCorrection.HasSource(source))return false;
            var original=NativeRasterFiles.Read(source+NativeRasterFiles.Suffix,source,cancellation);
            if(bounds.X<0 || bounds.Y<0 || bounds.Width<=0 || bounds.Height<=0 || bounds.X+bounds.Width>original.Width || bounds.Y+bounds.Height>original.Height)
                throw new ArgumentException("Некорректная область обрезки.");
            int width=bounds.Width,height=bounds.Height,channels=original.Channels;
            var colors=new byte[checked(width*height*channels)];var alpha=new byte[checked(width*height)];
            for(int y=0;y<height;y++)
            {
                cancellation.ThrowIfCancellationRequested();int from=(bounds.Y+y)*original.Width+bounds.X,to=y*width;
                Buffer.BlockCopy(original.Colors,from*channels,colors,to*channels,width*channels);Buffer.BlockCopy(original.Alpha,from,alpha,to,width);
            }
            NativeRasterCorrection.Save(output,new NativeRasterBuffer(width,height,original.Mode,original.DpiX,original.DpiY,colors,alpha,original.CmykOrder),cancellation);
            return true;
        }

    }
}
