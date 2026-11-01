using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class ThinElementWindow : Window
    {
        private readonly BitmapSource _source;
        private readonly RasterInsertionSource _placement;
        private readonly Image _overlay = new Image { Stretch = Stretch.Fill };
        private readonly Grid _picture = new Grid();
        private readonly TextBlock _message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        private readonly ComboBox _preset = new ComboBox { ItemsSource = new[] { "ДТФ", "УФ-ДТФ" }, SelectedIndex = 0, MinHeight = 28 };
        private readonly Slider _minimum, _safe, _dot, _alpha;
        private readonly CheckBox _white = new CheckBox { Content = "Белый считать фоном", FontSize = 11, Margin = new Thickness(0, 5, 0, 5) };
        private readonly Button _check = new Button { Content = "Проверить выделение", MinHeight = 30 };
        private readonly Button _insert = new Button { Content = "Маску на холст · не печатать", MinHeight = 30, IsEnabled = false };
        private CancellationTokenSource _cancellation;
        private ThinAuditResult _result;
        private bool _closed, _sync;
        private int _version;

        internal static Button Launcher(FrameworkElement owner, Action<string, bool> status)
        {
            var button = new Button { Content = "Тонкие элементы · ДТФ / УФ…", FontSize = 11, MinHeight = 28, Margin = new Thickness(0, 3, 0, 2),
                ToolTip = "Цветная маска тонких участков и векторная линейка 0,1–1 мм. Работает без интернета." };
            button.Click += async (_, __) => {
                button.IsEnabled = false; string path = null;
                try
                {
                    BitmapSource bitmap = null; RasterInsertionSource placement = null;
                    dynamic app = CorelApp.Get();
                    if (app.ActiveDocument == null) throw new InvalidOperationException("Сначала откройте документ.");
                    if (Convert.ToInt32(app.ActiveSelectionRange.Count) > 0)
                    {
                        status("Подготавливаю выделение для проверки толщины…", false); await Task.Yield();
                        placement = CaptureSource();
                        long estimate = checked((long)Math.Ceiling(placement.WidthMm * 400 / 25.4) * (long)Math.Ceiling(placement.HeightMm * 400 / 25.4));
                        RasterCaptureMemory.EnsureAvailable(2 * Math.Min(estimate, ThinElementAudit.MaxPixels));
                        path = AiSelectionCapture.CaptureForThinAudit(ThinElementAudit.MaxPixels);
                        bitmap = AiPrintTab.Bitmap(path); bitmap.Freeze();
                    }
                    var window = new ThinElementWindow(bitmap, placement);
                    if (PresentationSource.FromVisual(owner) is HwndSource host) new WindowInteropHelper(window).Owner = host.Handle;
                    window.ShowDialog();
                    status("Проверка тонких элементов закрыта.", false);
                }
                catch (Exception error) { Log.Error("Thin element audit failed.", error); status(error.GetBaseException().Message, true); }
                finally { button.IsEnabled = true; if (path != null) try { File.Delete(path); } catch { } }
            };
            return button;
        }
        private static RasterInsertionSource CaptureSource()
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument, range = app.ActiveSelectionRange;
            int unit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double left, bottom, width, height;
                range.GetBoundingBox(out left, out bottom, out width, out height, true);
                if (!(width > 0) || !(height > 0) || Double.IsInfinity(width + height))
                    throw new InvalidOperationException("Не удалось определить границы выделения с обводками.");
                var source = new RasterInsertionSource { Document = (object)doc, NativePage = (object)doc.ActivePage,
                    LeftMm = left, TopMm = bottom + height, WidthMm = width, HeightMm = height, NativeShapes = new System.Collections.Generic.List<object>() };
                foreach (dynamic shape in range.Shapes)
                {
                    shape.GetBoundingBox(out left, out bottom, out width, out height, true);
                    source.NativeShapes.Add((object)shape); source.Shapes.Add(new RasterInsertionShape {
                        LeftMm = left, TopMm = bottom + height, WidthMm = width, HeightMm = height });
                }
                return source;
            }
            finally { doc.Unit = unit; }
        }
        private ThinElementWindow(BitmapSource source, RasterInsertionSource placement)
        {
            _source = source; _placement = placement;
            Title = "Тонкие элементы · ДТФ / УФ-ДТФ";
            Width = Math.Min(1080, SystemParameters.WorkArea.Width - 40); Height = Math.Min(760, SystemParameters.WorkArea.Height - 40);
            MinWidth = 760; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; DockerTheme.Apply(this);
            var root = new Grid { Margin = new Thickness(12) }; Content = root;
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) }); root.ColumnDefinitions.Add(new ColumnDefinition());
            var controls = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            root.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            controls.Children.Add(new TextBlock { Text = "Экспресс-аудит", FontSize = 17, FontWeight = FontWeights.SemiBold }); controls.Children.Add(_preset);
            _minimum = Field(controls, "Красный: тоньше, мм", .1, 1.5, .6, .05);
            _safe = Field(controls, "Жёлтый: до, мм", .1, 2, .8, .05);
            _dot = Field(controls, "Отдельная точка: минимум, мм", .1, 2, 1, .05);
            _alpha = Field(controls, "Порог непрозрачности", 1, 255, 128, 1);
            controls.Children.Add(_white);
            controls.Children.Add(new TextBlock { Text = "Красный — ниже минимума; жёлтый — между порогами; зелёный — около безопасного. Широкие участки не отмечаются.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
            controls.Children.Add(_check); controls.Children.Add(_insert);
            var reference = new Button { Content = "Вставить векторную линейку", MinHeight = 30, Margin = new Thickness(0, 8, 0, 4) };
            controls.Children.Add(reference);
            controls.Children.Add(new TextBlock { Text = "Круги, полоски 0,1–1 мм, 45° и просветы. Можно наложить на подозрительный участок или распечатать 1:1.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
            controls.Children.Add(_message);
            var view = new DockPanel(); Grid.SetColumn(view, 1); root.Children.Add(view);
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(toolbar, Dock.Top); view.Children.Add(toolbar);
            var show = new CheckBox { Content = "Подсветка", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            show.Checked += (_, __) => _overlay.Visibility = Visibility.Visible; show.Unchecked += (_, __) => _overlay.Visibility = Visibility.Hidden;
            toolbar.Children.Add(show);
            var zoom = new Slider { Minimum = 1, Maximum = 300, Value = 50, Width = 140, Margin = new Thickness(12, 0, 8, 0) };
            toolbar.Children.Add(zoom);
            var zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; toolbar.Children.Add(zoomText);
            var fit = new Button { Content = "Вписать", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0) }; toolbar.Children.Add(fit);
            var full = new Button { Content = "1:1", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4, 0, 0, 0) }; toolbar.Children.Add(full);
            var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(72, 72, 72)), Content = _picture };
            view.Children.Add(scroll);
            _picture.HorizontalAlignment = HorizontalAlignment.Left; _picture.VerticalAlignment = VerticalAlignment.Top;
            if (source != null) _picture.Children.Add(new Image { Source = source, Stretch = Stretch.Fill });
            _picture.Children.Add(_overlay);
            Action scale = () => { if (source == null) return; _picture.Width = source.PixelWidth * zoom.Value / 100; _picture.Height = source.PixelHeight * zoom.Value / 100; zoomText.Text = Math.Round(zoom.Value) + "%"; };
            zoom.ValueChanged += (_, __) => scale(); full.Click += (_, __) => zoom.Value = 100;
            Action fitted = () => { if (source != null && scroll.ActualWidth > 0) zoom.Value = Math.Max(1, Math.Min(300,
                Math.Min((scroll.ActualWidth - 24) / source.PixelWidth, (scroll.ActualHeight - 24) / source.PixelHeight) * 100)); scale(); };
            fit.Click += (_, __) => fitted();
            scroll.PreviewMouseWheel += (_, e) => { if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { zoom.Value *= e.Delta > 0 ? 1.15 : 1 / 1.15; e.Handled = true; } };
            _preset.SelectionChanged += (_, __) => { _sync = true; var options = ThinAuditOptions.Preset(_preset.SelectedIndex == 1);
                _minimum.Value = options.MinimumMm; _safe.Value = options.SafeMm; _dot.Value = options.DotMm; _sync = false; Invalidate(); };
            foreach (var field in new[] { _minimum, _safe, _dot, _alpha }) field.ValueChanged += (_, __) => Invalidate();
            _white.Checked += (_, __) => Invalidate(); _white.Unchecked += (_, __) => Invalidate();
            _check.IsEnabled = source != null; _check.Click += async (_, __) => await Analyze();
            reference.Click += (_, __) => { try { ThinElementReference.Insert(_preset.SelectedIndex == 1, Options()); _message.Text = "Векторная линейка вставлена. Её можно переместить на макет."; }
                catch (Exception error) { _message.Text = error.GetBaseException().Message; } };
            _insert.Click += (_, __) => { try { InsertMask(); _message.Text = "Маска добавлена на отдельный непечатаемый слой."; }
                catch (Exception error) { _message.Text = error.GetBaseException().Message; } };
            Loaded += async (_, __) => { fitted(); if (source != null) await Analyze(); else _message.Text = "Без выделения доступна векторная линейка. Для подсветки выделите принт и откройте инструмент заново."; };
            Closed += (_, __) => { _closed = true; _cancellation?.Cancel(); };
        }
        private static Slider Field(Panel panel, string label, double min, double max, double value, double tick)
        {
            var row = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
            var number = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right }; DockPanel.SetDock(number, Dock.Right); row.Children.Add(number);
            row.Children.Add(new TextBlock { Text = label, FontSize = 11 }); panel.Children.Add(row);
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = tick, IsSnapToTickEnabled = true };
            Action update = () => number.Text = slider.Value.ToString("0.##"); update(); slider.ValueChanged += (_, __) => update(); panel.Children.Add(slider); return slider;
        }
        private ThinAuditOptions Options() => new ThinAuditOptions { MinimumMm = _minimum.Value, SafeMm = _safe.Value, DotMm = _dot.Value, Alpha = (int)_alpha.Value, IgnoreWhite = _white.IsChecked == true };
        private void Invalidate()
        { if (_sync) return; _version++; _cancellation?.Cancel(); _result = null; _overlay.Source = null; _insert.IsEnabled = false; _check.IsEnabled = _source != null; _message.Text = "Параметры изменены — нажмите «Проверить выделение»."; }
        private async Task Analyze()
        {
            if (_source == null || _closed) return;
            int version = ++_version; _cancellation?.Cancel(); var cancel = new CancellationTokenSource(); _cancellation = cancel;
            try
            {
                var options = Options(); options.Validate(); _check.IsEnabled = false; _insert.IsEnabled = false; _result = null; _overlay.Source = null;
                _message.Text = "Проверяю толщину… Закрытие окна отменит расчёт.";
                int w = _source.PixelWidth, h = _source.PixelHeight;
                var source = new FormatConvertedBitmap(_source, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[checked(w * h * 4)]; source.CopyPixels(pixels, w * 4, 0);
                var result = await Task.Run(() => ThinElementAudit.Analyze(pixels, w, h, _placement.WidthMm, _placement.HeightMm, options, cancel.Token));
                if (_closed || version != _version) return;
                _result = result;
                var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, result.Overlay, w * 4); bitmap.Freeze(); _overlay.Source = bitmap;
                _insert.IsEnabled = true;
                _message.Text = String.Format("{0} × {1} px · шаг {2:0.###} мм. Малых отдельных точек: {3}.\nПриблизительная оценка, погрешность около 1–2 пикселей. Углы, шрифты и полупрозрачные края проверяйте линейкой. Белый внутри непрозрачной картинки тоже считается изображением, пока не включён «Белый считать фоном».", w, h, result.PixelMm, result.SmallDots);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_closed && version == _version) _message.Text = error.GetBaseException().Message; }
            finally { if (!_closed && version == _version) _check.IsEnabled = true; }
        }
        private void InsertMask()
        {
            if (_result == null || _placement == null) return;
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (!Same((object)doc, _placement.Document) || !Same((object)doc.ActivePage, _placement.NativePage))
                throw new InvalidOperationException("Вернитесь на исходную страницу и повторите проверку.");
            for (int i = 0; i < _placement.NativeShapes.Count; i++)
            {
                dynamic shape = _placement.NativeShapes[i]; var before = _placement.Shapes[i];
                int old = Convert.ToInt32(doc.Unit);
                try { doc.Unit = CorelConstants.CdrMillimeter;
                    double left, bottom, width, height; shape.GetBoundingBox(out left, out bottom, out width, out height, true);
                    if (Math.Abs(left - before.LeftMm) > .02 || Math.Abs(bottom + height - before.TopMm) > .02 ||
                        Math.Abs(width - before.WidthMm) > .02 || Math.Abs(height - before.HeightMm) > .02)
                        throw new InvalidOperationException("Исходник перемещён или изменён. Откройте проверку заново."); }
                finally { doc.Unit = old; }
            }
            string path = Path.Combine(Path.GetTempPath(), "Vanya-Thin-Mask-" + Guid.NewGuid().ToString("N") + ".png");
            object layer = null, oldLayer = (object)doc.ActiveLayer; int unit = Convert.ToInt32(doc.Unit); bool started = false;
            try
            {
                using (var stream = File.Create(path)) FastRasterPng.Write(stream, _result.Overlay, _source.PixelWidth, _source.PixelHeight, 400, 400, CancellationToken.None);
                doc.Unit = CorelConstants.CdrMillimeter; doc.BeginCommandGroup("Vanya Tools · маска тонких элементов"); started = true;
                layer = (object)doc.ActivePage.CreateLayer("Vanya · аудит тонких элементов · НЕ ПЕЧАТАТЬ");
                dynamic target = layer; target.Printable = false; target.Visible = true; target.Editable = true; target.Activate();
                object importOptions = (object)app.CreateStructImportOptions();
                dynamic filter = layer.GetType().InvokeMember("ImportEx", System.Reflection.BindingFlags.InvokeMethod, null, layer,
                    new object[] { new System.Runtime.InteropServices.BStrWrapper(Path.GetFullPath(path)), 0, importOptions });
                filter.Finish(); dynamic bitmap = null;
                if (Convert.ToInt32(target.Shapes.Count) != 1) throw new InvalidOperationException("Corel не вернул один растр маски.");
                foreach (dynamic imported in target.Shapes) { bitmap = imported; break; }
                if (bitmap == null || Convert.ToInt32(bitmap.Type) != CorelConstants.CdrBitmapShape) throw new InvalidOperationException("Маска не импортирована.");
                bitmap.SetSize(_placement.WidthMm, _placement.HeightMm);
                bitmap.Move(_placement.LeftMm - Convert.ToDouble(bitmap.LeftX), _placement.TopMm - Convert.ToDouble(bitmap.TopY));
                bitmap.Name = "Диагностическая маска · удалить перед печатью";
                // A locked diagnostic layer stays out of ordinary selection and
                // cannot become the next print job merely because import selected it.
                target.Editable = false;
                for (int i = 0; i < _placement.NativeShapes.Count; i++)
                    if (i == 0) ((dynamic)_placement.NativeShapes[i]).CreateSelection();
                    else ((dynamic)_placement.NativeShapes[i]).AddToSelection();
            }
            catch { if (layer != null) try { ((dynamic)layer).Delete(); } catch { } throw; }
            finally { try { if (started) doc.EndCommandGroup(); } finally { doc.Unit = unit; ((dynamic)oldLayer).Activate(); try { File.Delete(path); } catch { } } }
        }
        private static bool Same(object a, object b)
        {
            if (a == null || b == null) return false;
            IntPtr x = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(a), y = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(b);
            try { return x == y; } finally { System.Runtime.InteropServices.Marshal.Release(x); System.Runtime.InteropServices.Marshal.Release(y); }
        }
    }
}
