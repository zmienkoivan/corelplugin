using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed partial class DtfCorrectionWindow
    {
        private TextBlock _resolutionInfo;
        private Border _resolutionBadge;
        private Button _normalizeResolution;
        private bool _normalizing, _resolutionNormalized, _normalizedSourceReady;
        private int _inputWidth, _inputHeight;
        private double _inputDpiX, _inputDpiY;
        private string _workingSourcePath, _normalizedSourcePath;
        private CancellationTokenSource _normalizationCancellation;
        private Point? _resampledPreviewCenter;

        private void CreateResolutionControls(Panel controls)
        {
            _resolutionInfo=new TextBlock { Text="Определяю DPI…",FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Black };
            _resolutionBadge=new Border { Child=_resolutionInfo,Padding=new Thickness(7,5,7,5),CornerRadius=new CornerRadius(4),Margin=new Thickness(0,3,0,3),Background=Brushes.LightGray };
            _resolutionBadge.ToolTip="Исходник: ниже 140 DPI — красный; 140–199 — жёлтый; от 200 — зелёный. Оценка по худшей оси.";
            controls.Children.Add(_resolutionBadge);
            _normalizeResolution=Button("Нормализовать до 300 DPI",async (_,__)=>await NormalizeResolutionAsync());
            _normalizeResolution.ToolTip="Один раз повысить разрешение временной копии. Размер в мм и исходник Corel сохраняются. Ресемпл сглаживает пиксели, но не восстанавливает утраченную детализацию.";
            _normalizeResolution.FontSize=10;controls.Children.Add(_normalizeResolution);
        }

        private static int ResolutionLevel(double dpi) => Math.Round(dpi,1)<140 ? 0 : Math.Round(dpi,1)<200 ? 1 : 2;
        internal static Color ResolutionColor(double dpi)
        {
            switch (ResolutionLevel(dpi))
            {
                case 0: return Color.FromRgb(255, 202, 202);
                case 1: return Color.FromRgb(255, 232, 164);
                default: return Color.FromRgb(194, 233, 202);
            }
        }

        internal static string FormatResolution(double dpiX, double dpiY) =>
            Math.Abs(dpiX - dpiY) < .05 ? dpiX.ToString("0.#") : dpiX.ToString("0.#") + " × " + dpiY.ToString("0.#");

        private bool CanNormalizeSource => Math.Min(_inputDpiX, _inputDpiY) < 299.99;

        internal static bool ResolutionSizeMatches(int oldPixels,double oldDpi,int pixels,double dpi)
        {
            if(pixels<oldPixels || Double.IsNaN(dpi) || Double.IsInfinity(dpi) || dpi<299.95)return false;
            double beforeMm=oldPixels*25.4/oldDpi,afterMm=pixels*25.4/dpi;
            // Preview PNG DPI has a 0.0254 step. Native channel metadata can be
            // more precise; comparing exact dimensions/DPIs would reject a
            // correctly normalized source near an integer pixel boundary.
            return Math.Abs(beforeMm-afterMm)<=.05+beforeMm*.013/oldDpi;
        }
        private void UpdateResolutionInfo()
        {
            if(_normalizeResolution==null || !_loaded) return;
            _resolutionBadge.Background = new SolidColorBrush(ResolutionColor(_resolutionNormalized ? 300 : Math.Min(_inputDpiX, _inputDpiY)));
            string dpi = FormatResolution(_inputDpiX, _inputDpiY);
            _resolutionInfo.Text=_resolutionNormalized ? "DPI: "+dpi+" → "+Math.Min(_originalPixels.DpiX,_originalPixels.DpiY).ToString("0.#")+" · "+_originalPixels.Width+" × "+_originalPixels.Height+" px" :
                "Исходник: "+dpi+" DPI · "+_inputWidth+" × "+_inputHeight+" px";
            _normalizeResolution.Content=_resolutionNormalized ? "Вернуть исходное разрешение" : CanNormalizeSource ? "Нормализовать до 300 DPI" : "Ресемпл не нужен · ≥300 DPI";
            _normalizeResolution.IsEnabled=_settingsPanel.IsEnabled && !_normalizing && !_closed && (_resolutionNormalized || CanNormalizeSource);
        }

        private async Task NormalizeResolutionAsync()
        {
            if(!_loaded || _closed || _normalizing || _applying || _insertingThinMask || _presetBusy ||
                !ReadOptions(out DtfCorrectionOptions settings)) return;
            bool normalize=!_resolutionNormalized;
            DtfResamplePlan expected;
            try { expected=normalize ? DtfResamplePlan.Create(_inputWidth,_inputHeight,_inputDpiX,_inputDpiY) : null; }
            catch(Exception error) { ShowMessage(error.Message,true);return; }
            if(normalize && !expected.Needed)return;
            _normalizing=true;SetControlsEnabled(false);_apply.IsEnabled=false;
            _delay.Stop();_revision++;CancelDetail(true);_previewCancellation?.Cancel();_qualityCancellation?.Cancel();
            _qualityRevision++;_qualityChanging=true;
            var cancelSource=new CancellationTokenSource();_normalizationCancellation=cancelSource;var cancel=cancelSource.Token;
            string normalizedPath=_normalizedSourcePath??(_normalizedSourcePath=Path.Combine(_workspace,"normalized-300.png"));
            string path=normalize ? normalizedPath : _sourcePath;
            bool cached=_normalizedSourceReady,retained=false;
            string failure=null;
            int quality=SelectedPreviewQuality();var processor=_processor;
            Task preview=_previewTask,detail=_detailTask,overviewTask=_qualityTask;
            try
            {
                ShowMessage(normalize ? "Поднимаю разрешение до 300 DPI…" : "Возвращаю исходное разрешение…",false);
                var task=Task.Run(async ()=> {
                    foreach(var previous in new[]{preview,detail,overviewTask}) if(previous!=null) try{await previous.ConfigureAwait(false);}catch{}
                    cancel.ThrowIfCancellationRequested();
                    if(normalize && !cached) processor(_sourcePath,normalizedPath,new DtfCorrectionOptions {Resample300=true},cancel);
                    var image=Load(path);cancel.ThrowIfCancellationRequested();
                    if(normalize && (!ResolutionSizeMatches(_inputWidth,_inputDpiX,image.PixelWidth,image.DpiX) || !ResolutionSizeMatches(_inputHeight,_inputDpiY,image.PixelHeight,image.DpiY)))
                        throw new InvalidOperationException("Ресемпл не сохранил ожидаемый размер и DPI. Исходник оставлен без изменений.");
                    var full=DtfCorrectionPixels.Capture(image);var overview=BuildPreviewOverview(image,quality,_workspace,cancel);
                    return Tuple.Create(full,overview);
                },cancel);
                var frame=await task.ConfigureAwait(false);
                await OnUiAsync(()=> {
                    if(_closed) {DeleteFile(frame.Item2.Path);return;}
                    foreach(var old in _overviewCache.Values)DeleteFile(old.Path);
                    _overviewCache.Clear();
                    var previous = _originalPixels;
                    var center = _resampledPreviewCenter ?? PreviewCenter();
                    _originalPixels = frame.Item1; UsePreviewOverview(frame.Item2);
                    if (!_zoomFit)
                    {
                        double sx = (double)_originalPixels.Width / previous.Width;
                        double sy = (double)_originalPixels.Height / previous.Height;
                        _zoomScale = Math.Max(.001, Math.Min(32, _zoomScale / sx));
                        if (center.HasValue)
                            _resampledPreviewCenter = new Point(center.Value.X * sx, center.Value.Y * sy);
                    }
                    _workingSourcePath=path;_resolutionNormalized=normalize;if(normalize)_normalizedSourceReady=true;
                    _syncing=true;
                    try {_units.SelectedIndex=0;_previousUnit=0;_radius.Text=DtfOutlineUnits.Format(settings.RadiusMm);_expansion.Text=DtfOutlineUnits.Format(settings.ExpansionMm);}
                    finally{_syncing=false;}
                    _previewScene = Rect.Empty; _baseRevision = -1;
                    _before.Source = _overview; _after.Source = null; retained = true;
                    UpdateResolutionInfo();
                });
            }
            catch(OperationCanceledException){}
            catch(Exception error) {
                Log.Error("Correction resolution preparation failed.",error);
                failure="DPI не изменён: "+error.GetBaseException().Message;
            }
            finally
            {
                if(normalize && !cached && !retained)DeleteFile(normalizedPath);
                await OnUiAsync(()=> {
                    _normalizing=false;_qualityChanging=false;cancelSource.Dispose();
                    if(ReferenceEquals(_normalizationCancellation,cancelSource))_normalizationCancellation=null;
                    if(_closed)Cleanup();else {
                        SetControlsEnabled(true);UpdateResolutionInfo();
                        if(failure==null)QueuePreview();else {_after.Opacity=1;_apply.IsEnabled=_after.Source!=null && OverviewReadyForSelection();ShowMessage(failure,true);}
                    }
                });
            }
        }
    }
}
