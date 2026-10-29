using System;
using System.Globalization;
using System.Linq;
using VanyaTools.Native;

internal static class Program
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Main()
    {
        try { Check(); Console.WriteLine("Color proof legend checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check()
    {
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        foreach (bool cmyk in new[] {true, false})
        foreach (bool compound in new[] {false, true}) {
            var options = new ColorProofGridOptions {Cmyk=cmyk, Compound=compound,
                BaseChannels=cmyk ? new[] {35,76,80,25} : new[] {70,100,130},
                Columns=3, Rows=3, HorizontalChannel=1, VerticalChannel=2, SheetWidthMm=570};
            int[] original = (int[])options.BaseChannels.Clone();
            var plan = ColorProofGridPlan.Build(options);
            var catalog = ColorProofCatalog.Build(new[] {plan});
            var legend = ColorProofGridLegendPlan.Build(plan, 451);
            Require(legend.Entries.Count == plan.Patches.Count, "Missing legend entry");
            string[] names = cmyk ? new[] {"C", "M", "Y", "K"} : new[] {"R", "G", "B"};
            for (int i=0; i<legend.Entries.Count; i++) {
                var entry=legend.Entries[i];
                Require(entry.Number == 451+i, "Continued numbering changed");
                Require(entry.Text == entry.Number + " " + String.Concat(catalog.Patches[i].Values.Select((v,c)=>names[c]+v.ToString(CultureInfo.InvariantCulture))), "Legend recipe differs from numbered catalog patch");
                Require(entry.Xmm == i%2*(ColorProofGridLegendPlan.ColumnWidthMm+ColorProofGridLegendPlan.ColumnGapMm), "Legend does not alternate columns");
                Require(entry.Ymm == ColorProofGridLegendPlan.HeadingHeightMm+i/2*ColorProofGridLegendPlan.RowPitchMm, "Legend rows are not aligned");
                Require(entry.Ymm+ColorProofGridLegendPlan.RowPitchMm <= legend.HeightMm+.000001, "Last legend row is clipped");
            }
            Require(plan.RequiredWidthMm <= options.SheetWidthMm, "Legend exceeds sheet width");
            Require(ColorProofGridLegendPlan.ContentHeight(plan) >= Math.Max(plan.HeightMm,legend.HeightMm), "Page clips legend");
            Require(options.BaseChannels.SequenceEqual(original) && plan.BaseChannels.SequenceEqual(original), "Source color changed");
        }
        foreach (int count in new[] {15,31}) {
            var plan=ColorProofGridPlan.Build(new ColorProofGridOptions {Compound=true, BaseChannels=new[] {50,50,50,50}, Columns=count, Rows=count, SheetWidthMm=1200});
            var legend=ColorProofGridLegendPlan.Build(plan);
            Require(legend.Entries.Count == count*count*4 && legend.Entries.Last().Number == count*count*4, "Large legend lost entries");
            Require(ColorProofGridLegendPlan.ContentHeight(plan) >= legend.HeightMm, "Large legend clipped");
        }
        try {
            ColorProofGridPlan.Build(new ColorProofGridOptions {BaseChannels=new[] {35,76,80,25},Columns=3,Rows=3,SheetWidthMm=100});
            throw new Exception("Accepted a sheet too narrow for the side legend");
        } catch (InvalidOperationException error) {Require(error.Message.Contains("легендой"), "Width error does not explain the legend");}
        try {ColorProofGridLegendPlan.Build(null); throw new Exception("Accepted null plan");}
        catch (ArgumentException) {}
        CheckLegendToggle();
    }

    private static void CheckLegendToggle()
    {
        Require(new ColorProofGridOptions().PrintLegend, "Printed formulas must be enabled by default");
        foreach (bool cmyk in new[] {true, false})
        foreach (bool compound in new[] {false, true}) {
            var options = new ColorProofGridOptions {Cmyk=cmyk, Compound=compound,
                BaseChannels=cmyk ? new[] {35,76,80,25} : new[] {70,100,130},
                Columns=15, Rows=15, SheetWidthMm=570};
            var enabled=ColorProofGridPlan.Build(options);
            options.PrintLegend=false;
            Require(!options.Copy().PrintLegend && !options.Clone().PrintLegend, "Options copy lost disabled legend");
            var disabled=ColorProofGridPlan.Build(options);
            Require(enabled.Options.PrintLegend && !disabled.Options.PrintLegend, "Plan snapshot did not preserve its own legend setting");
            Require(Math.Abs(enabled.RequiredWidthMm-disabled.RequiredWidthMm-ColorProofGridLegendPlan.SideWidthMm)<.000001,
                "Disabled formulas still reserve the side strip");
            Require(ColorProofGridLegendPlan.ContentHeight(disabled)==disabled.HeightMm,
                "Disabled formulas still reserve their long footer height");
            Require(enabled.WidthMm==disabled.WidthMm && enabled.HeightMm==disabled.HeightMm,
                "Legend toggle changed patch geometry");
            Require(enabled.Patches.Count==disabled.Patches.Count && enabled.Patches.Zip(disabled.Patches,
                (a,b)=>a.Values.SequenceEqual(b.Values)&&a.Xmm==b.Xmm&&a.Ymm==b.Ymm&&a.WidthMm==b.WidthMm&&a.HeightMm==b.HeightMm).All(value=>value),
                "Legend toggle changed patch colors or numbering positions");
            var catalog=ColorProofCatalog.Build(new[]{disabled});
            Require(catalog.Patches.Count==disabled.Patches.Count && catalog.FindPatch(catalog.Patches.Count).Values.SequenceEqual(
                ColorProofGridLegendPlan.Build(disabled).Entries.Count==catalog.Patches.Count ? catalog.Patches.Last().Values : new int[0]),
                "Turning off printing discarded saved recipes");
            options.SheetWidthMm=Math.Max(100,disabled.RequiredWidthMm);
            Require(ColorProofGridPlan.Build(options).RequiredWidthMm<=options.SheetWidthMm,
                "A fitting grid was rejected after disabling formulas");
            options.PrintLegend=true;
            try {ColorProofGridPlan.Build(options); throw new Exception("Narrow sheet accepted with formulas on");}
            catch (InvalidOperationException error) {Require(error.Message.Contains("легендой"),"On error must explain side formulas");}
        }
        foreach (bool cmyk in new[] {true,false})
        foreach (bool compound in cmyk ? new[] {false,true} : new[] {false}) {
            var grid=new ColorProofGridOptions {Cmyk=cmyk,Compound=compound,PrintLegend=false,
                BaseChannels=cmyk?new[]{35,76,80,25}:new[]{70,100,130},SheetWidthMm=570};
            var seriesOptions=new ColorProofGridSeriesOptions {Grid=grid,Channel=2,Count=3};
            Require(!seriesOptions.Copy().Grid.PrintLegend,"Series copy lost disabled formulas");
            var series=ColorProofGridSeriesPlan.Build(seriesOptions);
            Require(series.Pages.All(page=>!page.Options.PrintLegend&&ColorProofGridLegendPlan.ContentHeight(page)==page.HeightMm),
                "Some series pages print or reserve disabled formulas");
            var catalog=ColorProofCatalog.Build(series.Pages,series.OriginalBaseChannels);
            Require(catalog.Patches.Count==series.TotalPatchCount&&catalog.Patches.Last().Number==series.TotalPatchCount,
                "Series formulas were not catalogued when printing was off");
            seriesOptions.Grid.PrintLegend=true;
            var withLegend=ColorProofGridSeriesPlan.Build(seriesOptions);
            Require(withLegend.Pages.All(page=>page.Options.PrintLegend)&&withLegend.TotalPatchCount==series.TotalPatchCount,
                "Enabled formulas changed series count or failed to reach every page");
        }
    }
}
