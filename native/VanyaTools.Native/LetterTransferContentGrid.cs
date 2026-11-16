using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace VanyaTools.Native
{
    internal static class LetterTransferContentGrid
    {
        private struct Span{internal int Start,End;internal int Size=>End-Start;internal Span(int start,int end){Start=start;End=end;}}
        internal static List<Int32Rect> Find(byte[] pixels,int width,int height,byte[] matte,CancellationToken cancel)
        {
            var ink=new bool[width*height];var rows=new int[height];
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<width;x++)
                {
                    int at=y*width+x,p=at*4,b=pixels[p]-matte[0],g=pixels[p+1]-matte[1],r=pixels[p+2]-matte[2];
                    ink[at]=pixels[p+3]>=130 && b*b+g*g+r*r>55*55;if(ink[at])rows[y]++;
                }
            }
            var bands=Runs(rows,Math.Max(2,width/600));if(bands.Count==0 || bands.Count>100)return null;
            double typical=Median(bands.Select(b=>b.Size).Where(size=>size>=bands.Max(b=>b.Size)*.4));
            var merged=new List<Span>();int contentTop=0;
            foreach(var band in bands)
            {
                if(merged.Count>0)
                {
                    var previous=merged[merged.Count-1];int gap=band.Start-previous.End;
                    bool small=previous.Size<typical*.45 && band.Size>=typical*.5 && gap<typical*.4;
                    bool accent=small && FitsAbove(previous,band,ink,width);
                    if(small && !accent && merged.Count==1 && bands.Count(b=>b.Size>=typical*.5)>=2){contentTop=(previous.End+band.Start)/2;merged.RemoveAt(0);merged.Add(band);continue;}
                    if(gap<=2 || accent){merged[merged.Count-1]=new Span(previous.Start,band.End);continue;}
                }
                merged.Add(band);
            }
            if(merged.Count>16)return null;
            var boxes=new List<Int32Rect>();
            for(int row=0;row<merged.Count;row++)
            {
                cancel.ThrowIfCancellationRequested();var band=merged[row];
                int top=row==0?contentTop:(merged[row-1].End+band.Start)/2,bottom=row+1==merged.Count?height:(band.End+merged[row+1].Start)/2;
                var columns=new int[width];for(int y=band.Start;y<band.End;y++)for(int x=0;x<width;x++)if(ink[y*width+x])columns[x]++;
                var parts=Runs(columns,Math.Max(2,band.Size/200));if(parts.Count==0)continue;
                double gapLimit=parts.Count>1?Math.Min(typical*.1,Median(Enumerable.Range(1,parts.Count-1).Select(i=>parts[i].Start-parts[i-1].End))*.45):0;
                var cells=new List<Span>();
                foreach(var part in parts)
                {
                    if(cells.Count>0){var last=cells[cells.Count-1];if(part.Start-last.End<Math.Max(2,gapLimit) && part.End-last.Start<typical*1.6){cells[cells.Count-1]=new Span(last.Start,part.End);continue;}}
                    cells.Add(part);
                }
                for(int col=0;col<cells.Count;col++)
                {
                    int left=col==0?0:(cells[col-1].End+cells[col].Start)/2,right=col+1==cells.Count?width:(cells[col].End+cells[col+1].Start)/2;
                    if(right-left<8 || bottom-top<8)return null;
                    // Require genuine transparent-background gutters at every
                    // proposed cut. A guessed cell boundary must not slice ink.
                    if(left>0 && columns[left]>0 || right<width && columns[right-1]>0)return null;
                    boxes.Add(new Int32Rect(left,top,right-left,bottom-top));
                }
            }
            return boxes.Count>=1 && boxes.Count<=64?boxes:null;
        }
        private static bool FitsAbove(Span accent,Span body,bool[] ink,int width)
        {
            var covered=new bool[width];for(int y=body.Start;y<body.End;y++)for(int x=0;x<width;x++)if(ink[y*width+x])covered[x]=true;
            int total=0,outside=0;
            for(int y=accent.Start;y<accent.End;y++)for(int x=0;x<width;x++)if(ink[y*width+x]){total++;if(!covered[x])outside++;}
            return total>0 && outside<=total*.03;
        }
        private static List<Span> Runs(int[] values,int minimum)
        {
            var spans=new List<Span>();
            for(int i=0;i<values.Length;i++){if(values[i]<minimum)continue;int start=i;while(i+1<values.Length && values[i+1]>=minimum)i++;spans.Add(new Span(start,i+1));}
            return spans;
        }
        private static double Median(IEnumerable<int> values){var sorted=values.OrderBy(v=>v).ToArray();return sorted.Length==0?0:sorted[sorted.Length/2];}
    }
}
