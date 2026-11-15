using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal sealed class Apparel3DScene : Border
    {
        private readonly Viewport3D _viewport = new Viewport3D();
        private readonly PerspectiveCamera _camera = new PerspectiveCamera { FieldOfView = 37 };
        private readonly Model3DGroup _models = new Model3DGroup();
        private double _yaw = -22, _pitch = 10, _distance = 1.8, _radius = .6;
        private Point3D _target;
        private Point? _last;
        private bool _pan;
        public Color FabricColor = Color.FromRgb(238, 240, 241);
        private ApparelViewMap _textureFront, _textureBack;
        private BitmapSource[] _textures;
        private Color _textureColor;
        private readonly ApparelViewMap _demoFront = ApparelViewMap.Demo(), _demoBack = ApparelViewMap.Demo(true);
        public ApparelSurface[] Surfaces { get; private set; }
        public Apparel3DScene()
        {
            Background = new SolidColorBrush(Color.FromRgb(246, 247, 249)); ClipToBounds = true; Child = _viewport; MinHeight = 220;
            _viewport.Camera = _camera; _viewport.Children.Add(new ModelVisual3D { Content = _models });
            SizeChanged += (_, __) => Camera();
        }
        public void Set(ApparelOrderSnapshot snapshot, double folds, bool fit = false)
        {
            var front = snapshot?.Front ?? _demoFront;
            var back = snapshot?.Back ?? _demoBack;
            Surfaces = Apparel3DModel.Build(front, back, folds);
            if (_textures == null || !ReferenceEquals(front, _textureFront) || !ReferenceEquals(back, _textureBack) || _textureColor != FabricColor)
            {
                _textures = Apparel3DTexture.Create(front, back, FabricColor);
                _textureFront = front; _textureBack = back; _textureColor = FabricColor;
            }
            _models.Children.Clear();
            _models.Children.Add(new AmbientLight(Color.FromRgb(85, 85, 85)));
            _models.Children.Add(new DirectionalLight(Color.FromRgb(220, 220, 220), new Vector3D(-.6, -.7, -1)));
            _models.Children.Add(new DirectionalLight(Color.FromRgb(180, 185, 190), new Vector3D(.5, -.2, 1)));
            var materials = Array.ConvertAll(_textures, texture => Material(texture));
            var plain = Material(null);
            Rect3D bounds = Rect3D.Empty;
            foreach (var surface in Surfaces)
            {
                _models.Children.Add(new GeometryModel3D(surface.Mesh, surface.Collar ? plain : materials[surface.PrintPart])
                    { BackMaterial = plain });
                bounds.Union(surface.Mesh.Bounds);
            }
            _target = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            _radius = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2;
            if (fit) Fit(); else Camera();
        }
        private Material Material(BitmapSource image)
        {
            // Each authored part uses its own print chart. Absolute brush
            // viewports preserve its UVs, including sleeves and the curved neck.
            Brush brush = image == null ? (Brush)new SolidColorBrush(FabricColor) : new ImageBrush(image)
                { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(brush));
            material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(7, 7, 7)), 30));
            material.Freeze(); return material;
        }
        public void Fit()
        {
            double aspect = ActualHeight > 0 ? Math.Max(.2, ActualWidth / ActualHeight) : 1;
            _distance = _radius / Math.Sin(Math.Atan(Math.Tan(18.5 * Math.PI / 180) * Math.Min(1, aspect))) * 1.10;
            Camera();
        }
        public void View(double yaw) { _yaw = yaw; _pitch = 6; Fit(); }
        private void Camera()
        {
            double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
            var offset = new Vector3D(Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch)) * _distance;
            _camera.Position = _target + offset; _camera.LookDirection = -offset; _camera.UpDirection = new Vector3D(0, 1, 0);
            _camera.NearPlaneDistance = Math.Max(.001, _distance - 4 * _radius); _camera.FarPlaneDistance = _distance + 20 * _radius;
        }
        public BitmapSource RenderPng(int width = 1800)
        {
            if (ActualWidth < 2 || ActualHeight < 2) throw new InvalidOperationException("Сначала откройте окно 3D-примерки.");
            int height = Math.Max(1, (int)Math.Round(width * ActualHeight / ActualWidth));
            _viewport.InvalidateVisual(); UpdateLayout();
            double scale = width / ActualWidth;
            var image = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(this); image.Freeze(); return image;
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        { base.OnMouseDown(e); _last = e.GetPosition(this); _pan = e.ChangedButton != MouseButton.Left; CaptureMouse(); e.Handled = true; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (!_last.HasValue) return;
            var p = e.GetPosition(this); var delta = p - _last.Value; _last = p;
            if (_pan)
            {
                var direction = _camera.LookDirection; direction.Normalize();
                var right = Vector3D.CrossProduct(direction, _camera.UpDirection); right.Normalize();
                var up = Vector3D.CrossProduct(right, direction); up.Normalize();
                _target += (-right * delta.X + up * delta.Y) * (_distance * .0015);
            }
            else { _yaw -= delta.X * .45; _pitch = Math.Max(-75, Math.Min(75, _pitch + delta.Y * .4)); }
            Camera(); e.Handled = true;
        }
        protected override void OnMouseUp(MouseButtonEventArgs e)
        { base.OnMouseUp(e); _last = null; ReleaseMouseCapture(); e.Handled = true; }
        protected override void OnLostMouseCapture(MouseEventArgs e) { _last = null; base.OnLostMouseCapture(e); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        { _distance = Math.Max(_radius * .35, Math.Min(_radius * 18, _distance * Math.Pow(.85, e.Delta / 120d))); Camera(); e.Handled = true; }
    }
}
