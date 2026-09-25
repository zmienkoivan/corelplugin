using System;
using System.IO;

namespace VanyaTools.Native
{
    internal static class CutSpotColor
    {
        public const string ColorName = "CUT";
        private const string PaletteGuid = "7c44705b-6fe8-4dd5-9d10-8bc3ae58219f";
        private const string PaletteFileName = "VanyaTools.CutPalette.xml";

        public static void AssignToOutline(dynamic outline, dynamic app)
        {
            string paletteIdentifier = EnsurePaletteIsOpen(app);
            dynamic color = outline.Color;
            color.SpotAssignByName(paletteIdentifier, ColorName, 100);
            outline.Color = color;

            if (!Convert.ToBoolean(color.IsSpot) ||
                !string.Equals(Convert.ToString(color.SpotColorName), ColorName,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "CorelDRAW не назначил плашечный цвет CUT контуру реза.");
        }

        private static string EnsurePaletteIsOpen(dynamic app)
        {
            string palettePath = Path.Combine(
                Path.GetDirectoryName(typeof(CutSpotColor).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                PaletteFileName);
            if (!File.Exists(palettePath))
                throw new InvalidOperationException(
                    "Не найдена палитра плашечного цвета CUT. Переустановите Vanya Tools.");

            dynamic palettes = app.Palettes;
            int count = Convert.ToInt32(palettes.Count);
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic palette = palettes[i];
                    if (string.Equals(Convert.ToString(palette.Identifier), PaletteGuid,
                        StringComparison.OrdinalIgnoreCase))
                        return Convert.ToString(palette.Identifier);
                }
                catch { }
            }

            dynamic opened = palettes.Open(palettePath);
            string identifier = Convert.ToString(opened.Identifier);
            if (!string.Equals(identifier, PaletteGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "CorelDRAW загрузил палитру CUT с неожиданным идентификатором.");
            Log.Info("Loaded custom CUT spot-color palette: " + palettePath);
            return identifier;
        }
    }
}