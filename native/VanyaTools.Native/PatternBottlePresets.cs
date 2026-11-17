namespace VanyaTools.Native
{
    internal static class PatternBottlePresets
    {
        internal static PatternEditorSettings Bic()=>new PatternEditorSettings
        {
            Layout=new PatternFillOptions {Mode=PatternFillMode.Pebbles,SizeMm=17,GapMm=2},
            BottleTop=20,BottleBottom=20,BottleHeight=64,HeroSize=35,HeroGap=3,AlignHeroVertical=true
        };
        internal static double VerticalAngle(PatternPoint size)=>size.X>=size.Y?90:0;
    }
}
