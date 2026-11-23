using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed partial class PatternPreviewSurface
    {
        private readonly Dictionary<string,double> _motifTurns=new Dictionary<string,double>();
        private PatternPlacement[] _baseMotifs=Array.Empty<PatternPlacement>();
        private string[] _motifKeys=Array.Empty<string>();
        private string _selectedMotif,_motifLayout,_anchorScope;
        private readonly HashSet<string> _anchors=new HashSet<string>();
        internal bool SelectedMotifAnchored=>_selectedMotif!=null && _anchors.Contains(_selectedMotif);
        private bool _rotatingMotif;
        private Point _motifDragCenter;
        private Vector _motifDragVector;
        private double _motifDragAngle,_motifStartTurn;
        internal event Action MotifSelectionChanged;
        private bool CanEditMotifs=>_draft?.Options.Texture==true;
        private int SelectedMotif=>_selectedMotif==null?-1:Array.IndexOf(_motifKeys,_selectedMotif);
        internal bool HasSelectedMotif=>SelectedMotif>=0;
        internal string SelectedMotifDescription=>SelectedMotif<0?"":_draft.Motifs[_draft.Items[SelectedMotif].Motif].Label+" · "+_draft.Items[SelectedMotif].Angle.ToString("0.#",CultureInfo.CurrentCulture)+"°";

        private void UpdateMotifEdits()
        {
            if(_rotatingMotif){_rotatingMotif=false;ReleaseMouseCapture();}
            if(!CanEditMotifs){_baseMotifs=Array.Empty<PatternPlacement>();_motifKeys=Array.Empty<string>();_selectedMotif=null;MotifSelectionChanged?.Invoke();return;}
            var o=_draft.Options;
            string signature=String.Join("/",new[]{(double)o.Mode,o.Seed,o.SizeMm,o.GapMm,o.Angle,o.RepeatWidthMm,o.RepeatHeightMm,o.MinScale,o.MaxScale,o.AngleVariation,_frame.Width,_frame.Height}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)))+"/"+o.Loose+"/"+(_draft.Hero!=null)+"/"+String.Join(";",_draft.Motifs.Select(a=>a.Id+":"+a.Mirrored+":"+a.IsFiller))+"/"+(_frame.Bottle==null?"flat":_frame.Bottle.TopDiameter+":"+_frame.Bottle.BottomDiameter+":"+_frame.Bottle.Height+":"+_draft.BottleWhole);
            string scope=_frame.Width+"/"+_frame.Height+"/"+(_frame.Bottle==null?"flat":_frame.Bottle.TopDiameter+":"+_frame.Bottle.BottomDiameter+":"+_frame.Bottle.Height)+"/"+String.Join(";",_draft.Motifs.Select(a=>a.Id+":"+a.Mirrored+":"+a.IsFiller+":"+a.Size.X+":"+a.Size.Y));
            if(scope!=_anchorScope){_anchorScope=scope;_anchors.Clear();_addedMotifs.Clear();}
            if(signature!=_motifLayout){_motifLayout=signature;_motifTurns.Clear();_selectedMotif=null;_removedMotifs.Clear();_addedMotifs.RemoveAll(p=>!_anchors.Contains(p.Key));}
            _automaticMotifs=_draft.Items.Select(PatternMotifRotation.Copy).ToArray();
            if(_frame.Bottle!=null && (_addedMotifs.Count>0 || _removedMotifs.Count>0))
            {
                if(!ReflowBottle(null))throw new InvalidOperationException("Не удалось перестроить гальку вокруг опор.");
                MotifSelectionChanged?.Invoke();return;
            }
            var rows=_automaticMotifs.Select(p=>Tuple.Create(MotifKey(p),PatternMotifRotation.Copy(p)))
                .Where(p=>p.Item1==null || !_removedMotifs.Contains(p.Item1)).ToArray();
            // A moved hero can displace automatic prints. Keep a manual group only
            // when every copy still fits; retain its record so it can return
            // when that area is free again. Never silently overlap the hero.
            if(_frame.Bottle==null && _anchors.Count>0)
                rows=rows.Where(row=>_addedMotifs.Where(p=>_anchors.Contains(p.Key)).All(p=>PatternMotifRotation.Separated(_draft.Motifs[row.Item2.Motif].Footprint.At(row.Item2),p.Pose.Cell,o.GapMm))).ToArray();
            var safe=rows.ToList();int hidden=0;
            foreach(var group in _addedMotifs.GroupBy(p=>p.Key).OrderByDescending(g=>_anchors.Contains(g.Key)))
            {
                bool allowed=group.All(p=>
                {
                    var asset=_draft.Motifs[p.Pose.Motif];var hull=asset.Footprint.At(p.Pose);
                    if(_draft.Reserve!=null && _draft.Reserve.FillContainsWithDetail(PatternSilhouette.At(asset,p.Pose),.05,ToleranceType.Absolute)!=IntersectionDetail.Empty)return false;
                    if(_frame.Bottle!=null && !PatternBottleLayout.FitsOnRing(_frame,_draft.Motifs,p.Pose,safe.Select(other=>other.Item2).ToList(),o.GapMm,asset.IsFiller || _draft.BottleWhole))return false;
                    return safe.All(other=>PatternMotifRotation.Separated(hull,_draft.Motifs[other.Item2.Motif].Footprint.At(other.Item2),o.GapMm));
                });
                if(allowed)safe.AddRange(group.Select(p=>Tuple.Create(p.Key,PatternMotifRotation.Copy(p.Pose))));else if(_anchors.Contains(group.Key))throw new InvalidOperationException("Изменение перекрывает опорную гальку. Снимите опору или измените положение главного принта.");else hidden++;
            }
            rows=safe.ToArray();
            _baseMotifs=rows.Select(p=>p.Item2).ToArray();
            _draft.Items=_baseMotifs.Select(PatternMotifRotation.Copy).ToList();
            _motifKeys=rows.Select(p=>p.Item1).ToArray();
            foreach(var entry in _motifTurns.ToArray())if(!ApplyMotifTurn(entry.Key,entry.Value))_motifTurns.Remove(entry.Key);
            if(!CommitMotifItems(_draft.Items))throw new InvalidOperationException("Не удалось обновить стык бутылки после ручных правок.");
            if(SelectedMotif<0)_selectedMotif=null;
            if(hidden>0)MotifNotice?.Invoke("После изменения главного принта скрыто ручных галек: "+hidden+". Их место занято; они вернутся, когда оно освободится.");
            MotifSelectionChanged?.Invoke();
        }

        private string MotifKey(PatternPlacement item)
        {
            if(item.Cell==null || item.Cell.Length<3)return null;
            var asset=_draft.Motifs[item.Motif];var cell=item.Cell;
            if(_frame.Bottle!=null)
            {
                var middle=CellCenter(cell);var uv=_frame.Bottle.Project(new PatternPoint(middle.X+_frame.BottleOrigin.X,middle.Y+_frame.BottleOrigin.Y));
                int turns=-(int)Math.Floor((uv.X+.00001)/_frame.Bottle.Width);
                if(turns!=0)cell=cell.Select(p=>PatternBottleLayout.TurnPoint(_frame,p,turns)).ToArray();
            }
            var center=CellCenter(cell);double x=center.X,y=center.Y;
            double radians=-_draft.Options.Angle*Math.PI/180,c=Math.Cos(radians),s=Math.Sin(radians);
            if(_frame.Bottle==null && _draft.Options.Repeat)
            {
                double localX=x*c-y*s,localY=x*s+y*c;
                x=Modulo(localX,_draft.Options.RepeatWidthMm);y=Modulo(localY,_draft.Options.RepeatHeightMm);
            }
            string Number(double v)=>Math.Round(v,4).ToString("0.####",CultureInfo.InvariantCulture);
            return asset.Id+"/"+asset.IsFiller+"/"+asset.Mirrored+"/"+Number(x)+","+Number(y)+"/"+
                String.Join(";",cell.Select(p=>Number(p.X-center.X)+","+Number(p.Y-center.Y)));
        }

        private static double Modulo(double value,double period)
        {double n=(value%period+period)%period;return n<.00005 || period-n<.00005?0:n;}
        private static PatternPoint CellCenter(PatternPoint[] cell)=>new PatternPoint(cell.Average(p=>p.X),cell.Average(p=>p.Y));

        private bool ApplyMotifTurn(string key,double delta)=>FitMotifGroup(key,delta,false);

        private bool FitMotifGroup(string key,double delta,bool refit)
        {
            var indices=Enumerable.Range(0,_motifKeys.Length).Where(i=>_motifKeys[i]==key).ToArray();if(indices.Length==0)return false;
            if(Math.Abs(delta)<1e-6 && !refit){var original=_draft.Items.ToList();foreach(int i in indices)original[i]=PatternMotifRotation.Copy(_baseMotifs[i]);return CommitMotifItems(original);}
            var group=new HashSet<int>(indices);var candidates=new List<PatternPlacement>();double scale=Double.PositiveInfinity;
            foreach(int i in indices)
            {
                var original=_baseMotifs[i];var asset=_draft.Motifs[original.Motif];
                var fit=PatternMotifRotation.Fit(original,asset.Footprint,_draft.Options,original.Angle+delta);if(fit==null)return false;
                candidates.Add(fit);scale=Math.Min(scale,PatternTransform.Fit(asset.Size,fit).Scale);
                if(_draft.Options.Airy)
                {
                    var before=refit?_draft.Items[i]:original;
                    double longest=PatternHeroFill.Longest(_draft.Motifs[before.Motif],before);
                    scale=Math.Min(scale,longest/Math.Max(asset.Size.X,asset.Size.Y));
                }
            }
            Rect Bounds(PatternPoint[] hull)=>new Rect(new Point(hull.Min(p=>p.X),hull.Min(p=>p.Y)),new Point(hull.Max(p=>p.X),hull.Max(p=>p.Y)));
            var cells=candidates.Select(p=>{var box=Bounds(p.Cell);box.Inflate(_draft.Options.GapMm+.001,_draft.Options.GapMm+.001);return box;}).ToArray();
            var obstacles=new List<Tuple<Rect,PatternPoint[]>>();
            for(int j=0;j<_draft.Items.Count;j++)if(!group.Contains(j))
            {
                var other=_draft.Items[j];var hull=_draft.Motifs[other.Motif].Footprint.At(other);var box=Bounds(hull);
                if(cells.Any(cell=>cell.IntersectsWith(box)))obstacles.Add(Tuple.Create(box,hull));
            }
            bool Allowed(double candidateScale)
            {
                var hulls=new List<PatternPoint[]>();
                foreach(var pose in candidates)
                {
                    var asset=_draft.Motifs[pose.Motif];var item=PatternMotifRotation.Resize(pose,asset.Footprint,candidateScale);var hull=asset.Footprint.At(item);
                    if(_frame.Bottle!=null && !PatternBottleLayout.FitsOnRing(_frame,_draft.Motifs,item,_draft.Items.Where((p,index)=>!group.Contains(index)).ToList(),_draft.Options.GapMm,asset.IsFiller || _draft.BottleWhole))return false;
                    var area=Bounds(hull);area.Inflate(_draft.Options.GapMm+.001,_draft.Options.GapMm+.001);
                    if(_draft.Reserve!=null && _draft.Reserve.Bounds.IntersectsWith(area) &&
                        _draft.Reserve.FillContainsWithDetail(PatternSilhouette.At(asset,item),.05,ToleranceType.Absolute)!=IntersectionDetail.Empty)return false;
                    foreach(var obstacle in obstacles)if(area.IntersectsWith(obstacle.Item1) && !PatternMotifRotation.Separated(hull,obstacle.Item2,_draft.Options.GapMm))return false;
                    foreach(var other in hulls)if(!PatternMotifRotation.Separated(hull,other,_draft.Options.GapMm))return false;
                    hulls.Add(hull);
                }
                return true;
            }
            if(!Allowed(scale))
            {
                double low=scale*.00001,high=scale;if(!Allowed(low))return false;
                for(int n=0;n<16;n++){double mid=(low+high)/2;if(Allowed(mid))low=mid;else high=mid;}
                scale=low;
            }
            var updated=_draft.Items.ToList();
            for(int n=0;n<indices.Length;n++)updated[indices[n]]=PatternMotifRotation.Resize(candidates[n],_draft.Motifs[candidates[n].Motif].Footprint,scale);
            return CommitMotifItems(updated);
        }

        internal void ResetSelectedMotif()
        {
            if(SelectedMotif<0 || SelectedMotifAnchored)return;_motifTurns.Remove(_selectedMotif);ApplyMotifTurn(_selectedMotif,0);
            // Reapply edited neighbours from the original layout so removing
            // one override cannot leave a stale, conflicting pose behind.
            _draft.Items=_baseMotifs.Select(PatternMotifRotation.Copy).ToList();
            foreach(var turn in _motifTurns.ToArray())if(!ApplyMotifTurn(turn.Key,turn.Value))_motifTurns.Remove(turn.Key);
            CommitMotifItems(_draft.Items);
            InvalidateVisual();MotifSelectionChanged?.Invoke();
        }
        internal void ClearMotifSelection(){_selectedMotif=null;InvalidateVisual();MotifSelectionChanged?.Invoke();}

        private bool SelectMotif(Point screen)=>SelectMotifAt(screen,true);

        private bool SelectMotifAt(Point screen,bool cellFallback)
        {
            if(!CanEditMotifs || _scale<=0)return false;
            var world=_viewport.World(screen);var point=new PatternPoint(world.X,_frame.Height-world.Y);
            if(!(_draft.BottleCutView??_frame.Geometry).FillContains(world))return false;
            int found=FindMotif(point,cellFallback);
            if(found>=0){_selectedMotif=_motifKeys[found];InvalidateVisual();MotifSelectionChanged?.Invoke();return true;}
            ClearMotifSelection();return false;
        }

        private Point MotifHandle(out Point center)
        {
            int i=SelectedMotif;var item=_draft.Items[i];center=ToScreen(CellCenter(item.Cell));
            double radius=Math.Max(item.Cell.Max(p=>p.X)-item.Cell.Min(p=>p.X),item.Cell.Max(p=>p.Y)-item.Cell.Min(p=>p.Y))*_scale/2+20;
            var handle=new RotateTransform(-item.Angle,center.X,center.Y).Transform(new Point(center.X,center.Y-radius));
            return new Point(Math.Max(10,Math.Min(RenderSize.Width-10,handle.X)),Math.Max(10,Math.Min(RenderSize.Height-10,handle.Y)));
        }
        private bool HitMotifHandle(Point point)=>!SelectedMotifAnchored && SelectedMotif>=0 && (point-MotifHandle(out _)).Length<=9;
        private void DrawMotifHandle(DrawingContext drawing)
        {
            int i=SelectedMotif;if(i<0)return;var cell=_draft.Items[i].Cell;var pen=new Pen(SelectedMotifAnchored?Brushes.Crimson:Brushes.DarkOrange,1.5){DashStyle=DashStyles.Dash};
            for(int n=0;n<cell.Length;n++)drawing.DrawLine(pen,ToScreen(cell[n]),ToScreen(cell[(n+1)%cell.Length]));
            if(SelectedMotifAnchored)return;
            var handle=MotifHandle(out var center);pen=new Pen(Brushes.DarkOrange,1.5);drawing.DrawLine(pen,center,handle);
            drawing.DrawEllipse(Brushes.White,pen,handle,5,5);
        }
        private bool StartMotifTurn(Point point)
        {
            if(SelectedMotifAnchored || !HitMotifHandle(point) || !CaptureMouse())return false;
            MotifHandle(out _motifDragCenter);_motifDragVector=point-_motifDragCenter;_motifDragAngle=0;
            _motifStartTurn=_motifTurns.TryGetValue(_selectedMotif,out var turn)?turn:0;_rotatingMotif=true;Cursor=Cursors.Hand;return true;
        }
        private void TurnMotifTo(Point point)
        {
            var vector=point-_motifDragCenter;if(vector.Length<2 || _motifDragVector.Length<2)return;
            _motifDragAngle-=Vector.AngleBetween(_motifDragVector,vector);_motifDragVector=vector;
            double delta=(_motifStartTurn+_motifDragAngle)%360;
            if(ApplyMotifTurn(_selectedMotif,delta)){if(Math.Abs(delta)<1e-6)_motifTurns.Remove(_selectedMotif);else _motifTurns[_selectedMotif]=delta;InvalidateVisual();MotifSelectionChanged?.Invoke();}
        }
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if(e.Key==Key.Escape && HasSelectedMotif)
            {
                if(_rotatingMotif){ApplyMotifTurn(_selectedMotif,_motifStartTurn);if(Math.Abs(_motifStartTurn)<1e-6)_motifTurns.Remove(_selectedMotif);else _motifTurns[_selectedMotif]=_motifStartTurn;_rotatingMotif=false;ReleaseMouseCapture();InvalidateVisual();MotifSelectionChanged?.Invoke();}
                else ClearMotifSelection();e.Handled=true;return;
            }
            base.OnPreviewKeyDown(e);
        }
    }
}
