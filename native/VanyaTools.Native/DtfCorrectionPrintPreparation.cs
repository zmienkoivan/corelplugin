using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed partial class DtfCorrectionWindow
    {
        private CheckBox _knockout, _halftone;
        private TextBox _knockoutHex, _screenLpi, _screenAngle, _screenDot;
        private Slider _knockoutTolerance;
        private TextBlock _knockoutValue, _screenMetrics;
        private Button _knockoutSwatch, _screenAuto;
        private ToggleButton _knockoutPick;
        private ComboBox _screenMode;
        private bool _preparationSync, _automaticDot;

        private void CreateKnockoutControls(StackPanel controls)
        {
            _knockout = Check("Нокаут цвета", Options.Knockout);
            _knockout.ToolTip = "Убирает выбранный цвет из всего принта, включая закрытые внутренние области.";
            var body = new StackPanel { Margin = new Thickness(3, 4, 0, 4) };
            var section = new Expander { Header = _knockout, IsExpanded = Options.Knockout, Content = body,
                Margin = new Thickness(0, 7, 0, 2) };
            var colors = new Grid(); colors.ColumnDefinitions.Add(new ColumnDefinition());
            colors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _knockoutHex = Field(Hex(Options.KnockoutColor)); colors.Children.Add(_knockoutHex);
            System.Windows.Automation.AutomationProperties.SetName(_knockoutHex, "Цвет нокаута");
            _knockoutSwatch = new Button { Width = 28, Height = 28, Margin = new Thickness(4, 0, 4, 0),
                Padding = new Thickness(1), Background = new SolidColorBrush(Options.KnockoutColor), ToolTip = "Выбрать цвет нокаута" };
            _knockoutSwatch.Content = SwatchContent(_knockoutSwatch);
            Grid.SetColumn(_knockoutSwatch, 1); colors.Children.Add(_knockoutSwatch);
            _knockoutPick = new ToggleButton { Content = "⌖", Width = 29, Height = 28, FontSize = 16,
                Padding = new Thickness(0), ToolTip = "Пипетка: нажмите на цвет в левом исходнике" };
            System.Windows.Automation.AutomationProperties.SetName(_knockoutPick, "Пипетка нокаута с исходника");
            Grid.SetColumn(_knockoutPick, 2); colors.Children.Add(_knockoutPick); body.Children.Add(colors);
            var tolerance = new Grid { Margin = new Thickness(0, 5, 0, 3) };
            tolerance.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); tolerance.ColumnDefinitions.Add(new ColumnDefinition());
            tolerance.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tolerance.Children.Add(new TextBlock { Text = "Допуск", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            _knockoutTolerance = new Slider { Minimum = 0, Maximum = 255, Value = Math.Max(0, Math.Min(255, Options.KnockoutTolerance)),
                IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(6, 0, 6, 0) };
            System.Windows.Automation.AutomationProperties.SetName(_knockoutTolerance, "Допуск нокаута");
            Grid.SetColumn(_knockoutTolerance, 1); tolerance.Children.Add(_knockoutTolerance);
            _knockoutValue = Text(((int)_knockoutTolerance.Value).ToString(), 11);
            _knockoutValue.MinWidth = 25; Grid.SetColumn(_knockoutValue, 2); tolerance.Children.Add(_knockoutValue); body.Children.Add(tolerance);
            body.Children.Add(Text("Цвет внутри допуска становится прозрачным. Нокаут выполняется перед коррекцией и обводкой.", 10));
            controls.Children.Add(section);
            _knockout.Checked += (_, __) => { section.IsExpanded = true; QueuePreview(); };
            _knockout.Unchecked += (_, __) => { section.IsExpanded = false; _knockoutPick.IsChecked = false; QueuePreview(); };
            _knockoutHex.TextChanged += (_, __) => {
                if (TryColor(_knockoutHex.Text, out Color color)) _knockoutSwatch.Background = new SolidColorBrush(color);
                QueuePreview();
            };
            _knockoutTolerance.ValueChanged += (_, __) => { _knockoutValue.Text = ((int)_knockoutTolerance.Value).ToString(); QueuePreview(); };
            _knockoutSwatch.Click += (_, __) => {
                try {
                    if (!TryColor(_knockoutHex.Text, out Color current)) current = Colors.Black;
                    Color? color = WindowsColorPicker.Pick(new WindowInteropHelper(this).Handle, current);
                    if (color.HasValue) _knockoutHex.Text = Hex(color.Value);
                } catch (Exception error) { ShowMessage(error.GetBaseException().Message, true); }
            };
            _knockoutPick.Checked += (_, __) => { if (_beforeScroll != null) _beforeScroll.Cursor = Cursors.Cross; };
            _knockoutPick.Unchecked += (_, __) => { if (_beforeScroll != null) _beforeScroll.ClearValue(CursorProperty); };
        }

        private void CreateHalftoneControls(StackPanel controls)
        {
            _halftone = Check("Халфтонинг", Options.Halftone);
            var body = new StackPanel { Margin = new Thickness(3, 4, 0, 4) };
            var section = new Expander { Header = _halftone, Content = body, IsExpanded = Options.Halftone, Margin = new Thickness(0, 7, 0, 0) };
            _screenMode = Combo(new[] { "По альфе", "Всё изображение" }); _screenMode.SelectedIndex = Options.HalftoneAlphaOnly ? 0 : 1;
            _screenMode.ToolTip = "По альфе: плотные участки сохраняются. Всё изображение: сетка также на плотных участках. Промежутки прозрачные, цвета сохраняются.";
            body.Children.Add(_screenMode);
            var values = new UniformGrid { Columns = 2 };
            _screenLpi = Field(DtfOutlineUnits.Format(Options.HalftoneLpi));
            _screenAngle = Field(Options.HalftoneAngle.ToString("0.###", CultureInfo.CurrentCulture));
            System.Windows.Automation.AutomationProperties.SetName(_screenLpi, "Растр LPI");
            System.Windows.Automation.AutomationProperties.SetName(_screenAngle, "Угол растра");
            var first = Labeled("Растр, LPI", _screenLpi); first.Margin = new Thickness(0, 2, 5, 0); values.Children.Add(first);
            values.Children.Add(Labeled("Угол, °", _screenAngle)); body.Children.Add(values);
            var dots = new Grid(); dots.ColumnDefinitions.Add(new ColumnDefinition()); dots.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _screenDot = Field(DtfOutlineUnits.Format(Options.HalftoneDotMm)); dots.Children.Add(_screenDot);
            System.Windows.Automation.AutomationProperties.SetName(_screenDot, "Максимальный диаметр точки");
            _screenAuto = new Button { Content = "Авто", MinHeight = 27, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Авторазмер: сохраняет покрытие альфы; в режиме всего изображения оставляет промежутки между точками. Ручной ввод задаёт максимальный диаметр." };
            Grid.SetColumn(_screenAuto, 1); dots.Children.Add(_screenAuto); body.Children.Add(Labeled("Макс. точка, мм", dots));
            _screenMetrics = Text("", 10); body.Children.Add(_screenMetrics);
            body.Children.Add(Text("Сетка применяется после коррекции и обводки. Для растра по альфе отключите «Убрать полупрозрачность».", 10));
            controls.Children.Add(section); _automaticDot = Options.HalftoneAutoDot; UpdateAutomaticDot();
            _halftone.Checked += (_, __) => { section.IsExpanded = true; QueuePreview(); };
            _halftone.Unchecked += (_, __) => { section.IsExpanded = false; QueuePreview(); };
            _screenMode.SelectionChanged += (_, __) => { UpdateAutomaticDot(); QueuePreview(); };
            _screenLpi.TextChanged += (_, __) => { UpdateAutomaticDot(); QueuePreview(); };
            _screenAngle.TextChanged += (_, __) => QueuePreview();
            _screenDot.TextChanged += (_, __) => { if (!_preparationSync) { _automaticDot = false; _screenAuto.Content = "Авто"; QueuePreview(); } };
            _screenAuto.Click += (_, __) => { _automaticDot = true; UpdateAutomaticDot(); QueuePreview(); };
        }

        private void UpdateAutomaticDot()
        {
            if (!_automaticDot || _screenDot == null || !DtfOutlineUnits.TryNumber(_screenLpi.Text, out double lpi) || lpi < .5 || lpi > 200) return;
            _preparationSync = true;
            _screenDot.Text = DtfOutlineUnits.Format(25.4 / lpi * (_screenMode.SelectedIndex == 0 ? Math.Sqrt(2) : .75));
            _screenAuto.Content = "Авто ✓"; _preparationSync = false;
        }

        private bool ReadPrintPreparation(DtfCorrectionOptions options)
        {
            options.Knockout = _knockout.IsChecked == true;
            if (!TryColor(_knockoutHex.Text, out Color knockoutColor)) {
                if (options.Knockout) { ShowMessage("Нокаут: выберите цвет или введите #RRGGBB.", true); return false; }
                knockoutColor = Options.KnockoutColor;
            }
            options.KnockoutColor = knockoutColor; options.KnockoutTolerance = (int)_knockoutTolerance.Value;
            options.Halftone = _halftone.IsChecked == true; options.HalftoneAlphaOnly = _screenMode.SelectedIndex == 0;
            options.HalftoneAutoDot = _automaticDot;
            bool lpiOk = DtfOutlineUnits.TryNumber(_screenLpi.Text, out double lpi);
            bool dotOk = DtfOutlineUnits.TryNumber(_screenDot.Text, out double dot);
            bool angleOk = Double.TryParse((_screenAngle.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double angle) &&
                !Double.IsNaN(angle) && !Double.IsInfinity(angle);
            if (options.Halftone && (!lpiOk || !angleOk || !dotOk && !_automaticDot)) {
                ShowMessage("Халфтонинг: укажите растр, угол и диаметр точки числами.", true); return false;
            }
            options.HalftoneLpi = lpiOk ? lpi : Options.HalftoneLpi;
            options.HalftoneAngle = angleOk ? angle : Options.HalftoneAngle;
            options.HalftoneDotMm = dotOk ? dot : Options.HalftoneDotMm;
            try { options.Preparation().Validate(); }
            catch (Exception error) { ShowMessage(error.Message, true); return false; }
            if (options.Halftone) _screenMetrics.Text = "Шаг " + DtfOutlineUnits.Format(25.4 / options.HalftoneLpi) +
                " мм · точка ≤ " + DtfOutlineUnits.Format(options.Preparation().EffectiveDotMm) + " мм";
            return true;
        }

        private void UpdatePrintPreparationAvailability(bool available)
        {
            if (_knockout == null || _halftone == null) return;
            bool knockout = available && _knockout.IsChecked == true, halftone = available && _halftone.IsChecked == true;
            _knockoutHex.IsEnabled = _knockoutTolerance.IsEnabled = _knockoutSwatch.IsEnabled = _knockoutPick.IsEnabled = knockout;
            _screenLpi.IsEnabled = _screenAngle.IsEnabled = _screenDot.IsEnabled = _screenMode.IsEnabled = _screenAuto.IsEnabled = halftone;
            if (!knockout) _knockoutPick.IsChecked = false;
        }

        private bool PickKnockoutFromPreview(ScrollViewer scroll, Image image, MouseButtonEventArgs e)
        {
            if (_knockoutPick?.IsChecked != true || !ReferenceEquals(scroll, _beforeScroll) || e.ChangedButton != MouseButton.Left ||
                Keyboard.IsKeyDown(Key.Space) || !_loaded || _closed || _applying || _previewScene.IsEmpty || image.ActualWidth <= 0 || image.ActualHeight <= 0) return false;
            Point point = e.GetPosition(image);
            int x = (int)Math.Floor(_previewScene.X + point.X * _previewScene.Width / image.ActualWidth);
            int y = (int)Math.Floor(_previewScene.Y + point.Y * _previewScene.Height / image.ActualHeight);
            if (_originalPixels.Sample(x, y, out Color color)) { _knockoutHex.Text = Hex(color); _knockoutPick.IsChecked = false; }
            else ShowMessage("Выберите видимый цвет в левом исходнике.", false);
            e.Handled = true; return true;
        }
    }
}
