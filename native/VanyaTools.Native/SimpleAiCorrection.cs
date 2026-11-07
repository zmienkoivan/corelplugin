using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed partial class SimpleAiTab
    {
        private readonly Button _correctionButton;
        private Border _correctionResolutionBadge;
        private TextBlock _correctionResolutionText;
        private CheckBox _normalizeBeforeCorrection;
        private object _correctionDocument;

        private FrameworkElement CreateCorrectionResolutionRow()
        {
            _correctionResolutionText = new TextBlock {
                Text = "DPI —", FontSize = 11, Foreground = Brushes.Black
            };
            _correctionResolutionBadge = new Border {
                Child = _correctionResolutionText, Background = Brushes.LightGray,
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 0, 6, 2),
                ToolTip = "Выделите один растр. Цвет показывает оценку по меньшему DPI из двух осей."
            };
            _normalizeBeforeCorrection = new CheckBox {
                Content = "Нормализовать до 300 DPI", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2),
                ToolTip = "При открытии коррекции повысить DPI рабочей копии, если он ниже 300. Размер в миллиметрах сохраняется."
            };
            var row = new WrapPanel { Margin = new Thickness(1, 2, 1, 2) };
            row.Children.Add(_correctionResolutionBadge);
            row.Children.Add(_normalizeBeforeCorrection);
            return row;
        }

        internal void RefreshCorrectionResolution(object selection, int count)
        {
            if (_busy || _correctionResolutionBadge == null) return;
            string text = "DPI —";
            Color color = Colors.LightGray;
            if (count == 1 && selection != null)
            {
                try
                {
                    using (DockerUiWatchdog.Trace(Dispatcher, "Correction DPI metadata"))
                    {
                        dynamic shape = ((dynamic)selection).Shapes.Item[1];
                        if (Convert.ToInt32(shape.Type) == CorelConstants.CdrBitmapShape)
                        {
                            dynamic bitmap = shape.Bitmap;
                            double dpiX = Convert.ToDouble(bitmap.ResolutionX);
                            double dpiY = Convert.ToDouble(bitmap.ResolutionY);
                            if (Finite(dpiX) && Finite(dpiY) && dpiX > 0 && dpiY > 0)
                            {
                                text = DtfCorrectionWindow.FormatResolution(dpiX, dpiY) + " DPI";
                                color = DtfCorrectionWindow.ResolutionColor(Math.Min(dpiX, dpiY));
                            }
                        }
                    }
                }
                catch { /* Corel can temporarily reject metadata reads during editing. */ }
            }
            if (_correctionResolutionText.Text == text &&
                (_correctionResolutionBadge.Background as SolidColorBrush)?.Color == color) return;
            _correctionResolutionText.Text = text;
            _correctionResolutionBadge.Background = new SolidColorBrush(color);
        }

        private sealed class CorrectionPlacement
        {
            public string DocumentPath { get; set; }
            public int Page { get; set; }
            public double LeftMm { get; set; }
            public double TopMm { get; set; }
            public double OffsetX { get; set; }
            public double OffsetY { get; set; }
        }

        private async Task OpenCorrection()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Вставьте готовый результат или нажмите «Отложить».", true); return; }
            bool normalizeOnLoad = _normalizeBeforeCorrection?.IsChecked == true;
            string source = null;
            SetBusy(true, "Подготавливаю выделение для коррекции…");
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                dynamic app = CorelApp.Get();
                dynamic doc = app.ActiveDocument;
                if (doc == null) throw new InvalidOperationException("Откройте CDR и выделите принт.");
                dynamic selection = app.ActiveSelectionRange;
                if (selection == null || Convert.ToInt32(selection.Count) == 0)
                {
                    throw new InvalidOperationException("Выделите принт на холсте.");
                }
                var placement = new CorrectionPlacement { DocumentPath = CorrectionDocumentPath(doc),
                    Page = Convert.ToInt32(doc.ActivePage.Index) };
                int oldUnit = Convert.ToInt32(doc.Unit);
                try
                {
                    doc.Unit = CorelConstants.CdrMillimeter;
                    placement.LeftMm = Convert.ToDouble(selection.LeftX);
                    placement.TopMm = Convert.ToDouble(selection.TopY);
                }
                finally { doc.Unit = oldUnit; }
                _correctionDocument = doc;
                bool vectorCanvas = true;
                if (Convert.ToInt32(selection.Count) == 1) foreach (dynamic shape in selection.Shapes) { vectorCanvas = Convert.ToInt32(shape.Type) != CorelConstants.CdrBitmapShape; break; }
                CaptureRasterInsertion(vectorCanvas);
                var thinSource = ThinElementMask.CaptureSource(vectorCanvas);
                source = AiSelectionCapture.CaptureForCorrection(vectorCanvas);
                SetRasterInsertionInput(source);
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                int.TryParse(_alphaThreshold.Text, out int threshold);
                DtfOutlineUnits.TryNumber(_edgeRadius.Text, out double radius);
                DtfOutlineUnits.TryNumber(_edgeExpansion.Text, out double expansion);
                double sourceDpi;
                using (var stream = File.OpenRead(source))
                    sourceDpi = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).DpiX;
                double factor = _edgeLengthUnit.SelectedIndex == 0 ? 1 : 25.4 / sourceDpi;
                TryUnderlayColor(out var color);
                var initial = new DtfCorrectionOptions { RemoveSemiTransparent = false, Threshold = Math.Max(0, Math.Min(255, threshold)),
                    RadiusMm = radius * factor, ExpansionMm = expansion * factor, Color = color };
                var dialog = new DtfCorrectionWindow(source, initial, _outlineSettings, ProcessCorrection, false,
                    (mask, width, height) => ThinElementMask.Insert(thinSource, mask, width, height),
                    normalizeOnLoad: normalizeOnLoad);
                var owner = PresentationSource.FromVisual(this) as HwndSource;
                if (owner != null) new WindowInteropHelper(dialog).Owner = owner.Handle;
                _timer.Stop(); _progress.Text = "Подбор коррекции в отдельном окне · без интернета.";
                if (dialog.ShowDialog() != true || dialog.Result == null)
                { Report("Коррекция отменена. Исходник сохранён.", false); return; }
                var result = dialog.Result;
                placement.OffsetX = result.OffsetMm.Item1; placement.OffsetY = result.OffsetMm.Item2;
                // Keep the placement with the ready PNG so a failed import can
                // be retried without recapture, recomputation or lost offsets.
                PrepareRasterInsertion(result.OutputPath, placement.OffsetX, placement.OffsetY);
                if (_mode != SimpleAiMode.Raster)
                    File.WriteAllText(result.OutputPath + ".placement.json", new JavaScriptSerializer().Serialize(placement));
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), result.OutputPath);
                _readyOutput = result.OutputPath;
                _resultPreview.Source = PreviewBitmap(_readyOutput);
                _alphaThreshold.Text = dialog.Options.Threshold.ToString(CultureInfo.InvariantCulture);
                _edgeLengthUnit.SelectedIndex = 0;
                _edgeRadius.Text = DtfOutlineUnits.Format(dialog.Options.RadiusMm);
                _edgeExpansion.Text = DtfOutlineUnits.Format(dialog.Options.ExpansionMm);
                _underlayHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                    dialog.Options.Color.R, dialog.Options.Color.G, dialog.Options.Color.B);
                _importWarning = null;
                InsertReady(); ReportOutcome();
            }
            catch (Exception error)
            {
                Log.Error("Live raster correction failed.", error);
                Report(error.GetBaseException().Message, true);
            }
            finally
            {
                if (source != null) try { File.Delete(source); } catch { }
                SetBusy(false, null);
            }
        }

        private static DtfCorrectionResult ProcessCorrection(string source, string output,
            DtfCorrectionOptions options, CancellationToken cancellation)
        {
            if (NativeRasterCorrection.HasSource(source))
                return NativeRasterCorrection.Process(source, output, options, cancellation);
            if(options.Halftone && options.NeedsGeometry)
                return NativeRasterCorrection.ProcessRgb(source,output,options,cancellation);
            if (!options.NeedsGeometry)
            {
                var pixels = DtfCorrectionPixels.Capture(AiPrintTab.Bitmap(source)).Correct(options, cancellation);
                using (var stream = File.Create(output)) pixels.WritePng(stream, cancellation);
                return new DtfCorrectionResult { OutputPath = output };
            }
            var temporary = new List<string>();
            string current = source;
            double offsetX = 0, offsetY = 0;
            string Next()
            {
                string path = Path.Combine(Path.GetTempPath(), "Vanya-Correction-Work-" + Guid.NewGuid().ToString("N") + ".png");
                temporary.Add(path); return path;
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (options.Resample300)
                {
                    var pixels=DtfCorrectionPixels.Capture(AiPrintTab.Bitmap(current)).Resample300(cancellation);
                    string next=Next();using(var stream=File.Create(next))pixels.WritePng(stream,cancellation);current=next;
                }
                if (options.Knockout)
                {
                    var prep = options.Preparation(); prep.Halftone = false;
                    var pixels = DtfCorrectionPixels.Capture(AiPrintTab.Bitmap(current)).Prepare(prep, cancellation);
                    string next = Next(); using (var stream = File.Create(next)) pixels.WritePng(stream, cancellation); current = next;
                }
                if (options.WhitenNearWhite)
                {
                    string next = Next(); DtfNearWhiteFilter.Process(current, next, options.NearWhiteTolerance, cancellation); current = next;
                }
                if (options.RemoveSemiTransparent)
                {
                    string next = Next(); ThresholdAlpha(current, next, options.Threshold, cancellation); current = next;
                }
                if (options.SmoothStairs)
                {
                    var bitmap = AiPrintTab.Bitmap(current);
                    long pixels = (long)bitmap.PixelWidth * bitmap.PixelHeight;
                    int scale = pixels <= 2000000 ? 4 : pixels <= 3500000 ? 3 : 2;
                    if (options.StairsScale > 0) scale = Math.Max(1, Math.Min(4, options.StairsScale));
                    scale = Math.Min(scale, Math.Max(1, (int)Math.Sqrt(32000000.0 / pixels)));
                    RasterCaptureMemory.EnsureAvailable(pixels * scale * scale);
                    string next = Next();
                    SmoothOpaqueStairs(current, next, options.Threshold, cancellation, (crop, dpiX, dpiY) =>
                    { offsetX -= crop.X * 25.4 / dpiX; offsetY -= crop.Y * 25.4 / dpiY; }, 32000000, scale,
                        preserveAlpha: !options.RemoveSemiTransparent);
                    current = next;
                }
                if (options.Halftone)
                {
                    var prep = options.Preparation(-offsetX, -offsetY); prep.Knockout = false;
                    var pixels = DtfCorrectionPixels.Capture(AiPrintTab.Bitmap(current)).Prepare(prep, cancellation);
                    string next = Next(); using (var stream = File.Create(next)) pixels.WritePng(stream, cancellation); current = next;
                }
                if (options.Outline)
                {
                    string next = Next();
                    var edge = SmoothAlphaEdge(current, next, options.RadiusMm,
                        options.ExpansionMm, true, options.Color, cancellation);
                    offsetX += edge.Offset.Item1; offsetY += edge.Offset.Item2; current = next;
                }
                if (options.RemoveSemiTransparent && options.Outline)
                {
                    // The optional final threshold also applies to the new
                    // soft pixels introduced by the outline, not just the source.
                    string next = Next(); ThresholdAlpha(current, next, options.Threshold, cancellation); current = next;
                }
                if (options.WhitenNearWhite && !options.Halftone && (options.SmoothStairs || options.Outline))
                {
                    string next = Next(); DtfNearWhiteFilter.Process(current, next, options.NearWhiteTolerance, cancellation); current = next;
                }
                cancellation.ThrowIfCancellationRequested();
                File.Copy(current, output, true);
                return new DtfCorrectionResult { OutputPath = output, OffsetMm = Tuple.Create(offsetX, offsetY) };
            }
            finally { foreach (string path in temporary) try { File.Delete(path); } catch { } }
        }

        private static string CorrectionDocumentPath(dynamic doc)
        {
            try
            {
                string path = Convert.ToString(doc.FullFileName);
                return !String.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) ? Path.GetFullPath(path) : "";
            }
            catch { return ""; }
        }

        private static CorrectionPlacement ReadCorrectionPlacement(string output)
        {
            string path = output + ".placement.json";
            if (!File.Exists(path)) throw new InvalidOperationException("Не найдены данные размещения коррекции. Готовый PNG: " + output);
            var placement = new JavaScriptSerializer().Deserialize<CorrectionPlacement>(File.ReadAllText(path));
            if (placement == null || placement.Page < 1 || !Finite(placement.LeftMm) || !Finite(placement.TopMm) ||
                !Finite(placement.OffsetX) || !Finite(placement.OffsetY))
                throw new InvalidOperationException("Повреждены данные размещения коррекции. Готовый PNG: " + output);
            return placement;
        }
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);

        private void VerifyCorrectionDocument(CorrectionPlacement placement)
        {
            dynamic doc = CorelApp.Get().ActiveDocument;
            bool same = doc != null && ((_correctionDocument != null &&
                SportProjectPlayers.SameDocument((object)doc, _correctionDocument)) ||
                (!String.IsNullOrWhiteSpace(placement.DocumentPath) && String.Equals(CorrectionDocumentPath(doc),
                    placement.DocumentPath, StringComparison.OrdinalIgnoreCase)));
            if (!same || Convert.ToInt32(doc.ActivePage.Index) != placement.Page)
                throw new InvalidOperationException("Откройте исходный CDR и страницу " + placement.Page +
                    " перед вставкой коррекции. Готовый PNG сохранён; используйте «Повторить вставку»." +
                    (String.IsNullOrWhiteSpace(placement.DocumentPath) ? "" : "\n" + placement.DocumentPath));
        }

        private static string PlaceCorrection(CorrectionPlacement placement, string warning)
        {
            try
            {
                dynamic app = CorelApp.Get(); dynamic doc = app.ActiveDocument; dynamic shape = app.ActiveShape;
                if (shape == null || Convert.ToInt32(shape.Type) != CorelConstants.CdrBitmapShape)
                    throw new InvalidOperationException("Corel не вернул вставленный растр.");
                int oldUnit = Convert.ToInt32(doc.Unit);
                try
                {
                    doc.Unit = CorelConstants.CdrMillimeter;
                    shape.Move(placement.LeftMm + 10 - placement.OffsetX - Convert.ToDouble(shape.LeftX),
                        placement.TopMm + 10 + placement.OffsetY - Convert.ToDouble(shape.TopY));
                }
                finally { doc.Unit = oldUnit; }
            }
            catch (Exception error)
            {
                Log.Error("Correction inserted, but placement failed.", error);
                warning = (warning ?? "Результат вставлен.") + " Проверьте положение: " + error.GetBaseException().Message;
            }
            return warning;
        }
    }
}
