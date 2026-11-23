using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    internal sealed partial class PatternPreviewSurface
    {
        internal PatternProjectEdits SaveProjectEdits()=>new PatternProjectEdits{
            Automatic=_automaticMotifs.Select(PatternProjectPose.From).ToArray(),Base=_baseMotifs.Select(PatternProjectPose.From).ToArray(),
            Keys=(string[])_motifKeys.Clone(),Removed=_removedMotifs.ToArray(),Anchors=_anchors.ToArray(),
            Turns=new Dictionary<string,double>(_motifTurns),ManualId=_manualMotifId,NextMotif=_nextMotif,
            Added=_addedMotifs.Select(a=>new PatternProjectAdded{Key=a.Key,Pose=PatternProjectPose.From(a.Pose)}).ToArray()};

        internal void RestoreProjectEdits(PatternProjectEdits state,Dictionary<int,int> ids)
        {
            if(state==null || !CanEditMotifs)return;
            state.Validate(_draft.Motifs.Length,_draft.Items.Count);
            string Key(string value)
            {
                if(value==null)return null;if(value.Length>200000)throw new InvalidOperationException("Повреждено обозначение ячейки.");
                int slash=value.IndexOf('/');
                if(slash>0 && Int32.TryParse(value.Substring(0,slash),NumberStyles.Integer,CultureInfo.InvariantCulture,out int old) && ids.TryGetValue(old,out int replacement))
                    return replacement.ToString(CultureInfo.InvariantCulture)+value.Substring(slash);
                return value;
            }
            var automatic=state.Automatic.Select(p=>p.Read(_draft.Motifs.Length)).ToArray();
            var bases=state.Base.Select(p=>p.Read(_draft.Motifs.Length)).ToArray();
            var added=state.Added.Select(a=>new AddedMotif{Key=Key(a.Key),Pose=a.Pose.Read(_draft.Motifs.Length)}).ToArray();
            var turns=state.Turns.ToDictionary(p=>Key(p.Key),p=>p.Value);
            if(turns.Any(p=>!PatternProjectPose.Valid(p.Value)))throw new InvalidOperationException("Повреждён поворот в проекте.");
            _automaticMotifs=automatic;_baseMotifs=bases;_motifKeys=state.Keys.Select(Key).ToArray();
            _addedMotifs.Clear();_addedMotifs.AddRange(added);_removedMotifs.Clear();foreach(var k in state.Removed)_removedMotifs.Add(Key(k));
            _anchors.Clear();foreach(var k in state.Anchors)_anchors.Add(Key(k));_motifTurns.Clear();foreach(var pair in turns)_motifTurns.Add(pair.Key,pair.Value);
            _manualMotifId=state.ManualId;_nextMotif=state.NextMotif;_selectedMotif=null;
            if(!CommitMotifItems(_draft.Items))throw new InvalidOperationException("Не удалось восстановить стык сохранённого паттерна.");
            InvalidateVisual();MotifSelectionChanged?.Invoke();
        }
    }
}
