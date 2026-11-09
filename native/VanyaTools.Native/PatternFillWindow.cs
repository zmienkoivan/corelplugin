using System;
using System.Globalization;
using System.Linq;
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
        private readonly CheckBox _useHero;
        private readonly StackPanel _motifFields, _heroFields;
        private readonly Button _apply;
        private readonly TextBlock _message;
        private readonly DispatcherTimer _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        private PatternFrame _frame;
        private double _frameAllowance = Double.NaN;
        private int _seed;
        private bool _ready;

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
                _refresh.Stop(); RenderDraft();
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

            var pattern = DockerLayout.ToolGroup("Заполнение", "Пунктир на детали показывает линию шва. Голубой пунктир повторяет силуэт главного элемента с заданным зазором. Повторы, пересекающие эту зону, пропускаются.");
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
            _mode.SelectionChanged += (_, __) => QueuePreview();
            _useHero.Checked += (_, __) => QueuePreview();
            _useHero.Unchecked += (_, __) => QueuePreview();
            _refresh.Tick += (_, __) => { _refresh.Stop(); RenderDraft(); };
            Closed += (_, __) => { _ready = false; _refresh.Stop(); };
            _ready = true;
            RenderDraft();
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
            _apply.IsEnabled = false;
            if (!_refresh.IsEnabled) _refresh.Start();
        }

        private void RenderDraft()
        {
            Draft = null;
            try
            {
                var options = new PatternFillOptions { Mode = (PatternFillMode)_mode.SelectedIndex,
                    SizeMm = _size.Read(), GapMm = _gap.Read(), Angle = _angle.Read(), AngleVariation = _variation.Read(), Seed = _seed };
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
                var motifs = useHero ? _session.Motifs.Where(asset => asset.Id != _session.Hero.Id).ToArray() : _session.Motifs;
                var items = PatternFillLayout.Build(_frame.Width, _frame.Height, motifs.Select(asset => asset.Footprint).ToArray(), options);
                var next = new PatternEditorSettings { Layout = options, Allowance = allowance, UseHero = useHero || _session.Hero == null };
                var draft = new PatternDraft { Frame = _frame, Options = options, Motifs = motifs, Items = items, Allowance = allowance };
                if (useHero)
                {
                    next.HeroX = _heroX.Read(); next.HeroY = _heroY.Read(); next.HeroSize = _heroSize.Read();
                    next.HeroAngle = _heroAngle.Read(); next.HeroGap = _heroGap.Read();
                    var center = new PatternPoint(_frame.Width * next.HeroX / 100,
                        _frame.Height * (1 - next.HeroY / 100));
                    draft.Hero = new PatternPlacement { Center = center, Size = next.HeroSize, Angle = next.HeroAngle };
                    draft.Reserve = PatternSilhouette.Reserve(_session.Hero, draft.Hero, next.HeroGap);
                    PatternSilhouette.LeaveRoom(items, motifs, draft.Reserve);
                }
                Settings = next;
                Draft = draft;
                int fallback = motifs.Count(asset => asset.OutlineFallback);
                if (useHero && _session.Hero.OutlineFallback) fallback++;
                int used = items.Select(item => item.Motif).Distinct().Count();
                string message = "Повторов: " + items.Count + " · фигур в раскладке: " + used + " из " + motifs.Length;
                if (fallback > 0) message += " · по габаритам: " + fallback;
                ShowMessage(message, false);
            }
            catch (Exception error)
            {
                ShowMessage(error.Message, true);
            }
            _preview.Update(_frame, Draft);
            _apply.IsEnabled = Draft != null;
        }

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

            private bool TryRead(out double value) =>
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
