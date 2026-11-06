using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value,string message) { _checks++; if(!value) throw new Exception(message); }
    private static void Reject(Action action) { try {action();} catch(InvalidDataException) {_checks++;return;} throw new Exception("Invalid preset accepted"); }
    private static DtfCorrectionOptions Fixture() => new DtfCorrectionOptions {
        RemoveSemiTransparent=true,Threshold=77,SmoothStairs=true,WhitenNearWhite=true,NearWhiteTolerance=12,
        Outline=true,RadiusMm=.127,ExpansionMm=.3,Color=Color.FromRgb(10,20,30),Knockout=true,
        KnockoutColor=Color.FromRgb(45,67,89),KnockoutTolerance=17,Halftone=true,HalftoneAlphaOnly=false,
        HalftoneAutoDot=false,HalftoneLpi=32,HalftoneAngle=-22.5,HalftoneDotMm=.7,GridOriginXmm=1337,GridOriginYmm=-120,StairsScale=4 };
    private static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"Vanya-CorrectionPresetChecks-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"проба.vanya-correction.json");
            var source=Fixture(); DtfCorrectionPreset.Save(path,DtfCorrectionPreset.FromOptions(source));
            var preset=DtfCorrectionPreset.Load(path); var result=preset.ToOptions();
            Check(result.RemoveSemiTransparent && result.Threshold==77 && result.SmoothStairs,"Threshold/stairs lost");
            Check(result.WhitenNearWhite && result.NearWhiteTolerance==12,"Whitening lost");
            Check(result.Outline && result.RadiusMm==.127 && result.ExpansionMm==.3 && result.Color==source.Color,"Physical outline or colour lost");
            Check(result.Knockout && result.KnockoutColor==source.KnockoutColor && result.KnockoutTolerance==17,"Knockout lost");
            Check(result.Halftone && !result.HalftoneAlphaOnly && !result.HalftoneAutoDot && result.HalftoneLpi==32 && result.HalftoneAngle==-22.5 && result.HalftoneDotMm==.7,"Halftone lost");
            Check(result.GridOriginXmm==0 && result.GridOriginYmm==0 && result.StairsScale==1,"Source-specific geometry persisted");
            string json=File.ReadAllText(path);
            Check(!json.Contains("GridOrigin") && !json.Contains("StairsScale") && !json.Contains("Dpi") && !json.Contains("SourcePath"),"Preset contains source-specific data");
            string bom=Path.Combine(root,"bom.json"); File.WriteAllText(bom,json,new UTF8Encoding(true));
            Check(DtfCorrectionPreset.Load(bom).ToOptions().HalftoneAngle==-22.5,"UTF-8 BOM rejected");
            var next=Fixture();next.Threshold=200;next.HalftoneAutoDot=true;next.HalftoneLpi=.5;next.HalftoneDotMm=71.842;
            DtfCorrectionPreset.Save(path,DtfCorrectionPreset.FromOptions(next));
            Check(DtfCorrectionPreset.Load(path).ToOptions().Threshold==200 && DtfCorrectionPreset.Load(path).ToOptions().HalftoneAutoDot,"Atomic overwrite or auto-size lost");
            byte[] previous=File.ReadAllBytes(path);
            var invalid=DtfCorrectionPreset.FromOptions(source);invalid.Settings.RadiusMm=Double.NaN;
            Reject(()=>DtfCorrectionPreset.Save(path,invalid));
            Check(previous.SequenceEqual(File.ReadAllBytes(path)),"Rejected save overwrote existing preset");
            Check(!Directory.GetFiles(root,"*.tmp").Any(),"Preset save left temporary files");
            foreach(Action<DtfCorrectionPreset> change in new Action<DtfCorrectionPreset>[] {
                p=>p.Version=99,p=>p.Format="OtherTool",p=>p.Settings=null,p=>p.Settings.OutlineColor="#ZZ0000",
                p=>p.Settings.KnockoutColor="red",p=>p.Settings.Threshold=256,p=>p.Settings.NearWhiteTolerance=65,
                p=>p.Settings.KnockoutTolerance=-1,p=>p.Settings.HalftoneLpi=0,p=>p.Settings.HalftoneAngle=181,
                p=>p.Settings.HalftoneDotMm=0,p=>p.Settings.ExpansionMm=Double.PositiveInfinity })
            { var p=DtfCorrectionPreset.FromOptions(source);change(p);Reject(()=>p.ToOptions()); }
            foreach(string text in new[]{"{","null","{}","{\"Format\":\"VanyaTools.Correction\",\"Version\":1}",new string('x',65537)})
            { File.WriteAllText(bom,text,Encoding.UTF8);Reject(()=>DtfCorrectionPreset.Load(bom)); }
            Check(source.GridOriginXmm==1337 && source.GridOriginYmm==-120 && source.Threshold==77,"Saving edited source options");
            Console.WriteLine("Correction preset checks passed: "+_checks+". Owned JSON files, no Corel or dialogs.");return 0;
        }
        catch(Exception error) {Console.Error.WriteLine(error);return 1;}
        finally { foreach(string file in Directory.EnumerateFiles(root)) File.Delete(file); Directory.Delete(root,false); }
    }
}
