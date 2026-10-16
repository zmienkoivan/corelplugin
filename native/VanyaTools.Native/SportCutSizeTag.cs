using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace VanyaTools.Native
{
    // Works only on a cutting COPY. Straight runs can be sloped, split into
    // multiple segments, or represented by geometrically straight Beziers.
    internal static class SportCutSizeTag
    {
        private const double Bridge = 2, Neck = 2, BodyHeight = 6, LetterHeight = 3;
        private const double LetterWidth = 1.8, LetterStep = 2.5, StraightTolerance = 0.05;

        private sealed class Segment
        {
            public Point A, B, C, D;
            public bool Line;
        }

        private sealed class Path
        {
            public object Original;
            public double Area;
            public List<Segment> Segments = new List<Segment>();
        }

        private sealed class Run
        {
            public int Path;
            public Point A, D;
            public Vector Direction;
            public double Length;
            public List<Segment> Segments = new List<Segment>();
        }

        private sealed class Location
        {
            public Run Run;
            public Point Origin;
            public Vector Along, Outward;
            public double Offset;
            public Point World(double x, double depth) => Origin + Along * x + Outward * depth;
        }

        public static void Add(object application, object silhouetteObject, IList<object> cutShapes,
            string size, string part)
        {
            dynamic app = application, silhouette = silhouetteObject;
            string label = Label(size);
            double textWidth = LetterWidth + Math.Max(0, label.Length - 1) * LetterStep;
            double width = Math.Max(12, textWidth + 3);
            if (Convert.ToInt32(silhouette.Type) != CorelConstants.CdrCurveShape) silhouette.ConvertToCurves();
            long silhouetteId = Convert.ToInt64(silhouette.StaticID);
            object sourceCurve = silhouette.DisplayCurve;
            var paths = ReadPaths(sourceCurve);
            var other = new List<Segment>();
            foreach (object shape in cutShapes)
                if (Convert.ToInt64(((dynamic)shape).StaticID) != silhouetteId)
                    other.AddRange(ReadPaths((object)((dynamic)shape).DisplayCurve).SelectMany(p => p.Segments));

            var runs = StraightRuns(paths);
            Location location = null;
            foreach (Run run in runs.Where(r => r.Length >= width + 4)
                .OrderBy(r => (r.A.Y + r.D.Y) / 2).ThenByDescending(r => r.Length))
            {
                Vector along = run.Direction, outward = new Vector(along.Y, -along.X);
                Point midpoint = PointOnRun(run, run.A, along, run.Length / 2);
                bool plusInside = ((dynamic)sourceCurve).IsPointInside(
                    midpoint.X + outward.X * .5, midpoint.Y + outward.Y * .5);
                bool minusInside = ((dynamic)sourceCurve).IsPointInside(
                    midpoint.X - outward.X * .5, midpoint.Y - outward.Y * .5);
                if (plusInside == minusInside) continue;
                Point origin = run.A;
                if (plusInside) { along = -along; outward = -outward; origin = run.D; }

                var selected = new HashSet<Segment>(run.Segments);
                var obstacles = new List<Rect>();
                foreach (Segment segment in paths.SelectMany(p => p.Segments)
                    .Where(s => !selected.Contains(s)).Concat(other))
                    AddObstacles(segment, origin, along, outward, obstacles, 0);
                foreach (double offset in Positions(2, run.Length - width - 2))
                {
                    var zone = new Rect(offset - .3, -.3, width + .6, Neck + BodyHeight + .6);
                    if (obstacles.Any(o => o.IntersectsWith(zone))) continue;
                    location = new Location { Run = run, Origin = origin, Along = along,
                        Outward = outward, Offset = offset };
                    break;
                }
                if (location != null) break;
            }
            if (location == null)
            {
                double longest = runs.Count == 0 ? 0 : runs.Max(r => r.Length);
                Log.Info("Sport cutting: no size-tab location, size=" + size + ", part=" + part +
                    ", runs=" + runs.Count + ", longest=" + longest.ToString("0.###", CultureInfo.InvariantCulture));
                throw new InvalidOperationException("Размер «" + size + "», " + part +
                    ": не найден свободный прямой участок длиной хотя бы " +
                    (width + 4).ToString("0.#", CultureInfo.InvariantCulture) +
                    " мм для ушка. Проверяются наклонные края и надсечки; ушки можно отключить.");
            }

            dynamic curve = app.ActiveDocument.CreateCurve();
            double x0 = location.Offset;
            double l1 = x0 + 2, l2 = l1 + Bridge;
            double r2 = x0 + width - 2, r1 = r2 - Bridge;
            var gaps = new[] { new[] { l1, l2 }, new[] { r1, r2 } };
            var selectedSegments = new HashSet<Segment>(location.Run.Segments);
            for (int p = 0; p < paths.Count; p++)
            {
                if (p != location.Run.Path)
                { curve.AppendCurve(((dynamic)paths[p].Original).GetCopy()); continue; }
                object current = null;
                foreach (Segment segment in paths[p].Segments)
                {
                    var cuts = new List<double[]>();
                    if (selectedSegments.Contains(segment))
                    {
                        double start = Project(segment.A, location.Origin, location.Along);
                        double end = Project(segment.D, location.Origin, location.Along);
                        foreach (double[] gap in gaps)
                        {
                            double lo = Math.Max(Math.Min(start, end), gap[0]);
                            double hi = Math.Min(Math.Max(start, end), gap[1]);
                            if (hi <= lo + 1e-8) continue;
                            double a = Parameter(segment, location.Origin, location.Along, lo);
                            double b = Parameter(segment, location.Origin, location.Along, hi);
                            cuts.Add(new[] { Math.Min(a, b), Math.Max(a, b) });
                        }
                    }
                    double at = 0;
                    foreach (double[] cut in cuts.OrderBy(c => c[0]))
                    {
                        if (cut[0] > at + 1e-9) AppendInterval((object)curve, ref current, segment, at, cut[0]);
                        current = null;
                        at = cut[1];
                    }
                    if (at < 1 - 1e-9) AppendInterval((object)curve, ref current, segment, at, 1);
                }
            }

            // The four attachment endpoints are sampled on the original curve,
            // including Beziers. Uncut intervals can span source segment joins.
            Point a1 = PointOnRun(location.Run, location.Origin, location.Along, l1);
            Point a2 = PointOnRun(location.Run, location.Origin, location.Along, l2);
            Point b1 = PointOnRun(location.Run, location.Origin, location.Along, r1);
            Point b2 = PointOnRun(location.Run, location.Origin, location.Along, r2);
            Polyline((object)curve, new[] {
                a1, location.World(l1, Neck), location.World(x0, Neck),
                location.World(x0, Neck + BodyHeight), location.World(x0 + width, Neck + BodyHeight),
                location.World(x0 + width, Neck), location.World(r2, Neck), b2 });
            Polyline((object)curve, new[] { a2, location.World(l2, Neck), location.World(r1, Neck), b1 });
            double textLeft = x0 + (width - textWidth) / 2;
            double baseline = Neck + BodyHeight - (BodyHeight - LetterHeight) / 2;
            for (int i = 0; i < label.Length; i++)
                foreach (Point[] stroke in Glyph(label[i]))
                    Polyline((object)curve, stroke.Select(point => location.World(
                        textLeft + i * LetterStep + point.X * LetterWidth,
                        baseline - point.Y * LetterHeight)).ToArray());
            silhouette.Curve.CopyAssign(curve);
            GC.KeepAlive(sourceCurve);
            silhouette.Fill.ApplyNoFill();
            Log.Info("Sport cutting: size tab " + label + ", part=" + part +
                ", run=" + location.Run.Length.ToString("0.###", CultureInfo.InvariantCulture) +
                " mm, angle=" + (Math.Atan2(location.Along.Y, location.Along.X) * 180 / Math.PI)
                    .ToString("0.###", CultureInfo.InvariantCulture) + ", two 2x2 mm bridges.");
        }

        private static List<Run> StraightRuns(IList<Path> paths)
        {
            var result = new List<Run>();
            double largest = paths.Max(p => p.Area);
            for (int p = 0; p < paths.Count; p++)
            {
                // Tabs belong to the exterior silhouette, not an interior hole.
                if (paths[p].Area < largest - .001) continue;
                List<Segment> segments = paths[p].Segments;
                for (int start = 0; start < segments.Count; start++)
                {
                    Segment first = segments[start];
                    if (!Straight(first)) continue;
                    Vector direction = first.D - first.A; direction.Normalize();
                    Segment previous = segments[(start + segments.Count - 1) % segments.Count];
                    if (Straight(previous) && Follows(previous, first.A, direction)) continue;
                    var run = new Run { Path = p, A = first.A, Direction = direction };
                    for (int count = 0; count < segments.Count; count++)
                    {
                        Segment next = segments[(start + count) % segments.Count];
                        if (!Straight(next) || !Follows(next, first.A, direction)) break;
                        run.Segments.Add(next);
                        run.D = next.D;
                    }
                    if (run.Segments.Count == 0) continue;
                    run.Length = Project(run.D, run.A, direction);
                    result.Add(run);
                }
            }
            return result;
        }

        private static bool Follows(Segment segment, Point origin, Vector direction)
        {
            Vector delta = segment.D - segment.A;
            if (delta.Length < 1e-8) return false;
            delta.Normalize();
            return Vector.Multiply(delta, direction) >= .99999 &&
                Math.Abs(Vector.CrossProduct(segment.A - origin, direction)) <= StraightTolerance &&
                Math.Abs(Vector.CrossProduct(segment.D - origin, direction)) <= StraightTolerance &&
                (segment.Line ||
                    Math.Abs(Vector.CrossProduct(segment.B - origin, direction)) <= StraightTolerance &&
                    Math.Abs(Vector.CrossProduct(segment.C - origin, direction)) <= StraightTolerance);
        }

        private static bool Straight(Segment segment)
        {
            Vector chord = segment.D - segment.A;
            double length = chord.Length;
            if (length < 1e-6) return false;
            if (segment.Line) return true;
            chord /= length;
            return Math.Abs(Vector.CrossProduct(segment.B - segment.A, chord)) <= StraightTolerance &&
                Math.Abs(Vector.CrossProduct(segment.C - segment.A, chord)) <= StraightTolerance &&
                Project(segment.B, segment.A, chord) >= -1e-6 &&
                Project(segment.B, segment.A, chord) <= length + 1e-6 &&
                Project(segment.C, segment.A, chord) >= -1e-6 &&
                Project(segment.C, segment.A, chord) <= length + 1e-6;
        }

        private static IEnumerable<double> Positions(double left, double right)
        {
            double middle = (left + right) / 2;
            yield return middle;
            for (double step = 2; step <= right - left; step += 2)
            {
                if (middle - step >= left) yield return middle - step;
                if (middle + step <= right) yield return middle + step;
            }
            yield return left; yield return right;
        }

        private static double Project(Point point, Point origin, Vector direction) =>
            Vector.Multiply(point - origin, direction);

        private static Point Lerp(Point a, Point b, double t) => a + (b - a) * t;

        private static Point At(Segment segment, double t)
        {
            if (segment.Line) return Lerp(segment.A, segment.D, t);
            Point ab = Lerp(segment.A, segment.B, t), bc = Lerp(segment.B, segment.C, t),
                cd = Lerp(segment.C, segment.D, t);
            return Lerp(Lerp(ab, bc, t), Lerp(bc, cd, t), t);
        }

        private static void Split(Segment segment, double t, out Segment left, out Segment right)
        {
            Point ab = Lerp(segment.A, segment.B, t), bc = Lerp(segment.B, segment.C, t),
                cd = Lerp(segment.C, segment.D, t);
            Point abc = Lerp(ab, bc, t), bcd = Lerp(bc, cd, t), mid = Lerp(abc, bcd, t);
            left = new Segment { A = segment.A, B = ab, C = abc, D = mid };
            right = new Segment { A = mid, B = bcd, C = cd, D = segment.D };
        }

        private static double Parameter(Segment segment, Point origin, Vector direction, double distance)
        {
            double a = Project(segment.A, origin, direction), d = Project(segment.D, origin, direction);
            if (Math.Abs(distance - a) < 1e-8) return 0;
            if (Math.Abs(distance - d) < 1e-8) return 1;
            if (segment.Line) return Math.Max(0, Math.Min(1, (distance - a) / (d - a)));
            double lo = 0, hi = 1;
            for (int iteration = 0; iteration < 44; iteration++)
            {
                double mid = (lo + hi) / 2;
                double value = Project(At(segment, mid), origin, direction);
                if ((value < distance) == (d > a)) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        }

        private static Point PointOnRun(Run run, Point origin, Vector direction, double distance)
        {
            foreach (Segment segment in run.Segments)
            {
                double a = Project(segment.A, origin, direction), d = Project(segment.D, origin, direction);
                if (distance >= Math.Min(a, d) - 1e-6 && distance <= Math.Max(a, d) + 1e-6)
                    return At(segment, Parameter(segment, origin, direction, distance));
            }
            throw new InvalidOperationException("Не удалось определить точку крепления ушка на прямом участке.");
        }

        private static void AppendInterval(object curveObject, ref object pathObject,
            Segment segment, double from, double to)
        {
            Segment part = segment;
            if (segment.Line)
                part = new Segment { A = At(segment, from), D = At(segment, to), Line = true };
            else
            {
                if (to < 1) { Split(part, to, out Segment prefix, out _); part = prefix; }
                if (from > 0) { Split(part, from / to, out _, out Segment suffix); part = suffix; }
            }
            dynamic curve = curveObject;
            if (pathObject == null) pathObject = curve.CreateSubPath(part.A.X, part.A.Y);
            dynamic path = pathObject;
            if (part.Line) path.AppendLineSegment(part.D.X, part.D.Y);
            else path.AppendCurveSegment2(part.D.X, part.D.Y, part.B.X, part.B.Y, part.C.X, part.C.Y);
        }

        private static void AddObstacles(Segment segment, Point origin, Vector along, Vector outward,
            ICollection<Rect> result, int depth)
        {
            bool flat = segment.Line || Straight(segment) ||
                (segment.B - segment.A).Length <= StraightTolerance &&
                (segment.C - segment.A).Length <= StraightTolerance &&
                (segment.D - segment.A).Length <= StraightTolerance;
            if (!flat && depth < 14)
            {
                Split(segment, .5, out Segment left, out Segment right);
                AddObstacles(left, origin, along, outward, result, depth + 1);
                AddObstacles(right, origin, along, outward, result, depth + 1);
                return;
            }
            double ax = Project(segment.A, origin, along), ay = Project(segment.A, origin, outward);
            double dx = Project(segment.D, origin, along), dy = Project(segment.D, origin, outward);
            double minX = Math.Min(ax, dx), maxX = Math.Max(ax, dx);
            double minY = Math.Min(ay, dy), maxY = Math.Max(ay, dy);
            if (!flat)
            {
                minX = Math.Min(minX, Math.Min(Project(segment.B, origin, along), Project(segment.C, origin, along)));
                maxX = Math.Max(maxX, Math.Max(Project(segment.B, origin, along), Project(segment.C, origin, along)));
                minY = Math.Min(minY, Math.Min(Project(segment.B, origin, outward), Project(segment.C, origin, outward)));
                maxY = Math.Max(maxY, Math.Max(Project(segment.B, origin, outward), Project(segment.C, origin, outward)));
            }
            // Covers the flattening tolerance and avoids rejecting a candidate
            // just because a distant Bezier has a large control-point box.
            result.Add(new Rect(minX - .06, minY - .06, maxX - minX + .12, maxY - minY + .12));
        }

        private static List<Path> ReadPaths(object curveObject)
        {
            dynamic curve = curveObject;
            var result = new List<Path>();
            foreach (dynamic subpath in curve.SubPaths)
            {
                var path = new Path { Original = (object)subpath };
                if (Convert.ToBoolean(subpath.Closed)) path.Area = Math.Abs(Convert.ToDouble(subpath.Area));
                foreach (dynamic segment in subpath.Segments)
                {
                    var item = new Segment {
                        A = new Point(Convert.ToDouble(segment.StartNode.PositionX), Convert.ToDouble(segment.StartNode.PositionY)),
                        D = new Point(Convert.ToDouble(segment.EndNode.PositionX), Convert.ToDouble(segment.EndNode.PositionY)),
                        Line = Convert.ToInt32(segment.Type) == 0 };
                    if (!item.Line)
                    {
                        item.B = new Point(Convert.ToDouble(segment.StartingControlPointX), Convert.ToDouble(segment.StartingControlPointY));
                        item.C = new Point(Convert.ToDouble(segment.EndingControlPointX), Convert.ToDouble(segment.EndingControlPointY));
                    }
                    path.Segments.Add(item);
                }
                if (path.Segments.Count > 0) result.Add(path);
            }
            if (result.Count == 0) throw new InvalidOperationException("Не удалось прочитать контур для ушка размера.");
            return result;
        }

        private static void Polyline(object curveObject, Point[] points)
        {
            dynamic curve = curveObject;
            dynamic path = curve.CreateSubPath(points[0].X, points[0].Y);
            for (int i = 1; i < points.Length; i++) path.AppendLineSegment(points[i].X, points[i].Y);
        }

        private static string Label(string size)
        {
            string text = (size ?? "").Trim().ToUpperInvariant();
            text = Regex.Replace(text, @"^(ФО|РОСТ|РАЗМЕР)\s*[-_:]?\s*", "");
            text = Regex.Replace(text, @"\s*(РОСТ|Р-Р|РАЗМЕР)$", "").Trim();
            text = text.Replace('М', 'M').Replace('Х', 'X').Replace('С', 'S').Replace('Л', 'L');
            text = Regex.Replace(text, @"\s+", "");
            if (text.Length == 0 || text.Length > 12)
                throw new InvalidOperationException("Для ушка нужен короткий размер: например M, 2XL или 128.");
            foreach (char character in text) Glyph(character); // Validate before native edits.
            return text;
        }

        private static Point[] Stroke(params double[] coordinates)
        {
            var points = new Point[coordinates.Length / 2];
            for (int i = 0; i < points.Length; i++) points[i] = new Point(coordinates[i * 2], coordinates[i * 2 + 1]);
            return points;
        }

        private static Point[][] Glyph(char character)
        {
            // Single strokes, not outline-font contours. Gaps at closed bowls
            // and crossbars keep counters attached (stencil construction).
            if (character >= '0' && character <= '9')
            {
                string[] digits = { "abcedf", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgecd", "abc", "abcdefg", "abfgcd" };
                var strokes = new Dictionary<char, Point[]> {
                    ['a'] = Stroke(.1,1,.9,1), ['b'] = Stroke(1,.92,1,.58),
                    ['c'] = Stroke(1,.42,1,.08), ['d'] = Stroke(.9,0,.1,0),
                    ['e'] = Stroke(0,.08,0,.42), ['f'] = Stroke(0,.58,0,.92),
                    ['g'] = Stroke(.1,.5,.9,.5) };
                return digits[character - '0'].Select(segment => strokes[segment]).ToArray();
            }
            switch (character)
            {
                case 'A': return new[] { Stroke(0,0,.5,1,1,0), Stroke(.3,.45,.7,.45) };
                case 'B': return new[] { Stroke(0,0,0,1,.7,1,1,.8,.7,.55,.1,.55), Stroke(.1,.45,.7,.45,1,.2,.7,0,.1,0) };
                case 'C': return new[] { Stroke(1,1,0,1,0,0,1,0) };
                case 'D': return new[] { Stroke(0,0,0,1,.7,1,1,.8,1,.2,.7,0,.1,0) };
                case 'E': return new[] { Stroke(1,1,0,1,0,0,1,0), Stroke(.1,.5,.8,.5) };
                case 'F': return new[] { Stroke(1,1,0,1,0,0), Stroke(.1,.5,.8,.5) };
                case 'G': return new[] { Stroke(1,1,0,1,0,0,1,0,1,.45,.6,.45) };
                case 'H': return new[] { Stroke(0,0,0,1), Stroke(1,0,1,1), Stroke(.1,.5,.9,.5) };
                case 'I': return new[] { Stroke(.5,0,.5,1) };
                case 'J': return new[] { Stroke(1,1,1,0,0,0,0,.3) };
                case 'K': return new[] { Stroke(0,0,0,1), Stroke(1,1,.1,.5,1,0) };
                case 'L': return new[] { Stroke(0,1,0,0,1,0) };
                case 'M': return new[] { Stroke(0,0,0,1,.5,.5,1,1,1,0) };
                case 'N': return new[] { Stroke(0,0,0,1,1,0,1,1) };
                case 'O': return new[] { Stroke(.4,1,0,1,0,0,1,0,1,1,.6,1) };
                case 'P': return new[] { Stroke(0,0,0,1,1,1,1,.5,.1,.5) };
                case 'Q': return new[] { Stroke(.4,1,0,1,0,0,1,0,1,1,.6,1), Stroke(.6,.3,1,0) };
                case 'R': return new[] { Stroke(0,0,0,1,1,1,1,.5,.1,.5), Stroke(.55,.4,1,0) };
                case 'S': return new[] { Stroke(1,1,0,1,0,.5,1,.5,1,0,0,0) };
                case 'T': return new[] { Stroke(0,1,1,1), Stroke(.5,.9,.5,0) };
                case 'U': return new[] { Stroke(0,1,0,0,1,0,1,1) };
                case 'V': return new[] { Stroke(0,1,.5,0,1,1) };
                case 'W': return new[] { Stroke(0,1,0,0,.5,.5,1,0,1,1) };
                case 'X': return new[] { Stroke(0,0,1,1), Stroke(0,1,1,0) };
                case 'Y': return new[] { Stroke(0,1,.5,.55,1,1), Stroke(.5,.45,.5,0) };
                case 'Z': return new[] { Stroke(0,1,1,1,0,0,1,0) };
                case '-': return new[] { Stroke(0,.5,1,.5) };
                case '/': return new[] { Stroke(0,0,1,1) };
                case '+': return new[] { Stroke(0,.5,1,.5), Stroke(.5,0,.5,1) };
                default: throw new InvalidOperationException("Символ «" + character +
                    "» не поддерживается на прорезной бирке. Используйте латинский размер или рост цифрами.");
            }
        }
    }
}
