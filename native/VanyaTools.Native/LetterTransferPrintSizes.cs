using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VanyaTools.Native
{
    internal static class LetterTransferPrintSizes
    {
        internal const int MaximumCount=16;
        internal static double[] For(LetterTransferSet set)=>
            set.PrintHeightsMm==null || set.PrintHeightsMm.Count==0?new[]{set.HeightMm}:set.PrintHeightsMm.ToArray();
        internal static void Validate(double[] values)
        {
            if(values.Length<1 || values.Length>MaximumCount || values.Any(v=>!LetterTransferRules.Finite(v) || v<1 || v>500) || values.Distinct().Count()!=values.Length)
                throw new ArgumentException("Укажите от 1 до 16 разных высот букв, каждая от 1 до 500 мм. Разделяйте размеры точкой с запятой: 20; 80; 35,5.");
        }
        internal static double[] Parse(string text)
        {
            var parts=Regex.Split((text??"").Trim(),@"[;\s]+");var values=new List<double>();
            foreach(string part in parts)
            {
                if(String.IsNullOrWhiteSpace(part))continue;
                if(!Double.TryParse(part.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out double value))
                    throw new ArgumentException("Не удалось прочитать высоту «"+part+"». Пример: 20; 80; 35,5 мм.");
                if(!values.Contains(value))values.Add(value);
                if(values.Count>MaximumCount)break;
            }
            var result=values.ToArray();Validate(result);return result;
        }
        internal static string Format(LetterTransferSet set)=>String.Join("; ",For(set).Select(Label));
        internal static string Label(double value)=>value.ToString("G",CultureInfo.CurrentCulture);
    }
}
