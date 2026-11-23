using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal sealed class PatternBic3DScene : Border
    {
        private readonly Viewport3D _viewport=new Viewport3D();
        private readonly Model3DGroup _model=new Model3DGroup();
        private readonly PerspectiveCamera _camera=new PerspectiveCamera{FieldOfView=37};
        private GeometryModel3D _skin,_seam;
        private Point3D _target;
        private double _diameter,_height,_radius=40,_distance=160,_yaw=-25,_pitch=10;
        private Point? _previous;
        private bool _showSeam;
        internal BitmapSource Texture{get;private set;}
        internal bool ShowSeam{get=>_showSeam;set{_showSeam=value;SetSeam();}}
        internal PatternBic3DScene()
        {
            Background=new SolidColorBrush(Color.FromRgb(241,244,247));ClipToBounds=true;Child=_viewport;
            _viewport.Camera=_camera;_viewport.Children.Add(new ModelVisual3D{Content=_model});
            SizeChanged+=(_,__)=>Fit();
        }
        internal void Set(PatternBottle bottle,BitmapSource texture)
        {
            bool rebuild=_skin==null || _diameter!=bottle.TopDiameter || _height!=bottle.Height;
            if(rebuild)
            {
                _diameter=bottle.TopDiameter;_height=bottle.Height;
                double w=PatternBic3DModel.BodyWidth(_diameter),d=_diameter*.6,h=_height,head=d*.85;
                _model.Children.Clear();
                _model.Children.Add(new AmbientLight(Color.FromRgb(115,115,115)));
                _model.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-.6,-.7,-1)));
                _model.Children.Add(new DirectionalLight(Color.FromRgb(160,170,185),new Vector3D(.5,.2,1)));
                _skin=new GeometryModel3D{Geometry=PatternBic3DModel.Tube(w,d,0,h)};_model.Children.Add(_skin);
                var white=PatternBic3DModel.Material(Colors.White);
                Add(PatternBic3DModel.Tube(w*.995,d*.995,0,.15,cap:true),white);
                Add(PatternBic3DModel.Tube(w*.97,d*.97,h,h+1,cap:true),PatternBic3DModel.Material(Color.FromRgb(43,45,48)));
                Add(PatternBic3DModel.Tube(w*.72,d*.86,h+1,h+head,-w*.10,cap:true),PatternBic3DModel.Material(Color.FromRgb(185,190,194),true));
                Add(PatternBic3DModel.Ellipsoid(new Point3D(w*.32,h+head*.28,0),w*.19,head*.21,d*.43),PatternBic3DModel.Material(Color.FromRgb(202,31,34),true));
                Add(PatternBic3DModel.Wheel(new Point3D(w*.10,h+head,0),d*.23,d*.77),PatternBic3DModel.Material(Color.FromRgb(88,91,93),true));
                for(int side=-1;side<=1;side+=2)for(int i=0;i<4;i++)
                    Add(PatternBic3DModel.Ellipsoid(new Point3D(-w*.29+i*w*.10,h+head*.53,side*d*.432),w*.013,head*.10,.04),PatternBic3DModel.Material(Color.FromRgb(48,51,54)));
                _seam=new GeometryModel3D{Geometry=PatternBic3DModel.Tube(.12,.12,0,h)};
                _seam.Transform=new TranslateTransform3D(0,0,-d/2-.045);_model.Children.Add(_seam);SetSeam();
                var bounds=_model.Bounds;
                _target=new Point3D(0,bounds.Y+bounds.SizeY/2,0);
                _radius=Math.Sqrt(bounds.SizeX*bounds.SizeX+bounds.SizeY*bounds.SizeY+bounds.SizeZ*bounds.SizeZ)/2;
            }
            Texture=texture;
            var brush=new ImageBrush(texture){Stretch=Stretch.Fill,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,1,1)};brush.Freeze();
            var material=new MaterialGroup();material.Children.Add(new DiffuseMaterial(brush));material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(18,18,18)),35));material.Freeze();
            _skin.Material=material;_skin.BackMaterial=material;
            if(rebuild)Fit();else Camera();
        }
        private void Add(MeshGeometry3D mesh,Material material)=>_model.Children.Add(new GeometryModel3D(mesh,material){BackMaterial=material});
        private void SetSeam(){if(_seam!=null){var material=PatternBic3DModel.Material(_showSeam?Colors.OrangeRed:Colors.Transparent);_seam.Material=_seam.BackMaterial=material;}}
        internal void View(double yaw){_yaw=yaw;_pitch=8;Fit();}
        internal void Fit()
        {
            double aspect=ActualHeight>0?Math.Max(.15,ActualWidth/ActualHeight):1;
            _distance=_radius/Math.Sin(Math.Atan(Math.Tan(18.5*Math.PI/180)*Math.Min(1,1/aspect)))*1.1;Camera();
        }
        private void Camera()
        {
            double yaw=_yaw*Math.PI/180,pitch=_pitch*Math.PI/180;
            var direction=new Vector3D(Math.Sin(yaw)*Math.Cos(pitch),Math.Sin(pitch),Math.Cos(yaw)*Math.Cos(pitch));
            _camera.Position=_target+direction*_distance;_camera.LookDirection=-direction*_distance;_camera.UpDirection=new Vector3D(0,1,0);
            _camera.NearPlaneDistance=Math.Max(.01,_distance-3*_radius);_camera.FarPlaneDistance=_distance+10*_radius;
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {base.OnMouseDown(e);if(e.ChangedButton!=MouseButton.Left)return;_previous=e.GetPosition(this);CaptureMouse();e.Handled=true;}
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);if(!_previous.HasValue)return;
            if(e.LeftButton!=MouseButtonState.Pressed){ReleaseMouseCapture();return;}
            var point=e.GetPosition(this);var delta=point-_previous.Value;_previous=point;
            _yaw-=delta.X*.5;_pitch=Math.Max(-75,Math.Min(75,_pitch+delta.Y*.4));Camera();e.Handled=true;
        }
        protected override void OnMouseUp(MouseButtonEventArgs e){base.OnMouseUp(e);if(e.ChangedButton==MouseButton.Left){_previous=null;ReleaseMouseCapture();e.Handled=true;}}
        protected override void OnLostMouseCapture(MouseEventArgs e){_previous=null;base.OnLostMouseCapture(e);}
        protected override void OnMouseWheel(MouseWheelEventArgs e){_distance=Math.Max(_radius*.5,Math.Min(_radius*18,_distance*Math.Pow(.85,e.Delta/120d)));Camera();e.Handled=true;}
    }
}
