using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal static class PrintRasterResolution
    {
        internal static IList<KeyValuePair<string,object>> Settings(int dpi,bool lossless)
        {
            if(dpi!=150 && dpi!=300)throw new ArgumentException("Выберите качество экспорта 150 или 300 DPI.");
            // ZIP preserves the resampled bitmap without JPEG artefacts. It
            // must not disable the selected output resolution: tiny placements
            // can otherwise carry thousands of DPI into every repeated sticker.
            return new List<KeyValuePair<string,object>>{
                Pair("BitmapCompression",lossless?3:2),Pair("JPEGQualityFactor",95),
                Pair("DownsampleColor",true),Pair("ColorResolution",dpi),
                Pair("DownsampleGray",true),Pair("GrayResolution",dpi),
                Pair("DownsampleMono",true),Pair("MonoResolution",1200)
            };
        }
        private static KeyValuePair<string,object> Pair(string key,object value)=>new KeyValuePair<string,object>(key,value);
    }
}
