using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace VanyaTools.Native
{
    // Adds cut-only, detachable size tabs to a COPY of a cutting silhouette.
    // No weld/trim is used: existing Bezier segments and separate notches retain
    // their coordinates. Only two straight-edge intervals become uncut bridges.
    internal static class SportCutSizeTag
    {
        private const double Bridge = 2, Neck = 2, BodyHeight = 6, LetterHeight = 3;
        private const double LetterWidth = 1.8, LetterStep = 2.5;

        private sealed class Segment
        {
            public Point A, B, C, D;
            public bool Line;
        }

        private sealed class Path
        {
            public object Original;
            public List<Segment> Segments = new List<Segment>();
        }

        private sealed class Location
        {
            public int Path, Segment;
            public double Left, Y;
        }

        public static void Add(object application, object silhouetteObject, IList<object> cutShapes, string size)
        {
            dynamic app = application, silhouette = silhouetteObject;
            string label = Label(size);
            double textWidth = LetterWidth + Math.Max(0, label.Length - 1) * LetterStep;
            double width = Math.Max(12, textWidth + 3);
            if (Convert.ToInt32(silhouette.Type) != CorelConstants.CdrCurveShape) silhouette.ConvertToCurves();
            long silhouetteId = Convert.ToInt64(silhouette.StaticID);
            object sourceCurve = silhouette.DisplayCurve;
            var paths = ReadPaths(sourceCurve);
            double bottom = Convert.ToDouble(silhouette.BottomY);
            var obstacles = new List<Rect>();
            foreach (object shape in cutShapes)
                if (Convert.ToInt64(((dynamic)shape).StaticID) != silhouetteId)
                    foreach (var path in ReadPaths((object)((dynamic)shape).DisplayCurve))
                        foreach (var segment in path.Segments)
                        {
                            double minX = Math.Min(segment.A.X, segment.D.X), maxX = Math.Max(segment.A.X, segment.D.X);
                            double minY = Math.Min(segment.A.Y, segment.D.Y), maxY = Math.Max(segment.A.Y, segment.D.Y);
                            if (!segment.Line)
                            {
                                minX = Math.Min(minX, Math.Min(segment.B.X, segment.C.X));
                                maxX = Math.Max(maxX, Math.Max(segment.B.X, segment.C.X));
                                minY = Math.Min(minY, Math.Min(segment.B.Y, segment.C.Y));
                                maxY = Math.Max(maxY, Math.Max(segment.B.Y, segment.C.Y));
                            }
                            obstacles.Add(new Rect(minX - 0.3, minY - 0.3, maxX - minX + 0.6, maxY - minY + 0.6));
                        }
            Location location = null;
            for (int p = 0; p < paths.Count && location == null; p++)
                for (int s = 0; s < paths[p].Segments.Count && location == null; s++)
                {
                    var edge = paths[p].Segments[s];
                    if (!edge.Line || Math.Abs(edge.A.Y - edge.D.Y) > 0.001 ||
                        Math.Abs(edge.A.Y - bottom) > 0.1) continue;
                    double left = Math.Min(edge.A.X, edge.D.X) + 2;
                    double right = Math.Max(edge.A.X, edge.D.X) - width - 2;
                    if (right < left) continue;
                    // Prefer the middle of the hem, then try nearby positions
                    // to avoid a separate notch or internal cutting line.
                    foreach (double fraction in new[] { 0.5, 0.25, 0.75, 0.0, 1.0 })
                    {
                        double x = left + (right - left) * fraction;
                        var zone = new Rect(x - 0.3, edge.A.Y - Neck - BodyHeight - 0.3,
                            width + 0.6, Neck + BodyHeight + 0.8);
                        if (obstacles.Any(o => o.IntersectsWith(zone))) continue;
                        location = new Location { Path = p, Segment = s, Left = x, Y = edge.A.Y };
                        break;
                    }
                }
            if (location == null)
                throw new InvalidOperationException("Размер «" + size + "»: для ушка нужен прямой нижний край длиной хотя бы " +
                    (width + 4).ToString("0.#", CultureInfo.InvariantCulture) +
                    " мм без надсечек в месте крепления. Можно отключить ушки и создать обычную резку.");

            dynamic curve = app.ActiveDocument.CreateCurve();
            double x0 = location.Left, y0 = location.Y;
            double l1 = x0 + 2, l2 = l1 + Bridge;
            double r2 = x0 + width - 2, r1 = r2 - Bridge;
            for (int p = 0; p < paths.Count; p++)
            {
                if (p != location.Path)
                { curve.AppendCurve(((dynamic)paths[p].Original).GetCopy()); continue; }
                dynamic path = curve.CreateSubPath(paths[p].Segments[0].A.X, paths[p].Segments[0].A.Y);
                for (int s = 0; s < paths[p].Segments.Count; s++)
                {
                    var segment = paths[p].Segments[s];
                    if (s == location.Segment)
                    {
                        bool forward = segment.D.X > segment.A.X;
                        path.AppendLineSegment(forward ? l1 : r2, y0);
                        path = curve.CreateSubPath(forward ? l2 : r1, y0);
                        path.AppendLineSegment(forward ? r1 : l2, y0);
                        path = curve.CreateSubPath(forward ? r2 : l1, y0);
                        path.AppendLineSegment(segment.D.X, segment.D.Y);
                    }
                    else if (segment.Line) path.AppendLineSegment(segment.D.X, segment.D.Y);
                    else path.AppendCurveSegment2(segment.D.X, segment.D.Y,
                        segment.B.X, segment.B.Y, segment.C.X, segment.C.Y);
                }
            }
            // The outer path and central U, together with the split original
            // edge, leave exactly two 2x2 mm strips of fabric holding the tab.
            Polyline((object)curve, new[] {
                new Point(l1, y0), new Point(l1, y0 - Neck), new Point(x0, y0 - Neck),
                new Point(x0, y0 - Neck - BodyHeight), new Point(x0 + width, y0 - Neck - BodyHeight),
                new Point(x0 + width, y0 - Neck), new Point(r2, y0 - Neck), new Point(r2, y0) });
            Polyline((object)curve, new[] { new Point(l2, y0), new Point(l2, y0 - Neck),
                new Point(r1, y0 - Neck), new Point(r1, y0) });
            double textLeft = x0 + (width - textWidth) / 2;
            double textBottom = y0 - Neck - BodyHeight + (BodyHeight - LetterHeight) / 2;
            for (int i = 0; i < label.Length; i++)
                foreach (Point[] stroke in Glyph(label[i]))
                    Polyline((object)curve, stroke.Select(point => new Point(
                        textLeft + i * LetterStep + point.X * LetterWidth,
                        textBottom + point.Y * LetterHeight)).ToArray());
            silhouette.Curve.CopyAssign(curve);
            GC.KeepAlive(sourceCurve);
            silhouette.Fill.ApplyNoFill();
            Log.Info("Sport cutting: size tab " + label + ", " + width + "x" + BodyHeight +
                " mm, two " + Bridge + "x" + Neck + " mm bridges.");
        }

        private static List<Path> ReadPaths(object curveObject)
        {
            dynamic curve = curveObject;
            var result = new List<Path>();
            foreach (dynamic subpath in curve.SubPaths)
            {
                var path = new Path { Original = (object)subpath };
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
