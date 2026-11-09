using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class PatternEditorSettings
    {
        internal PatternFillOptions Layout = new PatternFillOptions();
        internal double Allowance, HeroSize, HeroAngle;
        internal double HeroX = 50, HeroY = 50, HeroGap = 5;
        internal bool UseHero = true;
    }

    internal sealed class PatternFillWindow : Window
    {
        private readonly PatternEditorSession _session;
        private readonly PatternPreviewSurface _preview;
        private readonly ComboBox _mode;
        private readonly ValueField _size, _gap, _allowance, _angle, _min, _max, _variation;
        private readonly ValueField _heroX, _heroY, _heroSize, _heroAngle, _heroGap;
        private readonly CheckBox _useHero, _repeat, _showCells, _showRepeatBorders;
        private readonly StackPanel _motifFields, _heroFields, _repeatFields;
        private readonly ValueField _repeatWidth, _repeatHeight;
        private readonly Button _apply;
        private readonly TextBlock _message;
        private readonly DispatcherTimer _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        private PatternFrame _frame;
        private double _frameAllowance = Double.NaN;
        private int _seed, _revision;
        private bool _ready, _rendering;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();

        // Used by one preview task at a time. Moving the hero reuses the placement and contour calculations.
        private PatternFrame _layoutFrame;
        private PatternFillOptions _layoutOptions;
        private bool _layoutUsesHero;
        private List<PatternPlacement> _layoutItems;
        private Geometry[] _layoutSilhouettes;
        private Geometry _layoutCellGuides, _layoutRepeatGuides;
        private Geometry _reserve;
        private double _reserveSize, _reserveAngle, _reserveGap;

        internal PatternDraft Draft { get; private set; }
        internal PatternEditorSettings Settings { get; private set; }

        internal PatternFillWindow(PatternEditorSession session, PatternEditorSettings initial)
        {
            _session = session;
            _frame = session.Seam;
            _seed = initial.Layout.Seed;
            Title = "Паттерн по контуру · Vanya Tools";
            Width = Math.Min(1180, SystemParameters.WorkArea.Width - 28);
            Height = Math.Min(840, SystemParameters.WorkArea.Height - 28);
            MinWidth = 680; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);
            DockerTheme.UseTheme(this, Control.BackgroundProperty, DockerTheme.WindowBackground);

            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;
            var footer = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            var cancel = Button("Закрыть", (_, __) => Close()); cancel.IsCancel = true;
            cancel.MinWidth = 84; DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
            _apply = DockerTheme.Primary(Button("Применить в Corel", (_, __) =>
            {
                if (Draft != null) DialogResult = true;
            }));
            _apply.IsEnabled = false; _apply.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(_apply, Dock.Right); footer.Children.Add(_apply);
            _message = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0) };
            footer.Children.Add(_message);

            var settings = new StackPanel { Margin = new Thickness(1, 0, 12, 0) };
            var scroll = new ScrollViewer { Content = settings, Width = 282, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            DockPanel.SetDock(scroll, Dock.Left); root.Children.Add(scroll);
            _preview = new PatternPreviewSurface(session);
            var frame = new Border { Child = _preview, BorderThickness = new Thickness(1), ClipToBounds = true };
            DockerTheme.UseTheme(frame, Border.BorderBrushProperty, DockerTheme.BorderBrush);
            DockerTheme.UseTheme(frame, Border.BackgroundProperty, DockerTheme.SurfaceBackground);
            root.Children.Add(frame);

            var pattern = DockerLayout.ToolGroup("Заполнение", "Пунктир на детали показывает линию шва. Голубой пунктир повторяет силуэт главного элемента с заданным зазором. Рисунки рядом подгоняются под свободное место.");
            settings.Children.Add(pattern);
            _mode = new ComboBox { ItemsSource = new[] { "Ряды", "Вразбежку", "Случайно", "Крокодиловая кожа", "Галька" },
                SelectedIndex = (int)initial.Layout.Mode,
                MinHeight = 27, Margin = new Thickness(0, 0, 0, 6) };
            pattern.Children.Add(_mode);
            double sliderSize = Math.Max(100, Math.Min(1000, Math.Max(session.Seam.Width, session.Seam.Height)));
            _size = AddField(pattern, "Размер элементов / ячейки, мм", .5, 2000, initial.Layout.SizeMm, sliderSize);
            _gap = AddField(pattern, "Зазор между повторами, мм", 0, 1000, initial.Layout.GapMm, 80);
            _allowance = AddField(pattern, "Припуск на шов, мм", 0, 100,
                session.Replace ? session.ExistingAllowance : initial.Allowance, 40);
            _allowance.View.IsEnabled = !session.Replace;
            _allowance.View.ToolTip = session.Replace ? "Для нового припуска выделите исходный контур детали." : "Припуск строится наружу от контура детали.";
            _angle = AddField(pattern, "Поворот, °", -360, 360, initial.Layout.Angle, 360);
            _motifFields = new StackPanel(); pattern.Children.Add(_motifFields);
            _min = AddField(_motifFields, "Размер от, %", 10, 300, initial.Layout.MinScale * 100, 300);
            _max = AddField(_motifFields, "Размер до, %", 10, 300, initial.Layout.MaxScale * 100, 300);
            _variation = AddField(pattern, "Разброс поворота, ±°", 0, 180, initial.Layout.AngleVariation, 180);
            pattern.Children.Add(Button("Другой вариант", (_, __) => { _seed = unchecked(_seed + 7919); QueuePreview(); }));

            _showCells = new CheckBox { Content = "Контуры ячеек", Margin = new Thickness(0, 6, 0, 5),
                ToolTip = "Контуры первоначальной посадки. При уплотнении фигуры могут выходить за эти линии." };
            pattern.Children.Add(_showCells);
            _repeat = new CheckBox { Content = "Повторять рапорт", IsChecked = initial.Layout.Repeat,
                Margin = new Thickness(0, 0, 0, 5),
                ToolTip = "Один блок повторяется по ширине и высоте. Поворот поворачивает весь рапорт." };
            pattern.Children.Add(_repeat);
            _repeatFields = new StackPanel();
            pattern.Children.Add(_repeatFields);
            double defaultRepeatSize = Math.Min(5000, Math.Max(150, initial.Layout.SizeMm * 5));
            double repeatSliderMax = Math.Max(500, sliderSize);
            _repeatWidth = AddField(_repeatFields, "Ширина рапорта, мм", .5, 5000,
                initial.Layout.Repeat ? initial.Layout.RepeatWidthMm : defaultRepeatSize, repeatSliderMax);
            _repeatHeight = AddField(_repeatFields, "Высота рапорта, мм", .5, 5000,
                initial.Layout.Repeat ? initial.Layout.RepeatHeightMm : defaultRepeatSize, repeatSliderMax);
            _showRepeatBorders = new CheckBox { Content = "Пунктир границ рапорта", IsChecked = true,
                Margin = new Thickness(0, 0, 0, 5) };
            _repeatFields.Children.Add(_showRepeatBorders);

            var hero = DockerLayout.ToolGroup("Главный элемент", "Щёлкните по детали или перетащите главный элемент. Координаты считаются от левого и верхнего краёв внешнего габарита детали. Размер задаёт длинную сторону с учётом поворота.");
            settings.Children.Add(hero);
            hero.Visibility = session.Hero == null ? Visibility.Collapsed : Visibility.Visible;
            _useHero = new CheckBox { Content = "Добавить главный элемент", IsChecked = session.Hero != null && initial.UseHero,
                Margin = new Thickness(0, 0, 0, 5) };
            hero.Children.Add(_useHero);
            _heroFields = new StackPanel(); hero.Children.Add(_heroFields);
            double suggested = Math.Max(.5, Math.Min(2000, Math.Min(session.Seam.Width, session.Seam.Height) * .35));
            _heroSize = AddField(_heroFields, "Размер, мм", .5, 2000, initial.HeroSize > 0 ? initial.HeroSize : suggested, sliderSize);
            _heroX = AddField(_heroFields, "От левого края, %", 0, 100, initial.HeroX, 100);
            _heroY = AddField(_heroFields, "От верхнего края, %", 0, 100, initial.HeroY, 100);
            _heroAngle = AddField(_heroFields, "Поворот главного элемента, °", -360, 360, initial.HeroAngle, 360);
            _heroGap = AddField(_heroFields, "Свободная зона, мм", 0, 1000, initial.HeroGap, 80);
            _heroFields.Children.Add(Button("По центру", (_, __) => SetPosition(50, 50)));
            _preview.HeroPositionChanged += SetPosition;
            _mode.SelectionChanged += (_, __) => { UpdateGuideControls(); QueuePreview(); };
            _repeat.Checked += (_, __) => { UpdateGuideControls(); QueuePreview(); };
            _repeat.Unchecked += (_, __) => { UpdateGuideControls(); QueuePreview(); };
            _showCells.Checked += (_, __) => UpdateGuideControls();
            _showCells.Unchecked += (_, __) => UpdateGuideControls();
            _showRepeatBorders.Checked += (_, __) => UpdateGuideControls();
            _showRepeatBorders.Unchecked += (_, __) => UpdateGuideControls();
            UpdateGuideControls();
            _useHero.Checked += (_, __) => QueuePreview();
            _useHero.Unchecked += (_, __) => QueuePreview();
            _refresh.Tick += (_, __) => { _refresh.Stop(); RenderDraft(); };
            Closed += (_, __) =>
            {
                _ready = false;
                _refresh.Stop();
                _closing.Cancel();
                if (!_rendering) _closing.Dispose();
            };
            _ready = true;
            ShowMessage("Готовлю предпросмотр…", false);
            QueuePreview();
        }

        private void UpdateGuideControls()
        {
            var mode = (PatternFillMode)_mode.SelectedIndex;
            bool pebbles = mode == PatternFillMode.Pebbles;
            bool texture = pebbles || mode == PatternFillMode.Crocodile;
            bool repeat = _repeat.IsChecked == true;
            _variation.View.IsEnabled = !pebbles;
            _variation.View.ToolTip = pebbles ? "В «Гальке» поворот рисунка подбирается автоматически по форме ячейки." : null;
            ToolTipService.SetShowOnDisabled(_variation.View, pebbles);
            _showCells.Visibility = texture ? Visibility.Visible : Visibility.Collapsed;
            _repeatFields.Visibility = repeat ? Visibility.Visible : Visibility.Collapsed;
            _preview.ShowGuides(texture && _showCells.IsChecked == true, repeat && _showRepeatBorders.IsChecked == true);
        }

        private void SetPosition(double x, double y)
        {
            _heroX.Set(Math.Round(x, 1));
            _heroY.Set(Math.Round(y, 1));
            QueuePreview();
        }

        private void QueuePreview()
        {
            if (!_ready) return;
            _revision++;
            _apply.IsEnabled = false;
            if (!_rendering && !_refresh.IsEnabled) _refresh.Start();
        }

        private async void RenderDraft()
        {
            int revision = _revision;
            _rendering = true;
            Draft = null;
            try
            {
                var mode = (PatternFillMode)_mode.SelectedIndex;
                if (!_variation.TryRead(out double variation))
                    variation = mode == PatternFillMode.Pebbles ? 0 : _variation.Read();
                var options = new PatternFillOptions { Mode = mode,
                    SizeMm = _size.Read(), GapMm = _gap.Read(), Angle = _angle.Read(), AngleVariation = variation, Seed = _seed };
                if (_repeat.IsChecked == true)
                {
                    options.RepeatWidthMm = _repeatWidth.Read();
                    options.RepeatHeightMm = _repeatHeight.Read();
                }
                _motifFields.Visibility = options.Texture ? Visibility.Collapsed : Visibility.Visible;
                bool useHero = _useHero.IsChecked == true && _session.Hero != null;
                _heroFields.Visibility = useHero ? Visibility.Visible : Visibility.Collapsed;
                if (!options.Texture)
                {
                    options.MinScale = _min.Read() / 100; options.MaxScale = _max.Read() / 100;
                }
                double allowance = _allowance.Read();
                if (_frameAllowance != allowance)
                {
                    _frame = PatternFillService.FrameFor(_session, allowance);
                    _frameAllowance = allowance;
                }
                var next = new PatternEditorSettings { Layout = options, Allowance = allowance, UseHero = useHero || _session.Hero == null };
                if (useHero)
                {
                    next.HeroX = _heroX.Read(); next.HeroY = _heroY.Read(); next.HeroSize = _heroSize.Read();
                    next.HeroAngle = _heroAngle.Read(); next.HeroGap = _heroGap.Read();
                }
                PatternFrame frame = _frame;
                var cancellation = _closing.Token;
                PatternDraft draft = await Task.Run(() => BuildDraft(frame, next, useHero, cancellation), cancellation);
                if (!_ready) return;
                _preview.Update(frame, draft);
                if (revision != _revision) return;
                Settings = next;
                Draft = draft;
                int fallback = draft.Motifs.Count(asset => asset.OutlineFallback);
                if (useHero && _session.Hero.OutlineFallback) fallback++;
                int used = draft.Items.Select(item => item.Motif).Distinct().Count();
                string message = "Повторов: " + draft.Items.Count + " · фигур в раскладке: " + used + " из " + draft.Motifs.Length;
                if (fallback > 0) message += " · по габаритам: " + fallback;
                ShowMessage(message, false);
                _apply.IsEnabled = true;
            }
            catch (OperationCanceledException) when (!_ready) { }
            catch (Exception error)
            {
                if (_ready && revision == _revision) ShowMessage(error.Message, true);
            }
            finally
            {
                _rendering = false;
                if (!_ready) _closing.Dispose();
                else if (revision != _revision) _refresh.Start();
            }
        }

        private PatternDraft BuildDraft(PatternFrame frame, PatternEditorSettings settings, bool useHero,
            CancellationToken cancellation)
        {
            var options = settings.Layout;
            var motifs = useHero ? _session.Motifs.Where(asset => asset.Id != _session.Hero.Id).ToArray() : _session.Motifs;
            if (_layoutFrame != frame || useHero != _layoutUsesHero || !SameLayout(_layoutOptions, options))
            {
                foreach (var motif in motifs)
                {
                    cancellation.ThrowIfCancellationRequested();
                    PatternFillService.PrepareSilhouette(motif);
                }
                var items = PatternFillLayout.Build(frame.Width, frame.Height, motifs.Select(asset => asset.Footprint).ToArray(),
                    options, out PatternLayoutGuides guides);
                Geometry[] silhouettes = null;
                if (useHero)
                {
                    silhouettes = new Geometry[items.Count];
                    for (int i = 0; i < items.Count; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        silhouettes[i] = PatternSilhouette.At(motifs[items[i].Motif], items[i]);
                    }
                }
                _layoutCellGuides = GuideGeometry(guides?.Cells, true);
                _layoutRepeatGuides = GuideGeometry(guides?.RepeatLines, false);
                _layoutItems = items;
                _layoutSilhouettes = silhouettes;
                _layoutFrame = frame; _layoutOptions = options; _layoutUsesHero = useHero;
            }

            var draft = new PatternDraft { Frame = frame, Options = options, Motifs = motifs,
                Items = _layoutItems, Allowance = settings.Allowance,
                CellGuides = _layoutCellGuides, RepeatGuides = _layoutRepeatGuides };
            if (!useHero) return draft;
            PatternFillService.PrepareSilhouette(_session.Hero);
            draft.Hero = new PatternPlacement { Size = settings.HeroSize, Angle = settings.HeroAngle,
                Center = new PatternPoint(frame.Width * settings.HeroX / 100, frame.Height * (1 - settings.HeroY / 100)) };
            if (_reserve == null || _reserveSize != settings.HeroSize || _reserveAngle != settings.HeroAngle || _reserveGap != settings.HeroGap)
            {
                _reserve = PatternSilhouette.Reserve(_session.Hero, draft.Hero, settings.HeroGap);
                _reserveSize = settings.HeroSize; _reserveAngle = settings.HeroAngle; _reserveGap = settings.HeroGap;
            }
            draft.Reserve = PatternSilhouette.MoveReserve(_reserve, draft.Hero.Center);
            draft.Items = PatternHeroFill.Adjust(_layoutItems, _layoutSilhouettes, motifs, options, draft.Reserve, cancellation);
            return draft;
        }

        private static Geometry GuideGeometry(IReadOnlyList<PatternPoint[]> paths, bool closed)
        {
            if (paths == null || paths.Count == 0) return null;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
                foreach (var path in paths)
                {
                    context.BeginFigure(new Point(path[0].X, path[0].Y), false, closed);
                    for (int i = 1; i < path.Length; i++) context.LineTo(new Point(path[i].X, path[i].Y), true, false);
                }
            geometry.Freeze();
            return geometry;
        }

        private static bool SameLayout(PatternFillOptions previous, PatternFillOptions next) =>
            previous != null && previous.Mode == next.Mode && previous.SizeMm == next.SizeMm && previous.GapMm == next.GapMm &&
            previous.MinScale == next.MinScale && previous.MaxScale == next.MaxScale && previous.Angle == next.Angle &&
            previous.AngleVariation == next.AngleVariation && previous.Seed == next.Seed &&
            previous.RepeatWidthMm == next.RepeatWidthMm && previous.RepeatHeightMm == next.RepeatHeightMm;

        private void ShowMessage(string message, bool error)
        {
            _message.Text = message;
            DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, error ? DockerTheme.ErrorTextBrush : DockerTheme.MutedTextBrush);
        }

        private ValueField AddField(Panel parent, string label, double minimum, double maximum, double value, double sliderMax)
        {
            var field = new ValueField(label, minimum, maximum, value, sliderMax, QueuePreview);
            parent.Children.Add(field.View);
            return field;
        }

        private static Button Button(string label, RoutedEventHandler click)
        {
            var button = new Button { Content = label, MinHeight = 28, Padding = new Thickness(8, 2, 8, 2) };
            button.Click += click;
            return button;
        }

        private sealed class ValueField
        {
            private readonly string _label;
            private readonly double _minimum, _maximum;
            private readonly TextBox _input;
            private readonly Slider _slider;
            private bool _sync;
            internal FrameworkElement View { get; }

            internal ValueField(string label, double minimum, double maximum, double value, double sliderMax, Action changed)
            {
                _label = label; _minimum = minimum; _maximum = maximum;
                _input = new TextBox { Width = 60, MinHeight = 24, VerticalContentAlignment = VerticalAlignment.Center };
                _slider = new Slider { Minimum = minimum, Maximum = sliderMax,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
                Set(value);
                _slider.ValueChanged += (_, __) =>
                {
                    if (_sync) return;
                    Set(Math.Round(_slider.Value, 1)); changed();
                };
                _input.TextChanged += (_, __) =>
                {
                    if (_sync) return;
                    if (TryRead(out double entered))
                    {
                        _sync = true;
                        _slider.Value = entered;
                        _sync = false;
                    }
                    changed();
                };
                var row = DockerLayout.Trailing(_slider, _input);
                var layout = DockerLayout.LabeledField(label, row);
                layout.Margin = new Thickness(0, 0, 0, 5);
                View = layout;
                System.Windows.Automation.AutomationProperties.SetName(_input, label);
                System.Windows.Automation.AutomationProperties.SetName(_slider, label);
            }

            internal double Read()
            {
                if (TryRead(out double value)) return value;
                throw new ArgumentException("«" + _label + "» — от " + _minimum + " до " + _maximum + ".");
            }

            internal bool TryRead(out double value) =>
                Double.TryParse(_input.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                value >= _minimum && value <= _maximum;

            internal void Set(double value)
            {
                _sync = true;
                _input.Text = value.ToString("0.##", CultureInfo.CurrentCulture);
                _slider.Value = value;
                _sync = false;
            }
        }
    }
}
