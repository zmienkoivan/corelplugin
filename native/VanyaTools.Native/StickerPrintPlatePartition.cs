using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class StickerPrintPlatePartition
    {
        internal static IReadOnlyList<IReadOnlyList<PathFigure>> Groups(PathGeometry geometry,double trapping)
        {
            var figures=geometry.Figures.ToArray();
            // Bound the pair search for exceptionally fragmented alpha masks.
            // A single batch is always correct and retains the existing limits.
            if(Math.Abs(trapping)<1e-9 || figures.Length>2048)
                return new IReadOnlyList<PathFigure>[] { figures };
            var bounds=figures.Select(Bounds).ToArray();
            // Keep counters, nested islands and potentially meeting spreads
            // together. Different batches cannot overlap after the offset, so
            // appending their curves cannot cancel ink through even-odd fill.
            for(int i=0;i<bounds.Length;i++)bounds[i].Inflate(Math.Abs(trapping)+1e-7,Math.Abs(trapping)+1e-7);
            var parent=Enumerable.Range(0,figures.Length).ToArray();
            int Root(int index)
            {
                while(parent[index]!=index){parent[index]=parent[parent[index]];index=parent[index];}
                return index;
            }
            for(int i=0;i<figures.Length;i++)for(int j=0;j<i;j++)
                if(bounds[i].IntersectsWith(bounds[j]))parent[Root(i)]=Root(j);
            var result=new Dictionary<int,List<PathFigure>>();
            for(int i=0;i<figures.Length;i++)
            {
                int root=Root(i);
                if(!result.TryGetValue(root,out var list))result.Add(root,list=new List<PathFigure>());
                list.Add(figures[i]);
            }
            return result.Values.Cast<IReadOnlyList<PathFigure>>().ToArray();
        }
        private static Rect Bounds(PathFigure figure)
        {
            var box=new Rect(figure.StartPoint,figure.StartPoint);
            foreach(var segment in figure.Segments)
            {
                if(segment is PolyLineSegment polyline)foreach(var point in polyline.Points)box.Union(point);
                else if(segment is LineSegment line)box.Union(line.Point);
                else throw new InvalidOperationException("Не удалось подготовить контур печатной плашки.");
            }
            return box;
        }
    }
}
