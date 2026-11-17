using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace VanyaTools.Native {
 internal static class Log {internal static void Info(string text){} internal static void Error(string text,Exception error){}}
 internal sealed class PatternAsset {internal int Id;internal string Label;internal object Shape;internal bool Mirrored,IsFiller;internal PatternPoint Size;internal PatternFootprint Footprint;internal BitmapSource Preview;internal Geometry Silhouette;}
 internal sealed class PatternFrame {internal double Width,Height,Left,Top;internal Geometry Geometry,BottleClip;internal PatternBottle Bottle;internal PatternPoint BottleOrigin;}
 internal sealed class PatternEditorSession {internal PatternAsset Hero;internal PatternFrame Seam;}
 internal sealed class PatternDraft {internal PatternFrame Frame;internal PatternFillOptions Options;internal PatternAsset[] Motifs;internal List<PatternPlacement> Items;internal PatternPlacement Hero;internal Geometry Reserve,CellGuides,RepeatGuides,CenterMarks,BottleCutView,BottleSeamGuide;}
 internal static class PatternBottleLayout {internal static bool FitsOnRing(PatternFrame frame,PatternAsset[] assets,PatternPlacement item,List<PatternPlacement> placed,double gap,bool whole)=>throw new Exception("Unexpected bottle placement");internal static PatternPoint OnFrame(PatternFrame frame,double x,double y)=>throw new Exception("Unexpected bottle/COM operation in flat pattern checks");}
}
