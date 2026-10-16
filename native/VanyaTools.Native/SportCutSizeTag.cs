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
        private const double StraightTolerance = 0.05;

        internal static double TextWidth(string text, double height) =>
            height * (.6 + Math.Max(0, text.Length - 1) * (5.0 / 6));

        internal static double WidthFor(string size, SportCutTagSettings settings) =>
            Math.Max(settings.WidthMm, Math.Max(TextWidth(Label(size), settings.LetterHeightMm) + 4,
                4 * settings.BridgeMm + 4));

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
            string size, string part, SportCutTagSettings settings, double leftOffsetMm)
        {
            settings.Validate();
            dynamic app = application, silhouette = silhouetteObject;
            string label = Label(size);
            double textWidth = TextWidth(label, settings.LetterHeightMm);
            double width = WidthFor(size, settings);
            double targetX = Convert.ToDouble(silhouette.LeftX) + leftOffsetMm;
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
                // Position is measured in the source template orientation, before
                // nesting. Only lower edges with an outward downward normal qualify.
                if (along.X < .2 || outward.Y > -.2) continue;
                double offset = (targetX - origin.X) / along.X;
                if (offset < .5 || offset + width > run.Length - .5) continue;

                var selected = new HashSet<Segment>(run.Segments);
                var obstacles = new List<Rect>();
                foreach (Segment segment in paths.SelectMany(p => p.Segments)
                    .Where(s => !selected.Contains(s)).Concat(other))
                    AddObstacles(segment, origin, along, outward, obstacles, 0);
                var zone = new Rect(offset - .3, -.3, width + .6, settings.HeightMm + .6);
                if (obstacles.Any(o => o.IntersectsWith(zone))) continue;
                location = new Location { Run = run, Origin = origin, Along = along,
                    Outward = outward, Offset = offset };
                if (location != null) break;
            }
            if (location == null)
            {
                double longest = runs.Count == 0 ? 0 : runs.Max(r => r.Length);
                Log.Info("Sport cutting: no size-tab location, size=" + size + ", part=" + part +
                    ", runs=" + runs.Count + ", longest=" + longest.ToString("0.###", CultureInfo.InvariantCulture));
                throw new InvalidOperationException("Размер «" + size + "», " + part +
                    ": язычок шириной " + width.ToString("0.#", CultureInfo.InvariantCulture) +
                    " мм не помещается на прямом нижнем крае с отступом " +
                    leftOffsetMm.ToString("0.#", CultureInfo.InvariantCulture) +
                    " мм от левого края детали. Проверьте надсечки, измените отступ/шаг или отключите язычки.");
            }

            dynamic curve = app.ActiveDocument.CreateCurve();
            double x0 = location.Offset;
            double side = (width - 2 * settings.BridgeMm) / 4;
            double l1 = x0 + side, l2 = l1 + settings.BridgeMm;
            double r2 = x0 + width - side, r1 = r2 - settings.BridgeMm;
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

            // U-shaped tab joins the source edge directly. The only bridges are
            // the two uncut intervals on that edge; there are no extra stalks.
            Polyline((object)curve, new[] {
                PointOnRun(location.Run, location.Origin, location.Along, x0),
                location.World(x0, settings.HeightMm), location.World(x0 + width, settings.HeightMm),
                PointOnRun(location.Run, location.Origin, location.Along, x0 + width) });
            double textLeft = x0 + (width - textWidth) / 2;
            double baseline = (settings.HeightMm + settings.LetterHeightMm) / 2;
            AppendLabel((object)curve, label, settings.LetterHeightMm,
                (x, y) => location.World(textLeft + x, baseline - y));
            silhouette.Curve.CopyAssign(curve);
            GC.KeepAlive(sourceCurve);
            silhouette.Fill.ApplyNoFill();
            Log.Info("Sport cutting: size tab " + label + ", part=" + part +
                ", run=" + location.Run.Length.ToString("0.###", CultureInfo.InvariantCulture) +
                " mm, angle=" + (Math.Atan2(location.Along.Y, location.Along.X) * 180 / Math.PI)
                    .ToString("0.###", CultureInfo.InvariantCulture) +
                ", tab=" + width.ToString("0.###", CultureInfo.InvariantCulture) + "x" + settings.HeightMm +
                ", letter=" + settings.LetterHeightMm + ", bridges=" + settings.BridgeMm +
                ", left offset=" + leftOffsetMm + " mm.");
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

        internal static void AppendLabel(object curve, string text, double height,
            Func<double, double, Point> world)
        {
            for (int i = 0; i < text.Length; i++)
                foreach (Point[] stroke in Glyph(text[i]))
                    Polyline(curve, stroke.Select(p => world(
                        i * height * (5.0 / 6) + p.X * height * .6, p.Y * height)).ToArray());
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
            switch (character)
            {
                case '0': return new[] { Stroke(.35,1,.15,1,0,.85,0,.15,.15,0,.85,0,1,.15,1,.85,.85,1,.65,1) };
                case '1': return new[] { Stroke(.15,.75,.5,1,.5,0), Stroke(.1,0,.9,0) };
                case '2': return new[] { Stroke(0,.85,.15,1,.85,1,1,.85,1,.65,0,.15,0,0,1,0) };
                case '3': return new[] { Stroke(0,1,.85,1,1,.85,1,.65,.75,.5,.25,.5), Stroke(.75,.5,1,.35,1,.15,.85,0,0,0) };
                case '4': return new[] { Stroke(.8,0,.8,1,0,.35,.65,.35), Stroke(.95,.35,1,.35) };
                case '5': return new[] { Stroke(1,1,0,1,0,.55,.85,.55,1,.4,1,.15,.85,0,0,0) };
                case '6': return new[] { Stroke(.9,1,.25,1,0,.75,0,.15,.15,0,.85,0,1,.15,1,.4,.85,.55,.15,.55) };
                case '7': return new[] { Stroke(0,1,1,1,.25,0) };
                case '8': return new[] { Stroke(.35,.55,0,.75,0,.85,.15,1,.85,1,1,.85,1,.75,.65,.55), Stroke(.35,.45,0,.25,0,.15,.15,0,.85,0,1,.15,1,.25,.65,.45) };
                case '9': return new[] { Stroke(.1,0,.75,0,1,.25,1,.85,.85,1,.15,1,0,.85,0,.6,.15,.45,.85,.45) };
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
                case 'S': return new[] { Stroke(1,1,.15,1,0,.85,0,.65,.15,.5,.85,.5,1,.35,1,.15,.85,0,0,0) };
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
                case '.': return new[] { Stroke(.45,0,.55,0) };
                default: throw new InvalidOperationException("Символ «" + character +
                    "» не поддерживается на прорезной бирке. Используйте латинский размер или рост цифрами.");
            }
        }
    }
}
