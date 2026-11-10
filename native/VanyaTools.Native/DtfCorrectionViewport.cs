using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed partial class DtfCorrectionWindow
    {
        private readonly System.Windows.Controls.Canvas _beforeDetailCanvas, _afterDetailCanvas;
        private readonly Image _beforeDetail, _afterDetail;
        private readonly DispatcherTimer _detailDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private Rect _previewScene = Rect.Empty;
        private CancellationTokenSource _detailCancellation;
        private Task _detailTask;
        private bool _detailRendering;
        private int _detailRevision, _baseRevision = -1, _tileRevision = -1;
        private Int32Rect _tileBounds;
        private sealed class DetailFrame
        {
            internal DtfCorrectionPixels Before, After;
            internal string Note, StairNote;
        }

        private void CancelDetail(bool clear)
        {
            ++_detailRevision; _detailDelay.Stop(); _detailCancellation?.Cancel();
            if (!clear) return;
            _tileRevision = -1; _beforeDetail.Source = _afterDetail.Source = null;
            _before.Clip = _after.Clip = null;
        }

        private void QueueDetail()
        {
            if (!_loaded || _closed || _applying || _insertingThinMask || _normalizing) return;
            CancelDetail(false);
            if (_view.SelectedIndex == 0) _detailDelay.Start();
        }

        private Rect VisibleWorld(ScrollViewer scroll, Image image)
        {
            if (_previewScene.IsEmpty || image.ActualWidth <= 0 || image.ActualHeight <= 0 ||
                scroll.ViewportWidth <= 0 || scroll.ViewportHeight <= 0) return Rect.Empty;
            Point origin = image.TranslatePoint(new Point(), scroll);
            var visible = new Rect(_previewScene.X - origin.X * _previewScene.Width / image.ActualWidth,
                _previewScene.Y - origin.Y * _previewScene.Height / image.ActualHeight,
                scroll.ViewportWidth * _previewScene.Width / image.ActualWidth,
                scroll.ViewportHeight * _previewScene.Height / image.ActualHeight);
            visible.Intersect(_previewScene); return visible;
        }

        private Point? PreviewCenter()
        {
            if (_zoomFit) return null;
            Rect visible = VisibleWorld(_beforeScroll, _before);
            return visible.IsEmpty ? (Point?)null : new Point(visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
        }

        private void RestorePreviewCenter(Point? point)
        {
            if (!point.HasValue || _zoomFit) return;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed || _zoomFit) return;
                foreach (var scroll in new[] { _beforeScroll, _afterScroll })
                {
                    var image = ReferenceEquals(scroll, _beforeScroll) ? _before : _after;
                    scroll.ScrollToHorizontalOffset(image.Margin.Left + (point.Value.X - _previewScene.X) * image.Width / _previewScene.Width - scroll.ViewportWidth / 2);
                    scroll.ScrollToVerticalOffset(image.Margin.Top + (point.Value.Y - _previewScene.Y) * image.Height / _previewScene.Height - scroll.ViewportHeight / 2);
                }
                QueueDetail();
            }), DispatcherPriority.Loaded);
        }

        private async Task DetailAsync()
        {
            if (_closed || !_loaded || _applying || _insertingThinMask || _normalizing || _qualityChanging ||
                _view.SelectedIndex != 0 || _baseRevision != _revision || _previewScene.IsEmpty) return;
            if (_rendering || _detailRendering || _qualityActive > 0) { _detailDelay.Start(); return; }
            if (_overviewPixels.Width == _originalPixels.Width && _overviewPixels.Height == _originalPixels.Height)
            { ShowMessage("Просмотр в исходном разрешении. «1:1» задаёт масштаб.", false); return; }
            Rect visible = VisibleWorld(_beforeScroll, _before);
            visible.Union(VisibleWorld(_afterScroll, _after));
            if (visible.IsEmpty) return;
            if (_tileRevision == _revision && CorrectionViewportPlan.WorldRect(_tileBounds).Contains(visible)) return;
            if (!ReadOptions(out DtfCorrectionOptions options)) return;
            ThinAuditOptions thin = _thinEnabled.IsChecked == true ? ThinOptions() : null;
            double guardMm = options.Outline ? options.RadiusMm + options.ExpansionMm : 0;
            if(options.Halftone && (options.HalftoneRemoveSmall || options.HalftoneGrowSmall))
                guardMm=Math.Max(guardMm,2*Math.Max(options.HalftoneRemoveSmall?options.HalftoneRemoveMm:0,options.HalftoneGrowSmall?options.HalftoneGrowMm:0));
            if (thin != null) guardMm = Math.Max(guardMm, 2 * Math.Max(thin.DotMm, thin.SafeMm + .2));
            var plan = CorrectionViewportPlan.Create(_originalPixels.Width, _originalPixels.Height, _previewScene, visible,
                CurrentZoom(), options.SmoothStairs ? options.StairsScale : 1,
                checked((int)Math.Ceiling(guardMm * _originalPixels.DpiX / 25.4) + 8),
                checked((int)Math.Ceiling(guardMm * _originalPixels.DpiY / 25.4) + 8));
            if (plan == null)
            { ShowMessage("Общий обзор. Увеличьте нужный участок — он уточнится автоматически.", false); return; }
            if (thin != null) { thin.CutEdges = plan.CutEdges; thin.IgnoreBoundaryDots = plan.CutEdges != 0; }
            int settingsRevision = _revision, detailRevision = _detailRevision;
            bool highlight = _highlight.IsChecked == true, red = _marker.SelectedIndex == 1, white = _highlightWhite.IsChecked == true;
            Color marker = _nearWhiteMarker;
            DtfCorrectionPixels source = _originalPixels; string workspace = _workspace; var processor = _processor;
            var cancellationSource = new CancellationTokenSource(); _detailCancellation = cancellationSource;
            CancellationToken cancellation = cancellationSource.Token;
            _detailRendering = true;
            try
            {
                var task = Task.Run(() => RenderDetail(source, plan, options, thin, highlight, red, white, marker, workspace, processor, cancellation), cancellation);
                _detailTask = task;
                DetailFrame frame = await task.ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (_closed || _applying || settingsRevision != _revision || detailRevision != _detailRevision || _view.SelectedIndex != 0) return;
                    _tileBounds = plan.Target; _tileRevision = settingsRevision;
                    _beforeDetail.Source = frame.Before.Bitmap(); _afterDetail.Source = frame.After.Bitmap(); PositionDetail();
                    if (thin != null) _thinNote.Text = frame.Note;
                    _stairEdge.Note.Text = frame.StairNote;
                    ShowMessage("Видимый участок — из исходника. Зум и прокрутка уточняются автоматически.", false);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Log.Error("Correction visible-area preview failed.", error);
                await OnUiAsync(() => { if (!_closed && settingsRevision == _revision && detailRevision == _detailRevision)
                    ShowMessage("Уточнение недоступно: " + error.GetBaseException().Message + ". Показан общий обзор.", true); });
            }
            finally
            {
                await OnUiAsync(() =>
                {
                    _detailRendering = false; cancellationSource.Dispose();
                    if (ReferenceEquals(_detailCancellation, cancellationSource)) _detailCancellation = null;
                    if (_closed) Cleanup();
                    else if (!_applying && !_qualityChanging && !_insertingThinMask)
                    {
                        if (_retryPreview || settingsRevision != _revision) { _delay.Stop(); _delay.Start(); }
                        else if (detailRevision != _detailRevision) QueueDetail();
                    }
                });
            }
        }

        private void PositionDetail()
        {
            if (_previewScene.IsEmpty) return;
            for (int i = 0; i < 2; i++)
            {
                Image image = i == 0 ? _before : _after, tile = i == 0 ? _beforeDetail : _afterDetail;
                var canvas = i == 0 ? _beforeDetailCanvas : _afterDetailCanvas;
                canvas.Width = image.Width; canvas.Height = image.Height; canvas.Margin = image.Margin;
                if (tile.Source == null || _tileRevision != _revision) { image.Clip = null; continue; }
                double sx = image.Width / _previewScene.Width, sy = image.Height / _previewScene.Height;
                var rect = new Rect((_tileBounds.X - _previewScene.X) * sx, (_tileBounds.Y - _previewScene.Y) * sy,
                    _tileBounds.Width * sx, _tileBounds.Height * sy);
                System.Windows.Controls.Canvas.SetLeft(tile, rect.X); System.Windows.Controls.Canvas.SetTop(tile, rect.Y);
                tile.Width = rect.Width; tile.Height = rect.Height;
                RenderOptions.SetBitmapScalingMode(tile, CurrentZoom() >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                // Transparent exact pixels must reveal the checkerboard, not a
                // blurred pixel from the overview underneath the detail tile.
                var clip = Geometry.Combine(new RectangleGeometry(new Rect(0, 0, image.Width, image.Height)),
                    new RectangleGeometry(rect), GeometryCombineMode.Exclude, null); clip.Freeze(); image.Clip = clip;
            }
        }

        private static DetailFrame RenderDetail(DtfCorrectionPixels source, CorrectionViewportPlan plan,
            DtfCorrectionOptions options, ThinAuditOptions thin, bool highlight, bool red, bool white, Color marker,
            string workspace, Func<string, string, DtfCorrectionOptions, CancellationToken, DtfCorrectionResult> processor,
            CancellationToken cancellation)
        {
            var input = source.Crop(plan.Input, cancellation);
            options = options.Copy();
            options.GridOriginXmm += plan.Input.X * 25.4 / source.DpiX;
            options.GridOriginYmm += plan.Input.Y * 25.4 / source.DpiY;
            bool geometry = options.NeedsGeometry;
            options.CleanupCutEdges=plan.CutEdges;
            // The processor performs the entire geometry pipeline. Do not
            // screen a second time just to decide whether it should run:
            // smoothing can reveal dots absent at the input sampling phase.
            var corrected = geometry ? input : input.Correct(options, cancellation);
            BitmapSource result; Tuple<double, double> offset = Tuple.Create(0d, 0d);
            string inputPath = null, outputPath = null;
            try
            {
                if (geometry && input.HasVisiblePixels(cancellation))
                {
                    inputPath = Path.Combine(workspace, "detail-input-" + Guid.NewGuid().ToString("N") + ".png");
                    outputPath = Path.Combine(workspace, "detail-output-" + Guid.NewGuid().ToString("N") + ".png");
                    Save(input.Bitmap(), inputPath); cancellation.ThrowIfCancellationRequested();
                    var processed = processor(inputPath, outputPath, options, cancellation);
                    result = Load(outputPath); offset = processed.OffsetMm;
                }
                else result = corrected.Bitmap();
                double sx = result.DpiX / source.DpiX, sy = result.DpiY / source.DpiY;
                BitmapSource original = input.Bitmap();
                string stairNote = "";
                if (options.AuditStairs)
                {
                    original = input.AuditStairs(true, options.StairStrength, cancellation, out int beforeElbows).Bitmap();
                    result = DtfCorrectionPixels.Capture(result).AuditStairs(true, options.StairStrength, cancellation, out int afterElbows).Bitmap();
                    stairNote = StairNote(options, beforeElbows, afterElbows, true);
                }
                int ow = Math.Max(1, (int)Math.Round(input.Width * sx)), oh = Math.Max(1, (int)Math.Round(input.Height * sy));
                if (ow != input.Width || oh != input.Height)
                { var scaled = new TransformedBitmap(original, new ScaleTransform((double)ow / input.Width, (double)oh / input.Height)); scaled.Freeze(); original = scaled; }
                int x = (int)Math.Round((plan.Input.X - plan.Work.X) * sx), y = (int)Math.Round((plan.Input.Y - plan.Work.Y) * sy);
                int width = Math.Max(1, (int)Math.Round(plan.Work.Width * sx)), height = Math.Max(1, (int)Math.Round(plan.Work.Height * sy));
                var before = DtfCorrectionPixels.Capture(Canvas(original, x, y, width, height, result.DpiX, result.DpiY, cancellation));
                var after = DtfCorrectionPixels.Capture(Canvas(result, x - (int)Math.Round((offset?.Item1 ?? 0) * result.DpiX / 25.4),
                    y - (int)Math.Round((offset?.Item2 ?? 0) * result.DpiY / 25.4), width, height, result.DpiX, result.DpiY, cancellation));
                var bt = AnalyzeThinPreview(before, thin, true, cancellation, out string bn);
                var at = AnalyzeThinPreview(after, thin, true, cancellation, out string an);
                before = before.Audit(highlight, red, white, options.NearWhiteTolerance, marker.R, marker.G, marker.B, cancellation).OverlayDiagnostic(bt?.Overlay, cancellation);
                after = after.Audit(highlight, red, white, options.NearWhiteTolerance, marker.R, marker.G, marker.B, cancellation).OverlayDiagnostic(at?.Overlay, cancellation);
                var focus = new Int32Rect((int)Math.Round((plan.Target.X - plan.Work.X) * sx), (int)Math.Round((plan.Target.Y - plan.Work.Y) * sy),
                    Math.Max(1, (int)Math.Round(plan.Target.Width * sx)), Math.Max(1, (int)Math.Round(plan.Target.Height * sy)));
                return new DetailFrame { Before = before.Crop(focus, cancellation), After = after.Crop(focus, cancellation), Note = bn == an ? an : "Исходник: " + bn + " Результат: " + an, StairNote = stairNote };
            }
            finally { DeleteFile(inputPath); DeleteFile(outputPath); }
        }
    }
}
