using System;
using System.IO;

namespace VanyaTools.Native
{
    // A separate context for each generation; never use the selection active
    // minutes later, or a different tool's pending raster operation.
    internal static class SublimationInsertion
    {
        internal static SublimationCapture Capture()
        {
            RasterInsertionSource source = RasterResultInsertion.CaptureSelection();
            string path = AiSelectionCapture.Capture(2048, 4000000);
            try
            {
                RasterResultInsertion.SetInputRaster(source, path);
                return new SublimationCapture { Path = path, Source = source };
            }
            catch
            {
                try { File.Delete(path); } catch { }
                throw;
            }
        }

        internal static string Import(string path, string operation, RasterInsertionSource source, int widthMm, int heightMm)
        {
            RasterResultInsertion.SaveSizedResult(path, source, widthMm, heightMm);
            return RasterResultInsertion.Import(path, operation, RasterInsertionMode.NewAside, source, keepResultLayer: true);
        }
    }
}
