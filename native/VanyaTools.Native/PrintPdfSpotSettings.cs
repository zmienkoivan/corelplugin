using System;
using System.Reflection;
namespace VanyaTools.Native
{
    internal static class PrintPdfSpotSettings
    {
        // Corel SDK: OutputSpotColorsAs=0 is pdfSpotAsSpot; Overprints retains
        // document attributes, rather than simulating them into process inks.
        internal static void Require(object settings)
        {
            try
            {
                Type type=settings.GetType();
                type.InvokeMember("OutputSpotColorsAs",BindingFlags.SetProperty,null,settings,new object[]{0});
                type.InvokeMember("Overprints",BindingFlags.SetProperty,null,settings,new object[]{true});
                if(Convert.ToInt32(type.InvokeMember("OutputSpotColorsAs",BindingFlags.GetProperty,null,settings,null))!=0 ||
                    !Convert.ToBoolean(type.InvokeMember("Overprints",BindingFlags.GetProperty,null,settings,null)))throw new InvalidOperationException("Corel не применил сохранение плашек и наложений.");
            }
            catch(Exception error){throw new InvalidOperationException("PDF не отправлен: не удалось сохранить плашечные цвета WHITE/CLEAR и наложения. "+error.GetBaseException().Message,error);}
        }
    }
}
