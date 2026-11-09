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
        private bool _dragging;

        internal event Action<double, double> HeroPositionChanged;

        internal PatternPreviewSurface(PatternEditorSession session)
        {
            _session = session; _frame = session.Seam;
            ClipToBounds = true;
            ToolTip = "Перетащите главный элемент или щёлкните там, где должен быть его центр.";
        }

        internal void Update(PatternFrame frame, PatternDraft draft)
        {
            _frame = frame; _draft = draft;
            Cursor = draft?.Hero == null ? Cursors.Arrow : Cursors.SizeAll;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            _scale = Math.Min((RenderSize.Width - 40) / _frame.Width, (RenderSize.Height - 40) / _frame.Height);
            if (_scale <= 0) return;
            _origin = new Point((RenderSize.Width - _frame.Width * _scale) / 2, (RenderSize.Height - _frame.Height * _scale) / 2);
            drawing.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _origin.X, _origin.Y));
            double hairline = 1 / _scale;
            drawing.DrawGeometry(Brushes.White, new Pen(Brushes.SlateGray, hairline), _frame.Geometry);
            if (_draft != null)
            {
                var cellBrush = new SolidColorBrush(_draft.Color);
                drawing.PushClip(_frame.Geometry);
                foreach (var item in _draft.Items)
                {
                    if (item.Cell == null) DrawMotif(drawing, _draft.Motifs[item.Motif], item, Brushes.SteelBlue);
                    else drawing.DrawGeometry(cellBrush, null, CellGeometry(item.Cell,
                        _draft.Options.Mode == PatternFillMode.Pebbles ? .37 : .11));
                }
                if (_draft.Hero != null)
                {
                    DrawMotif(drawing, _session.Hero, _draft.Hero, Brushes.MediumVioletRed);
                    if (_draft.Reserve.HasValue)
                    {
                        var reserve = _draft.Reserve.Value;
                        var guide = new Pen(Brushes.DeepSkyBlue, hairline) { DashStyle = DashStyles.Dash };
                        drawing.DrawRectangle(null, guide,
                            new Rect(reserve.Left, _frame.Height - reserve.Top, reserve.Right - reserve.Left, reserve.Top - reserve.Bottom));
                    }
                }
                drawing.Pop();
            }

            if (!ReferenceEquals(_frame, _session.Seam))
            {
                drawing.PushTransform(new TranslateTransform(_session.Seam.Left - _frame.Left, _frame.Top - _session.Seam.Top));
                drawing.DrawGeometry(null, new Pen(Brushes.SlateGray, hairline) { DashStyle = DashStyles.Dash }, _session.Seam.Geometry);
                drawing.Pop();
            }
            drawing.Pop();
        }

        private void DrawMotif(DrawingContext drawing, PatternAsset asset, PatternPlacement item, Brush fallback)
        {
            var size = PatternTransform.Fit(asset.Size, item);
            var center = ToView(item.Center);
            double width = Math.Max(.05, size.Width), height = Math.Max(.05, size.Height);
            var area = new Rect(center.X - width / 2, center.Y - height / 2, width, height);
            drawing.PushTransform(new RotateTransform(-item.Angle, center.X, center.Y));
            if (asset.Preview != null) drawing.DrawImage(asset.Preview, area);
            else drawing.DrawRectangle(fallback, null, area);
            drawing.Pop();
        }

        private Geometry CellGeometry(PatternPoint[] vertices, double rounding)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                int count = vertices.Length;
                context.BeginFigure(ToView(PatternPoint.Between(vertices[0], vertices[count - 1], rounding)), true, true);
                for (int i = 0; i < count; i++)
                {
                    var corner = vertices[i];
                    if (i > 0) context.LineTo(ToView(PatternPoint.Between(corner, vertices[i - 1], rounding)), true, false);
                    context.QuadraticBezierTo(ToView(corner),
                        ToView(PatternPoint.Between(corner, vertices[(i + 1) % count], rounding)), true, false);
                }
            }
            return geometry;
        }

        private Point ToView(PatternPoint point) => new Point(point.X, _frame.Height - point.Y);

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (_draft?.Hero == null || _scale <= 0) return;
            Point point = e.GetPosition(this);
            if (!new Rect(_origin.X, _origin.Y, _frame.Width * _scale, _frame.Height * _scale).Contains(point)) return;
            _dragging = CaptureMouse();
            MoveHero(point);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging && e.LeftButton == MouseButtonState.Pressed) MoveHero(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragging) return;
            MoveHero(e.GetPosition(this));
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _dragging = false;
        }

        private void MoveHero(Point point)
        {
            double xPercent = Math.Max(0, Math.Min(100, (point.X - _origin.X) * 100 / (_scale * _frame.Width)));
            double yPercent = Math.Max(0, Math.Min(100, (point.Y - _origin.Y) * 100 / (_scale * _frame.Height)));
            HeroPositionChanged?.Invoke(xPercent, yPercent);
        }
    }
}
