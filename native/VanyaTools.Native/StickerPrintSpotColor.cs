using System;
using System.IO;
namespace VanyaTools.Native
{
    internal static class StickerPrintSpotColor
    {
        private const string PaletteGuid="50af2116-63b7-4f63-8baa-b6bdd41010eb";
        internal static object Create(object application,StickerPrintPlateKind kind)
        {
            dynamic app=application,palettes=app.Palettes;string identifier=null;
            for(int i=1;i<=Convert.ToInt32(palettes.Count);i++)
            {
                try
                {
                    dynamic palette=palettes[i];
                    if(String.Equals(Convert.ToString(palette.Identifier),PaletteGuid,StringComparison.OrdinalIgnoreCase)){identifier=PaletteGuid;break;}
                }
                catch { /* Some built-in palettes do not expose an identifier. */ }
            }
            if(identifier==null)
            {
                string path=Path.Combine(Path.GetDirectoryName(typeof(StickerPrintSpotColor).Assembly.Location),"VanyaTools.PrintPalette.xml");
                if(!File.Exists(path))throw new InvalidOperationException("Не найдена палитра WHITE/CLEAR. Установите полный пакет Vanya Tools.");
                dynamic palette=palettes.Open(path);identifier=Convert.ToString(palette.Identifier);
                if(!String.Equals(identifier,PaletteGuid,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("CorelDRAW открыл неверную палитру WHITE/CLEAR.");
            }
            object color=(object)app.CreateSpotColorByName(identifier,StickerPrintPlateModel.Ink(kind),100);Validate(color,kind);return color;
        }
        internal static void Validate(object value,StickerPrintPlateKind kind)
        {
            dynamic color=value;string expected=StickerPrintPlateModel.Ink(kind);
            if(!Convert.ToBoolean(color.IsSpot) || !String.Equals(Convert.ToString(color.SpotColorName),expected,StringComparison.Ordinal))
                throw new InvalidOperationException("CorelDRAW не применил плашечный цвет "+expected+". Печатная плашка не создана.");
        }
    }
}
