using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class PatternPreviewSurface : FrameworkElement
    {
        private readonly PatternEditorSession _session;
        private PatternFrame _frame;
        private PatternDraft _draft;
        private Point _origin;
        private double _scale;
        private bool _showCells, _showRepeatBorders;
        private enum DragMode { None, Move, Scale, Rotate }
        private DragMode _drag;
        private Point _dragCenter;
        private Vector _dragVector, _moveOffset;
        private double _heroAngle, _dragAngle, _dragScale;

        internal event Action<double, double> HeroPositionChanged;
        internal event Action<double, double> HeroTransformChanged;

        internal PatternPreviewSurface(PatternEditorSession session, PatternFrame initialFrame)
        {
            _session = session; _frame = initialFrame;
            ClipToBounds = true;
            ToolTip = "Потяните за угол для масштаба, за кружок для поворота или за рисунок для перемещения.";
        }

        internal void Update(PatternFrame frame, PatternDraft draft, double heroAngle)
        {
            _frame = frame; _draft = draft; _heroAngle = heroAngle;
            if (draft?.Hero == null) Cursor = Cursors.Arrow;
            InvalidateVisual();
        }

        internal void ShowGuides(bool cells, bool repeat)
        {
            _showCells = cells;
            _showRepeatBorders = repeat;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            double markSpace = _draft?.CenterMarks == null ? 0 : 8;
            _scale = Math.Min((RenderSize.Width - 40) / _frame.Width, (RenderSize.Height - 40) / (_frame.Height + markSpace));
            if (_scale <= 0) return;
            _origin = new Point((RenderSize.Width - _frame.Width * _scale) / 2, (RenderSize.Height - _frame.Height * _scale) / 2);
            drawing.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _origin.X, _origin.Y));
            double hairline = 1 / _scale;
            drawing.DrawGeometry(Brushes.White, new Pen(Brushes.SlateGray, hairline), _frame.Geometry);
            if (_draft != null)
            {
                var lightOutline = new Pen(new SolidColorBrush(Color.FromArgb(100, 55, 75, 86)), hairline * .75);
                drawing.PushClip(_frame.Geometry);
                foreach (var item in _draft.Items)
                {
                    var motif = _draft.Motifs[item.Motif];
                    DrawMotif(drawing, motif, item, Brushes.SteelBlue);
                    if (_draft.Options.Texture) DrawGuide(drawing, PatternSilhouette.At(motif, item), lightOutline);
                }
                if (_showCells && _draft.CellGuides != null)
                    DrawGuide(drawing, _draft.CellGuides, new Pen(Brushes.MediumPurple, hairline) { DashStyle = DashStyles.Dot },
                        _frame.Bottle == null ? null : _draft.Reserve);
                if (_draft.Hero != null)
                {
                    DrawMotif(drawing, _session.Hero, _draft.Hero, Brushes.MediumVioletRed);
                    if (_draft.Reserve != null)
                    {
                        var guide = new Pen(Brushes.DeepSkyBlue, hairline) { DashStyle = DashStyles.Dash };
                        DrawGuide(drawing, _draft.Reserve, guide);
                    }
                }
                if (_showRepeatBorders && _draft.RepeatGuides != null)
                    DrawGuide(drawing, _draft.RepeatGuides, new Pen(Brushes.OrangeRed, hairline * 1.25) { DashStyle = DashStyles.Dash });
                drawing.Pop();
                if (_draft.CenterMarks != null)
                    DrawGuide(drawing, _draft.CenterMarks, new Pen(Brushes.Black, Math.Max(hairline, .15)));
            }

            if (_showRepeatBorders && _frame.Bottle != null) DrawBottleSeam(drawing, hairline);

            if (_session.Seam != null && !ReferenceEquals(_frame, _session.Seam))
            {
                drawing.PushTransform(new TranslateTransform(_session.Seam.Left - _frame.Left, _frame.Top - _session.Seam.Top));
                drawing.DrawGeometry(null, new Pen(Brushes.SlateGray, hairline) { DashStyle = DashStyles.Dash }, _session.Seam.Geometry);
                drawing.Pop();
            }
            drawing.Pop();
            if (_draft?.Hero != null) DrawHeroHandles(drawing);
        }

        private void DrawBottleSeam(DrawingContext drawing, double hairline)
        {
            var bottle = _frame.Bottle;
            double extension = Math.Min(3, 8 * hairline);
            var backing = new Pen(Brushes.White, 3.5 * hairline);
            var dashed = new Pen(Brushes.OrangeRed, 1.75 * hairline) { DashStyle = new DashStyle(new[] { 3d, 2d }, 0) };
            foreach (double u in new[] { 0d, bottle.Width })
            {
                // The two sides represent the same seam after wrapping the bottle.
                var bottom = ToView(PatternBottleLayout.OnFrame(_frame, u, -extension));
                var top = ToView(PatternBottleLayout.OnFrame(_frame, u, bottle.Slant + extension));
                drawing.DrawLine(backing, bottom, top);
                drawing.DrawLine(dashed, bottom, top);
            }
        }

        private void DrawHeroHandles(DrawingContext drawing)
        {
            var corners = HeroCorners(out var top, out var rotation);
            var pen = new Pen(Brushes.DodgerBlue, 1);
            for (int i = 0; i < corners.Length; i++)
            {
                drawing.DrawLine(pen, corners[i], corners[(i + 1) % corners.Length]);
                drawing.DrawRectangle(Brushes.White, pen, new Rect(corners[i].X - 3.5, corners[i].Y - 3.5, 7, 7));
            }
            drawing.DrawLine(pen, top, rotation);
            drawing.DrawEllipse(Brushes.White, pen, rotation, 4.5, 4.5);
        }

        private Point[] HeroCorners(out Point top, out Point rotation)
        {
            var item = _draft.Hero;
            var center = ToScreen(item.Center);
            var size = PatternTransform.Fit(_session.Hero.Size, item);
            double x = size.Width * _scale / 2, y = size.Height * _scale / 2;
            var transform = new RotateTransform(-item.Angle, center.X, center.Y);
            top = transform.Transform(new Point(center.X, center.Y - y));
            rotation = transform.Transform(new Point(center.X, center.Y - y - 18));
            return new[] { transform.Transform(new Point(center.X - x, center.Y - y)),
                transform.Transform(new Point(center.X + x, center.Y - y)),
                transform.Transform(new Point(center.X + x, center.Y + y)),
                transform.Transform(new Point(center.X - x, center.Y + y)) };
        }

        private DragMode HitHandle(Point point)
        {
            var corners = HeroCorners(out _, out var rotation);
            if ((point - rotation).Length <= 8) return DragMode.Rotate;
            foreach (var corner in corners)
                if ((point - corner).Length <= 8) return DragMode.Scale;
            return DragMode.Move;
        }

        private Point ToScreen(PatternPoint point) =>
            new Point(_origin.X + point.X * _scale, _origin.Y + (_frame.Height - point.Y) * _scale);

        private void DrawMotif(DrawingContext drawing, PatternAsset asset, PatternPlacement item, Brush fallback)
        {
            var size = PatternTransform.Fit(asset.Size, item);
            var center = ToView(item.Center);
            double width = Math.Max(.05, size.Width), height = Math.Max(.05, size.Height);
            var area = new Rect(center.X - width / 2, center.Y - height / 2, width, height);
            drawing.PushTransform(new RotateTransform(-item.Angle, center.X, center.Y));
            if (asset.Mirrored) drawing.PushTransform(new ScaleTransform(-1, 1, center.X, center.Y));
            if (asset.Preview != null) drawing.DrawImage(asset.Preview, area);
            else drawing.DrawRectangle(fallback, null, area);
            if (asset.Mirrored) drawing.Pop();
            drawing.Pop();
        }

        private void DrawGuide(DrawingContext drawing, Geometry geometry, Pen pen, Geometry reserve = null)
        {
            drawing.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, _frame.Height));
            if (reserve != null)
                drawing.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, _frame.Width, _frame.Height)), reserve));
            drawing.DrawGeometry(null, pen, geometry);
            if (reserve != null) drawing.Pop();
            drawing.Pop();
        }

        private Point ToView(PatternPoint point) => new Point(point.X, _frame.Height - point.Y);

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (_draft?.Hero == null || _scale <= 0) return;
            Point point = e.GetPosition(this);
            var mode = HitHandle(point);
            if (mode == DragMode.Move &&
                !new Rect(_origin.X, _origin.Y, _frame.Width * _scale, _frame.Height * _scale).Contains(point)) return;
            if (!CaptureMouse()) return;
            _drag = mode;
            var hero = _draft.Hero;
            _dragCenter = ToScreen(hero.Center);
            _dragVector = point - _dragCenter;
            var size = PatternTransform.Fit(_session.Hero.Size, hero);
            _dragScale = size.Scale;
            _dragAngle = _heroAngle;
            var unrotated = new RotateTransform(hero.Angle).Transform(new Point(_dragVector.X, _dragVector.Y));
            _moveOffset = Math.Abs(unrotated.X) <= size.Width * _scale / 2 && Math.Abs(unrotated.Y) <= size.Height * _scale / 2
                ? -_dragVector : default;
            if (mode == DragMode.Move && _moveOffset.LengthSquared == 0) MoveHero(point);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draft?.Hero == null || _scale <= 0) return;
            var point = e.GetPosition(this);
            if (_drag != DragMode.None && e.LeftButton == MouseButtonState.Pressed) DragTo(point);
            var mode = _drag == DragMode.None ? HitHandle(point) : _drag;
            Cursor = mode == DragMode.Rotate ? Cursors.Hand : mode == DragMode.Scale ? Cursors.SizeNWSE : Cursors.SizeAll;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_drag == DragMode.None) return;
            DragTo(e.GetPosition(this));
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _drag = DragMode.None;
        }

        private void DragTo(Point point)
        {
            if (_drag == DragMode.Move)
            {
                MoveHero(point + _moveOffset);
                return;
            }

            var vector = point - _dragCenter;
            double scale = _dragScale;
            if (_drag == DragMode.Scale)
            {
                if (_dragVector.Length < 1e-6) return;
                scale *= vector.Length / _dragVector.Length;
            }
            else
            {
                if (vector.Length < 1) return;
                _dragAngle = (_dragAngle - Vector.AngleBetween(_dragVector, vector)) % 360;
                _dragVector = vector;
            }
            var rotated = PatternTransform.RotatedSize(_session.Hero.Size.X, _session.Hero.Size.Y, _dragAngle);
            HeroTransformChanged?.Invoke(scale * Math.Max(rotated.X, rotated.Y), _dragAngle);
        }

        private void MoveHero(Point point)
        {
            double x = (point.X - _origin.X) / _scale, y = _frame.Height - (point.Y - _origin.Y) / _scale;
            double width = _frame.Width, height = _frame.Height;
            if (_frame.Bottle != null)
            {
                var projected = _frame.Bottle.Project(new PatternPoint(x + _frame.BottleOrigin.X, y + _frame.BottleOrigin.Y));
                x = projected.X; y = projected.Y; width = _frame.Bottle.Width; height = _frame.Bottle.Slant;
            }
            double xPercent = Math.Max(0, Math.Min(100, x * 100 / width));
            double yPercent = Math.Max(0, Math.Min(100, (1 - y / height) * 100));
            HeroPositionChanged?.Invoke(xPercent, yPercent);
        }
    }
}
