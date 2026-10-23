using System;
using System.Collections;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    // A PowerClip reserves its printed container, including the existing
    // bleed. Text and visible guides outside it reserve their ink bounds.
    internal static class SportPrintFootprint
    {
        internal struct Bounds
        {
            internal double Left, Bottom, Width, Height;
            internal double Right => Left + Width;
            internal double Top => Bottom + Height;
            internal double CenterX => Left + Width / 2;
            internal double CenterY => Bottom + Height / 2;
        }

        internal sealed class Body
        {
            internal double GeometryWidth, GeometryHeight, Width, Height;
            internal IList<SportContourNesting.Band> Bands;
        }

        // Reuse the expensive curve data for every player, while keeping every
        // text box in the profile at every search resolution and rotation.
        private sealed class CombinedBands : IList<SportContourNesting.Band>
        {
            private readonly IList<SportContourNesting.Band> _body, _extras;
            private readonly double _dx, _dy;
            internal CombinedBands(IList<SportContourNesting.Band> body,
                IList<SportContourNesting.Band> extras, double dx, double dy)
            { _body = body; _extras = extras; _dx = dx; _dy = dy; }
            public int Count => _body.Count + _extras.Count;
            public bool IsReadOnly => true;
            public SportContourNesting.Band this[int index]
            {
                get
                {
                    if (index >= _body.Count) return _extras[index - _body.Count];
                    var band = _body[index];
                    return new SportContourNesting.Band { Left = band.Left + _dx, Right = band.Right + _dx,
                        Top = band.Top + _dy, Bottom = band.Bottom + _dy };
                }
                set => throw new NotSupportedException();
            }
            public IEnumerator<SportContourNesting.Band> GetEnumerator()
            { for (int index = 0; index < Count; index++) yield return this[index]; }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public int IndexOf(SportContourNesting.Band value)
            { for (int index = 0; index < Count; index++) if (this[index].Equals(value)) return index; return -1; }
            public bool Contains(SportContourNesting.Band value) => IndexOf(value) >= 0;
            public void CopyTo(SportContourNesting.Band[] array, int index)
            { foreach (var band in this) array[index++] = band; }
            public void Add(SportContourNesting.Band value) => throw new NotSupportedException();
            public void Insert(int index, SportContourNesting.Band value) => throw new NotSupportedException();
            public bool Remove(SportContourNesting.Band value) => throw new NotSupportedException();
            public void RemoveAt(int index) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
        }

        internal static Bounds Measure(IList<object> shapes, bool outline = true)
        {
            if (shapes == null || shapes.Count == 0)
                throw new InvalidOperationException("В печатной детали не осталось объектов для измерения.");
            // Corel ranges can change membership after Group/MoveToLayer. Keep
            // explicit shape references and measure their current document bounds.
            Bounds result = Measure(shapes[0], outline);
            for (int index = 1; index < shapes.Count; index++)
            {
                Bounds next = Measure(shapes[index], outline);
                double left = Math.Min(result.Left, next.Left), bottom = Math.Min(result.Bottom, next.Bottom),
                    right = Math.Max(result.Right, next.Right), top = Math.Max(result.Top, next.Top);
                result = new Bounds { Left = left, Bottom = bottom, Width = right - left, Height = top - bottom };
            }
            if (result.Width <= 0 || result.Height <= 0)
                throw new InvalidOperationException("Нулевые габариты печатной детали: " +
                    result.Width.ToString("R") + " × " + result.Height.ToString("R") + " мм.");
            return result;
        }

        internal static Bounds Measure(object target, bool outline = true)
        {
            dynamic shape = target;
            double left = Double.NaN, bottom = Double.NaN, width = Double.NaN, height = Double.NaN;
            shape.GetBoundingBox(ref left, ref bottom, ref width, ref height, outline);
            if (!Finite(left) || !Finite(bottom) || !Finite(width) || !Finite(height) || width < 0 || height < 0)
                throw new InvalidOperationException("Corel вернул некорректные габариты объекта: " +
                    width.ToString("R") + " × " + height.ToString("R") + " мм.");
            return new Bounds { Left = left, Bottom = bottom, Width = width, Height = height };
        }

        internal static SportContourNesting.Item Build(object container, IList<object> extras, IList<object> inkShapes,
            IDictionary<string, Body> cache, string key, bool readContour = true)
        {
            if (!readContour)
            {
                Bounds rectangle = Measure(inkShapes);
                return new SportContourNesting.Item { Width = rectangle.Width, Height = rectangle.Height };
            }
            Bounds geometry = Measure(container, false), bodyBounds = Measure(container), ink = Measure(inkShapes);
            if (geometry.Width <= 0 || geometry.Height <= 0 || bodyBounds.Width <= 0 || bodyBounds.Height <= 0)
                throw new InvalidOperationException("Нулевые габариты основного контура: " +
                    geometry.Width.ToString("R") + " × " + geometry.Height.ToString("R") + " мм.");
            if (!cache.TryGetValue(key, out Body body) ||
                !Same(body.GeometryWidth, geometry.Width) || !Same(body.GeometryHeight, geometry.Height) ||
                !Same(body.Width, bodyBounds.Width) || !Same(body.Height, bodyBounds.Height))
            {
                body = ReadBody(container, geometry, bodyBounds);
                cache[key] = body;
            }
            var rectangles = new List<SportContourNesting.Band>();
            foreach (object extra in extras)
            {
                Bounds box = Measure(extra);
                if (box.Width <= 0 || box.Height <= 0) continue;
                rectangles.Add(new SportContourNesting.Band { Left = box.Left - ink.Left,
                    Right = box.Right - ink.Left, Top = ink.Top - box.Top, Bottom = ink.Top - box.Bottom });
            }
            IList<SportContourNesting.Band> bands = body.Bands;
            if (rectangles.Count > 0 || !Same(ink.Left, bodyBounds.Left) || !Same(ink.Bottom, bodyBounds.Bottom) ||
                !Same(ink.Width, bodyBounds.Width) || !Same(ink.Height, bodyBounds.Height))
                bands = new CombinedBands(body.Bands, rectangles, bodyBounds.Left - ink.Left, ink.Top - bodyBounds.Top);
            return new SportContourNesting.Item { Width = ink.Width, Height = ink.Height, Bands = bands };
        }

        private static Body ReadBody(object container, Bounds geometry, Bounds ink)
        {
            var bands = new List<SportContourNesting.Band>();
            try
            {
                dynamic shape = container;
                dynamic curve = shape.DisplayCurve;
                if (!Convert.ToBoolean(curve.Closed))
                    throw new InvalidOperationException("Печатный контур не замкнут.");
                SportContourNesting.Point Point(double x, double y) =>
                    new SportContourNesting.Point(x - geometry.Left, geometry.Top - y);
                foreach (dynamic segment in curve.Segments)
                {
                    var a = Point(Convert.ToDouble(segment.StartNode.PositionX), Convert.ToDouble(segment.StartNode.PositionY));
                    var d = Point(Convert.ToDouble(segment.EndNode.PositionX), Convert.ToDouble(segment.EndNode.PositionY));
                    if (!Within(a, geometry) || !Within(d, geometry))
                        throw new InvalidOperationException("Координаты печатного контура выходят за габариты детали.");
                    if (Convert.ToInt32(segment.Type) == 0) SportContourNesting.AddLine(bands, a, d);
                    else
                    {
                        var b = Point(Convert.ToDouble(segment.StartingControlPointX), Convert.ToDouble(segment.StartingControlPointY));
                        var c = Point(Convert.ToDouble(segment.EndingControlPointX), Convert.ToDouble(segment.EndingControlPointY));
                        if (!Finite(b.X) || !Finite(b.Y) || !Finite(c.X) || !Finite(c.Y))
                            throw new InvalidOperationException("Некорректные управляющие точки печатного контура.");
                        SportContourNesting.AddCubic(bands, a, b, c, d);
                    }
                }
                if (bands.Count == 0) throw new InvalidOperationException("Пустой печатный контур.");
                double padding = Math.Max(0, Math.Max(Math.Max(geometry.Left - ink.Left, ink.Right - geometry.Right),
                    Math.Max(geometry.Bottom - ink.Bottom, ink.Top - geometry.Top)));
                double dx = geometry.Left - ink.Left, dy = ink.Top - geometry.Top;
                for (int index = 0; index < bands.Count; index++)
                {
                    var band = bands[index];
                    bands[index] = new SportContourNesting.Band {
                        Left = Math.Max(0, band.Left + dx - padding), Right = Math.Min(ink.Width, band.Right + dx + padding),
                        Top = Math.Max(0, band.Top + dy - padding), Bottom = Math.Min(ink.Height, band.Bottom + dy + padding) };
                }
                Log.Info("Sport printing: contour read, bands=" + bands.Count);
            }
            catch (Exception error)
            {
                Log.Error("Sport printing: using conservative ink bounds", error);
                bands.Clear();
                bands.Add(new SportContourNesting.Band { Left = 0, Top = 0, Right = ink.Width, Bottom = ink.Height });
            }
            var body = new Body { GeometryWidth = geometry.Width, GeometryHeight = geometry.Height,
                Width = ink.Width, Height = ink.Height, Bands = bands };
            return body;
        }

        private static bool Within(SportContourNesting.Point point, Bounds bounds) =>
            Finite(point.X) && Finite(point.Y) && point.X >= -0.001 && point.X <= bounds.Width + 0.001 &&
            point.Y >= -0.001 && point.Y <= bounds.Height + 0.001;
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        private static bool Same(double a, double b) => Math.Abs(a - b) <= 0.00001;
    }
}
