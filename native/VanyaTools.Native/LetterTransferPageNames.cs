using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VanyaTools.Native
{
    internal static class LetterTransferPageNames
    {
        // Corel's native page-tab update copies names into a fixed buffer.
        // Use a conservative limit of 32 UTF-16 code units;
        // the full collection name remains in the saved letter set.
        internal const int MaximumLength=32;
        internal static string Create(string setName,double height,int page)
        {
            string detail=LetterTransferPrintSizes.Label(height)+" мм · "+page.ToString(CultureInfo.InvariantCulture);
            string name=Regex.Replace(setName??"",@"[\p{Cc}\s]+"," ").Trim();
            int budget=MaximumLength-detail.Length-3;
            var prefix=new StringBuilder();var elements=StringInfo.GetTextElementEnumerator(name);
            while(elements.MoveNext())
            {
                string element=elements.GetTextElement();if(prefix.Length+element.Length>budget)break;prefix.Append(element);
            }
            string result=prefix.ToString().TrimEnd();
            return result.Length==0?detail:result+" · "+detail;
        }
    }
}
