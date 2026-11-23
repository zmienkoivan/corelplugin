using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class PatternHeroFill
    {
        private const double Tolerance = .05;

        internal static List<PatternPlacement> Adjust(IReadOnlyList<PatternPlacement> items, Geometry[] silhouettes,
            PatternAsset[] motifs, PatternFillOptions options, Geometry reserve, CancellationToken cancellation)
        {
            var kept = PatternSilhouette.LeaveRoom(items, silhouettes, reserve, cancellation);
            if (kept.Count == items.Count || options.Airy) return kept;
            var retained = new HashSet<PatternPlacement>(kept);
            Refit(kept, items.Where(item => !retained.Contains(item)).ToArray(), motifs, options, reserve, cancellation);
            return kept;
        }

        internal static void FillVoids(PatternDraft draft, IReadOnlyList<PatternPlacement> baseline, CancellationToken cancellation)
        {
            var frame = draft.Frame;
            var options = draft.Options;
            if (frame.Bottle == null && options.Repeat) return;
            int[] fillers = Enumerable.Range(0, draft.Motifs.Length).Where(index => draft.Motifs[index].IsFiller).ToArray();
            if (fillers.Length == 0 || baseline.Count == 0) return;
            if(options.Airy){FillAccents(draft,fillers,cancellation);return;}

            double maximumSize = Math.Min(12, options.SizeMm * .25);
            if (maximumSize < .5) return;
            int limit = Math.Min(PatternFillLayout.MaximumElements,
                draft.Items.Count + Math.Max(1, Math.Min(32, baseline.Count / 3)));
            if (draft.Items.Count >= limit) return;

            var random = new Random(options.Seed);
            var variantRandom = new Random(unchecked(options.Seed ^ 0x4d495252));
            var groups = fillers.GroupBy(index => draft.Motifs[index].Footprint.Family)
                .Select(group => group.ToArray()).ToArray();
            var candidates = baseline.Where(item => item.Cell != null).OrderBy(_ => random.Next()).Take(160)
                .Select((item, i) => new PatternPlacement { Cell = item.Cell, Size = Math.Min(maximumSize,Longest(draft.Motifs[item.Motif],item)*.25),
                    Angle = item.Angle, Motif = PatternFillLayout.PickVariant(groups[i % groups.Length], variantRandom) }).ToArray();
            var placed = new List<PatternPlacement>(draft.Items);
            Geometry clip = frame.BottleClip;
            if (clip == null)
            {
                clip = frame.Geometry.Clone();
                clip.Transform = new MatrixTransform(clip.Transform.Value * new Matrix(1, 0, 0, -1, 0, frame.Height));
                clip.Freeze();
            }

            Refit(placed, candidates, draft.Motifs, options, draft.Reserve ?? Geometry.Empty, cancellation,
                Fits, fillers, maximumSize, limit);
            draft.Items = placed;
            LimitFillerSizes(draft);

            bool Fits(PatternPlacement placement)
            {
                var asset = draft.Motifs[placement.Motif];
                if (frame.Bottle != null)
                    return PatternBottleLayout.FitsOnRing(frame, draft.Motifs, placement, placed, options.GapMm, true);
                return clip.FillContainsWithDetail(PatternSilhouette.At(asset, placement), Tolerance,
                    ToleranceType.Absolute) == IntersectionDetail.FullyContains;
            }
        }

        internal static double Longest(PatternAsset asset,PatternPlacement item) =>
            PatternTransform.Fit(asset.Size,item).Scale*Math.Max(asset.Size.X,asset.Size.Y);

        private static void FillAccents(PatternDraft draft,int[] fillers,CancellationToken cancellation)
        {
            int main=draft.Items.Count(p=>!draft.Motifs[p.Motif].IsFiller);
            if(main==0)return;
            var placed=draft.Items.ToList();var options=draft.Options;var frame=draft.Frame;
            int count=Math.Min(32,Math.Max(1,(int)Math.Round(main*.45)));
            var random=new Random(unchecked(options.Seed^0x41495259));
            var groups=fillers.GroupBy(i=>draft.Motifs[i].Footprint.Family).Select(g=>g.ToArray()).ToArray();
            Geometry clip=frame.BottleClip;
            if(clip==null){clip=frame.Geometry.Clone();clip.Transform=new MatrixTransform(clip.Transform.Value*new Matrix(1,0,0,-1,0,frame.Height));clip.Freeze();}
            var obstacles=new List<PatternPoint[]>();
            foreach(var pose in placed)Obstacle(pose);
            for(int n=0;n<count && placed.Count<PatternFillLayout.MaximumElements;n++)
            {
                var family=groups[n%groups.Length];int index=family[random.Next(family.Length)];var asset=draft.Motifs[index];
                PatternPlacement best=null;double bestScore=0;
                for(int attempt=0;attempt<160;attempt++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var center=new PatternPoint(random.NextDouble()*frame.Width,random.NextDouble()*frame.Height);
                    double cap=FillerSizeAt(draft,center),longest=cap*(.82+random.NextDouble()*.16);
                    if(longest<.6)continue;
                    double angle=random.NextDouble()*360;
                    var rotated=PatternTransform.RotatedSize(asset.Size.X,asset.Size.Y,angle);
                    var pose=new PatternPlacement{Motif=index,Center=center,Angle=angle,Size=longest*Math.Max(rotated.X,rotated.Y)/Math.Max(asset.Size.X,asset.Size.Y)};
                    var hull=asset.Footprint.At(pose);
                    double score=obstacles.Min(p=>Distance(center,p));
                    if(score<=bestScore || score<Math.Pow(longest*.6+options.GapMm,2))continue;
                    var silhouette=PatternSilhouette.At(asset,pose);
                    if(draft.Reserve!=null && draft.Reserve.Bounds.IntersectsWith(silhouette.Bounds) && draft.Reserve.FillContainsWithDetail(silhouette,Tolerance,ToleranceType.Absolute)!=IntersectionDetail.Empty)continue;
                    bool fits=frame.Bottle!=null ? PatternBottleLayout.FitsOnRing(frame,draft.Motifs,pose,placed,options.GapMm,true) :
                        clip.FillContainsWithDetail(silhouette,Tolerance,ToleranceType.Absolute)==IntersectionDetail.FullyContains && obstacles.All(p=>PatternMotifRotation.Separated(hull,p,options.GapMm));
                    if(!fits)continue;
                    // Own a small, regular cell. Never squeeze a miniature into
                    // a residual sliver or scale it down until it becomes dirt.
                    double radius=longest*.72+options.GapMm/2;
                    pose.Cell=Enumerable.Range(0,6).Select(i=>new PatternPoint(center.X+radius*Math.Cos(i*Math.PI/3),center.Y+radius*Math.Sin(i*Math.PI/3))).ToArray();
                    best=pose;bestScore=score;
                }
                if(best==null)continue;placed.Add(best);Obstacle(best);
            }
            draft.Items=placed;LimitFillerSizes(draft);
            void Obstacle(PatternPlacement pose)
            {
                var asset=draft.Motifs[pose.Motif];obstacles.Add(asset.Footprint.At(pose));
                if(frame.Bottle!=null)foreach(int turn in new[]{-1,1})
                {var copy=PatternMotifRotation.Copy(pose);copy.Center=PatternBottleLayout.TurnPoint(frame,copy.Center,turn);copy.Angle+=frame.Bottle.RotationAt(turn*frame.Bottle.Width)-frame.Bottle.RotationAt(0);obstacles.Add(asset.Footprint.At(copy));}
            }
            double Distance(PatternPoint point,PatternPoint[] polygon)
            {
                if(PatternMotifRotation.Contains(polygon,point))return 0;
                double best=Double.PositiveInfinity;
                for(int i=0;i<polygon.Length;i++)
                {var a=polygon[i];var b=polygon[(i+1)%polygon.Length];double x=b.X-a.X,y=b.Y-a.Y,length=x*x+y*y,t=length<1e-15?0:Math.Max(0,Math.Min(1,((point.X-a.X)*x+(point.Y-a.Y)*y)/length));best=Math.Min(best,Math.Pow(point.X-a.X-t*x,2)+Math.Pow(point.Y-a.Y-t*y,2));}
                return best;
            }
        }

        internal static double FillerSizeAt(PatternDraft draft,PatternPoint point)
        {
            PatternPlacement nearest=null;double best=Double.PositiveInfinity;
            foreach(var item in draft.Items.Where(p=>!draft.Motifs[p.Motif].IsFiller))
            {
                double Distance(PatternPoint p)=>Math.Pow(p.X-point.X,2)+Math.Pow(p.Y-point.Y,2);
                double distance=Distance(item.Center);
                if(draft.Frame?.Bottle!=null)distance=Math.Min(distance,Math.Min(Distance(PatternBottleLayout.TurnPoint(draft.Frame,item.Center,-1)),Distance(PatternBottleLayout.TurnPoint(draft.Frame,item.Center,1))));
                if(distance<best){best=distance;nearest=item;}
            }
            double main=nearest==null?(draft.Hero?.Size??draft.Options.SizeMm):Longest(draft.Motifs[nearest.Motif],nearest);
            return Math.Min(12,main*.25);
        }

        internal static void LimitFillerSizes(PatternDraft draft)
        {
            for(int i=0;i<draft.Items.Count;i++)
            {
                var item=draft.Items[i];var asset=draft.Motifs[item.Motif];if(!asset.IsFiller)continue;
                double size=Longest(asset,item),cap=FillerSizeAt(draft,item.Center);
                if(size>cap && cap>0)draft.Items[i]=PatternMotifRotation.Resize(item,asset.Footprint,cap/Math.Max(asset.Size.X,asset.Size.Y));
            }
        }

        internal static void Refit(List<PatternPlacement> kept, IReadOnlyList<PatternPlacement> lost, PatternAsset[] motifs,
            PatternFillOptions options, Geometry reserve, CancellationToken cancellation, Func<PatternPlacement, bool> accepts = null,
            int[] variants = null, double maximumSize = double.PositiveInfinity, int limit = PatternFillLayout.MaximumElements)
        {
            if (lost.Count == 0) return;
            var edge = ReadBoundary(reserve);
            var alternatives = variants ?? Enumerable.Range(0, motifs.Length).Where(index => !motifs[index].IsFiller).ToArray();

            Rect nearby = Rect.Empty;
            foreach (var item in lost) nearby.Union(Bounds(item.Cell));
            nearby.Inflate(options.GapMm + Tolerance, options.GapMm + Tolerance);
            var blockers = new List<Obstacle>();
            foreach (var item in kept)
            {
                var motif = motifs[item.Motif];
                var size = PatternTransform.Fit(motif.Size, item);
                var area = new Rect(item.Center.X - size.RotatedWidth / 2, item.Center.Y - size.RotatedHeight / 2,
                    size.RotatedWidth, size.RotatedHeight);
                if (nearby.IntersectsWith(area)) blockers.Add(new Obstacle(motif.Footprint.At(item), item.Motif));
            }

            var fit = new PatternFillOptions { SizeMm = options.SizeMm,
                Mode = options.Mode == PatternFillMode.Pebbles ? PatternFillMode.Pebbles : PatternFillMode.Crocodile };
            foreach (var item in lost)
            {
                if (kept.Count >= limit) break;
                cancellation.ThrowIfCancellationRequested();
                var original = motifs[item.Motif];
                var cellBounds = Bounds(item.Cell);
                var center = new PatternPoint(item.Cell.Average(p => p.X), item.Cell.Average(p => p.Y));
                bool crossesBoundary = edge.Any(segment =>
                    Math.Max(segment.Start.X, segment.End.X) >= cellBounds.Left &&
                    Math.Min(segment.Start.X, segment.End.X) <= cellBounds.Right &&
                    Math.Max(segment.Start.Y, segment.End.Y) >= cellBounds.Top &&
                    Math.Min(segment.Start.Y, segment.End.Y) <= cellBounds.Bottom);
                if (!crossesBoundary && reserve.FillContains(new Point(center.X, center.Y), Tolerance, ToleranceType.Absolute))
                    continue;
                cellBounds.Inflate(options.GapMm + Tolerance, options.GapMm + Tolerance);
                var neighbors = blockers.Where(blocker => blocker.Bounds.IntersectsWith(cellBounds)).ToArray();
                var samples = new List<PatternPoint> { center };
                for (int i = 0; i < item.Cell.Length; i++)
                {
                    var vertex = item.Cell[i];
                    var middle = PatternPoint.Between(vertex, item.Cell[(i + 1) % item.Cell.Length], .5);
                    samples.Add(PatternPoint.Between(center, vertex, .72));
                    samples.Add(PatternPoint.Between(center, middle, .72));
                }
                var windows = new List<(List<PatternPoint> Polygon, double Area)>();
                foreach (var sample in samples)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var polygon = new List<PatternPoint>(item.Cell.Length);
                    if (crossesBoundary)
                    {
                        if (reserve.FillContains(new Point(sample.X, sample.Y), Tolerance, ToleranceType.Absolute)) continue;
                        Cut(item.Cell, sample, Closest(sample, edge), Tolerance, polygon);
                    }
                    else polygon.AddRange(item.Cell);
                    var buffer = new List<PatternPoint>(polygon.Count);
                    foreach (var neighbor in neighbors)
                    {
                        if (polygon.Count < 3) break;
                        if (Inside(sample, neighbor.Hull))
                        {
                            polygon.Clear();
                            break;
                        }
                        Cut(polygon, sample, Closest(sample, neighbor.Edges), options.GapMm + Tolerance, buffer);
                        var previous = polygon; polygon = buffer; buffer = previous;
                    }
                    double area = Area(polygon);
                    if (area > Tolerance) windows.Add((polygon, area));
                }

                fit.Angle = options.Texture ? options.Angle : item.Angle;
                var candidates = windows.OrderByDescending(window => window.Area).Take(4)
                    .Select(window => window.Polygon.ToArray()).ToArray();
                PatternPlacement best = Fit(item.Motif);
                double covered = CoveredArea(best);
                if (accepts != null && candidates.Length > 0 && (best == null || covered < Area(best.Cell) * .55))
                    foreach (int alternative in alternatives.Where(index => index != item.Motif)
                        .OrderBy(index => neighbors.Count(neighbor => ReferenceEquals(motifs[neighbor.Motif].Footprint.Family, motifs[index].Footprint.Family)))
                        .ThenBy(index => (index - item.Motif + motifs.Length) % motifs.Length).Take(4))
                    {
                        var candidate = Fit(alternative);
                        double area = CoveredArea(candidate);
                        if (area <= covered * 1.15) continue;
                        best = candidate;
                        covered = area;
                        if (covered >= Area(best.Cell) * .65) break;
                    }
                if (best == null) continue;
                kept.Add(best);
                blockers.Add(new Obstacle(motifs[best.Motif].Footprint.At(best), best.Motif));

                double CoveredArea(PatternPlacement placement)
                {
                    if (placement == null) return 0;
                    var asset = motifs[placement.Motif];
                    double scale = PatternTransform.Fit(asset.Size, placement).Scale;
                    return Area(asset.Footprint.Hull) * scale * scale;
                }

                PatternPlacement Fit(int index)
                {
                    var asset = motifs[index];
                    double minimumSize = original.IsFiller ? Math.Min(maximumSize,item.Size)*.15 :
                        PatternTransform.Fit(original.Size, item).Scale*Math.Max(original.Size.X, original.Size.Y)*.35;
                    double bestScale = minimumSize / Math.Max(asset.Size.X, asset.Size.Y);
                    PatternPlacement choice = null;
                    foreach (var polygon in candidates)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var fitted = PatternFillLayout.PlaceInCells(new List<PatternPoint[]> { polygon },
                            new[] { asset.Footprint }, fit, allowEmpty: true, fixedAngle: !options.Texture).SingleOrDefault();
                        if (fitted == null) continue;
                        double cap=asset.IsFiller?Math.Min(maximumSize,item.Size):maximumSize;
                        double longest=Longest(asset,fitted);
                        if (asset.IsFiller && longest>cap)fitted=PatternMotifRotation.Resize(fitted,asset.Footprint,cap/Math.Max(asset.Size.X,asset.Size.Y));
                        if (!asset.IsFiller && fitted.Size > maximumSize)
                        {
                            fitted.Size = maximumSize;
                            if (!asset.Footprint.At(fitted).All(point => Inside(point, polygon))) continue;
                        }
                        double scale = PatternTransform.Fit(asset.Size, fitted).Scale;
                        if (scale <= bestScale || reserve.FillContainsWithDetail(PatternSilhouette.At(asset, fitted),
                            Tolerance, ToleranceType.Absolute) != IntersectionDetail.Empty) continue;
                        fitted.Motif = index;
                        if (accepts != null && !accepts(fitted)) continue;
                        choice = fitted;
                        bestScale = scale;
                    }
                    return choice;
                }
            }
        }

        private static void Cut(IReadOnlyList<PatternPoint> polygon, PatternPoint sample, PatternPoint edge,
            double gap, List<PatternPoint> result)
        {
            double nx = edge.X - sample.X, ny = edge.Y - sample.Y;
            double length = Math.Sqrt(nx * nx + ny * ny);
            if (length <= gap)
            {
                result.Clear();
                return;
            }
            PatternFillLayout.Clip(polygon, nx, ny, nx * edge.X + ny * edge.Y - gap * length, result);
        }

        private static List<(PatternPoint Start, PatternPoint End)> ReadBoundary(Geometry reserve)
        {
            var path = reserve.GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute);
            var edges = new List<(PatternPoint, PatternPoint)>();
            foreach (var figure in path.Figures)
            {
                var first = ToPoint(figure.StartPoint);
                var previous = first;
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line) Add(line.Point);
                    else if (segment is PolyLineSegment polyline)
                        foreach (var point in polyline.Points) Add(point);
                }
                if (figure.IsClosed) edges.Add((previous, first));

                void Add(Point point)
                {
                    var next = ToPoint(point);
                    edges.Add((previous, next));
                    previous = next;
                }
            }
            return edges;

            PatternPoint ToPoint(Point point)
            {
                if (path.Transform != null) point = path.Transform.Transform(point);
                return new PatternPoint(point.X, point.Y);
            }
        }

        private static PatternPoint Closest(PatternPoint sample, IReadOnlyList<(PatternPoint Start, PatternPoint End)> edges)
        {
            double distance = double.PositiveInfinity;
            PatternPoint closest = sample;
            foreach (var edge in edges)
            {
                double dx = edge.End.X - edge.Start.X, dy = edge.End.Y - edge.Start.Y;
                double x = sample.X - edge.Start.X, y = sample.Y - edge.Start.Y;
                double length = dx * dx + dy * dy;
                if (length <= 1e-12) continue;
                double fraction = Math.Max(0, Math.Min(1, (x * dx + y * dy) / length));
                x -= fraction * dx;
                y -= fraction * dy;
                double squared = x * x + y * y;
                if (squared >= distance) continue;
                distance = squared;
                closest = new PatternPoint(sample.X - x, sample.Y - y);
            }
            return closest;
        }

        private static bool Inside(PatternPoint point, PatternPoint[] polygon)
        {
            for (int i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
                if ((b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X) < 0) return false;
            }
            return true;
        }

        private static Rect Bounds(PatternPoint[] polygon)
        {
            if (polygon == null || polygon.Length == 0) return Rect.Empty;
            var bounds = Rect.Empty;
            foreach (var point in polygon) bounds.Union(new Point(point.X, point.Y));
            return bounds;
        }

        private static double Area(IReadOnlyList<PatternPoint> polygon)
        {
            double twiceArea = 0;
            for (int i = 1; i + 1 < polygon.Count; i++)
            {
                var origin = polygon[0]; var a = polygon[i]; var b = polygon[i + 1];
                twiceArea += (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
            }
            return twiceArea / 2;
        }

        private sealed class Obstacle
        {
            internal readonly PatternPoint[] Hull;
            internal readonly Rect Bounds;
            internal readonly int Motif;
            internal readonly (PatternPoint Start, PatternPoint End)[] Edges;

            internal Obstacle(PatternPoint[] hull, int motif)
            {
                Hull = hull; Motif = motif;
                Bounds = PatternHeroFill.Bounds(hull);
                Edges = hull.Select((p, i) => (p, hull[(i + 1) % hull.Length])).ToArray();
            }
        }
    }
}
