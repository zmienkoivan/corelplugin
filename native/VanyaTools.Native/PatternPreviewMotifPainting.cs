using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed partial class PatternPreviewSurface
    {
        private sealed class AddedMotif {internal string Key;internal PatternPlacement Pose;}
        private readonly List<AddedMotif> _addedMotifs=new List<AddedMotif>();
        private readonly HashSet<string> _removedMotifs=new HashSet<string>();
        private int _motifTool,_manualMotifId,_nextMotif;
        private PatternPlacement[] _automaticMotifs=Array.Empty<PatternPlacement>();
        internal bool SupportsMotifEditing=>CanEditMotifs;
        internal event Action<string> MotifNotice;
        internal void SetMotifTool(int tool)
        {_motifTool=tool;_rotatingMotif=false;ReleaseMouseCapture();ClearMotifSelection();}

        internal void ClearMotifs()
        {
            if(!CanEditMotifs)return;
            foreach(string key in _motifKeys.Where(k=>k!=null))_removedMotifs.Add(key);
            _addedMotifs.Clear();_motifTurns.Clear();_selectedMotif=null;_baseMotifs=Array.Empty<PatternPlacement>();_motifKeys=Array.Empty<string>();_draft.Items.Clear();
            InvalidateVisual();MotifSelectionChanged?.Invoke();MotifNotice?.Invoke("Раскладка очищена локально. Добавляйте основные гальки и заполнители кликами.");
        }
        internal void RestoreAutomaticMotifs()
        {
            if(!CanEditMotifs)return;
            _addedMotifs.Clear();_removedMotifs.Clear();_motifTurns.Clear();_selectedMotif=null;
            _draft.Items=_automaticMotifs.Select(PatternMotifRotation.Copy).ToList();UpdateMotifEdits();InvalidateVisual();
            MotifNotice?.Invoke("Автоматическая раскладка восстановлена.");
        }

        private int FindMotif(PatternPoint point,bool cellFallback)
        {
            for(int i=_draft.Items.Count-1;i>=0;i--)
            {
                if(_motifKeys[i]==null)continue;var item=_draft.Items[i];var asset=_draft.Motifs[item.Motif];
                if(PatternMotifRotation.Contains(asset.Footprint.At(item),point) && PatternSilhouette.At(asset,item).FillContains(new Point(point.X,point.Y)))return i;
            }
            if(cellFallback)for(int i=_draft.Items.Count-1;i>=0;i--)if(_motifKeys[i]!=null && PatternMotifRotation.Contains(_draft.Items[i].Cell,point))return i;
            return -1;
        }

        private bool PaintMotif(Point screen)
        {
            if(!CanEditMotifs || _scale<=0)return false;
            var view=_viewport.World(screen);if(!_frame.Geometry.FillContains(view))return false;
            var point=new PatternPoint(view.X,_frame.Height-view.Y);
            if(_motifTool==3)return EraseMotif(point);
            return AddMotif(point,_motifTool==2);
        }

        private bool EraseMotif(PatternPoint point)
        {
            int found=FindMotif(point,false);if(found<0)return false;string key=_motifKeys[found];
            _removedMotifs.Add(key);_addedMotifs.RemoveAll(p=>p.Key==key);_motifTurns.Remove(key);
            var kept=Enumerable.Range(0,_baseMotifs.Length).Where(i=>_motifKeys[i]!=key).ToArray();
            _baseMotifs=kept.Select(i=>_baseMotifs[i]).ToArray();_draft.Items=kept.Select(i=>_draft.Items[i]).ToList();_motifKeys=kept.Select(i=>_motifKeys[i]).ToArray();
            _selectedMotif=null;PatternHeroFill.LimitFillerSizes(_draft);InvalidateVisual();MotifSelectionChanged?.Invoke();MotifNotice?.Invoke("Галька удалена. Остальные ячейки сохранены.");return true;
        }

        private bool AddMotif(PatternPoint point,bool filler)
        {
            var choices=Enumerable.Range(0,_draft.Motifs.Length).Where(i=>_draft.Motifs[i].IsFiller==filler).ToArray();
            if(choices.Length==0){MotifNotice?.Invoke(filler?"Добавьте в набор мини-заполнители. Основной принт не будет уменьшен вместо них.":"Добавьте основные рисунки в набор.");return false;}
            if(_draft.Reserve?.FillContains(new Point(point.X,point.Y))==true || FindMotif(point,false)>=0)
            {MotifNotice?.Invoke("Здесь уже есть рисунок. Выберите свободное место или удалите гальку ластиком.");return false;}
            double desired=filler?PatternHeroFill.FillerSizeAt(_draft,point):_draft.Options.SizeMm;
            if(desired<.1)return false;
            // Reserve the automatic poses too: resetting a turned neighbour must
            // remain possible without colliding with a newly added print.
            var obstacles=_draft.Items.Concat(_baseMotifs).Select(p=>_draft.Motifs[p.Motif].Footprint.At(p)).ToArray();
            var polygon=new List<PatternPoint>{new PatternPoint(point.X-desired,point.Y-desired),new PatternPoint(point.X+desired,point.Y-desired),new PatternPoint(point.X+desired,point.Y+desired),new PatternPoint(point.X-desired,point.Y+desired)};
            foreach(var hull in obstacles)
            {
                var edge=ClosestPoint(point,hull);double nx=edge.X-point.X,ny=edge.Y-point.Y,length=Math.Sqrt(nx*nx+ny*ny);
                if(length<1e-8)return false;var buffer=new List<PatternPoint>();
                PatternFillLayout.Clip(polygon,nx,ny,nx*edge.X+ny*edge.Y-_draft.Options.GapMm*length,buffer);polygon=buffer;if(polygon.Count<3)break;
            }
            if(polygon.Count<3){MotifNotice?.Invoke("В этом месте не хватает свободного пространства с заданным зазором.");return false;}
            Geometry clip=null;
            if(filler){clip=_frame.Geometry.Clone();clip.Transform=new MatrixTransform(clip.Transform.Value*new Matrix(1,0,0,-1,0,_frame.Height));clip.Freeze();}
            for(int pass=0;pass<choices.Length;pass++)
            {
                int index=choices[(_nextMotif+pass)%choices.Length];var asset=_draft.Motifs[index];double angle=_draft.Options.Angle;
                var rotated=PatternTransform.RotatedSize(asset.Size.X,asset.Size.Y,angle);
                var original=new PatternPlacement{Motif=index,Angle=angle,Size=desired*Math.Max(rotated.X,rotated.Y)/Math.Max(asset.Size.X,asset.Size.Y),Center=point,Cell=polygon.ToArray()};
                var fitted=PatternMotifRotation.Fit(original,asset.Footprint,_draft.Options,angle);if(fitted==null)continue;
                var copies=PlacementCopies(fitted,asset).ToArray();if(copies.Length==0 || copies.Length+_draft.Items.Count>PatternFillLayout.MaximumElements)continue;
                double scale=PatternTransform.Fit(asset.Size,fitted).Scale;
                bool Allowed(double candidate)
                {
                    var hulls=new List<PatternPoint[]>();
                    foreach(var copy in copies)
                    {
                        var pose=PatternMotifRotation.Resize(copy,asset.Footprint,candidate);var hull=asset.Footprint.At(pose);var silhouette=PatternSilhouette.At(asset,pose);
                        if(clip!=null && clip.FillContainsWithDetail(silhouette,.05,ToleranceType.Absolute)!=IntersectionDetail.FullyContains)return false;
                        if(_draft.Reserve!=null && _draft.Reserve.FillContainsWithDetail(silhouette,.05,ToleranceType.Absolute)!=IntersectionDetail.Empty)return false;
                        foreach(var obstacle in obstacles)if(!PatternMotifRotation.Separated(hull,obstacle,_draft.Options.GapMm))return false;
                        foreach(var other in hulls)if(!PatternMotifRotation.Separated(hull,other,_draft.Options.GapMm))return false;hulls.Add(hull);
                    }
                    return true;
                }
                if(!Allowed(scale))
                {
                    double low=scale*.00001,high=scale;if(!Allowed(low))continue;
                    for(int n=0;n<16;n++){double mid=(low+high)/2;if(Allowed(mid))low=mid;else high=mid;}scale=low;
                }
                double actual=scale*Math.Max(asset.Size.X,asset.Size.Y);if(actual<Math.Max(.1,desired*(filler ? .12 : .3)))continue;
                string key="manual/"+(++_manualMotifId);var poses=copies.Select(p=>PatternMotifRotation.Resize(p,asset.Footprint,scale)).ToArray();
                foreach(var pose in poses)_addedMotifs.Add(new AddedMotif{Key=key,Pose=PatternMotifRotation.Copy(pose)});
                _baseMotifs=_baseMotifs.Concat(poses.Select(PatternMotifRotation.Copy)).ToArray();_motifKeys=_motifKeys.Concat(poses.Select(_=>key)).ToArray();_draft.Items.AddRange(poses);
                _nextMotif=(_nextMotif+pass+1)%choices.Length;_selectedMotif=null;PatternHeroFill.LimitFillerSizes(_draft);InvalidateVisual();MotifSelectionChanged?.Invoke();
                MotifNotice?.Invoke(filler?"Добавлен небольшой заполнитель из готового набора.":"Добавлена основная галька из готового набора.");return true;
            }
            MotifNotice?.Invoke("Рисунок не помещается с заданным зазором. Щёлкните в более свободном месте.");return false;
        }

        private IEnumerable<PatternPlacement> PlacementCopies(PatternPlacement item,PatternAsset asset)
        {
            if(!_draft.Options.Repeat){yield return item;yield break;}
            double angle=_draft.Options.Angle*Math.PI/180,c=Math.Cos(angle),s=Math.Sin(angle),width=_draft.Options.RepeatWidthMm,height=_draft.Options.RepeatHeightMm;
            // Enumerate the inverse-rotated frame, with a bounded budget for
            // very small periods. A partial rapport must never be accepted.
            var hull=asset.Footprint.At(item);double radius=hull.Max(p=>Math.Sqrt(Math.Pow(p.X-item.Center.X,2)+Math.Pow(p.Y-item.Center.Y,2)));
            var corners=new[]{new Point(-radius,-radius),new Point(_frame.Width+radius,-radius),new Point(_frame.Width+radius,_frame.Height+radius),new Point(-radius,_frame.Height+radius)};
            var local=corners.Select(p=>new Point((p.X-item.Center.X)*c+(p.Y-item.Center.Y)*s,-(p.X-item.Center.X)*s+(p.Y-item.Center.Y)*c)).ToArray();
            double minX=Math.Ceiling(local.Min(p=>p.X)/width),maxX=Math.Floor(local.Max(p=>p.X)/width),minY=Math.Ceiling(local.Min(p=>p.Y)/height),maxY=Math.Floor(local.Max(p=>p.Y)/height);
            if((maxX-minX+1)*(maxY-minY+1)>PatternFillLayout.MaximumElements*32d)yield break;
            var frame=new Rect(0,0,_frame.Width,_frame.Height);int count=0;
            for(int y=(int)minY;y<=maxY;y++)for(int x=(int)minX;x<=maxX;x++)
            {
                double dx=x*width*c-y*height*s,dy=x*width*s+y*height*c;var copy=PatternMotifRotation.Copy(item);
                copy.Center=new PatternPoint(item.Center.X+dx,item.Center.Y+dy);copy.Cell=item.Cell.Select(p=>new PatternPoint(p.X+dx,p.Y+dy)).ToArray();
                var copyHull=asset.Footprint.At(copy);var bounds=new Rect(new Point(copyHull.Min(p=>p.X),copyHull.Min(p=>p.Y)),new Point(copyHull.Max(p=>p.X),copyHull.Max(p=>p.Y)));
                if(!bounds.IntersectsWith(frame))continue;yield return copy;if(++count>PatternFillLayout.MaximumElements)yield break;
            }
        }

        private static PatternPoint ClosestPoint(PatternPoint point,PatternPoint[] hull)
        {
            double best=Double.PositiveInfinity;var result=point;
            for(int i=0;i<hull.Length;i++)
            {
                var a=hull[i];var b=hull[(i+1)%hull.Length];double dx=b.X-a.X,dy=b.Y-a.Y,length=dx*dx+dy*dy;
                double t=length>0?Math.Max(0,Math.Min(1,((point.X-a.X)*dx+(point.Y-a.Y)*dy)/length)):0;
                var candidate=new PatternPoint(a.X+t*dx,a.Y+t*dy);double distance=Math.Pow(candidate.X-point.X,2)+Math.Pow(candidate.Y-point.Y,2);
                if(distance<best){best=distance;result=candidate;}
            }
            return result;
        }
    }
}
