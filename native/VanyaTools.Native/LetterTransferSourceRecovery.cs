using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace VanyaTools.Native
{
    internal static class LetterTransferSourceRecovery
    {
        private sealed class Source
        {
            internal LetterTransferJob Job;
            internal readonly Dictionary<Int32Rect,LetterTransferCell> Cells=new Dictionary<Int32Rect,LetterTransferCell>();
            internal readonly Dictionary<string,List<Int32Rect>> Fingerprints=new Dictionary<string,List<Int32Rect>>();
            internal readonly HashSet<Int32Rect> Used=new HashSet<Int32Rect>();
        }
        private static Source Read(string directory,LetterTransferJob job,int threshold,CancellationToken cancel)
        {
            var image=LetterTransferImages.Load(LetterTransferStore.Resolve(directory,job.Output));
            var current=LetterTransferSheet.Read(image,job.Letters,job.Key,threshold,cancel);
            var legacy=LetterTransferSheet.ReadLegacy(image,job.Letters,job.Key,threshold,cancel);
            var source=new Source{Job=job};
            foreach(var cell in current.Cells.Where(c=>c.Image!=null))source.Cells[cell.Bounds]=cell;
            foreach(var cell in legacy.Cells.Concat(current.Cells).Where(c=>c.Image!=null))
            {
                string fingerprint=LetterTransferImages.Fingerprint(cell.Image);
                if(!source.Fingerprints.TryGetValue(fingerprint,out var rectangles)){rectangles=new List<Int32Rect>();source.Fingerprints.Add(fingerprint,rectangles);}
                if(!rectangles.Contains(cell.Bounds))rectangles.Add(cell.Bounds);
            }
            return source;
        }
        private static LetterTransferJob Job(LetterTransferSet set,LetterTransferGlyph glyph)
        {
            if(!String.IsNullOrEmpty(glyph.SourceJobId))return set.Jobs.FirstOrDefault(j=>j.Id==glyph.SourceJobId);
            const string prefix="ИИ · ";if(glyph.Source==null || !glyph.Source.StartsWith(prefix,StringComparison.Ordinal))return null;
            string shortId=glyph.Source.Substring(prefix.Length).Trim();if(shortId.Length!=8)return null;
            var matches=set.Jobs.Where(j=>j.Id.StartsWith(shortId,StringComparison.Ordinal)).ToArray();return matches.Length==1?matches[0]:null;
        }
        internal static int Rebuild(string directory,LetterTransferSet set,CancellationToken cancel,Action<string,LetterTransferSet> save=null)
        {
            LetterTransferRules.Validate(set);var previous=set.Glyphs;var updated=new List<LetterTransferGlyph>();var created=new List<string>();var sources=new Dictionary<string,Source>();int rebuilt=0;
            try
            {
                foreach(var glyph in previous)
                {
                    cancel.ThrowIfCancellationRequested();var job=Job(set,glyph);
                    if(job==null || !LetterTransferAi.HasResult(directory,job)){updated.Add(glyph);continue;}
                    if(!sources.TryGetValue(job.Id,out var source)){source=Read(directory,job,set.AlphaThreshold,cancel);sources.Add(job.Id,source);}
                    Int32Rect? selected=null;
                    if(glyph.SourceCell!=null && glyph.SourceJobId==job.Id)
                    {
                        var box=new Int32Rect(glyph.SourceCell[0],glyph.SourceCell[1],glyph.SourceCell[2],glyph.SourceCell[3]);
                        if(source.Cells.ContainsKey(box) && !source.Used.Contains(box))selected=box;
                    }
                    if(selected==null)
                    {
                        string original=LetterTransferStore.Resolve(directory,String.IsNullOrEmpty(glyph.OriginalFile)?glyph.File:glyph.OriginalFile);
                        if(File.Exists(original))
                        {
                            string fingerprint=LetterTransferImages.Fingerprint(LetterTransferImages.Load(original));
                            if(source.Fingerprints.TryGetValue(fingerprint,out var boxes))foreach(var box in boxes)if(!source.Used.Contains(box) && source.Cells.ContainsKey(box)){selected=box;break;}
                        }
                    }
                    if(selected==null || !source.Cells[selected.Value].Safe){updated.Add(glyph);continue;}
                    source.Used.Add(selected.Value);var cell=source.Cells[selected.Value];
                    var image=LetterTransferImages.Physical(set,glyph.Letter,LetterTransferEdges.Finish(cell.Image,set,glyph.Letter,cancel));string suffix=Guid.NewGuid().ToString("N");
                    var next=new LetterTransferGlyph{Id=glyph.Id,Letter=glyph.Letter,Source=glyph.Source,SourceJobId=job.Id,SourceCell=new[]{cell.Bounds.X,cell.Bounds.Y,cell.Bounds.Width,cell.Bounds.Height},OriginalFile="glyphs/"+glyph.Id+"-restored-"+suffix+"-source.png",File="glyphs/"+glyph.Id+"-restored-"+suffix+".png",Width=image.PixelWidth,Height=image.PixelHeight,BodyHeight=LetterTransferImages.BodyHeight(glyph.Letter,image),EdgeTrimPx=set.EdgeTrimPx,Smoothed=set.SmoothEdges,SharpContour=set.SharpContour,ContourSmoothPx=set.SharpContour?set.ContourSmoothPx:0};
                    string raw=LetterTransferStore.Resolve(directory,next.OriginalFile),output=LetterTransferStore.Resolve(directory,next.File);created.Add(raw);LetterTransferImages.Save(cell.Image,raw);created.Add(output);LetterTransferImages.Save(image,output);updated.Add(next);rebuilt++;
                }
                cancel.ThrowIfCancellationRequested();if(rebuilt==0)return 0;set.Glyphs=updated;(save??LetterTransferStore.Save)(directory,set);return rebuilt;
            }
            catch
            {
                set.Glyphs=previous;foreach(string file in created)try{File.Delete(file);}catch(Exception error){Log.Error("Could not remove an uncommitted restored letter.",error);}throw;
            }
        }
    }
}
