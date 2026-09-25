using System;
using System.IO;

namespace VanyaTools.Native
{
    internal static class CutSpotColor
    {
        public const string ColorName = "CUT";
        private const string PaletteGuid = "7c44705b-6fe8-4dd5-9d10-8bc3ae58219f";
        private const string PaletteFileName = "VanyaTools.CutPalette.xml";

        public static void ValidateAvailable(dynamic app)
        {
            CreateColor(app);
        }

        public static void AssignToOutline(dynamic outline, dynamic app)
        {
            dynamic color = CreateColor(app);
            outline.Color = color;

            dynamic assigned = outline.Color;
            ValidateColor(assigned);
            Log.Info("Applied named spot outline color CUT.");
        }

        private static dynamic CreateColor(dynamic app)
        {
            string paletteIdentifier = EnsurePaletteIsOpen(app);
            dynamic color = app.CreateSpotColorByName(paletteIdentifier, ColorName, 100);
            ValidateColor(color);
            return color;
        }

        private static void ValidateColor(dynamic color)
        {
            bool isSpot = Convert.ToBoolean(color.IsSpot);
            string name = Convert.ToString(color.SpotColorName) ?? "";
            if (!isSpot || !string.Equals(name, ColorName, StringComparison.OrdinalIgnoreCase))
            {
                string type = "";
                try { type = Convert.ToString(color.Type) ?? ""; } catch { }
                throw new InvalidOperationException(
                    "CorelDRAW вернул для линии цвет " + (isSpot ? "«" + name + "»" : "не spot") +
                    " (тип " + type + ") вместо плашечного цвета CUT.");
            }
        }

        private static string EnsurePaletteIsOpen(dynamic app)
        {
            string palettePath = Path.Combine(
                Path.GetDirectoryName(typeof(CutSpotColor).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                PaletteFileName);
            if (!File.Exists(palettePath))
            {
                Log.Error("CUT palette file is missing: " + palettePath,
                    new FileNotFoundException("VanyaTools.CutPalette.xml was not installed.", palettePath));
                throw new InvalidOperationException(
                    "Не найдена палитра плашечного цвета CUT: " + palettePath +
                    ". Установите обновление Vanya Tools и перезапустите CorelDRAW.");
            }

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
                    "CorelDRAW загрузил палитру CUT с неожиданным идентификатором: " + identifier);
            Log.Info("Loaded custom CUT spot-color palette: " + palettePath);
            return identifier;
        }
    }
}