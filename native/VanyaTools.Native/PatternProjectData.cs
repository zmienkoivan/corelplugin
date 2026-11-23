using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Media;

namespace VanyaTools.Native
{
    public sealed class PatternProjectPose
    {
        public int Motif;
        public double Size,Angle,X,Y;
        public double[][] Cell;
        internal static PatternProjectPose From(PatternPlacement p)=>p==null?null:new PatternProjectPose{Motif=p.Motif,Size=p.Size,Angle=p.Angle,X=p.Center.X,Y=p.Center.Y,Cell=p.Cell?.Select(v=>new[]{v.X,v.Y}).ToArray()};
        internal PatternPlacement Read(int count)
        {
            if(Motif<0 || Motif>=Math.Max(1,count) || !Valid(Size) || Size<=0 || !Valid(Angle) || !Valid(X) || !Valid(Y) || Cell?.Length>10000)
                throw new InvalidOperationException("Повреждено положение рисунка в проекте.");
            if(Cell!=null && Cell.Any(p=>p==null || p.Length!=2 || !Valid(p[0]) || !Valid(p[1])))throw new InvalidOperationException("Повреждена ячейка проекта.");
            return new PatternPlacement{Motif=Motif,Size=Size,Angle=Angle,Center=new PatternPoint(X,Y),Cell=Cell?.Select(p=>new PatternPoint(p[0],p[1])).ToArray()};
        }
        internal static bool Valid(double n)=>!Double.IsNaN(n) && !Double.IsInfinity(n) && Math.Abs(n)<=10000000;
    }
    public sealed class PatternProjectGeometry
    {
        public string Path;
        public double[] Matrix;
        internal static PatternProjectGeometry From(Geometry geometry)
        {
            if(geometry==null)return null;
            var path=PathGeometry.CreateFromGeometry(geometry);var m=path.Transform.Value;
            return new PatternProjectGeometry{Path=path.ToString(CultureInfo.InvariantCulture),Matrix=new[]{m.M11,m.M12,m.M21,m.M22,m.OffsetX,m.OffsetY}};
        }
        internal Geometry Read()
        {
            if(Path==null || Path.Length>16000000 || Matrix==null || Matrix.Length!=6 || Matrix.Any(n=>!PatternProjectPose.Valid(n)))throw new InvalidOperationException("Повреждён контур проекта.");
            var g=Geometry.Parse(Path).Clone();g.Transform=new MatrixTransform(Matrix[0],Matrix[1],Matrix[2],Matrix[3],Matrix[4],Matrix[5]);g.Freeze();return g;
        }
    }
    public sealed class PatternProjectAdded {public string Key;public PatternProjectPose Pose;}
    public sealed class PatternProjectEdits
    {
        public PatternProjectPose[] Automatic=Array.Empty<PatternProjectPose>(),Base=Array.Empty<PatternProjectPose>();
        public string[] Keys=Array.Empty<string>(),Removed=Array.Empty<string>(),Anchors=Array.Empty<string>();
        public PatternProjectAdded[] Added=Array.Empty<PatternProjectAdded>();
        public Dictionary<string,double> Turns=new Dictionary<string,double>();
        public int ManualId,NextMotif;
        internal void Validate(int motifs,int items)
        {
            if(Automatic==null || Base==null || Keys==null || Removed==null || Anchors==null || Added==null || Turns==null ||
                Base.Length!=items || Keys.Length!=items || Automatic.Length>PatternFillLayout.MaximumElements || Added.Length>PatternFillLayout.MaximumElements ||
                Anchors.Length>PatternFillLayout.MaximumElements || Removed.Length>100000 || Turns.Count>100000 || ManualId<0 || ManualId>1000000 || NextMotif<0)
                throw new InvalidOperationException("Повреждены ручные правки проекта паттерна.");
            bool ValidKey(string key)=>key==null || key.Length<=200000;
            if(Keys.Concat(Removed).Concat(Anchors).Any(k=>!ValidKey(k)) || Added.Any(a=>a==null || a.Key==null || !ValidKey(a.Key) || a.Pose==null) ||
                Turns.Any(p=>!ValidKey(p.Key) || !PatternProjectPose.Valid(p.Value)))throw new InvalidOperationException("Повреждено обозначение или поворот ячейки.");
            foreach(var pose in Automatic.Concat(Base).Concat(Added.Select(a=>a.Pose)))
            {if(pose==null)throw new InvalidOperationException("Не найдено положение ручной гальки.");pose.Read(motifs);}
        }
    }
    public sealed class PatternProjectSource
    {
        public int Id;public string Label;public double Width,Height;public bool Filler;
        public double[][] Hull;
        public PatternProjectGeometry Silhouette;
        public bool OutlineFallback;
    }
    public sealed class PatternProjectData
    {
        public int Schema=1;
        public bool Bottle,Whole;
        public Dictionary<string,object> Settings,Layout;
        public PatternProjectSource[] Sources;
        public int[] Motifs,DraftSources,Mirrors;
        public bool[] DraftMirrors;
        public int Hero=-1;
        public PatternProjectPose[] Items;
        public PatternProjectPose Main;
        public PatternProjectEdits Edits;
        public PatternProjectGeometry Reserve,CellGuides,RepeatGuides,CenterMarks,CutView,SeamGuide;
        public double Width,Height;
        internal static Dictionary<string,object> Values(object value)=>value.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
            .Where(f=>f.FieldType.IsEnum || f.FieldType==typeof(double) || f.FieldType==typeof(int) || f.FieldType==typeof(bool))
            .ToDictionary(f=>f.Name,f=>f.GetValue(value));
        internal static void ReadValues(object target,Dictionary<string,object> values)
        {
            if(values==null || values.Count>40)throw new InvalidOperationException("Не найдены настройки проекта.");
            foreach(var field in target.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
            {
                if(!values.TryGetValue(field.Name,out var value))continue;
                if(field.FieldType.IsEnum){int n=Convert.ToInt32(value);if(!Enum.IsDefined(field.FieldType,n))throw new InvalidOperationException("Неизвестный режим паттерна.");field.SetValue(target,Enum.ToObject(field.FieldType,n));}
                else if(field.FieldType==typeof(double)){double n=Convert.ToDouble(value,CultureInfo.InvariantCulture);if(!PatternProjectPose.Valid(n))throw new InvalidOperationException("Некорректное число в настройках проекта.");field.SetValue(target,n);}
                else if(field.FieldType==typeof(int) || field.FieldType==typeof(bool))field.SetValue(target,Convert.ChangeType(value,field.FieldType,CultureInfo.InvariantCulture));
            }
        }
    }
    internal sealed class PatternProjectOpened
    {
        internal PatternEditorSession Session;
        internal PatternEditorSettings Settings;
        internal PatternDraft Draft;
        internal PatternProjectEdits Edits;
        internal Dictionary<int,int> Ids;
        internal string FilePath;
    }
}
