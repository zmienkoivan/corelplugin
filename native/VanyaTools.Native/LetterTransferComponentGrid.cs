using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    // Fallback for sheets without dependable gutters. Label opaque components
    // after unmatting, group bodies by row and attach accents/close stems. Each
    // output copies only its own components, so overlapping bounding rectangles
    // cannot include a neighbor or truncate a descender.
    internal static class LetterTransferComponentGrid
    {
        private sealed class Part
        {
            internal int Id,Left,Top,Right,Bottom,Area;
            internal int Width=>Right-Left+1;
            internal int Height=>Bottom-Top+1;
            internal double CenterY=>(Top+Bottom)*.5;
        }
        private sealed class Glyph
        {
            internal readonly List<Part> Parts=new List<Part>();
            internal int Left=>Parts.Min(p=>p.Left);
            internal int Right=>Parts.Max(p=>p.Right);
            internal int Top=>Parts.Min(p=>p.Top);
            internal int Bottom=>Parts.Max(p=>p.Bottom);
        }
        internal static LetterTransferSheet Read(BitmapSource source,string[] requested,CancellationToken cancel)
        {
            int width=source.PixelWidth,height=source.PixelHeight;
            var data=LetterTransferImages.Pixels(source);var labels=new int[width*height];
            var queue=new List<int>();var parts=new List<Part>();int nextId=0;
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<width;x++)
                {
                    int start=y*width+x;if(labels[start]!=0 || data[start*4+3]==0)continue;
                    var part=new Part{Id=++nextId,Left=x,Right=x,Top=y,Bottom=y};queue.Clear();queue.Add(start);labels[start]=part.Id;
                    for(int cursor=0;cursor<queue.Count;cursor++)
                    {
                        if((cursor&4095)==0)cancel.ThrowIfCancellationRequested();
                        int at=queue[cursor],px=at%width,py=at/width;part.Area++;
                        part.Left=Math.Min(part.Left,px);part.Right=Math.Max(part.Right,px);part.Top=Math.Min(part.Top,py);part.Bottom=Math.Max(part.Bottom,py);
                        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                        {
                            int nx=px+dx,ny=py+dy;if(nx<0 || ny<0 || nx>=width || ny>=height)continue;
                            int n=ny*width+nx;if(labels[n]!=0 || data[n*4+3]==0)continue;labels[n]=part.Id;queue.Add(n);
                        }
                    }
                    if(part.Area>=16)parts.Add(part);
                }
            }
            if(parts.Count==0)return null;
            int tallest=parts.Max(p=>p.Height);
            var heights=parts.Where(p=>p.Height>=tallest*.4).Select(p=>p.Height).OrderBy(n=>n).ToArray();
            double typical=heights[heights.Length/2];
            if(requested.Length>=8 && typical>height*.6)return null;
            var bodies=parts.Where(p=>p.Height>=typical*.5 && p.Width>=typical*.08 && p.Area>=typical*typical*.04).OrderBy(p=>p.CenterY).ToArray();
            if(bodies.Length==0 || bodies.Length>100)return null;
            var rows=new List<List<Part>>();
            foreach(var body in bodies)
            {
                var row=rows.LastOrDefault();
                if(row==null || Math.Abs(body.CenterY-row.Average(p=>p.CenterY))>typical*.5){row=new List<Part>();rows.Add(row);}
                row.Add(body);
            }
            var glyphs=new List<Glyph>();
            foreach(var row in rows)
            {
                var ordered=row.OrderBy(p=>p.Left).ToArray();var gaps=Enumerable.Range(1,ordered.Length-1).Select(i=>ordered[i].Left-ordered[i-1].Right-1).Where(n=>n>0).OrderBy(n=>n).ToArray();
                double gapLimit=gaps.Length>0?Math.Min(typical*.12,gaps[gaps.Length/2]*.45):0;
                Glyph previous=null;
                foreach(var part in ordered)
                {
                    // Ы often has two tall disconnected stems, with a much
                    // smaller gap than the one between neighboring letters.
                    if(previous==null || part.Left-previous.Right-1>=Math.Max(2,gapLimit) || part.Right-previous.Left+1>typical*1.6 || part.Width>typical*.45)
                    {previous=new Glyph();glyphs.Add(previous);}
                    previous.Parts.Add(part);
                }
            }
            bool punctuation=requested.Any(l=>LetterTransferRules.Punctuation.Contains(l));
            var mainIds=new HashSet<int>(bodies.Select(p=>p.Id));
            foreach(var part in parts.Where(p=>!mainIds.Contains(p.Id)))
            {
                Glyph best=null;double score=Double.MaxValue;
                foreach(var glyph in glyphs)
                {
                    int dx=Math.Max(0,Math.Max(glyph.Left-part.Right-1,part.Left-glyph.Right-1));
                    int dy=Math.Max(0,Math.Max(glyph.Top-part.Bottom-1,part.Top-glyph.Bottom-1));
                    bool accent=part.Bottom<glyph.Top && dx==0 && dy<typical*.45;
                    bool lowerDot=punctuation && dx==0 && part.Top>glyph.Bottom && glyph.Right-glyph.Left<typical*.8 && dy<typical*.45;
                    if(!accent && !lowerDot && (dx>typical*.1 || dy>typical*.12))continue;
                    double distance=dx*dx+dy*dy;if(distance>=score)continue;score=distance;best=glyph;
                }
                if(best!=null){best.Parts.Add(part);mainIds.Add(part.Id);}
            }
            if(punctuation)
            {
                AddPunctuation(glyphs,parts.Where(p=>!mainIds.Contains(p.Id)).ToArray(),typical);
                // A final row may contain only the short hyphen and colon.
                // Sort by visual rows without enlarging those marks to cap height.
                var ordered=glyphs.OrderBy(g=>(g.Top+g.Bottom)*.5).ToArray();var orderedRows=new List<List<Glyph>>();
                foreach(var glyph in ordered)
                {
                    var row=orderedRows.LastOrDefault();double center=(glyph.Top+glyph.Bottom)*.5;
                    if(row==null || Math.Abs(center-row.Average(g=>(g.Top+g.Bottom)*.5))>typical*.5){row=new List<Glyph>();orderedRows.Add(row);}
                    row.Add(glyph);
                }
                glyphs=orderedRows.SelectMany(row=>row.OrderBy(g=>g.Left)).ToList();
            }
            if(glyphs.Count<1 || glyphs.Count>64 || requested.Length>1 && glyphs.Count<2)return null;
            var cells=new List<LetterTransferCell>();
            foreach(var glyph in glyphs)
            {
                cancel.ThrowIfCancellationRequested();int left=glyph.Left,top=glyph.Top,w=glyph.Right-left+1,h=glyph.Bottom-top+1;
                var ids=new HashSet<int>(glyph.Parts.Select(p=>p.Id));var pixels=new byte[w*h*4];
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                {
                    int at=(top+y)*width+left+x;if(ids.Contains(labels[at]))Buffer.BlockCopy(data,at*4,pixels,(y*w+x)*4,4);
                }
                bool edge=left==0 || top==0 || left+w==width || top+h==height;
                bool safe=!edge && w<=typical*2.2 && h<=typical*1.8;
                var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,pixels,w*4);image.Freeze();
                cells.Add(new LetterTransferCell{Image=image,Preview=image,Bounds=new Int32Rect(left,top,w,h),Safe=safe,
                    Warning=edge?"Рисунок касается края исходного листа — проверьте целостность.":safe?null:"Найдены соединённые рисунки — проверьте разделение."});
            }
            // Component grouping is evidence about shapes, not letter identity.
            // Recognition/manual review supplies labels, including duplicates.
            return new LetterTransferSheet{Cells=cells.ToArray(),Requested=requested.ToArray(),ContentLayout=true,LayoutChanged=true,
                Notice="Лист разделён по силуэтам. Найдено рисунков: "+cells.Count+", целых: "+cells.Count(c=>c.Safe)+". Проверьте обозначения; повторы сохраняются как варианты."};
        }
        private static void AddPunctuation(List<Glyph> glyphs,Part[] parts,double typical)
        {
            var used=new HashSet<int>();
            foreach(var upper in parts.OrderBy(p=>p.Top))
            {
                if(used.Contains(upper.Id) || upper.Width>typical*.4 || upper.Height>typical*.4)continue;
                var lower=parts.Where(p=>!used.Contains(p.Id) && p.Top>upper.Bottom && p.Width<=typical*.4 && p.Height<=typical*.4 &&
                    p.Width>=upper.Width*.5 && p.Width<=upper.Width*2 && p.Height>=upper.Height*.5 && p.Height<=upper.Height*2 &&
                    Math.Abs((p.Left+p.Right-upper.Left-upper.Right)*.5)<=Math.Max(3,Math.Max(p.Width,upper.Width)*.3) &&
                    p.Top-upper.Bottom<=typical*.6 && p.Bottom-upper.Top<typical).OrderBy(p=>p.Top).FirstOrDefault();
                if(lower==null)continue;
                var glyph=new Glyph();glyph.Parts.Add(upper);glyph.Parts.Add(lower);glyphs.Add(glyph);used.Add(upper.Id);used.Add(lower.Id);
            }
            foreach(var part in parts.Where(p=>!used.Contains(p.Id) && p.Width>=typical*.3 && p.Height<=typical*.3 && p.Width>=p.Height*2.5))
            {var glyph=new Glyph();glyph.Parts.Add(part);glyphs.Add(glyph);}
        }
    }
}
