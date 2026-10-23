using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class SportNestingWindow : Window
    {
        private readonly IList<SportSheetNesting.Item> _items;
        private readonly IList<string> _labels;
        private readonly bool _cutting;
        private readonly TextBox _width, _length, _gap, _left, _right, _top, _bottom, _pressWidth;
        private readonly ComboBox _density, _grouping, _search, _press, _zoom, _pressGrouping, _orientation;
        private readonly Panel _groupingFields;
        private readonly CheckBox _rotate, _borders;
        private readonly StackPanel _pressFields;
        private readonly ListBox _history;
        private readonly TextBlock _message, _metrics, _zone, _pressWidthTitle;
        private readonly Button _apply, _stop;
        private readonly Image _image;
        private readonly ScrollViewer _viewport;
        private readonly DispatcherTimer _debounce;
        private CancellationTokenSource _cancellation;
        private bool _loading = true, _closed;
        private int _revision;
        private double _drawingWidth = 1, _drawingHeight = 1;
        private SportNestingChoice _display;
        private IList<double> _pageOffsets;
        internal SportNestingChoice Choice { get; private set; }

        internal SportNestingWindow(IList<SportSheetNesting.Item> items, IList<string> labels,
            SportLayoutOptions options, bool cutting)
        {
            _items = items; _labels = labels; _cutting = cutting;
            Title = "Vanya Tools — подбор раскладки " + (cutting ? "резки" : "печати");
            Width = Math.Min(1280, SystemParameters.WorkArea.Width - 30);
            Height = Math.Min(880, SystemParameters.WorkArea.Height - 30);
            MinWidth = 850; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var root = new DockPanel { Margin = new Thickness(10) }; Content = root;
            var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            _apply = new Button { Content = "Создать выбранный вариант в Corel", IsEnabled = false,
                MinHeight = 36, Padding = new Thickness(12, 4, 12, 4) };
            _apply.Click += (_, __) => { if (Choice != null) DialogResult = true; };
            DockPanel.SetDock(_apply, Dock.Right); footer.Children.Add(_apply);
            var close = new Button { Content = "Закрыть", Margin = new Thickness(0, 0, 8, 0), MinWidth = 85 };
            close.Click += (_, __) => Close(); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
            _message = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0) }; footer.Children.Add(_message);
            var settings = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            var settingsScroll = new ScrollViewer { Content = settings, Width = 265,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            DockPanel.SetDock(settingsScroll, Dock.Left); root.Children.Add(settingsScroll);
            settings.Children.Add(new TextBlock { Text = "Параметры варианта", FontSize = 16, FontWeight = FontWeights.SemiBold });
            DockerHelp.Add(settings, "Детали подготовлены один раз. Настройки меняют только расчёт и предпросмотр. " +
                "Страницы создаются кнопкой выбранного варианта. В списке сохраняются до 10 расчётов этой сессии. " +
                "Быстрый поиск не выполняет дополнительный перебор; уплотнение добавляет поиск перестановок. " +
                "Для листового пресса задайте поля с каждой стороны. Листы уменьшаются по заполнению, " +
                "располагаются прямоугольными блоками на рулоне и не превышают полезную зону стола. " +
                "По образцам: малые корпуса — отдельная широкая пара, спинка слева и перед справа. " +
                "Крупные корпуса стоят горловиной вверх на узких высоких листах. Сначала размещаются все листы корпусов, " +
                "затем свободные полосы заполняются стопками по 2–4 рукава одним столбцом. Левые и правые рукава " +
                "собираются раздельно, по порядку комплектов; разные размеры в стопке допустимы. " +
                "Если разрешено, рукава могут поворачиваться на 90°. Корпуса при этом остаются на своих местах. " +
                "Остаток рукавов и горловины размещаются после корпусов; одиночный остаток не теряется. " +
                "Ширина 750 мм ограничивает вертикальные листы; малые пары используют ширину плиты. " +
                "Расположение по образцу имеет приоритет; для оставшихся деталей сохраняется свободный расчёт. " +
                "В свободном режиме ширина ограничивает все листы. Размеры включают поля; 0 означает доступную ширину. " +
                "Серые границы в окне показывают место разрезания; их печать включается отдельно.");
            _width = Field(settings, "Ширина рулона, мм");
            _length = Field(settings, "Макс. длина страницы, м");
            _gap = Field(settings, "Между деталями, мм");
            _density = ChoiceBox(settings, "Геометрия", "Прямоугольники", "Упрощённые фигуры", "Полный контур");
            _groupingFields = new StackPanel(); settings.Children.Add(_groupingFields);
            _grouping = ChoiceBox(_groupingFields, "Держать рядом", "Свободно", "Перед и спина парами", "Все детали комплектом");
            _rotate = new CheckBox { Content = cutting ? "Разрешить 180°" : "Поворачивать рукава и горловину на 90°",
                Margin = new Thickness(0, 5, 0, 8) }; settings.Children.Add(_rotate);
            _search = ChoiceBox(settings, "Поиск", "Быстрый", "С дополнительным уплотнением");
            var pressSection = new StackPanel(); settings.Children.Add(pressSection);
            _press = ChoiceBox(pressSection, "Способ переноса", "Каландр · рулон", "Пресс 120 × 80 см", "Пресс 120 × 100 см");
            if (cutting) pressSection.Visibility = Visibility.Collapsed;
            _pressFields = new StackPanel(); settings.Children.Add(_pressFields);
            _pressGrouping = ChoiceBox(_pressFields, "Собирать листы", "По образцам · корпуса и стопки рукавов",
                "По образцам · остальные детали парами", "Свободно по размерам");
            _orientation = ChoiceBox(_pressFields, "Ориентация листа", "По схеме / автоматически", "Вертикально", "Горизонтально");
            _pressWidthTitle = new TextBlock { Text = "Макс. ширина вертикального листа, мм · 0 = авто",
                Margin = new Thickness(0, 5, 0, 2) };
            _pressFields.Children.Add(_pressWidthTitle);
            _pressWidth = new TextBox { MinHeight = 27 }; _pressFields.Children.Add(_pressWidth);
            _left = Field(_pressFields, "Поле слева, мм"); _right = Field(_pressFields, "Поле справа, мм");
            _top = Field(_pressFields, "Поле сверху, мм"); _bottom = Field(_pressFields, "Поле снизу, мм");
            _zone = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 5) };
            _pressFields.Children.Add(_zone);
            _borders = new CheckBox { Content = "Печатать границы отрезных листов", Margin = new Thickness(0, 0, 0, 8) };
            _pressFields.Children.Add(_borders);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var calculate = new Button { Content = "Рассчитать", MinHeight = 30, Padding = new Thickness(10, 2, 10, 2) };
            calculate.Click += (_, __) => { _debounce.Stop(); Compute(); };
            _stop = new Button { Content = "Стоп", IsEnabled = false, Margin = new Thickness(5, 0, 0, 0) };
            _stop.Click += (_, __) => { _debounce.Stop(); _revision++; _cancellation?.Cancel(); _stop.IsEnabled = false;
                _message.Text = "Расчёт остановлен. Можно выбрать готовый вариант или изменить настройки."; };
            actions.Children.Add(calculate); actions.Children.Add(_stop); settings.Children.Add(actions);
            settings.Children.Add(new TextBlock { Text = "Варианты", FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 5) });
            _history = new ListBox { Height = 160, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var variantText = new FrameworkElementFactory(typeof(TextBlock));
            variantText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            variantText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            variantText.SetValue(TextBlock.FontSizeProperty, 11.0);
            _history.ItemTemplate = new DataTemplate { VisualTree = variantText };
            ScrollViewer.SetHorizontalScrollBarVisibility(_history, ScrollBarVisibility.Disabled);
            settings.Children.Add(_history);
            _history.SelectionChanged += (_, __) => SelectVariant();
            var view = new DockPanel(); root.Children.Add(view);
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(bar, Dock.Top); view.Children.Add(bar);
            _zoom = new ComboBox { ItemsSource = new[] { "50%", "По ширине", "200%", "300%" },
                SelectedIndex = 1, Width = 110, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(_zoom, Dock.Right); bar.Children.Add(_zoom);
            _metrics = new TextBlock { TextWrapping = TextWrapping.Wrap }; bar.Children.Add(_metrics);
            _image = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
            _image.MouseMove += (_, e) =>
            {
                if (_display == null || _image.ActualWidth <= 0 || _image.ActualHeight <= 0) return;
                Point point = e.GetPosition(_image);
                double x = point.X / _image.ActualWidth * _drawingWidth - 20,
                    y = point.Y / _image.ActualHeight * _drawingHeight;
                var hit = _display.Layout.Placements.FirstOrDefault(p => x >= p.Left && x <= p.Left + p.Width &&
                    y >= _pageOffsets[p.Page] + p.Top && y <= _pageOffsets[p.Page] + p.Top + p.Height);
                _image.ToolTip = hit == null ? "П — перед, С — спина, ЛР / ПР — рукава, Г — горловина; № — комплект." :
                    _labels[hit.Index] + " · " + hit.Width.ToString("0.#") + " × " + hit.Height.ToString("0.#") + " мм";
            };
            _viewport = new ScrollViewer { Content = _image, Background = Brushes.DimGray,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            view.Children.Add(_viewport);
            _viewport.SizeChanged += (_, __) => ResizeImage(); _zoom.SelectionChanged += (_, __) => ResizeImage();
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _debounce.Tick += (_, __) => { _debounce.Stop(); Compute(); };
            foreach (var box in new[] { _width, _length, _gap, _left, _right, _top, _bottom, _pressWidth })
                box.TextChanged += (_, __) => Changed();
            foreach (var combo in new[] { _density, _grouping, _search, _press, _pressGrouping, _orientation })
                combo.SelectionChanged += (_, __) => Changed();
            _rotate.Click += (_, __) => Changed(); _borders.Click += (_, __) => Changed();
            LoadSettings(SportNestingSettings.From(options, cutting));
            _search.SelectedIndex = 0;
            _loading = false;
            Loaded += (_, __) => Compute();
            Closing += (_, __) => { _closed = true; _revision++; _debounce.Stop(); _cancellation?.Cancel(); };
            DockerTheme.Apply(root);
        }

        private static TextBox Field(Panel panel, string title)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 5, 0, 2) });
            var box = new TextBox { MinHeight = 27 }; panel.Children.Add(box); return box;
        }
        private static ComboBox ChoiceBox(Panel panel, string title, params string[] choices)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 6, 0, 2) });
            var combo = new ComboBox { ItemsSource = choices, MinHeight = 27, SelectedIndex = 0 };
            panel.Children.Add(combo); return combo;
        }
        private static double Read(TextBox box, double min, double max, string name)
        {
            if (!Double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
                out double value) || Double.IsNaN(value) || Double.IsInfinity(value) || value < min || value > max)
                throw new InvalidOperationException(name + ": от " + min + " до " + max + ".");
            return value;
        }
        private SportNestingSettings ReadSettings() => new SportNestingSettings {
            Width = Read(_width, 300, 4000, "Ширина"), Length = Read(_length, .5, 45, "Длина") * 1000,
            Gap = Read(_gap, 0, 100, "Зазор"), Density = (SportNestingMode)_density.SelectedIndex,
            Grouping = (SportGroupingMode)_grouping.SelectedIndex, Rotate = _rotate.IsChecked == true,
            Improve = _search.SelectedIndex == 1, Press = _cutting ? 0 : _press.SelectedIndex,
            PressGrouping = _pressGrouping.SelectedIndex, PressOrientation = _pressGrouping.SelectedIndex == 2 ? _orientation.SelectedIndex : 0,
            PressSheetWidth = Read(_pressWidth, 0, 1200, "Ширина листа"),
            Left = Read(_left, 0, 200, "Левое поле"), Right = Read(_right, 0, 200, "Правое поле"),
            Top = Read(_top, 0, 200, "Верхнее поле"), Bottom = Read(_bottom, 0, 200, "Нижнее поле"),
            Borders = _borders.IsChecked == true };
        private void LoadSettings(SportNestingSettings value)
        {
            _loading = true;
            _width.Text = value.Width.ToString("0.###"); _length.Text = (value.Length / 1000).ToString("0.###");
            _gap.Text = value.Gap.ToString("0.###"); _density.SelectedIndex = (int)value.Density;
            _grouping.SelectedIndex = (int)value.Grouping; _rotate.IsChecked = value.Rotate;
            _search.SelectedIndex = value.Improve ? 1 : 0; _press.SelectedIndex = value.Press;
            _pressGrouping.SelectedIndex = value.PressGrouping; _orientation.SelectedIndex = value.PressOrientation;
            _pressWidth.Text = value.PressSheetWidth.ToString("0.###");
            _left.Text = value.Left.ToString("0.###"); _right.Text = value.Right.ToString("0.###");
            _top.Text = value.Top.ToString("0.###"); _bottom.Text = value.Bottom.ToString("0.###");
            _borders.IsChecked = value.Borders;
            _pressFields.Visibility = value.Press == 0 ? Visibility.Collapsed : Visibility.Visible;
            _groupingFields.Visibility = value.Press == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePressControls();
            _loading = false; UpdateZone(value);
        }
        private void UpdateZone(SportNestingSettings s)
        {
            if (s.Press == 0) return;
            double shortSide = s.Press == 1 ? 800 : 1000;
            if (s.PressGrouping != 2)
            {
                double smallWidth = Math.Min(s.Width, 1200) - s.Left - s.Right;
                double cap = s.PressSheetWidth > 0 ? s.PressSheetWidth : shortSide;
                double largeWidth = Math.Min(s.Width, Math.Min(cap, shortSide)) - s.Left - s.Right;
                _zone.Text = "Пары: до " + smallWidth.ToString("0.#") + " × " +
                    (Math.Min(s.Length, shortSide) - s.Top - s.Bottom).ToString("0.#") +
                    " мм. Вертикальные листы: до " + largeWidth.ToString("0.#") + " × " +
                    (Math.Min(s.Length, 1200) - s.Top - s.Bottom).ToString("0.#") + " мм внутри полей.";
                return;
            }
            double pressWidth = s.PressOrientation == 1 ? shortSide : 1200;
            double pressHeight = s.PressOrientation == 1 ? 1200 : shortSide;
            double sheetWidth = Math.Min(s.Width, pressWidth);
            if (s.PressSheetWidth > 0) sheetWidth = Math.Min(sheetWidth, s.PressSheetWidth);
            _zone.Text = (s.PressOrientation == 0 ? "Автовыбор ориентации. " : "") +
                "Внутри полей: до " + (sheetWidth - s.Left - s.Right).ToString("0.#") + " × " +
                (Math.Min(s.Length, pressHeight) - s.Top - s.Bottom).ToString("0.#") +
                " мм" + (s.PressOrientation == 0 ? " в горизонтальном варианте" : "") +
                ". Листы уменьшаются по заполнению; разные размеры не смешиваются.";
        }
        private void Changed()
        {
            if (_loading || _closed) return;
            UpdatePressControls();
            _revision++; _cancellation?.Cancel(); Choice = null; _apply.IsEnabled = false;
            _history.SelectedItem = null;
            _pressFields.Visibility = !_cutting && _press.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;
            _groupingFields.Visibility = _pressFields.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _message.Text = "Параметры изменены. Рассчитываю новый вариант…";
            _debounce.Stop(); _debounce.Start();
        }
        private void UpdatePressControls()
        {
            bool templates = _pressGrouping.SelectedIndex != 2;
            _orientation.IsEnabled = !templates;
            if (templates && _orientation.SelectedIndex != 0)
            {
                bool loading = _loading; _loading = true;
                _orientation.SelectedIndex = 0; _loading = loading;
            }
            _pressWidthTitle.Text = templates ? "Макс. ширина вертикального листа, мм · 0 = авто" :
                "Макс. ширина листа, мм · 0 = авто";
        }
        private async void Compute()
        {
            if (_closed) return;
            int revision = ++_revision;
            _cancellation?.Cancel();
            var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
            Choice = null; _apply.IsEnabled = false; _stop.IsEnabled = true;
            _history.SelectedItem = null;
            _message.Text = "Расчёт в памяти…";
            try
            {
                var settings = ReadSettings(); UpdateZone(settings);
                var clock = Stopwatch.StartNew();
                var choice = await Task.Run(() => SportNestingCancellation.Run(cancellation.Token,
                    () => SportNestingPreview.Calculate(_items, settings)));
                if (_closed || revision != _revision) return;
                SportNestingPreview.Validate(choice, _items.Count);
                choice.Seconds = clock.Elapsed.TotalSeconds;
                _history.Items.Insert(0, choice);
                if (_history.Items.Count > 10) _history.Items.RemoveAt(10);
                _history.SelectedItem = choice;
                _message.Text = "Готово за " + choice.Seconds.ToString("0.0") + " с. Страницы ещё не созданы.";
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!_closed && revision == _revision) { _message.Text = error.GetBaseException().Message;
                    Log.Error("Sport nesting preview failed", error); }
            }
            finally
            {
                if (!_closed && ReferenceEquals(_cancellation, cancellation)) _stop.IsEnabled = false;
                cancellation.Dispose();
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            }
        }
        private void SelectVariant()
        {
            if (!(_history.SelectedItem is SportNestingChoice choice)) return;
            _revision++; _debounce.Stop(); _cancellation?.Cancel();
            Choice = choice; LoadSettings(choice.Settings); _apply.IsEnabled = true;
            _stop.IsEnabled = false; _message.Text = "Выбран готовый вариант. Страницы ещё не созданы.";
            _metrics.Text = choice.Layout.PageHeights.Count + " страниц · " + _items.Count + " деталей · " +
                (choice.Layout.PageHeights.Sum() / 1000).ToString("0.###") + " м" +
                (choice.Panels.Count == 0 ? "" : " · " + choice.Panels.Count + " отрезных листов") +
                (choice.PairedGarments + choice.LargeGarments == 0 ? "" : "\nПо образцам: " +
                    choice.PairedGarments + " малых, " + choice.LargeGarments + " крупных комплектов") +
                "\n" + choice.Settings.Width.ToString("0.#") + " мм · " + choice.Seconds.ToString("0.0") + " с";
            try { Draw(choice); }
            catch (Exception error)
            {
                Choice = null; _apply.IsEnabled = false;
                _message.Text = "Не удалось показать вариант: " + error.GetBaseException().Message;
                Log.Error("Could not draw sport nesting preview", error);
            }
        }
        private void Draw(SportNestingChoice choice)
        {
            var drawing = new DrawingGroup(); var offsets = new List<double>(); double y = 40;
            var colors = new[] { Colors.LightSkyBlue, Colors.PaleGreen, Colors.Plum, Colors.Khaki, Colors.Salmon };
            using (var context = drawing.Open())
            {
                for (int page = 0; page < choice.Layout.PageHeights.Count; page++)
                {
                    offsets.Add(y);
                    context.DrawRectangle(Brushes.White, null, new Rect(20, y, choice.Settings.Width, choice.Layout.PageHeights[page]));
                    Text(context, "Страница " + (page + 1) + " · " + choice.Layout.PageHeights[page].ToString("0.#") + " мм",
                        20, y - 35, 24, Brushes.White);
                    y += choice.Layout.PageHeights[page] + 60;
                }
                foreach (var panel in choice.Panels)
                    context.DrawRectangle(null, new Pen(Brushes.Gray, 1),
                        new Rect(20 + panel.Left, offsets[panel.Page] + panel.Top, panel.Width, panel.Height));
                var geometries = new Dictionary<SportContourNesting.Profile, Geometry>();
                foreach (var placed in choice.Layout.Placements)
                {
                    Geometry geometry;
                    if (placed.Profile == null) geometry = Outline(placed);
                    else if (!geometries.TryGetValue(placed.Profile, out geometry))
                    {
                        geometry = Outline(placed); geometries.Add(placed.Profile, geometry);
                    }
                    context.PushTransform(new TranslateTransform(20 + placed.Left, offsets[placed.Page] + placed.Top));
                    context.DrawGeometry(new SolidColorBrush(colors[_items[placed.Index].Part % colors.Length]),
                        new Pen(Brushes.SlateGray, .8), geometry);
                    double font = Math.Min(28, Math.Min(placed.Width / 6, placed.Height / 4));
                    if (font >= 12) Text(context, "№" + _items[placed.Index].Garment + " " +
                        new[] { "П", "С", "ЛР", "ПР", "Г" }[_items[placed.Index].Part],
                        Math.Max(2, placed.Width / 2 - font * 1.5), placed.Height / 2, font, Brushes.Black);
                    context.Pop();
                }
                context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, choice.Settings.Width + 40, y));
            }
            drawing.Freeze(); _image.Source = new DrawingImage(drawing);
            _display = choice; _pageOffsets = offsets;
            _drawingWidth = choice.Settings.Width + 40; _drawingHeight = y; ResizeImage();
            _image.ToolTip = "П — перед, С — спина, ЛР / ПР — рукава, Г — горловина; № — комплект.";
        }
        private static Geometry Outline(SportSheetNesting.Placement placed)
        {
            var profile = placed.Profile;
            if (profile == null) return new RectangleGeometry(new Rect(0, 0, placed.Width, placed.Height));
            double step = placed.Width / profile.Top.Length;
            int stride = Math.Max(1, (int)Math.Ceiling(5 / step));
            var top = new List<Point>(); var bottom = new List<Point>();
            for (int first = 0; first < profile.Top.Length; first += stride)
            {
                int end = Math.Min(profile.Top.Length, first + stride);
                double a = Double.PositiveInfinity, b = Double.NegativeInfinity;
                for (int at = first; at < end; at++) { a = Math.Min(a, profile.Top[at]); b = Math.Max(b, profile.Bottom[at]); }
                top.Add(new Point(first * step, a)); top.Add(new Point(end * step, a));
                bottom.Add(new Point(first * step, b)); bottom.Add(new Point(end * step, b));
            }
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                c.BeginFigure(top[0], true, true);
                c.PolyLineTo(top.Skip(1).Concat(bottom.AsEnumerable().Reverse()).ToArray(), true, false);
            }
            geometry.Freeze(); return geometry;
        }
        private static void Text(DrawingContext context, string value, double x, double y, double size, Brush color) =>
            context.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, color, 1), new Point(x, y));
        private void ResizeImage()
        {
            if (_image.Source == null) return;
            double zoom = new[] { .5, 1, 2, 3 }[Math.Max(0, _zoom.SelectedIndex)];
            _image.Width = Math.Max(100, _viewport.ActualWidth - 35) * zoom;
            _image.Height = _image.Width * _drawingHeight / _drawingWidth;
        }
    }
}
