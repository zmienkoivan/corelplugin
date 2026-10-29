namespace VanyaTools.Native
{
    internal sealed partial class SimpleAiTab
    {
        private RasterInsertionSource _rasterInsertionSource;
        private RasterInsertionMode _rasterOperationMode = RasterInsertionMode.NewAside;

        private void CaptureRasterInsertion()
        {
            _rasterInsertionSource = null;
            if (_mode != SimpleAiMode.Raster) return;
            _rasterOperationMode = SelectedRasterInsertionMode;
            _rasterInsertionSource = RasterResultInsertion.CaptureSelection();
        }

        private void SetRasterInsertionInput(string source)
        {
            if (_mode == SimpleAiMode.Raster && _rasterInsertionSource != null)
                RasterResultInsertion.SetInputRaster(_rasterInsertionSource, source);
        }

        private void PrepareRasterInsertion(string output, double offsetX = 0, double offsetY = 0,
            bool preserveSourceSize = false, bool diagnostic = false)
        {
            if (_mode == SimpleAiMode.Raster)
                RasterResultInsertion.SaveResult(output, _rasterInsertionSource, _rasterOperationMode,
                    offsetX, offsetY, preserveSourceSize, diagnostic);
        }
    }
}
