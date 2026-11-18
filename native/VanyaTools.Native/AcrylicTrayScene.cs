using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal sealed class AcrylicTrayScene : Border
    {
        private readonly Viewport3D _viewport = new Viewport3D();
        private readonly PerspectiveCamera _camera = new PerspectiveCamera { FieldOfView = 40 };
        private readonly Model3DGroup _models = new Model3DGroup();
        private readonly Model3DGroup _samples = new Model3DGroup();
        private readonly List<GeometryModel3D> _faces = new List<GeometryModel3D>();
        private int _solidCount;
        private Point3D _target;
        private Rect3D _bounds;
        private double _yaw = -55, _pitch = 32, _radius = 100, _distance = 400;
        private Point? _previous;
        private bool _pan, _showSamples = true;
        public AcrylicTrayAssembly3D Assembly { get; private set; }
        public bool ShowSamples
        {
            get { return _showSamples; }
            set { _showSamples = value; _samples.Transform = new ScaleTransform3D(value ? 1 : 0, value ? 1 : 0, value ? 1 : 0); }
        }

        public AcrylicTrayScene()
        {
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 247)); ClipToBounds = true;
            Child = _viewport; _viewport.Camera = _camera;
            _viewport.Children.Add(new ModelVisual3D { Content = _models });
            SizeChanged += (_, __) => Fit();
        }
        public void Set(AcrylicTrayPattern pattern, AcrylicTrayAssembly3D assembly = null)
        {
            Assembly = assembly ?? AcrylicTrayAssembly3D.Build(pattern);
            _models.Children.Clear(); _samples.Children.Clear(); _faces.Clear();
            _models.Children.Add(new AmbientLight(Color.FromRgb(125, 125, 125)));
            _models.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.5, 0.5, -1)));
            _models.Children.Add(new DirectionalLight(Color.FromRgb(160, 175, 185), new Vector3D(0.6, -0.3, -0.4)));
            var faces = Material(Color.FromArgb(94, 174, 225, 232), true);
            var edges = Material(Color.FromArgb(205, 76, 139, 151), false);
            // Draw opaque cut edges first; sort translucent sheets back to front
            // in Camera() after rotations, avoiding a fixed transparency order.
            foreach (var part in Assembly.Parts)
                _models.Children.Add(new GeometryModel3D(part.Edges, edges) { BackMaterial = edges });
            foreach (var part in Assembly.Parts)
                _faces.Add(new GeometryModel3D(part.Faces, faces) { BackMaterial = faces });
            AddSamples(pattern); _models.Children.Add(_samples); ShowSamples = _showSamples;
            _solidCount = _models.Children.Count;
            var b = Assembly.Bounds;
            foreach (Model3D sample in _samples.Children) b.Union(sample.Bounds);
            _bounds = b;
            _target = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            _radius = Math.Max(1, Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ) / 2);
            Fit();
        }
        public void Clear() { Assembly = null; _models.Children.Clear(); _samples.Children.Clear(); _faces.Clear(); _solidCount = 0; }
        public void View(int view)
        {
            _yaw = view == 1 ? -90 : view == 2 ? 0 : -55;
            _pitch = view == 3 ? 89.5 : view == 0 ? 32 : 5;
            Fit();
        }
        public void Fit()
        {
            double aspect = ActualHeight > 0 ? Math.Max(0.2, ActualWidth / ActualHeight) : 1.3;
            double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
            var back = new Vector3D(Math.Cos(yaw) * Math.Cos(pitch), Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch));
            var right = Vector3D.CrossProduct(-back, new Vector3D(0, 0, 1)); right.Normalize();
            var up = Vector3D.CrossProduct(right, -back); up.Normalize();
            double tangent = Math.Tan(20 * Math.PI / 180), distance = _radius;
            // Fit all eight projected corners, including the display samples.
            // This fills a narrow docker without cropping wide or deep trays.
            for (int i = 0; i < 8; i++)
            {
                var point = new Point3D(_bounds.X + ((i & 1) == 0 ? 0 : _bounds.SizeX),
                    _bounds.Y + ((i & 2) == 0 ? 0 : _bounds.SizeY),
                    _bounds.Z + ((i & 4) == 0 ? 0 : _bounds.SizeZ)) - _target;
                double depth = Vector3D.DotProduct(point, back);
                distance = Math.Max(distance, depth + Math.Abs(Vector3D.DotProduct(point, right)) / tangent * 1.08);
                distance = Math.Max(distance, depth + Math.Abs(Vector3D.DotProduct(point, up)) / tangent * aspect * 1.08);
            }
            _distance = distance;
            Camera();
        }
        private void Camera()
        {
            double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
            var offset = new Vector3D(Math.Cos(yaw) * Math.Cos(pitch), Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch)) * _distance;
            _camera.Position = _target + offset; _camera.LookDirection = -offset; _camera.UpDirection = new Vector3D(0, 0, 1);
            _camera.NearPlaneDistance = Math.Max(0.01, _distance - _radius * 3);
            _camera.FarPlaneDistance = _distance + _radius * 12;
            if (_faces.Count > 0)
            {
                while (_models.Children.Count > _solidCount) _models.Children.RemoveAt(_models.Children.Count - 1);
                foreach (var face in _faces.OrderByDescending(face =>
                {
                    var b = face.Bounds;
                    return (new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2) - _camera.Position).LengthSquared;
                })) _models.Children.Add(face);
            }
        }
        public BitmapSource RenderPng(int width = 1200)
        {
            if (ActualWidth < 2 || ActualHeight < 2) throw new InvalidOperationException("Сначала откройте 3D-предпросмотр.");
            UpdateLayout(); int height = Math.Max(1, (int)Math.Round(width * ActualHeight / ActualWidth));
            // A docked child can have a nonzero layout offset. Paint its visual
            // bounds explicitly so PNGs never inherit that offset and crop.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); bitmap.Freeze(); return bitmap;
        }
        private static Material Material(Color color, bool shine)
        {
            var material = new MaterialGroup(); material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
            if (shine) material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 65));
            material.Freeze(); return material;
        }
        private void AddSamples(AcrylicTrayPattern p)
        {
            if (p.Options.CellWidth < 31 || p.Options.CellDepth < 8) return;
            for (int r = 0; r < p.Options.Rows; r++)
            {
                var f = p.Floors[p.Options.FloorMode == AcrylicTrayFloorMode.Plane ? 0 : r];
                var rear = p.Crosses[p.Options.FloorMode == AcrylicTrayFloorMode.Plane ? r + 1 : r * 2 + 1];
                double y = rear.Y - p.Options.Thickness / 2 - 5, z = f.Top(y) + 0.2;
                for (int c = 0; c < p.Options.Columns; c++)
                {
                    double x = p.CellCenter(c);
                    var mesh = new MeshGeometry3D { Positions = new Point3DCollection {
                        new Point3D(x-15,y,z), new Point3D(x+15,y,z),
                        new Point3D(x+15,y+3,z+30), new Point3D(x-15,y+3,z+30) },
                        TextureCoordinates = new PointCollection { new Point(0,1),new Point(1,1),new Point(1,0),new Point(0,0) },
                        TriangleIndices = new Int32Collection { 0,1,2,0,2,3 } };
                    mesh.Freeze(); var artwork = SampleArtwork((r + c) % 3);
                    _samples.Children.Add(new GeometryModel3D(mesh, artwork) { BackMaterial = Material(Color.FromRgb(248, 248, 245), false) });
                }
            }
        }
        private static Material SampleArtwork(int index)
        {
            Color[] colors = { Color.FromRgb(220, 137, 176), Color.FromRgb(130, 169, 217), Color.FromRgb(231, 186, 92) };
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.LightGray, 0.3), new Rect(0, 0, 30, 30));
                for (int i = 0; i < 5; i++)
                {
                    double a = i * Math.PI * 2 / 5;
                    dc.DrawEllipse(new SolidColorBrush(colors[index]), null, new Point(15 + Math.Cos(a) * 5, 14 + Math.Sin(a) * 5), 4, 4);
                }
                dc.DrawEllipse(Brushes.Goldenrod, null, new Point(15, 14), 2.7, 2.7);
                dc.DrawLine(new Pen(Brushes.OliveDrab, 1.1), new Point(15, 20), new Point(15, 26));
            }
            var brush = new DrawingBrush(drawing) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
            var material = new DiffuseMaterial(brush); material.Freeze(); return material;
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        { base.OnMouseDown(e); _previous = e.GetPosition(this); _pan = e.ChangedButton != MouseButton.Left; CaptureMouse(); e.Handled = true; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (!_previous.HasValue) return;
            var point = e.GetPosition(this); var delta = point - _previous.Value; _previous = point;
            if (_pan)
            {
                var direction = _camera.LookDirection; direction.Normalize();
                var right = Vector3D.CrossProduct(direction, _camera.UpDirection); right.Normalize();
                var up = Vector3D.CrossProduct(right, direction); up.Normalize();
                _target += (-right * delta.X + up * delta.Y) * (_distance * 0.0015);
            }
            else { _yaw -= delta.X * 0.5; _pitch = Math.Max(-80, Math.Min(89.5, _pitch + delta.Y * 0.45)); }
            Camera(); e.Handled = true;
        }
        protected override void OnMouseUp(MouseButtonEventArgs e)
        { base.OnMouseUp(e); _previous = null; ReleaseMouseCapture(); e.Handled = true; }
        protected override void OnLostMouseCapture(MouseEventArgs e) { _previous = null; base.OnLostMouseCapture(e); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        { _distance = Math.Max(_radius * 0.4, Math.Min(_radius * 20, _distance * Math.Pow(0.85, e.Delta / 120d))); Camera(); e.Handled = true; }
    }
}
