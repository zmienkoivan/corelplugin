using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    // Same numbering and geometry for the printed vectors and the on-screen preview.
    internal sealed class ColorProofGridLegendPlan
    {
        public const double GapMm = 6, ColumnWidthMm = 30, ColumnGapMm = 4;
        public const double WidthMm = 2 * ColumnWidthMm + ColumnGapMm;
        public const double SideWidthMm = GapMm + WidthMm;
        public const double HeadingHeightMm = 5, RowPitchMm = 2.8;
        public const float FontPoints = 6.5f;

        internal sealed class Entry
        {
            public int Number { get; internal set; }
            public string Text { get; internal set; }
            public double Xmm { get; internal set; }
            public double Ymm { get; internal set; }
        }

        public IReadOnlyList<Entry> Entries { get; private set; }
        public double HeightMm { get; private set; }

        public static ColorProofGridLegendPlan Build(ColorProofGridPlan plan, int firstNumber = 1)
        {
            if (plan == null || firstNumber < 1) throw new ArgumentException("Не задана сетка или первый номер легенды.");
            string[] names = plan.Options.Cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
            var entries = new List<Entry>(plan.Patches.Count);
            foreach (ColorProofGridPatch patch in plan.Patches.OrderBy(item => item.Ymm).ThenBy(item => item.Xmm))
            {
                int index = entries.Count, number = checked(firstNumber + index);
                string channels = String.Concat(patch.Values.Select((value, channel) =>
                    names[channel] + value.ToString(CultureInfo.InvariantCulture)));
                entries.Add(new Entry {
                    Number = number, Text = number.ToString(CultureInfo.InvariantCulture) + " " + channels,
                    Xmm = index % 2 * (ColumnWidthMm + ColumnGapMm),
                    Ymm = HeadingHeightMm + index / 2 * RowPitchMm
                });
            }
            return new ColorProofGridLegendPlan {
                Entries = entries.AsReadOnly(),
                HeightMm = HeadingHeightMm + (entries.Count + 1) / 2 * RowPitchMm
            };
        }

        public static double ContentHeight(ColorProofGridPlan plan) =>
            plan.Options.PrintLegend
                ? Math.Max(plan.HeightMm, HeadingHeightMm + (plan.Patches.Count + 1) / 2 * RowPitchMm)
                : plan.HeightMm;
    }
}
