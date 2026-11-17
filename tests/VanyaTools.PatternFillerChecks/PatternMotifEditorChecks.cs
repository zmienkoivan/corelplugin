using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class PatternMotifEditorChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object Call(object value,string name,params object[] args)=>value.GetType().GetMethod(name,Flags).Invoke(value,args);
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,Flags).GetValue(value);
    private static void Set(object value,string name,object data)=>value.GetType().GetField(name,Flags).SetValue(value,data);
    private static string Pose(PatternPlacement p)=>String.Join("/",new[]{p.Angle,p.Size,p.Center.X,p.Center.Y}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));
    private static string Cells(IEnumerable<PatternPlacement> items)=>String.Join("|",items.Select(p=>String.Join(";",p.Cell.Select(v=>v.X.ToString("R",CultureInfo.InvariantCulture)+","+v.Y.ToString("R",CultureInfo.InvariantCulture)))));
    private static void Render(FrameworkElement root)
    {root.Measure(new Size(1000,740));root.Arrange(new Rect(0,0,1000,740));root.UpdateLayout();var image=new RenderTargetBitmap(1000,740,96,96,PixelFormats.Pbgra32);image.Render(root);}
    internal static void Run(Action<bool,string> check)
    {
        var asymmetric=new PatternFootprint(new PatternPoint(16,10),new[]{new PatternPoint(-8,-5),new PatternPoint(8,-5),new PatternPoint(3,5),new PatternPoint(-5,4)});
        foreach(var mode in new[]{PatternFillMode.Pebbles,PatternFillMode.Crocodile})
        {
            var o=new PatternFillOptions{Mode=mode,SizeMm=34,GapMm=3,Seed=8123};var placements=PatternFillLayout.Build(160,100,new[]{asymmetric},o);
            foreach(double angle in new[]{-179d,-90,-45,10,57,90,179})
            {
                var original=placements[placements.Count/2];var turned=PatternMotifRotation.Fit(original,asymmetric,o,angle);
                check(turned!=null && Math.Abs(turned.Angle-angle)<1e-8 && ReferenceEquals(original.Cell,turned.Cell),"Requested print angle changed the cell or was ignored");
                check(asymmetric.At(turned).All(p=>PatternMotifRotation.Contains(turned.Cell,new PatternPoint(p.X*.999999+PatternMotifRotation.InkCenter(turned,asymmetric).X*.000001,p.Y*.999999+PatternMotifRotation.InkCenter(turned,asymmetric).Y*.000001))),"Turned print escaped its fixed cell");
                check(PatternTransform.Fit(asymmetric.Size,turned).Scale<=PatternTransform.Fit(asymmetric.Size,original).Scale+1e-8,"Manual rotation unexpectedly enlarged the print");
            }
            Editor(check,mode,false,0);Editor(check,mode,true,31);
        }
    }
    private static void Editor(Action<bool,string> check,PatternFillMode mode,bool repeat,double gridAngle)
    {
        var options=new PatternFillOptions{Mode=mode,SizeMm=31,GapMm=2,Seed=8123,Angle=gridAngle,RepeatWidthMm=repeat?95:0,RepeatHeightMm=repeat?90:0};
        var footprint=new PatternFootprint(new PatternPoint(16,8));var geometry=new RectangleGeometry(new Rect(-8,-4,16,8));geometry.Freeze();
        var asset=new PatternAsset{Id=7,Label="Эталон",Size=footprint.Size,Footprint=footprint,Silhouette=geometry};
        var frameGeometry=new RectangleGeometry(new Rect(0,0,260,180));frameGeometry.Freeze();
        var frame=new PatternFrame{Width=260,Height=180,Geometry=frameGeometry};var session=new PatternEditorSession{Seam=frame};
        var originals=PatternFillLayout.Build(frame.Width,frame.Height,new[]{footprint},options);string beforeCells=Cells(originals);var before=originals.Select(Pose).ToArray();
        var draft=new PatternDraft{Items=originals,Options=options,Motifs=new[]{asset}};var preview=new PatternPreviewSurface(session,frame);int updates=0;preview.MotifSelectionChanged+=()=>updates++;
        using(var source=new HwndSource(new HwndSourceParameters("Vanya selected motif checks"){Width=1000,Height=740,WindowStyle=0}))
        {
            var root=new Border{Child=preview,Background=Brushes.White};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);
            int picked=Enumerable.Range(0,draft.Items.Count).OrderBy(i=>Math.Pow(draft.Items[i].Center.X-130,2)+Math.Pow(draft.Items[i].Center.Y-90,2)).First();
            var cell=draft.Items[picked].Cell;var center=new PatternPoint(cell.Average(p=>p.X),cell.Average(p=>p.Y));var screen=(Point)Call(preview,"ToScreen",center);
            check((bool)Call(preview,"SelectMotif",screen) && preview.HasSelectedMotif,"Click did not select a texture print without a hero");
            string selected=Field<string>(preview,"_selectedMotif");var keys=Field<string[]>(preview,"_motifKeys");var selectedIndices=Enumerable.Range(0,keys.Length).Where(i=>keys[i]==selected).ToArray();
            if(repeat)check(selectedIndices.Length>1,"Rapport copies did not share a logical selected cell");
            object[] handleArgs={null};var handle=(Point)Call(preview,"MotifHandle",handleArgs);var dragCenter=(Point)handleArgs[0];
            check((bool)Call(preview,"HitMotifHandle",handle),"Round print rotation handle is not hit-testable");
            var vector=handle-dragCenter;Set(preview,"_motifDragCenter",dragCenter);Set(preview,"_motifDragVector",vector);Set(preview,"_motifStartTurn",0d);Set(preview,"_motifDragAngle",0d);
            var moved=dragCenter+new Vector(vector.X*Math.Cos(-.6)-vector.Y*Math.Sin(-.6),vector.X*Math.Sin(-.6)+vector.Y*Math.Cos(-.6));Call(preview,"TurnMotifTo",moved);
            check(selectedIndices.All(i=>Math.Abs(draft.Items[i].Angle-originals[i].Angle)>.1) && draft.Items.Where((p,i)=>keys[i]!=selected).Select(Pose).SequenceEqual(originals.Where((p,i)=>keys[i]!=selected).Select(Pose)),"Mouse rotation changed unrelated prints or ignored the selected one");
            check(Cells(draft.Items)==beforeCells && originals.Select(Pose).SequenceEqual(before),"Manual rotation mutated cells or the cached automatic layout");
            for(int i=0;i<draft.Items.Count;i++)for(int j=i+1;j<draft.Items.Count;j++)check(PatternMotifRotation.Separated(footprint.At(draft.Items[i]),footprint.At(draft.Items[j]),options.GapMm),"Manual rotation violated a neighbouring print gap");
            var repeated=new PatternDraft{Items=originals,Options=options,Motifs=new[]{asset}};preview.Update(frame,repeated,0);Render(root);
            check(selectedIndices.All(i=>Math.Abs(repeated.Items[i].Angle-originals[i].Angle)>.1),"Refreshing the same layout discarded the chosen print angle");
            preview.ResetSelectedMotif();check(repeated.Items.Select(Pose).SequenceEqual(before),"Automatic rotation reset did not restore original poses");
            preview.ClearMotifSelection();check(!preview.HasSelectedMotif && updates>2,"Selection controls did not clear or notify the window");
        }
    }
}
