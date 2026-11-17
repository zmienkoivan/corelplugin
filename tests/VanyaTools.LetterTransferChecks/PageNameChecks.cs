using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using VanyaTools.Native;

internal static class PageNameChecks
{
    internal static void Run(string root)
    {
        string observedName=new string('Ж',46); // Same UTF-16 length as the private captured name.
        // The captured crash has this generated page label overwriting the
        // security cookie, 40 UTF-16 characters after the native buffer starts.
        string previous=observedName+" · 15 мм · лист 1";
        Program.Check(previous.Length>40,"Crash fixture no longer reaches the observed page-tab cookie");
        foreach(string name in new[]{observedName,new string('Я',120),String.Concat(Enumerable.Repeat("А😀Е\u0301",20)),"\r\nНабор\tбукв\0", "", "А"})
            foreach(double height in new[]{15d,20d,80d,35.5,123.123456789012})
                foreach(int page in new[]{1,200})
                {
                    string actual=LetterTransferPageNames.Create(name,height,page);
                    Program.Check(actual.Length<=32 && !actual.Any(Char.IsControl),"Unsafe generated page label");
                    Program.Check(actual.EndsWith(LetterTransferPrintSizes.Label(height)+" мм · "+page.ToString(CultureInfo.InvariantCulture)),"Truncation lost physical height or page number");
                    for(int i=0;i<actual.Length;i++)if(Char.IsSurrogate(actual[i]))
                    {Program.Check(Char.IsHighSurrogate(actual[i]) && i+1<actual.Length && Char.IsLowSurrogate(actual[++i]),"Page label split an emoji surrogate pair");}
                }
        var set=Program.Printable();set.Name=observedName;set.AutoLength=true;set.SheetWidthMm=300;set.PrintHeightsMm=new System.Collections.Generic.List<double>{20,80};
        string directory=LetterTransferStore.Create(Path.Combine(root,"long-page-name"),set);
        foreach(var glyph in set.Glyphs)LetterTransferImages.Save(Program.Solid(glyph.Width,glyph.Height,Colors.Blue),LetterTransferStore.Resolve(directory,glyph.File));
        var app=new FakeLetterApp();LetterTransferService.Insert(app,directory,set,false);
        var pages=app.ActiveDocument.Pages.Skip(1).ToArray();
        Program.Check(pages.Length==2 && pages.All(page=>page.Name.Length<40),"Letter insertion would overwrite the observed Corel page-tab buffer on refresh");
        Program.Check(pages.Select(page=>page.Name).Distinct().Count()==2 && pages[0].Name.Contains("20 мм") && pages[1].Name.Contains("80 мм"),"Short page names no longer identify print sizes");
        Program.Check(set.Name==observedName && LetterTransferStore.Load(directory).Name==observedName,"Short page labels damaged the full saved set name");
    }
}
