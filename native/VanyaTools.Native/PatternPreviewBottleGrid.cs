using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
namespace VanyaTools.Native
{
    internal sealed partial class PatternPreviewSurface
    {
        private sealed class BottleSite
        {
            internal string Key;internal PatternPlacement Original;internal PatternPoint Position,Start;internal double Radius;
            internal bool Added,Fixed,Held;
        }
        private PatternPoint BottleUv(PatternPoint point)
        {
            var uv=_frame.Bottle.Project(new PatternPoint(point.X+_frame.BottleOrigin.X,point.Y+_frame.BottleOrigin.Y));
            return new PatternPoint(_frame.Bottle.Wrap(uv.X),uv.Y);
        }
        private double BottleDx(double dx)=>dx-Math.Round(dx/_frame.Bottle.Width)*_frame.Bottle.Width;
        private bool AddBottlePin(PatternPoint point,int[] choices,bool filler)
        {
            if(_anchors.Any(key=>Enumerable.Range(0,_motifKeys.Length).Any(i=>_motifKeys[i]==key && PatternMotifRotation.Contains(_draft.Items[i].Cell,point))))
            {MotifNotice?.Invoke("Здесь опорная галька. Снимите опору или выберите другое место.");return false;}
            double desired=filler?PatternHeroFill.FillerSizeAt(_draft,point):_draft.Options.SizeMm;
            int index=choices[_nextMotif%choices.Length];var asset=_draft.Motifs[index];var uv=BottleUv(point);
            double angle=_draft.Options.Angle+_frame.Bottle.RotationAt(uv.X);var rotated=PatternTransform.RotatedSize(asset.Size.X,asset.Size.Y,angle);
            var pose=new PatternPlacement{Motif=index,Angle=angle,Center=point,Size=desired*Math.Max(rotated.X,rotated.Y)/Math.Max(asset.Size.X,asset.Size.Y)};
            double radius=desired*.7+_draft.Options.GapMm/2;
            pose.Cell=Enumerable.Range(0,6).Select(n=>new PatternPoint(point.X+radius*Math.Cos(n*Math.PI/3),point.Y+radius*Math.Sin(n*Math.PI/3))).ToArray();
            string key="manual/"+(++_manualMotifId);var added=new AddedMotif{Key=key,Pose=pose};_addedMotifs.Add(added);
            if(!ReflowBottle(key)){_addedMotifs.Remove(added);MotifNotice?.Invoke("Новая ячейка не помещается с заданным зазором и опорами. Выберите другое место.");return false;}
            _nextMotif++;_selectedMotif=key;InvalidateVisual();MotifSelectionChanged?.Invoke();
            MotifNotice?.Invoke(filler?"Добавлена мини-галька. Соседняя сетка перестроена вокруг неё.":"Добавлена галька. Соседние ячейки перестроены; красные опоры сохранены.");return true;
        }

        private bool ReflowBottle(string heldKey)
        {
            var bottle=_frame.Bottle;double width=bottle.Width,height=bottle.Slant,size=_draft.Options.SizeMm,gap=_draft.Options.GapMm;
            var sites=new List<BottleSite>();
            foreach(var pose in _automaticMotifs)
            {
                string key=MotifKey(pose);if(_removedMotifs.Contains(key))continue;
                AddSite(key,pose,false);
            }
            foreach(var added in _addedMotifs)AddSite(added.Key,added.Pose,true);
            if(sites.Count>PatternFillLayout.MaximumElements)return false;
            void AddSite(string key,PatternPlacement pose,bool added)
            {
                var uv=BottleUv(pose.Center);double radius=PatternHeroFill.Longest(_draft.Motifs[pose.Motif],pose)*.42;
                bool fixedCell=_anchors.Contains(key);
                if(fixedCell && pose.Cell!=null)radius=Math.Max(radius,pose.Cell.Max(p=>{var q=BottleUv(p);return Math.Sqrt(Math.Pow(BottleDx(q.X-uv.X),2)+Math.Pow(q.Y-uv.Y,2));}));
                sites.Add(new BottleSite{Key=key,Original=pose,Position=uv,Start=uv,Added=added,Fixed=fixedCell,Held=key==heldKey,Radius=radius});
            }
            // Soft seeds yield to the newly inserted cell and explicit red anchors.
            // Work in cylindrical coordinates so neighbours across the seam agree.
            var controls=sites.Where(s=>s.Added || s.Fixed).ToArray();
            for(int iteration=0;iteration<24;iteration++)
            {
                var next=new PatternPoint[sites.Count];
                for(int i=0;i<sites.Count;i++)
                {
                    var site=sites[i];next[i]=site.Position;if(site.Fixed || site.Held)continue;
                    if(!controls.Any(p=>Math.Pow(BottleDx(site.Position.X-p.Position.X),2)+Math.Pow(site.Position.Y-p.Position.Y,2)<size*size*9))continue;
                    double fx=BottleDx(site.Start.X-site.Position.X)*.025,fy=(site.Start.Y-site.Position.Y)*.025;
                    for(int j=0;j<sites.Count;j++)if(i!=j)
                    {
                        var other=sites[j];double dx=BottleDx(site.Position.X-other.Position.X),dy=site.Position.Y-other.Position.Y,dist=Math.Sqrt(dx*dx+dy*dy),limit=site.Radius+other.Radius+gap;
                        if(dist>=limit)continue;
                        if(dist<.0001){double a=(i+1)*2.3999632297;dx=Math.Cos(a);dy=Math.Sin(a);dist=1;}
                        double push=(limit-dist)*((other.Fixed || other.Held)?.55:.25);fx+=dx/dist*push;fy+=dy/dist*push;
                    }
                    double y=Math.Max(_draft.BottleWhole?site.Radius:0,Math.Min(height-(_draft.BottleWhole?site.Radius:0),site.Position.Y+fy));
                    var moved=new PatternPoint(bottle.Wrap(site.Position.X+fx),y);var world=PatternBottleLayout.OnFrame(_frame,moved.X,moved.Y);
                    if(_draft.Reserve?.FillContains(new Point(world.X,world.Y))!=true)next[i]=moved;
                }
                for(int i=0;i<sites.Count;i++)sites[i].Position=next[i];
            }
            var rows=new List<Tuple<string,PatternPlacement>>();
            foreach(var site in sites.OrderByDescending(s=>s.Fixed).ThenByDescending(s=>s.Held).ThenByDescending(s=>s.Added))
            {
                var asset=_draft.Motifs[site.Original.Motif];PatternPlacement fitted;
                if(site.Fixed)fitted=PatternMotifRotation.Copy(site.Original);
                else
                {
                    var s=site.Position;double reach=size*2;
                    var polygon=new List<PatternPoint>{new PatternPoint(s.X-reach,Math.Max(s.Y-reach,_draft.BottleWhole || asset.IsFiller?0:-size/2)),new PatternPoint(s.X+reach,Math.Max(s.Y-reach,_draft.BottleWhole || asset.IsFiller?0:-size/2)),new PatternPoint(s.X+reach,Math.Min(s.Y+reach,_draft.BottleWhole || asset.IsFiller?height:height+size/2)),new PatternPoint(s.X-reach,Math.Min(s.Y+reach,_draft.BottleWhole || asset.IsFiller?height:height+size/2))};
                    foreach(var other in sites)for(int turn=-1;turn<=1;turn++)
                    {
                        if(ReferenceEquals(site,other) && turn==0 || polygon.Count<3)continue;
                        double x=other.Position.X+turn*width,y=other.Position.Y,nx=x-s.X,ny=y-s.Y,length=Math.Sqrt(nx*nx+ny*ny);if(length<.00001)continue;
                        double reachLimit=Math.Sqrt(8)*size;
                        double cutDistance=(length*length+site.Radius*site.Radius-other.Radius*other.Radius)/(2*length)-gap/2;
                        if(cutDistance>=reachLimit)continue;
                        double cut=(x*x+y*y-other.Radius*other.Radius-s.X*s.X-s.Y*s.Y+site.Radius*site.Radius)/2-gap*length/2;
                        var buffer=new List<PatternPoint>();PatternFillLayout.Clip(polygon,nx,ny,cut,buffer);polygon=buffer;
                    }
                    foreach(var anchor in sites.Where(p=>p.Fixed && !ReferenceEquals(p,site)))
                    {
                        var outline=PatternFootprint.ConvexHull(anchor.Original.Cell.Select(p=>{var q=BottleUv(p);return new PatternPoint(s.X+BottleDx(q.X-s.X),q.Y);}));
                        if(PatternMotifRotation.Contains(outline,s)){polygon.Clear();break;}
                        var edge=ClosestPoint(s,outline);double nx=edge.X-s.X,ny=edge.Y-s.Y,length=Math.Sqrt(nx*nx+ny*ny);
                        if(length<.00001){polygon.Clear();break;}
                        var buffer=new List<PatternPoint>();PatternFillLayout.Clip(polygon,nx,ny,nx*edge.X+ny*edge.Y-gap*length/2,buffer);polygon=buffer;
                    }
                    if(polygon.Count<3){if(site.Added)return false;continue;}
                    var cell=PatternFootprint.ConvexHull(polygon.Select(p=>PatternBottleLayout.OnFrame(_frame,p.X,p.Y)));
                    if(site.Added && asset.IsFiller)
                    {
                        // A miniature owns a genuinely small cell, not a large
                        // parent cell containing a scaled-down picture.
                        var center=PatternBottleLayout.OnFrame(_frame,s.X,s.Y);double cap=PatternHeroFill.Longest(asset,site.Original)*.7+gap/2;
                        var mini=Enumerable.Range(0,6).Select(n=>new PatternPoint(center.X+cap*Math.Cos(n*Math.PI/3),center.Y+cap*Math.Sin(n*Math.PI/3))).ToArray();
                        var clipped=cell.ToList();
                        for(int n=0;n<mini.Length;n++){var a=mini[n];var b=mini[(n+1)%mini.Length];double nx=b.Y-a.Y,ny=a.X-b.X;var buffer=new List<PatternPoint>();PatternFillLayout.Clip(clipped,nx,ny,nx*a.X+ny*a.Y,buffer);clipped=buffer;}
                        cell=clipped.ToArray();
                    }
                    if(cell.Length<3){if(site.Added)return false;continue;}
                    var original=PatternMotifRotation.Copy(site.Original);original.Cell=cell;
                    var oldUv=bottle.Project(new PatternPoint(original.Center.X+_frame.BottleOrigin.X,original.Center.Y+_frame.BottleOrigin.Y));
                    double angle=original.Angle+bottle.RotationAt(s.X)-bottle.RotationAt(oldUv.X);
                    fitted=PatternMotifRotation.Fit(original,asset.Footprint,_draft.Options,angle);
                    if(fitted==null){if(site.Added)return false;continue;}
                }
                bool Fits(PatternPlacement pose)=>PatternBottleLayout.FitsOnRing(_frame,_draft.Motifs,pose,rows.Select(r=>r.Item2).ToList(),gap,asset.IsFiller || _draft.BottleWhole) &&
                    (_draft.Reserve==null || _draft.Reserve.FillContainsWithDetail(PatternSilhouette.At(asset,pose),.05,ToleranceType.Absolute)==IntersectionDetail.Empty);
                if(!Fits(fitted))
                {
                    if(site.Fixed)return false;
                    double high=PatternTransform.Fit(asset.Size,fitted).Scale,low=high*.01;
                    if(!Fits(PatternMotifRotation.Resize(fitted,asset.Footprint,low))){if(site.Added)return false;continue;}
                    for(int n=0;n<14;n++){double mid=(low+high)/2;if(Fits(PatternMotifRotation.Resize(fitted,asset.Footprint,mid)))low=mid;else high=mid;}
                    fitted=PatternMotifRotation.Resize(fitted,asset.Footprint,low);
                }
                if(PatternHeroFill.Longest(asset,fitted)<.1){if(site.Added)return false;continue;}
                rows.Add(Tuple.Create(site.Key,fitted));
            }
            var keys=rows.Select(r=>r.Item1).ToArray();
            if(!CommitMotifItems(rows.Select(r=>r.Item2).ToList(),keys))return false;
            _baseMotifs=_draft.Items.Select(PatternMotifRotation.Copy).ToArray();_motifKeys=keys;
            foreach(var added in _addedMotifs.Where(p=>!_anchors.Contains(p.Key)))
            {int index=Array.IndexOf(keys,added.Key);if(index>=0)added.Pose=PatternMotifRotation.Copy(_draft.Items[index]);}
            foreach(var angle in _motifTurns.ToArray())if(!_anchors.Contains(angle.Key) && !ApplyMotifTurn(angle.Key,angle.Value))_motifTurns.Remove(angle.Key);
            return true;
        }
    }
}
