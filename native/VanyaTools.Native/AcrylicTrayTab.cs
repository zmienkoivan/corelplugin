using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class AcrylicTrayTab : UserControl
    {
        private readonly Action<string, bool> _status;
        private readonly ComboBox _mode;
        private readonly TextBox _columns, _rows, _width, _depth, _height, _angle, _rise, _thickness, _clearance;
        private readonly FrameworkElement _angleRow, _riseRow;
        private readonly CheckBox _decorative;
        private readonly Image _side, _plan;
        private readonly AcrylicTrayScene _scene;
        private AcrylicTrayScene _largeScene;
        private Window _previewWindow;
        private readonly CheckBox _samples;
        private readonly TextBlock _summary;
        private readonly Button _create, _assembly, _save, _open;
        private readonly DispatcherTimer _timer;
        private AcrylicTrayPattern _pattern;
        private string _folder;
        private bool _ready, _busy;

        public AcrylicTrayTab(Action<string, bool> status)
        {
            _status = status;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, __) => { _timer.Stop(); Update(); };
            Unloaded += (_, __) => _timer.Stop();
            Loaded += (_, __) => { if (_ready) Update(); };
            var panel = new StackPanel { Margin = new Thickness(6) };
            var root = new DockPanel(); Content = root;
            var preview = new StackPanel { Margin = new Thickness(6, 6, 6, 2) };
            DockPanel.SetDock(preview, Dock.Top); root.Children.Add(preview);
            preview.Children.Add(new TextBlock { Text = "Акриловый лоток · 3D", FontSize = 14,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) });
            _scene = new AcrylicTrayScene { Height = 210 }; preview.Children.Add(_scene);
            var views = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) }; preview.Children.Add(views);
            views.Children.Add(Button("¾", (_, __) => _scene.View(0)));
            views.Children.Add(Button("Спереди", (_, __) => _scene.View(1)));
            views.Children.Add(Button("Сбоку", (_, __) => _scene.View(2)));
            views.Children.Add(Button("Сверху", (_, __) => _scene.View(3)));
            views.Children.Add(Button("Развернуть 3D", (_, __) => Safe(OpenPreview)));
            _samples = new CheckBox { Content = "Образцы 30 × 30 мм", IsChecked = true, FontSize = 11,
                Margin = new Thickness(0, 1, 0, 4) }; preview.Children.Add(_samples);
            _samples.Checked += (_, __) => Samples(); _samples.Unchecked += (_, __) => Samples();
            preview.Children.Add(Note("Мышь — вращение · колесо — масштаб · правая кнопка — перемещение"));
            root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            panel.Children.Add(Note("Боковины стоят на столе. Полоски соединяются встречными пазами и шипами входят в дно."));
            _mode = new ComboBox { FontSize = 11, Margin = new Thickness(0, 2, 0, 6) };
            _mode.Items.Add("Цельное плоское дно · угол 0–60°");
            _mode.Items.Add("Ступени · подъём каждого ряда");
            _mode.SelectedIndex = 0; _mode.SelectionChanged += (_, __) => Changed(); panel.Children.Add(_mode);
            _columns = Number(panel, "Столбцов по ширине", "4");
            _rows = Number(panel, "Рядов в глубину", "3");
            _width = Number(panel, "Ширина ячейки внутри, мм", "40");
            _depth = Number(panel, "Глубина ячейки в плане, мм", "20");
            _height = Number(panel, "Высота борта по вертикали, мм", "15");
            _angle = Number(panel, "Угол дна, ° (0 = горизонтально)", "20", out _angleRow);
            _rise = Number(panel, "Подъём следующего ряда, мм", "15", out _riseRow);
            _thickness = Number(panel, "Толщина акрила, мм", "3");
            _clearance = Number(panel, "Полный зазор пазов, мм", "0,1");
            _decorative = new CheckBox { Content = new TextBlock { Text = "Витринные боковины со скруглениями", TextWrapping = TextWrapping.Wrap }, IsChecked = true,
                FontSize = 11, Margin = new Thickness(0, 3, 0, 6) };
            _decorative.Checked += (_, __) => Changed(); _decorative.Unchecked += (_, __) => Changed();
            panel.Children.Add(_decorative);
            panel.Children.Add(Note("Глубина — горизонтальное расстояние между стенками. На наклоне длина по дну больше. Борт измеряется по вертикали."));
            var diagrams = new StackPanel();
            panel.Children.Add(new Expander { Header = "Боковой разрез и план", Content = diagrams, Margin = new Thickness(0, 4, 0, 6) });
            diagrams.Children.Add(Note("Боковой разрез"));
            _side = new Image { Height = 130, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 5) }; diagrams.Children.Add(_side);
            diagrams.Children.Add(Note("План · голубой — стенки, фиолетовый — дно, зелёный — поперечные полоски"));
            _plan = new Image { Height = 115, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 5) }; diagrams.Children.Add(_plan);
            _summary = Note(""); panel.Children.Add(_summary);
            _create = Button("Контуры резки в CorelDRAW", (_, __) => Open(false)); panel.Children.Add(_create);
            _assembly = Button("Схема сборки в CorelDRAW", (_, __) => Open(true)); panel.Children.Add(_assembly);
            _save = DockerTheme.Primary(Button("Сохранить комплект SVG", async (_, __) => await Save())); panel.Children.Add(_save);
            _open = Button("Открыть папку комплекта", (_, __) => Safe(() =>
                Process.Start(new ProcessStartInfo(_folder) { UseShellExecute = true }))); _open.IsEnabled = false; panel.Children.Add(_open);
            panel.Children.Add(Note("Контуры 1:1, без подписей. Комплект содержит карту деталей, схему сборки и параметры. Компенсацию лазерного реза задайте в программе станка; сначала проверьте посадку на обрезке."));
            _ready = true; Update();
        }

        private AcrylicTrayOptions Options()
        {
            bool plane = _mode.SelectedIndex == 0;
            return new AcrylicTrayOptions { FloorMode = plane ? AcrylicTrayFloorMode.Plane : AcrylicTrayFloorMode.Steps,
                Columns = Count(_columns, "Столбцов"), Rows = Count(_rows, "Рядов"),
                CellWidth = Read(_width, "Ширина ячейки"), CellDepth = Read(_depth, "Глубина ячейки"),
                BoardHeight = Read(_height, "Высота борта"), AngleDegrees = plane ? Read(_angle, "Угол дна") : 0,
                RowRise = plane ? 0 : Read(_rise, "Подъём ряда"), Thickness = Read(_thickness, "Толщина акрила"),
                SlotClearance = Read(_clearance, "Зазор пазов"), DecorativeSides = _decorative.IsChecked == true };
        }
        private void Changed()
        {
            if (!_ready) return;
            // Never export the previous valid geometry while a new value is pending.
            _pattern = null; Actions(false); _timer.Stop(); _timer.Start();
        }
        private void Update()
        {
            if (!_ready) return;
            _angleRow.Visibility = _mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            _riseRow.Visibility = _mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            try
            {
                _pattern = AcrylicTrayPattern.Build(Options()); var p = _pattern;
                _scene.Set(p); Samples();
                _largeScene?.Set(p, _scene.Assembly);
                _side.Source = SidePreview(p); _plan.Source = PlanPreview(p);
                DockerTheme.UseTheme(_summary, TextBlock.ForegroundProperty, DockerTheme.TextBrush);
                _summary.Text = "Габариты: " + F(p.OverallWidth) + " × " + F(p.OverallDepth) + " × " + F(p.Height) + " мм\n" +
                    p.Options.Columns * p.Options.Rows + " ячеек · " + p.Parts.Count + " деталей · дно: " + p.Floors.Count + " шт.\n" +
                    "Поперечный паз в дне: " + F(p.CrossSlotWidth) + " мм";
                Actions(true);
            }
            catch (Exception ex)
            {
                _pattern = null; _side.Source = _plan.Source = null;
                _scene.Clear(); _largeScene?.Clear();
                _summary.Text = ex.GetBaseException().Message;
                DockerTheme.UseTheme(_summary, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush); Actions(false);
            }
        }
        private void Samples()
        {
            _scene.ShowSamples = _samples.IsChecked == true;
            if (_largeScene != null) _largeScene.ShowSamples = _samples.IsChecked == true;
        }
        private void OpenPreview()
        {
            if (_previewWindow != null) { _previewWindow.Activate(); return; }
            if (_pattern == null) return;
            var scene = new AcrylicTrayScene(); scene.Set(_pattern, _scene.Assembly);
            scene.ShowSamples = _samples.IsChecked == true;
            var root = new DockPanel { Margin = new Thickness(10) };
            var bar = new WrapPanel(); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
            string[] names = { "¾", "Спереди", "Сбоку", "Сверху" };
            for (int i = 0; i < names.Length; i++)
            { int view = i; bar.Children.Add(Button(names[i], (_, __) => scene.View(view))); }
            var help = Note("Размеры меняются в панели лотка — модель обновляется сразу. Мышь — вращение, колесо — масштаб.");
            DockPanel.SetDock(help, Dock.Bottom); root.Children.Add(help); root.Children.Add(scene);
            var window = new Window { Title = "Акриловый лоток · собранная конструкция 3D", Content = root,
                Width = Math.Min(1000, SystemParameters.WorkArea.Width - 50), Height = Math.Min(740, SystemParameters.WorkArea.Height - 50),
                MinWidth = 460, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            DockerTheme.Apply(window);
            var owner = PresentationSource.FromVisual(this) as HwndSource;
            if (owner != null) new WindowInteropHelper(window).Owner = owner.Handle;
            _largeScene = scene; _previewWindow = window;
            window.Closed += (_, __) => { _previewWindow = null; _largeScene = null; };
            window.Show();
        }
        private void Actions(bool valid)
        {
            _create.IsEnabled = _assembly.IsEnabled = _save.IsEnabled = valid && !_busy;
        }
        private void Open(bool assembly)
        {
            Safe(() =>
            {
                var p = AcrylicTrayPattern.Build(Options());
                string path = Path.Combine(Path.GetTempPath(), "Vanya-Acrylic-Tray-" + Guid.NewGuid().ToString("N") + ".svg");
                (assembly ? AcrylicTrayOutput.Assembly(p) : AcrylicTrayOutput.Layout(p, false)).Save(path);
                AcrylicTrayOutput.OpenInCorel(path);
                _status(assembly ? "Открыта схема сборки лотка." : "Детали лотка открыты в отдельном документе, 1:1.", false);
            });
        }
        private async Task Save()
        {
            try
            {
                var p = AcrylicTrayPattern.Build(Options());
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Куда сохранить детали акрилового лотка?" })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    string parent = dialog.SelectedPath; _busy = true; Actions(false);
                    _folder = await Task.Run(() => AcrylicTrayOutput.Save(parent, p));
                    _open.IsEnabled = true; _status("Комплект лотка сохранён: " + _folder, false);
                }
            }
            catch (Exception ex) { Error(ex); }
            finally { _busy = false; Update(); }
        }
        private void Safe(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
        private void Error(Exception ex) { Log.Error("Acrylic tray failed.", ex); _status(ex.GetBaseException().Message, true); }

        private static DrawingImage SidePreview(AcrylicTrayPattern p)
        {
            var g = new DrawingGroup();
            using (DrawingContext dc = g.Open())
            {
                dc.DrawGeometry(Brush("#b8ecee"), new Pen(Brushes.SlateGray, 0.35), p.Parts[0].AssemblyGeometry);
                double t = p.Options.Thickness;
                foreach (var f in p.Floors)
                {
                    var poly = new StreamGeometry();
                    using (var c = poly.Open())
                    {
                        c.BeginFigure(new Point(f.StartY, -f.FrontZ), true, true);
                        c.LineTo(new Point(f.EndY, -f.Top(f.EndY)), true, false);
                        c.LineTo(new Point(f.EndY + t * f.Sine, -f.Top(f.EndY) + t * f.Cosine), true, false);
                        c.LineTo(new Point(f.StartY + t * f.Sine, -f.FrontZ + t * f.Cosine), true, false);
                    }
                    dc.DrawGeometry(Brush("#a855db"), null, poly);
                }
                foreach (var cross in p.Crosses)
                    dc.DrawRectangle(Brush("#20c878"), null, new Rect(cross.Y - t / 2,
                        -cross.Bottom - p.Options.BoardHeight, t, p.Options.BoardHeight));
                dc.DrawLine(new Pen(Brushes.Gray, 0.4), new Point(p.Floors[0].StartY, 0), new Point(p.Floors[p.Floors.Count - 1].EndY, 0));
            }
            g.Freeze(); return new DrawingImage(g);
        }
        private static DrawingImage PlanPreview(AcrylicTrayPattern p)
        {
            var g = new DrawingGroup();
            using (DrawingContext dc = g.Open())
            {
                double t = p.Options.Thickness, start = p.Floors[0].StartY;
                dc.DrawRectangle(Brush("#b8ecee"), null, new Rect(-t, start, p.OverallWidth, p.Depth));
                foreach (var f in p.Floors)
                    dc.DrawRectangle(Brush("#f0e6fa"), null, new Rect(0, f.StartY, p.InnerWidth, f.EndY - f.StartY));
                foreach (double x in p.Longitudinals)
                    dc.DrawRectangle(Brush("#8dcbd2"), null, new Rect(x - t / 2, start, t, p.Depth));
                foreach (var cross in p.Crosses)
                    dc.DrawRectangle(Brush("#20c878"), null, new Rect(0, cross.Y - t / 2, p.InnerWidth, t));
            }
            g.Freeze(); return new DrawingImage(g);
        }
        private static SolidColorBrush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        private static double Read(TextBox box, string title)
        {
            double n;
            if (!Double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out n)
                || Double.IsNaN(n) || Double.IsInfinity(n)) throw new InvalidOperationException(title + ": введите число.");
            return n;
        }
        private static int Count(TextBox box, string title)
        {
            double n = Read(box, title);
            if (n < 1 || n > 20 || n != Math.Truncate(n)) throw new InvalidOperationException(title + ": целое число от 1 до 20.");
            return (int)n;
        }
        private static string F(double n) { return n.ToString("0.##", CultureInfo.CurrentCulture); }
        private TextBox Number(Panel panel, string title, string value)
        { FrameworkElement row; return Number(panel, title, value, out row); }
        private TextBox Number(Panel panel, string title, string value, out FrameworkElement element)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            var label = Note(title); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
            var field = new TextBox { Text = value, FontSize = 11, Padding = new Thickness(3) };
            Grid.SetColumn(field, 1); row.Children.Add(field); panel.Children.Add(row);
            field.TextChanged += (_, __) => Changed(); element = row; return field;
        }
        private static TextBlock Note(string text)
        {
            var note = new TextBlock { Text = text, FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 3, 4) };
            DockerTheme.UseTheme(note, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush); return note;
        }
        private static Button Button(string title, RoutedEventHandler click)
        {
            var button = new Button { Content = title, FontSize = 11, Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(5) };
            button.Click += click; return button;
        }
    }

}
