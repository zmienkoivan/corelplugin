using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static partial class PatternBottleLayout
    {
        internal static void CloseSeam(PatternDraft draft, PatternAsset hero, PatternEditorSettings settings, CancellationToken cancellation)
        {
            if (draft.Items.Count == 0) return;
            draft.Items = new SeamPacking(draft, hero, settings, cancellation).Pack();
        }

        private sealed class SeamPacking
        {
            private readonly PatternDraft _draft;
            private readonly PatternBottle _bottle;
            private readonly SeamSide _left, _right;
            private readonly bool _whole;
            private readonly CancellationToken _cancellation;
            private readonly double _gap, _heroGap, _size, _step, _band, _referenceSize, _minimumSize;
            private readonly int[] _coverage;
            private readonly List<SeamItem> _items;
            private readonly SeamItem _hero;
            private readonly PatternPoint _heroSize;
            private readonly List<SeamCut> _cuts = new List<SeamCut>();

            internal SeamPacking(PatternDraft draft, PatternAsset hero, PatternEditorSettings settings, CancellationToken cancellation)
            {
                _draft = draft; _bottle = draft.Frame.Bottle; _whole = settings.BottleWhole; _cancellation = cancellation;
                _gap = draft.Options.GapMm; _heroGap = Math.Max(_gap, settings.HeroGap);
                _size = Math.Max(.5, Math.Min(draft.Options.SizeMm, _bottle.Width * .45));
                _band = Math.Max(.25, _gap / 2 + _size * .10);
                _coverage = new int[Math.Max(1, Math.Min(320, (int)Math.Ceiling(_bottle.Slant / Math.Max(.3, _size / 12))))];
                _step = _bottle.Slant / _coverage.Length;
                _left = new SeamSide(draft.Frame, 0, 1);
                _right = new SeamSide(draft.Frame, _bottle.Width, -1);
                _items = draft.Items.Select(item => Make(draft.Motifs[item.Motif].Footprint, item)).ToList();
                var regular = _items.Where(item => !_draft.Motifs[item.Flat.Item.Motif].IsFiller).ToArray();
                var interior = regular.Where(item => item.Distance > _band + _size * .6).ToArray();
                var sizes = (interior.Length >= 4 ? interior : regular).Select(MotifSize).OrderBy(size => size).ToArray();
                _referenceSize = sizes.Length == 0 ? _size : sizes[sizes.Length / 2];
                double ratio = draft.Options.Texture ? .75 : Math.Min(.75,
                    draft.Options.MinScale / Math.Max(draft.Options.MinScale, draft.Options.MaxScale));
                _minimumSize = Math.Max(.5, _referenceSize * ratio);
                foreach (var item in _items) Count(item, 1);
                if (draft.Hero != null)
                {
                    _heroSize = hero.Footprint.Size;
                    _hero = Make(hero.Footprint, draft.Hero);
                    Count(_hero, 1);
                }
            }

            internal List<PatternPlacement> Pack()
            {
                int[] edgeItems = Enumerable.Range(0, _items.Count)
                    .Where(i => _items[i].Distance < _band + _size * .6)
                    .OrderBy(i => _items[i].Distance).ToArray();
                MatchCuts(edgeItems);
                foreach (int index in edgeItems)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    PullCloser(index);
                }
                for (int i = _items.Count - 1; i >= 0; i--)
                    if (SmallEdge(_items[i]))
                    {
                        Count(_items[i], -1);
                        _items.RemoveAt(i);
                    }
                FillHoles();
                SetCutOutline();
                return _items.Select(item => item.Flat.Item).ToList();
            }

            private double MotifSize(SeamItem entry)
            {
                var item = entry.Flat.Item;
                var size = _draft.Motifs[item.Motif].Size;
                return PatternTransform.Fit(size, item).Scale * Math.Max(size.X, size.Y);
            }

            private bool SmallEdge(SeamItem entry) =>
                !_draft.Motifs[entry.Flat.Item.Motif].IsFiller &&
                entry.Distance < _band + _size * .6 && MotifSize(entry) < _minimumSize - Tolerance;

            private void PullCloser(int index)
            {
                var original = _items[index];
                var item = original.Flat.Item;
                var footprint = _draft.Motifs[item.Motif].Footprint;
                double travel = ExtentDistance(footprint, item, original.Side) - EdgeInset;
                if (travel <= Tolerance) return;
                travel = Math.Min(travel, _size * .65);
                Count(original, -1);
                var best = original;
                int bestScore = Score(original);
                foreach (double fraction in new[] { 1d, .75, .5, .25 })
                {
                    foreach (double along in new[] { 0, -_size * .16, _size * .16 })
                    {
                        var candidate = new PatternPlacement { Motif = item.Motif, Angle = item.Angle, Size = item.Size,
                            Cell = item.Cell, CrossesBottleSeam = item.CrossesBottleSeam,
                            Center = original.Side.Move(item.Center, -travel * fraction, along) };
                        var next = Make(footprint, candidate);
                        int score = Score(next);
                        if (score < bestScore || score == bestScore && next.Distance >= best.Distance - Tolerance ||
                            !Fits(footprint, next, index, _whole)) continue;
                        best = next;
                        bestScore = score;
                    }
                }
                _items[index] = best;
                Count(best, 1);
            }

            private void MatchCuts(IEnumerable<int> edgeItems)
            {
                double rightAngle = _bottle.RotationAt(_bottle.Width), leftAngle = _bottle.RotationAt(0);
                // Leave a straight seam on very wide sectors whose two flat edges
                // would nearly touch each other around the tip of the cone.
                if (Math.Abs(rightAngle - leftAngle) > 300) return;
                foreach (int index in edgeItems.Where(i => _items[i].Flat.Item.CrossesBottleSeam || SmallEdge(_items[i]))
                    .OrderBy(i => _items[i].Joined.Item.Center.Y))
                {
                    _cancellation.ThrowIfCancellationRequested();
                    var original = _items[index];
                    var item = original.Flat.Item;
                    var footprint = _draft.Motifs[item.Motif].Footprint;
                    double angle = item.Angle + (original.Side == _right ? leftAngle - rightAngle : 0);
                    var rotated = PatternTransform.RotatedSize(footprint.Size.X, footprint.Size.Y, angle);
                    double scale = PatternTransform.Fit(footprint.Size, item).Scale;
                    if (SmallEdge(original))
                        scale = _referenceSize * .9 / Math.Max(footprint.Size.X, footprint.Size.Y);
                    var crossing = new PatternPlacement { Motif = item.Motif, Angle = angle, Cell = item.Cell,
                        Size = Math.Max(rotated.X, rotated.Y) * scale,
                        Center = _left.Move(_left.Origin, 0, original.Joined.Item.Center.Y) };
                    double width = Bounds(new PatternFootprint(footprint.Size).At(crossing).Select(_left.Project).ToArray()).Width;
                    double maxDepth = Math.Min(Math.Min(width * .32, _size * .28), _bottle.Width * .045);
                    var center = crossing.Center;
                    double inset = ExtentDistance(footprint, crossing, _left);
                    foreach (double depth in new[] { maxDepth, maxDepth * .65, maxDepth * .4 })
                    {
                        if (depth < .25) break;
                        crossing.Center = _left.Move(center, -depth - inset, 0);
                        var span = Bounds(new PatternFootprint(footprint.Size).At(crossing).Select(_left.Project).ToArray());
                        var cut = new SeamCut(span.Top - .2, span.Bottom + .2, depth + .2);
                        if (cut.Start < .2 || cut.End > _bottle.Slant - .2 ||
                            _cuts.Any(other => cut.Start < other.End + .25 && cut.End + .25 > other.Start)) continue;
                        var candidate = Make(footprint, crossing);
                        if (!ReserveRight(cut, candidate, footprint, index)) continue;
                        Count(original, -1);
                        _items[index] = candidate;
                        Count(candidate, 1);
                        break;
                    }
                }
            }

            private bool ReserveRight(SeamCut cut, SeamItem crossing, PatternFootprint footprint, int skip)
            {
                if (_hero != null && BlocksCut(new PatternFootprint(_heroSize).At(_hero.Flat.Item), cut))
                    return false;
                var moved = new List<(int Index, SeamItem Original)>();
                var previousCrossing = _items[skip];
                _items[skip] = crossing;
                _cuts.Add(cut);
                for (int i = 0; i < _items.Count; i++)
                {
                    if (i == skip) continue;
                    var other = _items[i];
                    var item = other.Flat.Item;
                    var asset = _draft.Motifs[item.Motif].Footprint;
                    var size = PatternTransform.Fit(asset.Size, item);
                    double reach = Math.Max(size.RotatedWidth, size.RotatedHeight);
                    if (other.Joined == null && !MayTouchCut(item, reach, cut)) continue;
                    if (AcrossSeam(crossing, other, _gap) && !BlocksCut(new PatternFootprint(asset.Size).At(item), cut)) continue;
                    if (other.Side != _right || !MoveInward(i, other, asset)) return RollBack();
                }
                if (!Fits(footprint, crossing, skip, true, crossesSeam: true)) return RollBack();
                return true;

                bool MoveInward(int index, SeamItem original, PatternFootprint asset)
                {
                    var item = original.Flat.Item;
                    double inset = ExtentDistance(asset, item, _right);
                    double offset = Math.Max(Tolerance, Math.Max(cut.Depth, _gap - crossing.Distance) - inset + EdgeInset);
                    if (offset > _size * .8) return false;
                    foreach (double along in new[] { 0, -_size * .18, _size * .18 })
                    {
                        var placement = new PatternPlacement { Motif = item.Motif, Angle = item.Angle, Size = item.Size,
                            Cell = item.Cell, CrossesBottleSeam = item.CrossesBottleSeam,
                            Center = _right.Move(item.Center, offset, along) };
                        var next = Make(asset, placement);
                        if (!AcrossSeam(crossing, next, _gap) || !Fits(asset, next, index, _whole)) continue;
                        moved.Add((index, original));
                        Count(original, -1);
                        _items[index] = next;
                        Count(next, 1);
                        return true;
                    }
                    return false;
                }

                bool RollBack()
                {
                    _items[skip] = previousCrossing;
                    _cuts.RemoveAt(_cuts.Count - 1);
                    foreach (var entry in moved)
                    {
                        Count(_items[entry.Index], -1);
                        _items[entry.Index] = entry.Original;
                        Count(entry.Original, 1);
                    }
                    return false;
                }
            }

            private bool MayTouchCut(PatternPlacement item, double reach, SeamCut cut)
            {
                var center = _right.Project(item.Center);
                return center.X - reach <= 0 && center.X + reach >= -cut.Depth &&
                    center.Y - reach <= cut.End && center.Y + reach >= cut.Start;
            }

            private bool BlocksCut(PatternPoint[] extent, SeamCut cut) =>
                cut.Bounds.IntersectsWith(Bounds(extent.Select(_right.Project).ToArray()));

            private void SetCutOutline()
            {
                if (_cuts.Count == 0) return;
                var contour = _cuts.OrderBy(cut => cut.Start).SelectMany(cut => new[]
                {
                    new PatternPoint(0, cut.Start), new PatternPoint(-cut.Depth, cut.Top),
                    new PatternPoint(-cut.Depth, cut.Bottom), new PatternPoint(0, cut.End)
                }).ToArray();
                _draft.BottleCutEdges = new[]
                {
                    contour.Select(point => _left.Move(_left.Origin, point.X, point.Y)).ToArray(),
                    contour.Select(point => _right.Move(_right.Origin, -point.X, point.Y)).ToArray()
                };
                var frame = _draft.Frame;
                var view = TraceOutline(_bottle, CutOutline(_draft));
                view.Transform = new MatrixTransform(1, 0, 0, -1, -frame.BottleOrigin.X, frame.BottleOrigin.Y + frame.Height);
                view.Freeze();
                _draft.BottleCutView = view;
            }

            private void FillHoles()
            {
                int limit = Math.Min(PatternFillLayout.MaximumElements, _items.Count + Math.Min(28,
                    Math.Max(2, (int)Math.Ceiling(_bottle.Slant / _size) * 2)));
                int minimum = Math.Max(2, (int)Math.Ceiling(Math.Max(1.0, _size * .16) / _step));
                var skipped = new bool[_coverage.Length];
                while (_items.Count < limit)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    int start = 0, length = 0;
                    for (int i = 0; i < skipped.Length;)
                    {
                        if (_coverage[i] != 0 || skipped[i]) { i++; continue; }
                        int end = i + 1;
                        while (end < skipped.Length && _coverage[end] == 0 && !skipped[end]) end++;
                        if (end - i > length) { start = i; length = end - i; }
                        i = end;
                    }
                    if (length < minimum) break;

                    double v = (start + length / 2d) * _step;
                    var best = FillAt(v, start, start + length);
                    if (best == null)
                    {
                        for (int i = start; i < start + length; i++) skipped[i] = true;
                        continue;
                    }
                    _items.Add(best);
                    Count(best, 1);
                }
            }

            private SeamItem FillAt(double v, int start, int end)
            {
                var nearest = _items.Where(item => item.Distance < _band + _size &&
                    Math.Abs(item.Joined.Item.Center.Y - v) < _size).ToArray();
                int[] variants = Enumerable.Range(0, _draft.Motifs.Length)
                    .Where(i => !_draft.Motifs[i].IsFiller)
                    .OrderBy(i => nearest.Count(item => item.Flat.Item.Motif == i))
                    .ThenBy(i => (i + start) % _draft.Motifs.Length).Take(8).ToArray();
                bool leftFirst = nearest.Count(item => item.Side == _left) <= nearest.Length / 2;
                var sides = leftFirst ? new[] { _left, _right } : new[] { _right, _left };
                double room = (end - start) * _step;
                double alongStep = Math.Min(room * .24, _size * .22);
                double[] along = { v, v - alongStep, v + alongStep };
                SeamItem best = null;
                int bestScore = 0;
                foreach (double size in new[] { 1d, .9, .8 }.Select(factor => Math.Max(_minimumSize, _referenceSize * factor)))
                {
                    foreach (var side in sides)
                    foreach (int motif in variants)
                    {
                        _cancellation.ThrowIfCancellationRequested();
                        var asset = _draft.Motifs[motif];
                        double baseAngle = _draft.Options.Angle + _bottle.RotationAt(side.U);
                        foreach (double rotation in new[] { 0d, 45d, -45d, 90d })
                        {
                            double angle = baseAngle + rotation;
                            var rotated = PatternTransform.RotatedSize(asset.Size.X, asset.Size.Y, angle);
                            var item = new PatternPlacement { Motif = motif, Angle = angle,
                                Size = size * Math.Max(rotated.X, rotated.Y) / Math.Max(asset.Size.X, asset.Size.Y),
                                Center = side.Origin };
                            double inward = EdgeInset - ExtentDistance(asset.Footprint, item, side);
                            foreach (double height in along)
                            foreach (double inset in new[] { 0d, _band * .55 })
                            {
                                var placed = new PatternPlacement { Motif = motif, Angle = item.Angle,
                                    Size = item.Size, Center = side.Move(side.Origin, inward + inset, height) };
                                var entry = Make(asset.Footprint, placed);
                                int score = Score(entry, start, end);
                                if (score <= bestScore || !Fits(asset.Footprint, entry, -1, true)) continue;
                                best = entry;
                                bestScore = score;
                                if (bestScore >= end - start) return best;
                            }
                        }
                    }
                    if (best != null && bestScore * _step >= room * .65) break;
                }
                return best;
            }

            private bool Fits(PatternFootprint footprint, SeamItem item, int skip, bool whole, bool crossesSeam = false)
            {
                var candidate = item.Flat;
                var center = _bottle.Project(new PatternPoint(candidate.Item.Center.X + _draft.Frame.BottleOrigin.X,
                    candidate.Item.Center.Y + _draft.Frame.BottleOrigin.Y));
                var extent = new PatternFootprint(footprint.Size).At(candidate.Item);
                double reach = extent.Max(point => Distance(point, candidate.Item.Center));
                if ((!crossesSeam && !ClearsSides(_draft.Frame, extent, center.X, center.Y, reach, EdgeInset)) ||
                    (whole && !_bottle.InsideRims(candidate.Hull, _draft.Frame.BottleOrigin, EdgeInset / 2))) return false;
                if (_hero != null && !AcrossSeam(item, _hero, _heroGap)) return false;
                if (_cuts.Any(cut => BlocksCut(extent, cut))) return false;
                if (_draft.Reserve != null && _draft.Reserve.Bounds.IntersectsWith(candidate.Bounds) &&
                    _draft.Reserve.FillContainsWithDetail(PatternSilhouette.At(_draft.Motifs[candidate.Item.Motif], candidate.Item),
                        Tolerance, ToleranceType.Absolute) != IntersectionDetail.Empty) return false;
                for (int i = 0; i < _items.Count; i++)
                {
                    if (i == skip) continue;
                    var other = _items[i];
                    if (!Separated(candidate, other.Flat, _gap)) return false;
                    if (!AcrossSeam(item, other, _gap)) return false;
                }
                return Relation(_draft.Frame, candidate.Hull) != IntersectionDetail.Empty;
            }

            private static bool AcrossSeam(SeamItem item, SeamItem other, double gap) =>
                item.Side == other.Side || item.Distance + other.Distance >= gap + Tolerance ||
                Separated(item.Joined, other.Joined, gap);

            private SeamItem Make(PatternFootprint footprint, PatternPlacement item)
            {
                var flat = new Pose(item, footprint.At(item));
                var center = _bottle.Project(new PatternPoint(item.Center.X + _draft.Frame.BottleOrigin.X,
                    item.Center.Y + _draft.Frame.BottleOrigin.Y));
                var side = center.X <= _bottle.Width / 2 ? _left : _right;
                double edgeReach = flat.Hull.Max(point => Distance(point, item.Center)) +
                    Math.Max(_heroGap, _band + _size * .6);
                if (Math.Abs(side.U - center.X) > _bottle.SeamReach(edgeReach, center.Y))
                    return new SeamItem(flat, null, side, double.PositiveInfinity, 0, 0);
                var hull = flat.Hull.Select(side.Project).ToArray();
                var joined = new Pose(new PatternPlacement { Center = side.Project(item.Center) }, hull);
                double distance = side.Sign > 0 ? joined.Bounds.Left : -joined.Bounds.Right;
                double low = double.PositiveInfinity, high = double.NegativeInfinity;
                for (int i = 0; i < hull.Length; i++)
                {
                    var point = hull[i]; var next = hull[(i + 1) % hull.Length];
                    double a = point.X * side.Sign, b = next.X * side.Sign;
                    if (a <= _band) Include(point.Y);
                    if ((a <= _band) != (b <= _band)) Include(point.Y + (next.Y - point.Y) * (_band - a) / (b - a));
                }
                int first = double.IsPositiveInfinity(low) ? 0 : Math.Max(0, (int)Math.Ceiling(low / _step - .5));
                int last = double.IsNegativeInfinity(high) ? 0 : Math.Min(_coverage.Length, (int)Math.Floor(high / _step + .5));
                return new SeamItem(flat, joined, side, distance, first, last);

                void Include(double y) { low = Math.Min(low, y); high = Math.Max(high, y); }
            }

            private static double ExtentDistance(PatternFootprint footprint, PatternPlacement item, SeamSide side) =>
                new PatternFootprint(footprint.Size).At(item).Min(point => side.Sign * side.Project(point).X);

            private int Score(SeamItem item, int start = 0, int end = int.MaxValue)
            {
                int count = 0;
                for (int i = Math.Max(start, item.First); i < Math.Min(end, item.Last); i++)
                    if (_coverage[i] == 0) count++;
                return count;
            }

            private void Count(SeamItem item, int delta)
            {
                for (int i = item.First; i < item.Last; i++) _coverage[i] += delta;
            }
        }

        // Each edge becomes the same vertical line after wrapping. The artwork
        // on its two sides can then be compared in one coordinate system.
        private sealed class SeamSide
        {
            internal readonly double U, Sign;
            internal readonly PatternPoint Origin;
            private readonly double _tx, _ty;

            internal SeamSide(PatternFrame frame, double u, double sign)
            {
                U = u; Sign = sign;
                Origin = OnFrame(frame, u, 0);
                var end = OnFrame(frame, u, frame.Bottle.Slant);
                _tx = (end.X - Origin.X) / frame.Bottle.Slant;
                _ty = (end.Y - Origin.Y) / frame.Bottle.Slant;
            }

            internal PatternPoint Project(PatternPoint point)
            {
                double x = point.X - Origin.X, y = point.Y - Origin.Y;
                return new PatternPoint(_ty * x - _tx * y, _tx * x + _ty * y);
            }

            internal PatternPoint Move(PatternPoint point, double inward, double along) =>
                new PatternPoint(point.X + _ty * Sign * inward + _tx * along,
                    point.Y - _tx * Sign * inward + _ty * along);
        }

        private readonly struct SeamCut
        {
            internal readonly double Top, Bottom, Depth;
            internal double Start => Top - Math.Min(1.2, Depth * .4);
            internal double End => Bottom + Math.Min(1.2, Depth * .4);
            internal Rect Bounds => new Rect(-Depth, Start, Depth, End - Start);

            internal SeamCut(double top, double bottom, double depth)
            { Top = top; Bottom = bottom; Depth = depth; }
        }

        private sealed class SeamItem
        {
            internal readonly Pose Flat, Joined;
            internal readonly SeamSide Side;
            internal readonly double Distance;
            internal readonly int First, Last;

            internal SeamItem(Pose flat, Pose joined, SeamSide side, double distance, int first, int last)
            {
                Flat = flat; Joined = joined; Side = side; Distance = distance; First = first; Last = last;
            }
        }
    }
}
