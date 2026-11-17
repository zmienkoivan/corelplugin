using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
namespace VanyaTools.Native
{
    internal sealed partial class PatternPreviewSurface
    {
        internal void SetSelectedAnchor(bool value)
        {
            int selected=SelectedMotif;if(selected<0)return;
            string key=_selectedMotif;
            if(value && !_anchors.Contains(key))
            {
                var group=Enumerable.Range(0,_motifKeys.Length).Where(i=>_motifKeys[i]==key).ToArray();
                string anchored="anchor/"+(++_manualMotifId);
                _removedMotifs.Add(key);_addedMotifs.RemoveAll(p=>p.Key==key);_motifTurns.Remove(key);
                foreach(int i in group){var pose=PatternMotifRotation.Copy(_draft.Items[i]);_addedMotifs.Add(new AddedMotif{Key=anchored,Pose=pose});_baseMotifs[i]=PatternMotifRotation.Copy(pose);_motifKeys[i]=anchored;}
                _anchors.Add(anchored);_selectedMotif=anchored;
            }
            else if(!value)_anchors.Remove(key);
            InvalidateVisual();MotifSelectionChanged?.Invoke();MotifNotice?.Invoke(value?"Опорная галька выделена красным. Генератор сохраняет её положение, размер и угол.":"Опора снята. Галька снова участвует в перестроении сетки.");
        }

        private bool CommitMotifItems(List<PatternPlacement> items,string[] keys=null)
        {
            var proposed=new PatternDraft {Frame=_frame,Options=_draft.Options,Motifs=_draft.Motifs,Items=items,
                Hero=_draft.Hero==null?null:PatternMotifRotation.Copy(_draft.Hero),Reserve=_draft.Reserve,BottleWhole=_draft.BottleWhole};
            var rowKeys=keys??_motifKeys;
            var fixedItems=new HashSet<int>(Enumerable.Range(0,items.Count).Where(i=>i<rowKeys.Length && _anchors.Contains(rowKeys[i])));
            var fixedPoses=fixedItems.ToDictionary(i=>i,i=>PatternMotifRotation.Copy(items[i]));
            PatternHeroFill.LimitFillerSizes(proposed);
            foreach(var row in fixedPoses)proposed.Items[row.Key]=row.Value;
            if(_frame.Bottle==null){_draft.Items=proposed.Items;return true;}
            try{PatternBottleLayout.CloseSeam(proposed,_session.Hero,System.Threading.CancellationToken.None,fixedItems);}
            catch(Exception error)
            {
                Log.Error("Could not update bottle seam after a manual edit.",error);
                MotifNotice?.Invoke("Правка не применена: "+error.Message);return false;
            }
            _draft.Items=proposed.Items;_draft.Hero=proposed.Hero;_draft.BottleCutView=proposed.BottleCutView;
            _draft.BottleSeamGuide=proposed.BottleSeamGuide;_draft.CellGuides=proposed.CellGuides;
            return true;
        }
    }
}
