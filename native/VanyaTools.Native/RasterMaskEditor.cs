using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VanyaTools.Native
{
    internal sealed class RasterMaskEditor
    {
        private readonly List<RasterMaskShape>[] _layers;
        private readonly TabControl _tabs = new TabControl();
        private readonly StackPanel _tools = new StackPanel();
        private readonly ComboBox _tool = new ComboBox { ItemsSource = new[] { "Кисть", "Ластик", "Прямоугольник", "Овал" }, SelectedIndex = 0 };
        private readonly Slider _size = new Slider { Minimum = 4, Maximum = 160, Value = 32, Width = 96, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _sizeValue = new TextBlock { MinWidth = 42, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Path _brushOutline = new Path { IsHitTestVisible = false, Stroke = Brushes.Black, StrokeThickness = 3, Visibility = Visibility.Collapsed };
        private readonly Path _brushCursor = new Path { IsHitTestVisible = false, Stroke = Brushes.White, StrokeThickness = 1, Visibility = Visibility.Collapsed };
        private readonly Button _undo = new Button { Content = "Отменить" }, _clear = new Button { Content = "Очистить" };
        private readonly Path _overlay = new Path { IsHitTestVisible = false, Fill = Brushes.MediumPurple, Opacity = .52 };
        private readonly Canvas _canvas = new Canvas { ClipToBounds = true, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly List<RasterMaskPoint> _stroke = new List<RasterMaskPoint>();
        private readonly Action _changed;
        private readonly Func<Rect> _world;
        private readonly Func<bool> _enabled;
        private readonly Image _image;
        private readonly ScrollViewer _scroll;
        private int _drawingLayer = -1;
        private double _radius;
        private RasterMaskTool _drawingTool;
        private bool Active => _tabs.SelectedIndex > 0;
        private RasterMaskTool SelectedTool => _tool.SelectedIndex == 1 ? RasterMaskTool.Eraser :
            _tool.SelectedIndex == 2 ? RasterMaskTool.Rectangle : _tool.SelectedIndex == 3 ? RasterMaskTool.Ellipse : RasterMaskTool.Brush;
        private static bool Freehand(RasterMaskTool tool) => tool == RasterMaskTool.Brush || tool == RasterMaskTool.Eraser;

        internal RasterMaskEditor(Panel host, Image image, ScrollViewer scroll, Func<Rect> world,
            Func<bool> enabled, Action changed, string normalTab, params string[] masks)
        {
            _image = image; _scroll = scroll; _world = world; _enabled = enabled; _changed = changed;
            _layers = new List<RasterMaskShape>[masks.Length];
            _tabs.Items.Add(new TabItem { Header = normalTab });
            for (int i = 0; i < masks.Length; i++)
            {
                _layers[i] = new List<RasterMaskShape>();
                _tabs.Items.Add(new TabItem { Header = masks[i] });
            }
            _tabs.SelectedIndex = 0;
            _tabs.ToolTip = "Вкладки масок защищают закрашенные области от выбранного эффекта. Рисуйте слева на исходнике. Ластик снимает только маску, не стирая изображение.";
            host.Children.Add(_tabs);
            var toolbar = new WrapPanel { Margin = new Thickness(0, 3, 0, 1) };
            _tools.Children.Add(toolbar);
            toolbar.Children.Add(_tool);
            _size.ToolTip = "Диаметр кисти или ластика на экране";
            _size.Margin = new Thickness(8, 0, 8, 0);
            toolbar.Children.Add(_size); toolbar.Children.Add(_sizeValue); toolbar.Children.Add(_undo);
            _size.ValueChanged += (_, __) => { UpdateSizeValue(); RefreshBrushCursor(); };
            UpdateSizeValue(); toolbar.Children.Add(_clear);
            host.Children.Add(_tools);
            _tool.SelectionChanged += (_, __) => { EndStroke(false); _size.IsEnabled = _sizeValue.IsEnabled = Freehand(SelectedTool); RefreshBrushCursor(); };
            _tabs.SelectionChanged += (_, __) => { EndStroke(false); Refresh(); };
            _undo.Click += (_, __) =>
            {
                if (!Active || !_enabled()) return;
                var layer = _layers[_tabs.SelectedIndex - 1];
                if (layer.Count == 0) return;
                layer.RemoveAt(layer.Count - 1); Refresh(); _changed();
            };
            _clear.Click += (_, __) =>
            {
                if (!Active || !_enabled()) return;
                _layers[_tabs.SelectedIndex - 1].Clear(); Refresh(); _changed();
            };

            var layers = (Grid)scroll.Content;
            // The overview is clipped underneath non-interactive detail tiles.
            // Give that same drawing surface a hit target in the clipped area.
            if (layers.Background == null) layers.Background = Brushes.Transparent;
            _canvas.Children.Add(_overlay); _canvas.Children.Add(_brushOutline); _canvas.Children.Add(_brushCursor); layers.Children.Add(_canvas);
            scroll.PreviewMouseDown += (_, e) =>
            {
                bool pan = Keyboard.IsKeyDown(Key.Space) || e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right;
                if (pan) { EndStroke(true); HideBrushCursor(); return; }
                if (e.ChangedButton != MouseButton.Left || !BeginStroke(e.OriginalSource, e.GetPosition(image))) return;
                if (!scroll.CaptureMouse()) { EndStroke(false); return; }
                e.Handled = true;
            };
            scroll.PreviewMouseMove += (_, e) =>
            {
                UpdateBrushCursor(e.OriginalSource, e.GetPosition(image), Navigating());
                if (_drawingLayer < 0) return;
                if (e.LeftButton != MouseButtonState.Pressed || Keyboard.IsKeyDown(Key.Space) ||
                    e.MiddleButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || !MoveStroke(e.GetPosition(image)))
                { EndStroke(true); return; }
                e.Handled = true;
            };
            scroll.PreviewMouseUp += (_, e) =>
            {
                if (_drawingLayer < 0 || e.ChangedButton != MouseButton.Left) { RefreshBrushCursor(); return; }
                MoveStroke(e.GetPosition(image)); EndStroke(true); e.Handled = true;
            };
            // Navigation ends a stroke at its last valid point. A later mouse
            // event cannot join the pre-scroll point to the shifted image.
            scroll.PreviewMouseWheel += (_, __) => { EndStroke(true); HideBrushCursor(); };
            scroll.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) { EndStroke(true); HideBrushCursor(); } };
            scroll.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) RefreshBrushCursor(); };
            scroll.MouseLeave += (_, __) => HideBrushCursor();
            scroll.IsEnabledChanged += (_, __) => RefreshBrushCursor();
            scroll.ScrollChanged += (_, e) =>
            {
                if (e.HorizontalChange != 0 || e.VerticalChange != 0 || e.ViewportWidthChange != 0 ||
                    e.ViewportHeightChange != 0 || e.ExtentWidthChange != 0 || e.ExtentHeightChange != 0) { EndStroke(true); RefreshBrushCursor(); }
            };
            scroll.LostMouseCapture += (_, __) => { EndStroke(true); RefreshBrushCursor(); };
            Refresh();
        }

        internal RasterProtectionMask Snapshot(int index)
        {
            var layer = _layers[index];
            return layer.Count == 0 ? null : new RasterProtectionMask(layer.ToArray());
        }

        internal void ShowPreview() { _tabs.SelectedIndex=0;Refresh(); }

        internal void MoveOrigin(double leftMm,double topMm)
        {
            EndStroke(false);
            foreach(var layer in _layers)for(int i=0;i<layer.Count;i++)
            {
                var shape=layer[i];var points=new RasterMaskPoint[shape.Points.Length];
                for(int j=0;j<points.Length;j++)points[j]=new RasterMaskPoint(shape.Points[j].X-leftMm,shape.Points[j].Y-topMm);
                layer[i]=new RasterMaskShape(shape.Tool,points,shape.Radius);
            }
            Refresh();
        }

        internal void Refresh()
        {
            _tools.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
            _canvas.Visibility = Active ? Visibility.Visible : Visibility.Collapsed;
            if (!Active) { HideBrushCursor(); return; }
            var layer = _layers[_tabs.SelectedIndex - 1];
            _undo.IsEnabled = _clear.IsEnabled = layer.Count > 0;
            Rect world = _world();
            if (world.IsEmpty || _image.Source == null || Double.IsNaN(_image.Width) || Double.IsNaN(_image.Height))
            { _overlay.Data = null; HideBrushCursor(); return; }
            _canvas.Width = _image.Width; _canvas.Height = _image.Height; _canvas.Margin = _image.Margin;
            Geometry geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
            void Apply(RasterMaskShape shape)
            {
                var part = Draw(shape);
                if (shape.Tool == RasterMaskTool.Eraser) geometry = new CombinedGeometry(GeometryCombineMode.Exclude, geometry, part);
                else if (geometry is GeometryGroup group) group.Children.Add(part);
                else { var added = new GeometryGroup { FillRule = FillRule.Nonzero }; added.Children.Add(geometry); added.Children.Add(part); geometry = added; }
            }
            foreach (var shape in layer) Apply(shape);
            if (_drawingLayer == _tabs.SelectedIndex - 1)
                Apply(new RasterMaskShape(_drawingTool, _stroke.ToArray(), _radius));
            double sx = _image.Width / world.Width, sy = _image.Height / world.Height;
            geometry.Transform = new MatrixTransform(sx, 0, 0, sy, -world.X * sx, -world.Y * sy);
            _overlay.Data = geometry; RefreshBrushCursor();
        }

        private void UpdateSizeValue() => _sizeValue.Text = "Ø " + _size.Value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        private bool Navigating() => Keyboard.IsKeyDown(Key.Space) || Mouse.MiddleButton == MouseButtonState.Pressed ||
            Mouse.RightButton == MouseButtonState.Pressed || (_scroll.IsMouseCaptured && _drawingLayer < 0);
        private void HideBrushCursor() { _brushOutline.Visibility = _brushCursor.Visibility = Visibility.Collapsed; }
        private void RefreshBrushCursor()
        {
            if (!_scroll.IsMouseOver) { HideBrushCursor(); return; }
            UpdateBrushCursor(_scroll.InputHitTest(Mouse.GetPosition(_scroll)), Mouse.GetPosition(_image), Navigating());
        }
        private void UpdateBrushCursor(object source, Point at, bool navigating)
        {
            if (!Active || !_scroll.IsEnabled || !_image.IsEnabled || !Freehand(SelectedTool) || navigating || !TryPoint(at, out _) ||
                (!IsImageTarget(source) && !(_drawingLayer >= 0 && _scroll.IsMouseCaptured)))
            { HideBrushCursor(); return; }
            Rect world = _world();
            double radius = _drawingLayer >= 0 ? _radius : _size.Value * .5 * world.Width / _image.ActualWidth;
            // Use the same world-to-preview scale as the actual stroke. This
            // also handles non-square source DPI, crop origins and exact tiles.
            var geometry = new EllipseGeometry(at, radius * _image.ActualWidth / world.Width, radius * _image.ActualHeight / world.Height);
            _brushOutline.Data = _brushCursor.Data = geometry;
            _brushOutline.Visibility = _brushCursor.Visibility = Visibility.Visible;
        }

        // Exact-detail tiles are non-interactive and may clip the overview
        // image underneath. In that region the content grid is the hit target.
        private bool IsImageTarget(object source) => ReferenceEquals(source, _image) || ReferenceEquals(source, _scroll.Content) ||
            source is Visual visual && _image.IsAncestorOf(visual);

        private bool TryPoint(Point at, out RasterMaskPoint point)
        {
            point = default(RasterMaskPoint); Rect world = _world();
            if (!_enabled() || world.IsEmpty || world.Width <= 0 || world.Height <= 0 || _image.Source == null || _image.ActualWidth <= 0 || _image.ActualHeight <= 0 ||
                at.X < 0 || at.Y < 0 || at.X >= _image.ActualWidth || at.Y >= _image.ActualHeight) return false;
            // The image can be larger than the viewport and extend underneath
            // its scrollbars. Image bounds alone do not exclude those controls.
            var viewport = _scroll.Template?.FindName("PART_ScrollContentPresenter", _scroll) as FrameworkElement ?? _scroll;
            Point visible = _image.TranslatePoint(at, viewport);
            if (visible.X < 0 || visible.Y < 0 || visible.X >= viewport.ActualWidth || visible.Y >= viewport.ActualHeight) return false;
            point = new RasterMaskPoint(world.X + at.X / _image.ActualWidth * world.Width,
                world.Y + at.Y / _image.ActualHeight * world.Height);
            return true;
        }

        private bool BeginStroke(object source, Point at)
        {
            if (!Active || !IsImageTarget(source) || !TryPoint(at, out var point)) return false;
            EndStroke(false); _drawingLayer = _tabs.SelectedIndex - 1; _drawingTool = SelectedTool;
            _radius = _size.Value * .5 * _world().Width / _image.ActualWidth;
            _stroke.Add(point);
            if (!Freehand(_drawingTool)) _stroke.Add(point);
            Refresh(); return true;
        }

        private bool MoveStroke(Point at)
        {
            if (_drawingLayer < 0 || !TryPoint(at, out var point)) return false;
            if (Freehand(_drawingTool))
            {
                var last = _stroke[_stroke.Count - 1];
                if (last.X == point.X && last.Y == point.Y) return true;
                _stroke.Add(point);
            }
            else _stroke[1] = point;
            Refresh(); return true;
        }

        private void EndStroke(bool commit)
        {
            if (_drawingLayer < 0) return;
            var layer = _layers[_drawingLayer];
            _drawingLayer = -1;
            commit = commit && _enabled() && (Freehand(_drawingTool) ||
                (_stroke[0].X != _stroke[1].X && _stroke[0].Y != _stroke[1].Y));
            if (commit) layer.Add(new RasterMaskShape(_drawingTool, _stroke.ToArray(), _radius));
            _stroke.Clear();
            if (_scroll.IsMouseCaptured) _scroll.ReleaseMouseCapture();
            Refresh();
            if (commit) _changed();
        }

        private static Geometry Draw(RasterMaskShape shape)
        {
            var points = shape.Points;
            Point first = new Point(points[0].X, points[0].Y);
            if (!Freehand(shape.Tool))
            {
                var bounds = new Rect(first, new Point(points[1].X, points[1].Y));
                return shape.Tool == RasterMaskTool.Rectangle ? (Geometry)new RectangleGeometry(bounds) : new EllipseGeometry(bounds);
            }
            if (points.Length == 1) return new EllipseGeometry(first, shape.Radius, shape.Radius);
            var line = new StreamGeometry();
            using (var context = line.Open())
            {
                context.BeginFigure(first, false, false);
                for (int i = 1; i < points.Length; i++)
                    context.LineTo(new Point(points[i].X, points[i].Y), true, false);
            }
            return line.GetWidenedPathGeometry(new Pen(Brushes.Black, shape.Radius * 2)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, .01, ToleranceType.Absolute);
        }
    }
}
