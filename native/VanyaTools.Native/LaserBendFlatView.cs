using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class LaserBendFlatView : FrameworkElement
    {
        private readonly LaserBendModel _model;
        private readonly PatternViewport _view = new PatternViewport();
        private Point? _pan;
        internal int Selected;
        internal event Action<int> SelectFold;
        internal event Action<int> SelectPanel;
        internal LaserBendFlatView(LaserBendModel model)
        {
            _model = model; ClipToBounds = true; Focusable = true;
            SizeChanged += (_, __) => InvalidateVisual();
            ToolTip = "Нажмите на линию сгиба, затем на сгибаемую часть. Колесо — масштаб; правая кнопка — перемещение.";
        }
        internal void Fit() { _view.Fit(); InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(244,246,248)), null, new Rect(RenderSize));
            _view.Update(_model.Bounds, RenderSize); if (_view.Scale <= 0) return;
            var moving = Selected >= 0 && Selected < _model.Lines.Length ? _model.MovingPanels(Selected) : null;
            dc.PushTransform(new TranslateTransform(_view.Origin.X, _view.Origin.Y));
            dc.PushTransform(new ScaleTransform(_view.Scale, _view.Scale));
            var border = new Pen(new SolidColorBrush(Color.FromRgb(94,109,122)), 1 / _view.Scale);
            for(int i=0;i<_model.Panels.Length;i++)
                dc.DrawGeometry(new SolidColorBrush(moving != null && moving.Contains(i) ? Color.FromRgb(132,202,239) : Color.FromRgb(218,225,231)), border, _model.Panels[i].Shape);
            for(int i=0;i<_model.Lines.Length;i++)
            {
                var fold=_model.Lines[i]; bool selected=i==Selected;
                var pen=new Pen(selected ? Brushes.DarkOrange : Brushes.SlateGray, (selected?3:1.4)/_view.Scale) { DashStyle=DashStyles.Dash };
                dc.DrawLine(pen,fold.A,fold.B);
            }
            dc.Pop(); dc.Pop();
            for(int i=0;i<_model.Lines.Length;i++)
            {
                var fold=_model.Lines[i]; var mid=new Point((fold.A.X+fold.B.X)/2,(fold.A.Y+fold.B.Y)/2);
                var p=new Point(_view.Origin.X+mid.X*_view.Scale,_view.Origin.Y+mid.Y*_view.Scale);
                dc.DrawEllipse(i==Selected?Brushes.DarkOrange:Brushes.SlateGray, new Pen(Brushes.White,1),p,11,11);
                var text=new FormattedText((i+1).ToString(),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),12,Brushes.White,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text,new Point(p.X-text.Width/2,p.Y-text.Height/2));
            }
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            if(e.ChangedButton==MouseButton.Right || e.ChangedButton==MouseButton.Middle)
            { _pan=e.GetPosition(this); CaptureMouse(); e.Handled=true; return; }
            if(e.ChangedButton!=MouseButton.Left || _view.Scale<=0)return;
            var point=_view.World(e.GetPosition(this)); double best=9/_view.Scale; int nearest=-1;
            for(int i=0;i<_model.Lines.Length;i++)
            {
                var line=_model.Lines[i];var axis=line.B-line.A;
                double t=Math.Max(0,Math.Min(1,Vector.Multiply(point-line.A,axis)/axis.LengthSquared));
                double distance=(point-(line.A+axis*t)).Length;
                if(distance<best){best=distance;nearest=i;}
            }
            if(nearest>=0)SelectFold?.Invoke(nearest);
            else for(int i=0;i<_model.Panels.Length;i++)if(_model.Panels[i].Shape.FillContains(point)){SelectPanel?.Invoke(i);break;}
            e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if(!_pan.HasValue)return;
            var p=e.GetPosition(this);_view.Pan(p-_pan.Value);_pan=p;InvalidateVisual();e.Handled=true;
        }
        protected override void OnMouseUp(MouseButtonEventArgs e)
        { base.OnMouseUp(e); if(_pan.HasValue){_pan=null;ReleaseMouseCapture();e.Handled=true;} }
        protected override void OnLostMouseCapture(MouseEventArgs e) { _pan=null; base.OnLostMouseCapture(e); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        { _view.ZoomAt(Math.Pow(1.15,e.Delta/120d),e.GetPosition(this));InvalidateVisual();e.Handled=true; }
    }
}
