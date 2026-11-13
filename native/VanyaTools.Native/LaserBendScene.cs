using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal sealed class LaserBendScene : Border
    {
        private readonly LaserBendModel _model;
        private readonly Viewport3D _viewport = new Viewport3D();
        private readonly PerspectiveCamera _camera = new PerspectiveCamera { FieldOfView=40 };
        private readonly GeometryModel3D[] _parts;
        private readonly Dictionary<Model3D,int> _indices=new Dictionary<Model3D,int>();
        private readonly Material _steel=Material(Color.FromRgb(168,183,195)), _moving=Material(Color.FromRgb(57,157,214));
        private Matrix3D[] _poses;
        private Point3D _target;
        private double _yaw=-25,_pitch=53,_distance=500,_radius=100;
        private Point? _previous;
        private Point _down;
        private bool _pan,_dragged;
        internal event Action<int> SelectPanel;
        internal LaserBendScene(LaserBendModel model)
        {
            _model=model;Child=_viewport;ClipToBounds=true;Background=new SolidColorBrush(Color.FromRgb(233,237,241));
            _viewport.Camera=_camera;
            var group=new Model3DGroup();group.Children.Add(new AmbientLight(Color.FromRgb(115,115,115)));
            group.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-.4,-.6,-1)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(135,145,160),new Vector3D(.6,.2,1)));
            _parts=model.Panels.Select((panel,index)=> {
                var part=new GeometryModel3D(LaserBendMesh.Create(panel),_steel){BackMaterial=_steel};
                group.Children.Add(part);_indices.Add(part,index);return part;
            }).ToArray();
            _viewport.Children.Add(new ModelVisual3D{Content=group});
            SizeChanged+=(_,__)=>Camera();
            Update(-1);Fit();
        }
        internal void Update(int selected)
        {
            _poses=_model.Poses();var moving=selected>=0?_model.MovingPanels(selected):null;
            for(int i=0;i<_parts.Length;i++)
            {
                _parts[i].Transform=new MatrixTransform3D(_poses[i]);
                _parts[i].Material=_parts[i].BackMaterial=moving!=null && moving.Contains(i)?_moving:_steel;
            }
        }
        internal void Fit(bool resetView=false)
        {
            if(resetView){_yaw=-25;_pitch=53;}
            var box=Rect3D.Empty;
            for(int i=0;i<_parts.Length;i++)box.Union(new MatrixTransform3D(_poses[i]).TransformBounds(_parts[i].Geometry.Bounds));
            _target=new Point3D(box.X+box.SizeX/2,box.Y+box.SizeY/2,box.Z+box.SizeZ/2);
            _radius=Math.Max(1,Math.Sqrt(box.SizeX*box.SizeX+box.SizeY*box.SizeY+box.SizeZ*box.SizeZ)/2);
            double aspect=ActualHeight>0?Math.Max(.1,ActualWidth/ActualHeight):1;
            _distance=_radius/Math.Sin(Math.Atan(Math.Tan(20*Math.PI/180)*Math.Min(1,aspect)))*1.12;
            Camera();
        }
        private void Camera()
        {
            double yaw=_yaw*Math.PI/180,pitch=_pitch*Math.PI/180;
            var offset=new Vector3D(Math.Sin(yaw)*Math.Cos(pitch),Math.Sin(pitch),Math.Cos(yaw)*Math.Cos(pitch))*_distance;
            _camera.Position=_target+offset;_camera.LookDirection=-offset;_camera.UpDirection=new Vector3D(0,1,0);
            _camera.NearPlaneDistance=Math.Max(.01,_distance-_radius*4);
            _camera.FarPlaneDistance=_distance+_radius*20;
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);_previous=_down=e.GetPosition(this);_dragged=false;
            _pan=e.ChangedButton!=MouseButton.Left;CaptureMouse();e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);if(!_previous.HasValue)return;
            var p=e.GetPosition(this);var delta=p-_previous.Value;_previous=p;
            if((p-_down).Length>3)_dragged=true;
            if(_pan)
            {
                var direction=_camera.LookDirection;direction.Normalize();var right=Vector3D.CrossProduct(direction,_camera.UpDirection);right.Normalize();
                var up=Vector3D.CrossProduct(right,direction);up.Normalize();
                double scale=2*_distance*Math.Tan(20*Math.PI/180)/Math.Max(1,ActualHeight);
                _target+=(-right*delta.X+up*delta.Y)*scale;
            }
            else{_yaw-=delta.X*.45;_pitch=Math.Max(-85,Math.Min(85,_pitch+delta.Y*.45));}
            Camera();e.Handled=true;
        }
        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);if(!_previous.HasValue)return;
            if(!_dragged && !_pan)
            {
                var hit=VisualTreeHelper.HitTest(_viewport,e.GetPosition(_viewport)) as RayMeshGeometry3DHitTestResult;
                if(hit!=null && _indices.TryGetValue(hit.ModelHit,out int panel))SelectPanel?.Invoke(panel);
            }
            _previous=null;ReleaseMouseCapture();e.Handled=true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { _previous=null;base.OnLostMouseCapture(e); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        { _distance=Math.Max(_radius*.08,Math.Min(_radius*80,_distance*Math.Pow(.85,e.Delta/120d)));Camera();e.Handled=true; }
        private static Material Material(Color color)
        {
            var group=new MaterialGroup();group.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
            group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(80,80,80)),35));group.Freeze();return group;
        }
    }
}
