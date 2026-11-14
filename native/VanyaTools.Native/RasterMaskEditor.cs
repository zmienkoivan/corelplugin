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
        private readonly ComboBox _tool = new ComboBox { ItemsSource = new[] { "Кисть", "Прямоугольник", "Овал" }, SelectedIndex = 0 };
        private readonly Slider _size = new Slider { Minimum = 4, Maximum = 160, Value = 32, Width = 96, VerticalAlignment = VerticalAlignment.Center };
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
            _tabs.ToolTip = "Вкладки масок защищают закрашенные области от выбранного эффекта. Рисуйте слева на исходнике.";
            host.Children.Add(_tabs);
            var toolbar = new WrapPanel { Margin = new Thickness(0, 3, 0, 1) };
            _tools.Children.Add(toolbar);
            toolbar.Children.Add(_tool);
            _size.ToolTip = "Диаметр кисти на экране";
            _size.Margin = new Thickness(8, 0, 8, 0);
            toolbar.Children.Add(_size); toolbar.Children.Add(_undo); toolbar.Children.Add(_clear);
            host.Children.Add(_tools);
            _tool.SelectionChanged += (_, __) => _size.IsEnabled = _tool.SelectedIndex == 0;
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
            _canvas.Children.Add(_overlay); layers.Children.Add(_canvas);
            scroll.PreviewMouseDown += (_, e) =>
            {
                if (!Active || e.ChangedButton != MouseButton.Left || Keyboard.IsKeyDown(Key.Space)) return;
                Point at = e.GetPosition(image); Rect worldRect = _world();
                if (!_enabled() || worldRect.IsEmpty || at.X < 0 || at.Y < 0 ||
                    at.X >= image.ActualWidth || at.Y >= image.ActualHeight) return;
                e.Handled = true;
                _drawingLayer = _tabs.SelectedIndex - 1;
                _drawingTool = (RasterMaskTool)_tool.SelectedIndex;
                _radius = _size.Value * .5 * worldRect.Width / image.ActualWidth;
                _stroke.Add(Map(at, worldRect));
                if (_drawingTool != RasterMaskTool.Brush) _stroke.Add(_stroke[0]);
                scroll.CaptureMouse(); Refresh();
            };
            scroll.PreviewMouseMove += (_, e) =>
            {
                if (_drawingLayer < 0 || e.LeftButton != MouseButtonState.Pressed) return;
                MoveStroke(e.GetPosition(image)); e.Handled = true;
            };
            scroll.PreviewMouseUp += (_, e) =>
            {
                if (_drawingLayer < 0 || e.ChangedButton != MouseButton.Left) return;
                MoveStroke(e.GetPosition(image)); EndStroke(true); e.Handled = true;
            };
            scroll.LostMouseCapture += (_, __) => EndStroke(true);
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
            if (!Active) return;
            var layer = _layers[_tabs.SelectedIndex - 1];
            _undo.IsEnabled = _clear.IsEnabled = layer.Count > 0;
            Rect world = _world();
            if (world.IsEmpty || _image.Source == null || Double.IsNaN(_image.Width) || Double.IsNaN(_image.Height))
            { _overlay.Data = null; return; }
            _canvas.Width = _image.Width; _canvas.Height = _image.Height; _canvas.Margin = _image.Margin;
            var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var shape in layer) geometry.Children.Add(Draw(shape));
            if (_drawingLayer == _tabs.SelectedIndex - 1)
                geometry.Children.Add(Draw(new RasterMaskShape(_drawingTool, _stroke.ToArray(), _radius)));
            double sx = _image.Width / world.Width, sy = _image.Height / world.Height;
            geometry.Transform = new MatrixTransform(sx, 0, 0, sy, -world.X * sx, -world.Y * sy);
            _overlay.Data = geometry;
        }

        private void MoveStroke(Point at)
        {
            Rect world = _world();
            if (world.IsEmpty || _image.ActualWidth <= 0 || _image.ActualHeight <= 0) return;
            var point = Map(at, world);
            if (_drawingTool == RasterMaskTool.Brush)
            {
                var last = _stroke[_stroke.Count - 1];
                if (last.X == point.X && last.Y == point.Y) return;
                _stroke.Add(point);
            }
            else _stroke[1] = point;
            Refresh();
        }

        private RasterMaskPoint Map(Point at, Rect world) => new RasterMaskPoint(
            world.X + Math.Max(0, Math.Min(1, at.X / _image.ActualWidth)) * world.Width,
            world.Y + Math.Max(0, Math.Min(1, at.Y / _image.ActualHeight)) * world.Height);

        private void EndStroke(bool commit)
        {
            if (_drawingLayer < 0) return;
            var layer = _layers[_drawingLayer];
            _drawingLayer = -1;
            commit = commit && _enabled() && (_drawingTool == RasterMaskTool.Brush ||
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
            if (shape.Tool != RasterMaskTool.Brush)
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
