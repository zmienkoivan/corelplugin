using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class DtfCorrectionOptions
    {
        internal bool RemoveSemiTransparent, SmoothStairs, Outline, WhitenNearWhite;
        // One-time input preparation, not a persistent filter/preset setting.
        internal bool Resample300;
        internal int Threshold = 130, NearWhiteTolerance = 6, StairsScale = 1;
        internal double RadiusMm = 2 * 25.4 / 400, ExpansionMm = .175;
        internal Color Color = Colors.White;
        internal bool Knockout, Halftone, HalftoneAlphaOnly = true, HalftoneAutoDot = true;
        internal bool KnockoutSoft = true;
        internal bool HalftoneRemoveSmall, HalftoneGrowSmall;
        internal double HalftoneRemoveMm=.3, HalftoneGrowMm=.5;
        internal int CleanupCutEdges;
        internal bool NeedsGeometry => Outline || SmoothStairs || Halftone && (HalftoneRemoveSmall || HalftoneGrowSmall);
        internal DtfDotShape HalftoneShape;
        internal int HalftoneColorBoost = 25;
        internal Color KnockoutColor = Colors.Black;
        internal int KnockoutTolerance = 10;
        internal double HalftoneLpi = 20, HalftoneAngle = 22.5, HalftoneDotMm = 1;
        internal double GridOriginXmm, GridOriginYmm;
        internal RasterProtectionMask KnockoutMask, HalftoneMask, WhiteMask, AlphaMask;
        internal bool HasProtection => Knockout && KnockoutMask != null || Halftone && HalftoneMask != null ||
            WhitenNearWhite && WhiteMask != null || RemoveSemiTransparent && AlphaMask != null;
        internal DtfCorrectionOptions Copy() => (DtfCorrectionOptions)MemberwiseClone();
        internal DtfPrintPreparationOptions Preparation(double offsetX = 0, double offsetY = 0) => new DtfPrintPreparationOptions {
            Knockout = Knockout, Red = KnockoutColor.R, Green = KnockoutColor.G, Blue = KnockoutColor.B,
            Tolerance = KnockoutTolerance, Halftone = Halftone, AlphaOnly = HalftoneAlphaOnly,
            SoftKnockout = KnockoutSoft, Shape = HalftoneShape, ColorBoost = HalftoneColorBoost,
            RemoveSmall=HalftoneRemoveSmall, GrowSmall=HalftoneGrowSmall, RemoveBelowMm=HalftoneRemoveMm, GrowToMm=HalftoneGrowMm, CutEdges=CleanupCutEdges,
            AutoDot = HalftoneAutoDot, FrequencyLpi = HalftoneLpi, AngleDegrees = HalftoneAngle,
            DotDiameterMm = HalftoneDotMm, OriginXmm = GridOriginXmm + offsetX, OriginYmm = GridOriginYmm + offsetY };
    }
}
