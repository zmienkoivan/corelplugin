using System;
using System.IO;
namespace VanyaTools.Native
{
    internal static class StickerPrintSpotColor
    {
        private const string PaletteGuid="50af2116-63b7-4f63-8baa-b6bdd41010eb";

        internal static void Show() => Show((object)CorelApp.Get());

        internal static void Show(object application)
        {
            string identifier = EnsurePaletteIsOpen(application);
            CreateColor(application, identifier, StickerPrintPlateKind.White);
            CreateColor(application, identifier, StickerPrintPlateKind.Clear);
        }

        internal static object Create(object application,StickerPrintPlateKind kind)
        {
            return CreateColor(application, EnsurePaletteIsOpen(application), kind);
        }

        private static object CreateColor(object application, string identifier, StickerPrintPlateKind kind)
        {
            dynamic app = application;
            object color = (object)app.CreateSpotColorByName(identifier, StickerPrintPlateModel.Ink(kind), 100);
            Validate(color, kind);
            return color;
        }

        private static string EnsurePaletteIsOpen(object application)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(StickerPrintSpotColor).Assembly.Location),
                "VanyaTools.PrintPalette.xml");
            if (!File.Exists(path))
                throw new InvalidOperationException("Не найдена палитра WHITE/CLEAR. Установите полный пакет Vanya Tools.");

            dynamic app=application,palettes=app.Palettes;
            object found = null;
            for(int i=1;i<=Convert.ToInt32(palettes.Count);i++)
            {
                try
                {
                    dynamic palette=palettes[i];
                    if(String.Equals(Convert.ToString(palette.Identifier),PaletteGuid,StringComparison.OrdinalIgnoreCase))
                    { found = (object)palette; break; }
                }
                catch { /* Some built-in palettes do not expose an identifier. */ }
            }
            dynamic opened = found ?? (object)palettes.Open(path);
            string identifier = Convert.ToString(opened.Identifier);
            if (!String.Equals(identifier, PaletteGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CorelDRAW открыл неверную палитру WHITE/CLEAR.");

            // A registered palette can be closed. Loading it by identifier does
            // not show its swatches; Palette.Open explicitly opens it on screen.
            if (!Convert.ToBoolean(opened.IsOpen)) opened.Open();
            if (!Convert.ToBoolean(opened.IsOpen))
                throw new InvalidOperationException("CorelDRAW не показал палитру WHITE/CLEAR. Откройте её повторно кнопкой «Палитра WHITE/CLEAR».");
            return identifier;
        }
        internal static void Validate(object value,StickerPrintPlateKind kind)
        {
            dynamic color=value;string expected=StickerPrintPlateModel.Ink(kind);
            if(!Convert.ToBoolean(color.IsSpot) || !String.Equals(Convert.ToString(color.SpotColorName),expected,StringComparison.Ordinal))
                throw new InvalidOperationException("CorelDRAW не применил плашечный цвет "+expected+". Печатная плашка не создана.");
        }
    }
}
