using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal enum FrameBagView { Side, Front, Pieces }

    internal sealed class FrameBagPreview : FrameworkElement
    {
        private readonly FrameBagView _view;
        private readonly PatternViewport _viewport = new PatternViewport();
        private FrameBagPlan _plan;
        private bool _pan;
        private Point _last;
        private int _editSide, _editBag, _anchor = -1, _divisionAnchor = -1, _divisionSelected = -1;
        private BagPoint[] _divisionWorking;
        internal event Action<int, int, double, double> AnchorChanged;
        internal event Action<BagPoint[]> DivisionChanged;
        internal event Action<bool> DivisionSelectionChanged;
        internal void EditSide(int side)
        {
            side = side == 1 ? 1 : 0;
            if (side == _editSide) return;
            if (IsMouseCaptured) ReleaseMouseCapture();
            double center = MirrorX(_viewport.Center.X);
            if (_viewport.Scale > 0 && _plan != null)
                _viewport.Pan(new Vector((_viewport.Center.X - center) * _viewport.Scale, 0));
            _editSide = side; InvalidateVisual();
        }
        internal void EditBag(int bag)
        {
            bag = bag == 1 ? 1 : 0;
            if (bag == _editBag) return;
            if (IsMouseCaptured) ReleaseMouseCapture();
            _editBag = bag; InvalidateVisual();
        }
        internal FrameBagPlan ZipperPlan => _plan?.Sections.Length == 2 ? _plan.Sections[_editBag] : _plan;

        internal FrameBagPreview(FrameBagView view)
        { _view = view; ClipToBounds = true; Focusable = true; ToolTip = "Колесо — масштаб. Средняя/правая кнопка или пробел + перетаскивание — перемещение."; }
        internal void Update(FrameBagPlan plan)
        {
            _plan = plan;
            if (!IsMouseCaptured) _divisionWorking = null;
            if (_divisionSelected >= (plan?.Setup.DivisionLine?.Length ?? 0)) _divisionSelected = -1;
            DivisionSelectionChanged?.Invoke(CanDeleteDivisionPoint); InvalidateVisual();
        }
        internal void Fit() { _viewport.Fit(); InvalidateVisual(); }
        internal void Zoom(double factor) { _viewport.ZoomAt(factor, new Point(ActualWidth / 2, ActualHeight / 2)); InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
            if (_plan == null || ActualWidth < 30 || ActualHeight < 30) return;
            var plan = _plan;
            double minX = plan.Frame.Min(p => p.X), maxX = plan.Frame.Max(p => p.X), maxY = plan.Frame.Max(p => p.Y);
            Rect bounds = _view == FrameBagView.Pieces ? new Rect(0, 0, plan.KitWidth, plan.KitHeight) :
                _view == FrameBagView.Side ? new Rect(minX - 30, -35, maxX - minX + 65, maxY + 65) :
                new Rect(-Math.Max((maxX - minX) / 6, Math.Max(plan.Setup.TopDepth, plan.Setup.BottomDepth) / 2 + 35), -35,
                    Math.Max((maxX - minX) / 3, Math.Max(plan.Setup.TopDepth, plan.Setup.BottomDepth) + 70), maxY + 65);
            if (_view == FrameBagView.Side && plan.Setup.DivideBags && plan.Setup.DivisionLine != null)
            {
                try
                {
                    var line = FrameBagDivision.ReadLine(plan.Setup);
                    bounds.Union(new Rect(line.Min(p => p.X) - 20, line.Min(p => p.Y) - 25,
                        line.Max(p => p.X) - line.Min(p => p.X) + 40, line.Max(p => p.Y) - line.Min(p => p.Y) + 50));
                }
                catch (ArgumentException) { }
            }
            _viewport.Update(bounds, RenderSize);
            double scale = _viewport.Scale;
            if (scale <= 0) return;
            var origin = _viewport.Origin;
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, origin.X, origin.Y));
            double hair = 1 / scale;
            var seam = new Pen(Brushes.SeaGreen, hair * 1.7);
            var fill = new SolidColorBrush(Color.FromArgb(95, 60, 180, 125));
            if (_view == FrameBagView.Side)
            {
                Polygon(dc, plan.Frame, null, new Pen(Brushes.SlateGray, hair * 4));
                var diagonal = new Pen(Brushes.MediumPurple, hair) { DashStyle = DashStyles.Dash };
                if (!plan.Setup.TriangleFrame) dc.DrawLine(diagonal, Point(plan.Frame[0]), Point(plan.Frame[2]));
                Polygon(dc, plan.Bag, fill, seam);
                if (plan.Sections.Length == 2)
                {
                    Polygon(dc, plan.Sections[0].Bag, new SolidColorBrush(Color.FromArgb(50, 50, 175, 100)), seam);
                    Polygon(dc, plan.Sections[1].Bag, new SolidColorBrush(Color.FromArgb(65, 60, 130, 230)), seam);
                }
                if (plan.Sections.Length == 2)
                    for (int n = 0; n < plan.Sections.Length; n++) DrawZipper(dc, plan.Sections[n], hair, n == _editBag);
                else DrawZipper(dc, plan, hair, true);
                DrawDivision(dc, plan, hair);
                for (int n = 0; n < plan.Frame.Length; n++)
                {
                    var at = plan.Frame[n]; dc.DrawEllipse(Brushes.White, new Pen(Brushes.SlateGray, hair), Point(at), hair * 3, hair * 3);
                    Text(dc, ((char)('A' + n)).ToString(), at.X + 6 * hair, at.Y - 19 * hair, 13 * hair, Brushes.DimGray);
                }
                string[] labels = plan.Setup.TriangleFrame ? new[] { "Верхняя " + N(plan.Setup.Top), "Нижняя " + N(plan.Setup.Down), "Подседельная " + N(plan.Setup.Seat) } :
                    new[] { "Верхняя " + N(plan.Setup.Top), "Стакан " + N(plan.Setup.Head), "Нижняя " + N(plan.Setup.Down), "Подседельная " + N(plan.Setup.Seat) };
                for (int n = 0; n < plan.Frame.Length; n++)
                {
                    var a = plan.Frame[n]; var b = plan.Frame[(n + 1) % plan.Frame.Length];
                    Text(dc, labels[n], (a.X + b.X) / 2, (a.Y + b.Y) / 2 - 16 * hair, 11 * hair, Brushes.SlateGray);
                }
                if (!plan.Setup.TriangleFrame) Text(dc, "A–C: " + N(plan.Setup.Diagonal), plan.Frame[2].X / 2, plan.Frame[2].Y / 2, 11 * hair, Brushes.MediumPurple);
                for(int n=0;n<plan.Breaks.Length;n++)
                {
                    var point=plan.Bag[plan.Breaks[n]];
                    Text(dc,"Угол " + (char)('A'+n),point.X+3*hair,point.Y+3*hair,10*hair,Brushes.SeaGreen);
                }
            }
            else if (_view == FrameBagView.Front)
            {
                double a = plan.Setup.TopDepth / 2, b = plan.Setup.BottomDepth / 2, h = plan.ActualHeight;
                Polygon(dc, new[] { new BagPoint(-a, 0), new BagPoint(a, 0), new BagPoint(b, h), new BagPoint(-b, h) }, fill, seam);
                var axis = new Pen(Brushes.LightSlateGray, hair) { DashStyle = DashStyles.Dash };
                dc.DrawLine(axis, new Point(0, -10), new Point(0, h + 10));
                if (plan.Sections.Length == 2 && plan.DivisionPath.Length >= 2)
                {
                    var ends = new[] { plan.DivisionPath[0], plan.DivisionPath[plan.DivisionPath.Length - 1] };
                    double y = ends.OrderByDescending(p => p.X).First().Y - plan.Bag.Min(p => p.Y);
                    double half = a + (b - a) * y / h;
                    dc.DrawLine(new Pen(Brushes.DarkOrange, 2 * hair) { DashStyle = DashStyles.Dash }, new Point(-half, y), new Point(half, y));
                }
                Text(dc, N(plan.Setup.TopDepth) + " мм", -a, -22 * hair, 12 * hair, Brushes.SeaGreen);
                Text(dc, N(plan.Setup.BottomDepth) + " мм", -b, h + 7 * hair, 12 * hair, Brushes.SeaGreen);
                Text(dc, N(h) + " мм", Math.Max(a, b) + 8 * hair, h / 2, 12 * hair, Brushes.SeaGreen);
            }
            else
            {
                foreach (var part in plan.Parts)
                {
                    dc.PushTransform(new TranslateTransform(part.Left, part.Top));
                    Polygon(dc, part.Cut, new SolidColorBrush(Color.FromArgb(20, 200, 50, 50)), new Pen(Brushes.Firebrick, hair));
                    foreach (var hole in part.Cutouts) Polygon(dc, hole, Brushes.White, new Pen(Brushes.Firebrick, hair));
                    foreach (var mark in part.Marks) dc.DrawLine(new Pen(Brushes.SteelBlue, hair), Point(mark[0]), Point(mark[mark.Length - 1]));
                    Polygon(dc, part.Seam, null, new Pen(Brushes.SteelBlue, hair) { DashStyle = DashStyles.Dash });
                    Text(dc, part.Name, part.Cut.Min(p => p.X) + 4, part.Cut.Min(p => p.Y) + 4, 11 * hair, Brushes.DimGray);
                    dc.Pop();
                }
            }
            dc.Pop();
        }

        private void DrawDivision(DrawingContext dc, FrameBagPlan plan, double hair)
        {
            if (!plan.Setup.DivideBags || plan.Setup.DivisionLine == null) return;
            BagPoint[] line;
            try { line = _divisionWorking ?? FrameBagDivision.ReadLine(plan.Setup); } catch (ArgumentException) { return; }
            var pen = new Pen(plan.Sections.Length == 2 ? Brushes.DarkOrange : Brushes.OrangeRed, 2 * hair) { DashStyle = DashStyles.Dash };
            for (int n = 0; n + 1 < line.Length; n++) dc.DrawLine(pen, Point(line[n]), Point(line[n + 1]));
            for (int n = 0; n < line.Length; n++)
            {
                dc.DrawEllipse(n == _divisionSelected ? Brushes.Gold : Brushes.White, pen, Point(line[n]), 5 * hair, 5 * hair);
                Text(dc, "Р" + (n + 1), line[n].X + 7 * hair, line[n].Y + 5 * hair, 11 * hair, Brushes.DarkOrange);
            }
        }
        internal bool CanDeleteDivisionPoint => _plan?.Setup.DivideBags == true && _divisionSelected > 0 &&
            _divisionSelected < (_divisionWorking?.Length ?? _plan.Setup.DivisionLine?.Length ?? 0) - 1;
        internal void DeleteDivisionPoint()
        {
            if (!CanDeleteDivisionPoint) return;
            var line = _divisionWorking ?? FrameBagDivision.ReadLine(_plan.Setup);
            var next = line.Where((_, index) => index != _divisionSelected).ToArray();
            _divisionSelected = -1; _divisionWorking = null;
            DivisionChanged?.Invoke(next); DivisionSelectionChanged?.Invoke(false); InvalidateVisual();
        }
        internal void MoveDivisionPoint(int index, BagPoint at)
        {
            if (_plan?.Setup.DivideBags != true) return;
            var line = _divisionWorking ?? FrameBagDivision.ReadLine(_plan.Setup);
            if (index < 0 || index >= line.Length) return;
            line = (BagPoint[])line.Clone(); line[index] = at;
            _divisionSelected = index; _divisionWorking = line;
            DivisionChanged?.Invoke((BagPoint[])line.Clone()); DivisionSelectionChanged?.Invoke(CanDeleteDivisionPoint); InvalidateVisual();
        }
        internal int AddDivisionPoint(BagPoint at, double tolerance)
        {
            if (_plan?.Setup.DivideBags != true) return -1;
            var line = _divisionWorking ?? FrameBagDivision.ReadLine(_plan.Setup);
            if (line.Length >= FrameBagDivision.MaximumPoints) return -1;
            int best = -1; double nearest = tolerance; BagPoint projected = at;
            for (int n = 0; n + 1 < line.Length; n++)
            {
                var d = BagPoint.Sub(line[n + 1], line[n]); var r = BagPoint.Sub(at, line[n]);
                if (d.Length < .000001) continue;
                double t = Math.Max(0, Math.Min(1, (r.X * d.X + r.Y * d.Y) / (d.X * d.X + d.Y * d.Y)));
                var point = BagPoint.Add(line[n], BagPoint.Mul(d, t));
                double distance = BagPoint.Sub(point, at).Length;
                if (distance > nearest || BagPoint.Sub(point, line[n]).Length < .2 || BagPoint.Sub(point, line[n + 1]).Length < .2) continue;
                best = n; nearest = distance; projected = point;
            }
            if (best < 0 || !FrameBagClosures.Inside(_plan.Bag, projected)) return -1;
            _divisionWorking = line.Take(best + 1).Concat(new[] { projected }).Concat(line.Skip(best + 1)).ToArray();
            _divisionSelected = best + 1;
            DivisionChanged?.Invoke((BagPoint[])_divisionWorking.Clone()); DivisionSelectionChanged?.Invoke(true); InvalidateVisual();
            return _divisionSelected;
        }

        private void DrawZipper(DrawingContext dc, FrameBagPlan plan, double hair, bool selected)
        {
            var mode = FrameBagClosures.Mode(plan.Setup, _editSide);
            if (mode == BagZipper.None) return;
            var a = FrameBagClosures.Anchor(plan, _editSide, 0); var b = FrameBagClosures.Anchor(plan, _editSide, 1);
            var flatA = FrameBagClosures.Flat(plan, a); var flatB = FrameBagClosures.Flat(plan, b);
            var direction = BagPoint.Sub(b, a);
            bool valid = FrameBagClosures.Inside(plan.SideSeam, flatA) && FrameBagClosures.Inside(plan.SideSeam, flatB);
            var pen = new Pen(valid ? (selected ? Brushes.DarkSlateBlue : Brushes.SlateGray) : Brushes.Red, 1.5 * hair);
            if (direction.Length > plan.Setup.ZipperWidth + 5)
            {
                if (mode == BagZipper.Split)
                {
                    var clip = new StreamGeometry();
                    using (var path = clip.Open())
                    {
                        path.BeginFigure(Point(plan.Bag[0]), true, true);
                        foreach(var point in plan.Bag.Skip(1))path.LineTo(Point(point),true,false);
                    }
                    dc.PushClip(clip);
                    dc.DrawLine(new Pen(pen.Brush, plan.Setup.ZipperWidth) { DashStyle = DashStyles.Solid },
                        Point(BagPoint.Sub(a,BagPoint.Mul(direction,100))),Point(BagPoint.Add(b,BagPoint.Mul(direction,100))));
                    dc.Pop();
                }
                else
                {
                    var slot = FrameBagClosures.Capsule(flatA,flatB,plan.Setup.ZipperWidth);
                    valid = FrameBagClosures.ContainsPath(plan.SideSeam, slot);
                    Polygon(dc,slot.Select(p=>FrameBagClosures.Project(plan,p)).ToArray(),Brushes.White,
                        new Pen(valid?pen.Brush:Brushes.Red,hair));
                    if(mode==BagZipper.Flap)
                    {
                        var cover=FrameBagClosures.FlapCover(plan,flatA,flatB);
                        bool fits=FrameBagClosures.ContainsPath(plan.SideSeam, cover);
                        Polygon(dc,cover.Select(p=>FrameBagClosures.Project(plan,p)).ToArray(),
                            new SolidColorBrush(Color.FromArgb(70,235,175,35)),new Pen(fits?Brushes.DarkGoldenrod:Brushes.Red,hair){DashStyle=DashStyles.Dash});
                    }
                }
            }
            dc.DrawLine(new Pen(pen.Brush,hair){DashStyle=DashStyles.Dash},Point(a),Point(b));
            Text(dc,N(BagPoint.Sub(flatB,flatA).Length)+" мм",(a.X+b.X)/2,(a.Y+b.Y)/2-18*hair,11*hair,pen.Brush);
            if (!selected) return;
            foreach(var pair in new[]{Tuple.Create(a,"М1"),Tuple.Create(b,"М2")})
            {
                dc.DrawEllipse(Brushes.White,pen,Point(pair.Item1),5*hair,5*hair);
                Text(dc,pair.Item2,pair.Item1.X+7*hair,pair.Item1.Y-15*hair,12*hair,pen.Brush);
            }
        }

        private double MirrorX(double x) => _plan == null ? x : _plan.Frame.Min(p => p.X) + _plan.Frame.Max(p => p.X) - x;
        private bool Mirrored => _view == FrameBagView.Side && _editSide == 1;
        private Point Point(BagPoint p) => new Point(Mirrored ? MirrorX(p.X) : p.X, p.Y);
        private Point World(Point screen)
        {
            var point = _viewport.World(screen);
            return new Point(Mirrored ? MirrorX(point.X) : point.X, point.Y);
        }
        private static string N(double n) => n.ToString("0.#", CultureInfo.CurrentCulture);
        private void Polygon(DrawingContext dc, BagPoint[] points, Brush fill, Pen pen)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(Point(points[0]), fill != null, true);
                foreach (var p in points.Skip(1)) path.LineTo(Point(p), true, false);
            }
            dc.DrawGeometry(fill, pen, geometry);
        }
        private void Text(DrawingContext dc, string text, double x, double y, double size, Brush color)
        {
            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(Mirrored ? MirrorX(x) - label.Width : x, y));
        }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _viewport.Pan(new Vector(e.Delta, 0));
            else _viewport.ZoomAt(Math.Pow(1.2, e.Delta / 120d), e.GetPosition(this));
            InvalidateVisual(); e.Handled = true;
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (_view == FrameBagView.Side && _plan?.Setup.DivideBags == true && e.ChangedButton == MouseButton.Left && !Keyboard.IsKeyDown(Key.Space))
            {
                var at = World(e.GetPosition(this)); var line = FrameBagDivision.ReadLine(_plan.Setup);
                for (int n = 0; n < line.Length; n++)
                {
                    if (BagPoint.Sub(line[n], new BagPoint(at.X, at.Y)).Length * _viewport.Scale > 12) continue;
                    _divisionSelected = n; DivisionSelectionChanged?.Invoke(CanDeleteDivisionPoint);
                    if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { DeleteDivisionPoint(); e.Handled = true; return; }
                    if (!CaptureMouse()) return;
                    _divisionWorking = (BagPoint[])line.Clone(); _divisionAnchor = n; Focus(); Cursor = Cursors.Hand;
                    InvalidateVisual(); e.Handled = true; return;
                }
            }
            if (_view == FrameBagView.Side && _plan != null && e.ChangedButton == MouseButton.Left && !Keyboard.IsKeyDown(Key.Space) &&
                FrameBagClosures.Mode(ZipperPlan.Setup, _editSide) != BagZipper.None)
            {
                var point = World(e.GetPosition(this));
                for (int n=0;n<2;n++)
                {
                    var at=FrameBagClosures.Anchor(ZipperPlan,_editSide,n);
                    if ((new Point(at.X,at.Y)-point).Length*_viewport.Scale>12)continue;
                    if(!CaptureMouse())return;
                    Focus();_anchor=n;Cursor=Cursors.Hand;e.Handled=true;return;
                }
            }
            if (_view == FrameBagView.Side && _plan?.Setup.DivideBags == true && e.ChangedButton == MouseButton.Left && !Keyboard.IsKeyDown(Key.Space))
            {
                var at = World(e.GetPosition(this));
                int index = AddDivisionPoint(new BagPoint(at.X, at.Y), 8 / _viewport.Scale);
                if (index >= 0)
                {
                    if (CaptureMouse()) { _divisionAnchor = index; Focus(); Cursor = Cursors.Hand; }
                    e.Handled = true; return;
                }
            }
            if(e.ChangedButton!=MouseButton.Middle && e.ChangedButton!=MouseButton.Right && !Keyboard.IsKeyDown(Key.Space)) return;
            if(!CaptureMouse())return;
            Focus(); _pan=true; _last=e.GetPosition(this); Cursor=Cursors.Hand; e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_divisionAnchor >= 0 && _plan != null)
            {
                var at = World(e.GetPosition(this)); MoveDivisionPoint(_divisionAnchor, new BagPoint(at.X, at.Y));
                e.Handled = true; return;
            }
            if (_anchor>=0 && _plan!=null)
            {
                var at=World(e.GetPosition(this));
                AnchorChanged?.Invoke(_editSide, _anchor, at.X, at.Y);
                e.Handled=true;return;
            }
            if(!_pan)return;
            var next=e.GetPosition(this); _viewport.Pan(next-_last); _last=next; InvalidateVisual(); e.Handled=true;
        }
        protected override void OnMouseUp(MouseButtonEventArgs e) { base.OnMouseUp(e); if(_pan || _anchor>=0 || _divisionAnchor>=0){ReleaseMouseCapture();e.Handled=true;} }
        protected override void OnLostMouseCapture(MouseEventArgs e) {base.OnLostMouseCapture(e);_pan=false;_anchor=-1;_divisionAnchor=-1;_divisionWorking=null;Cursor=Cursors.Arrow;}
    }
}
