using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly PatternViewport _viewport = new PatternViewport();
        private bool _panning;
        private Point _panPoint;
        private readonly Dictionary<int, System.Windows.Media.Imaging.BitmapSource> _details =
            new Dictionary<int, System.Windows.Media.Imaging.BitmapSource>();
        private readonly Dictionary<int, int> _detailAttempts = new Dictionary<int, int>();
        internal event Action ViewChanged;
        internal double ZoomPercent => _viewport.Percent;
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
            ClipToBounds = true; Focusable = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            ToolTip = "Колесо — масштаб; средняя/правая кнопка или пробел + перетаскивание — перемещение. У главного принта: углы — размер, кружок — поворот.";
        }

        internal void Update(PatternFrame frame, PatternDraft draft, double heroAngle)
        {
            _frame = frame; _draft = draft; _heroAngle = heroAngle;
            if (draft?.Hero == null) Cursor = Cursors.Arrow;
            InvalidateVisual();
            ViewChanged?.Invoke();
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
            var outline = _draft?.BottleCutView ?? _frame.Geometry;
            Rect bounds = outline.Bounds;
            double markSpace = _draft?.CenterMarks == null ? 0 : 8;
            if (markSpace > 0) bounds.Inflate(0, markSpace);
            _viewport.Update(bounds, RenderSize);
            _scale = _viewport.Scale; _origin = _viewport.Origin;
            if (_scale <= 0) return;
            drawing.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _origin.X, _origin.Y));
            var visible = new Rect(_viewport.World(new Point(0, 0)), _viewport.World(new Point(RenderSize.Width, RenderSize.Height)));
            double hairline = 1 / _scale;
            drawing.DrawGeometry(Brushes.White, new Pen(Brushes.SlateGray, hairline), outline);
            if (_draft != null)
            {
                var lightOutline = new Pen(new SolidColorBrush(Color.FromArgb(100, 55, 75, 86)), hairline * .75);
                drawing.PushClip(outline);
                foreach (var item in _draft.Items)
                {
                    var motif = _draft.Motifs[item.Motif];
                    if (!DrawArea(motif, item).IntersectsWith(visible)) continue;
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

        internal void Zoom(double factor)
        {
            _viewport.ZoomAt(factor, new Point(RenderSize.Width / 2, RenderSize.Height / 2));
            Changed();
        }

        internal void Fit() { _viewport.Fit(); Changed(); }

        private void Changed()
        {
            _scale = _viewport.Scale; _origin = _viewport.Origin;
            InvalidateVisual(); ViewChanged?.Invoke();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            InvalidateVisual(); ViewChanged?.Invoke();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (_drag != DragMode.None || _panning) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _viewport.Pan(new Vector(e.Delta, 0));
            else _viewport.ZoomAt(Math.Pow(1.2, e.Delta / 120d), e.GetPosition(this));
            Changed(); e.Handled = true;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right) StartPan(e);
        }

        private void StartPan(MouseButtonEventArgs e)
        {
            if (_drag != DragMode.None || _scale <= 0 || !CaptureMouse()) return;
            Focus(); _panning = true; _panPoint = e.GetPosition(this); Cursor = Cursors.Hand; e.Handled = true;
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_panning) return;
            ReleaseMouseCapture(); e.Handled = true;
        }

        private Rect DrawArea(PatternAsset asset, PatternPlacement item)
        {
            var fit = PatternTransform.Fit(asset.Size, item);
            var size = PatternTransform.RotatedSize(fit.Width, fit.Height, item.Angle);
            var center = ToView(item.Center);
            return new Rect(center.X - size.X / 2, center.Y - size.Y / 2, size.X, size.Y);
        }

        // Higher-resolution source previews are requested only for visible assets,
        // after navigation settles. Rendering itself never calls Corel.
        internal bool RefineVisible(Func<PatternAsset, int, System.Windows.Media.Imaging.BitmapSource> capture)
        {
            if (_draft == null || _scale <= 0 || _panning || _drag != DragMode.None) return false;
            var visible = new Rect(_viewport.World(new Point(0, 0)),
                _viewport.World(new Point(RenderSize.Width, RenderSize.Height)));
            var requests = new Dictionary<int, Tuple<PatternAsset, int>>();
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            void Request(PatternAsset asset, PatternPlacement item)
            {
                var area = DrawArea(asset, item);
                if (!area.IntersectsWith(visible)) return;
                var fit = PatternTransform.Fit(asset.Size, item);
                int side = Math.Min(2048, (int)Math.Ceiling(Math.Max(fit.Width, fit.Height) * _scale * dpi / 256) * 256);
                if (!requests.TryGetValue(asset.Id, out var prior) || side > prior.Item2)
                    requests[asset.Id] = Tuple.Create(asset, side);
            }
            foreach (var item in _draft.Items) Request(_draft.Motifs[item.Motif], item);
            if (_draft.Hero != null) Request(_session.Hero, _draft.Hero);
            foreach (var request in requests.Values.OrderByDescending(value => value.Item2))
            {
                var asset = request.Item1; int side = request.Item2;
                if (asset.Shape == null || side <= 640) continue;
                var current = _details.TryGetValue(asset.Id, out var cached) ? cached : asset.Preview;
                int available = current == null ? 0 : Math.Max(current.PixelWidth, current.PixelHeight);
                if (side <= available * 1.25 || (_detailAttempts.TryGetValue(asset.Id, out int attempted) && side <= attempted)) continue;
                const long budget = 16000000;
                long needed = (long)side * side;
                long used = _details.Where(pair => pair.Key != asset.Id).Sum(pair => (long)pair.Value.PixelWidth * pair.Value.PixelHeight);
                foreach (int old in _details.Keys.Where(id => id != asset.Id && !requests.ContainsKey(id)).ToArray())
                {
                    if (used + needed <= budget) break;
                    used -= (long)_details[old].PixelWidth * _details[old].PixelHeight;
                    _details.Remove(old); _detailAttempts.Remove(old);
                }
                if (used + needed > budget) continue;
                _detailAttempts[asset.Id] = side;
                var image = capture(asset, side);
                if (image != null) { _details[asset.Id] = image; InvalidateVisual(); }
                return true;
            }
            return false;
        }

        private void DrawBottleSeam(DrawingContext drawing, double hairline)
        {
            var bottle = _frame.Bottle;
            double extension = Math.Min(3, 8 * hairline);
            var backing = new Pen(Brushes.White, 3.5 * hairline) { DashStyle = new DashStyle(new[] { 1.5, 1d }, 0) };
            var dashed = new Pen(Brushes.OrangeRed, 1.75 * hairline) { DashStyle = new DashStyle(new[] { 3d, 2d }, 0) };
            if (_draft?.BottleSeamGuide != null)
            {
                DrawGuide(drawing, _draft.BottleSeamGuide, backing);
                DrawGuide(drawing, _draft.BottleSeamGuide, dashed);
                return;
            }
            for (int side = 0; side < 2; side++)
            {
                double u = side == 0 ? 0 : bottle.Width;
                var line = new StreamGeometry();
                using (var path = line.Open())
                {
                    path.BeginFigure(ToView(PatternBottleLayout.OnFrame(_frame, u, -extension)), false, false);
                    path.LineTo(ToView(PatternBottleLayout.OnFrame(_frame, u, bottle.Slant + extension)), true, false);
                }
                drawing.DrawGeometry(null, backing, line);
                drawing.DrawGeometry(null, dashed, line);
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
            var image = _details.TryGetValue(asset.Id, out var detail) ? detail : asset.Preview;
            if (image != null) drawing.DrawImage(image, area);
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
            Focus();
            if (Keyboard.IsKeyDown(Key.Space)) { StartPan(e); return; }
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
            if (_panning)
            {
                var next = e.GetPosition(this); _viewport.Pan(next - _panPoint); _panPoint = next;
                Changed(); e.Handled = true; return;
            }
            if (_draft?.Hero == null || _scale <= 0) return;
            var point = e.GetPosition(this);
            if (_drag != DragMode.None && e.LeftButton == MouseButtonState.Pressed) DragTo(point);
            var mode = _drag == DragMode.None ? HitHandle(point) : _drag;
            Cursor = mode == DragMode.Rotate ? Cursors.Hand : mode == DragMode.Scale ? Cursors.SizeNWSE : Cursors.SizeAll;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_panning) { ReleaseMouseCapture(); e.Handled = true; return; }
            if (_drag == DragMode.None) return;
            DragTo(e.GetPosition(this));
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _drag = DragMode.None; _panning = false; Cursor = Cursors.Arrow;
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
