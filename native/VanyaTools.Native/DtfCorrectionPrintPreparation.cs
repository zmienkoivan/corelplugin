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
        private CheckBox _knockout, _knockoutSoft, _halftone;
        private TextBox _knockoutHex, _screenLpi, _screenAngle, _screenDot, _screenBoost;
        private Slider _knockoutTolerance, _screenLpiSlider, _screenAngleSlider, _screenDotSlider, _screenBoostSlider;
        private TextBlock _knockoutValue, _screenMetrics;
        private Button _knockoutSwatch, _screenAuto;
        private ToggleButton _knockoutPick;
        private ComboBox _screenMode, _screenShape;
        private bool _preparationSync, _automaticDot;
        private Action<double> _screenDotMaximum;

        private void CreateKnockoutControls(StackPanel controls)
        {
            _knockout = Check("Нокаут цвета", Options.Knockout);
            _knockout.ToolTip = "Убирает вклад выбранного цвета ткани, включая закрытые области, с плавной прозрачностью.";
            var body = new StackPanel { Margin = new Thickness(3, 4, 0, 4) };
            var section = new Expander { Header = WithHelp(_knockout,"Выберите цвет ткани и фон просмотра «Цвет ткани». Плавный нокаут убирает его вклад из смешанных цветов. Допуск от −255 до 255: минус сужает действие, плюс расширяет; точный цвет удаляется всегда. При выключенной плавности допуск 0–255."), IsExpanded = Options.Knockout, Content = body,
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
            _knockoutTolerance = new Slider { Minimum = Options.KnockoutSoft ? -255 : 0, Maximum = 255, Value = Math.Max(Options.KnockoutSoft ? -255 : 0, Math.Min(255, Options.KnockoutTolerance)),
                IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(6, 0, 6, 0) };
            System.Windows.Automation.AutomationProperties.SetName(_knockoutTolerance, "Допуск нокаута");
            Grid.SetColumn(_knockoutTolerance, 1); tolerance.Children.Add(_knockoutTolerance);
            _knockoutValue = Text(((int)_knockoutTolerance.Value).ToString(), 11);
            _knockoutValue.MinWidth = 25; Grid.SetColumn(_knockoutValue, 2); tolerance.Children.Add(_knockoutValue); body.Children.Add(tolerance);
            _knockoutSoft = Check("Плавно", Options.KnockoutSoft); body.Children.Add(_knockoutSoft);
            _knockoutSoft.ToolTip = "Разделяет цвет на вклад ткани и цвет печати, сохраняет переходы. При выключении — прежнее вырезание по допуску.";
            controls.Children.Add(section);
            _knockout.Checked += (_, __) => { section.IsExpanded = true; QueuePreview(); };
            _knockout.Unchecked += (_, __) => { section.IsExpanded = false; _knockoutPick.IsChecked = false; QueuePreview(); };
            _knockoutHex.TextChanged += (_, __) => {
                if (TryColor(_knockoutHex.Text, out Color color)) _knockoutSwatch.Background = new SolidColorBrush(color);
                if (_previewBackground?.SelectedIndex == 3) ChangePreviewBackground();
                QueuePreview();
            };
            _knockoutTolerance.ValueChanged += (_, __) => { _knockoutValue.Text = ((int)_knockoutTolerance.Value).ToString(); QueuePreview(); };
            _knockoutSoft.Checked += (_, __) => {_knockoutTolerance.Minimum=-255;QueuePreview();};
            _knockoutSoft.Unchecked += (_, __) => {_knockoutTolerance.Minimum=0;QueuePreview();};
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
            _screenMetrics=Text("",10);
            var help=new StackPanel();help.Children.Add(Text("По альфе — плотные участки остаются сплошными; «Весь принт» — сетка также на плотных участках. Для работы по альфе отключите удаление полупрозрачности. Колорбуст усиливает насыщенность и контраст. Обводка точек — плотная, без размытия; поле сглаживания при растре не применяется.",10));help.Children.Add(_screenMetrics);
            var section = new Expander { Header = WithHelp(_halftone,help), Content = body, IsExpanded = Options.Halftone, Margin = new Thickness(0, 7, 0, 0) };
            _screenMode = Combo(new[] { "По альфе", "Всё изображение" }); _screenMode.SelectedIndex = Options.HalftoneAlphaOnly ? 0 : 1;
            _screenMode.ToolTip = "По альфе: плотные участки сохраняются. Всё изображение: сетка также на плотных участках; доступен колорбуст.";
            var modes=new UniformGrid {Columns=2};_screenMode.FontSize=10;modes.Children.Add(_screenMode);
            _screenShape = Combo(new[] { "●  Круг", "⬭  Овал", "◆  Ромб", "⬡  Сота", "★  Звезда", "■  Квадрат", "✚  Крест" });
            _screenShape.SelectedIndex = (int)Options.HalftoneShape;_screenShape.FontSize=10;modes.Children.Add(_screenShape);body.Children.Add(modes);
            System.Windows.Automation.AutomationProperties.SetName(_screenShape, "Форма точки");
            _screenLpi = Field(DtfOutlineUnits.Format(Options.HalftoneLpi));
            _screenAngle = Field(Options.HalftoneAngle.ToString("0.###", CultureInfo.CurrentCulture));
            System.Windows.Automation.AutomationProperties.SetName(_screenLpi, "Растр LPI");
            System.Windows.Automation.AutomationProperties.SetName(_screenAngle, "Угол растра");
            _screenLpiSlider = ScreenSlider(body, "Растр, LPI", _screenLpi, .5, 200, .5);
            _screenAngleSlider = ScreenSlider(body, "Угол, °", _screenAngle, -180, 180, .5);
            _screenDot = Field(DtfOutlineUnits.Format(Options.HalftoneDotMm));
            System.Windows.Automation.AutomationProperties.SetName(_screenDot, "Максимальный диаметр точки");
            _screenAuto = new Button { Content = "Авто", MinHeight = 27, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Авторазмер: сохраняет покрытие альфы; в режиме всего изображения оставляет промежутки между точками. Ручной ввод задаёт максимальный диаметр." };
            _screenDotSlider = ScreenSlider(body, "Размер, мм", _screenDot, .01, 250, .01, _screenAuto);
            _screenBoost = Field(Options.HalftoneColorBoost.ToString(CultureInfo.CurrentCulture));
            _screenBoostSlider = ScreenSlider(body, "Колорбуст, %", _screenBoost, 0, 100, 1);
            _screenBoostSlider.ToolTip = "Насыщенность и контраст в режиме «Всё изображение». 0% — исходные цвета. Потерю площади точек полностью не компенсирует.";
            controls.Children.Add(section); _automaticDot = Options.HalftoneAutoDot; UpdateAutomaticDot();
            _halftone.Checked += (_, __) => { section.IsExpanded = true; QueuePreview(); };
            _halftone.Unchecked += (_, __) => { section.IsExpanded = false; QueuePreview(); };
            _screenMode.SelectionChanged += (_, __) => { UpdateAutomaticDot(); QueuePreview(); };
            _screenShape.SelectionChanged += (_, __) => { UpdateAutomaticDot(); QueuePreview(); };
            _screenLpi.TextChanged += (_, __) => { UpdateAutomaticDot(); QueuePreview(); };
            _screenAngle.TextChanged += (_, __) => QueuePreview();
            _screenDot.TextChanged += (_, __) => { if (!_preparationSync) { _automaticDot = false; _screenAuto.Content = "Авто"; QueuePreview(); } };
            _screenAuto.Click += (_, __) => { _automaticDot = true; UpdateAutomaticDot(); QueuePreview(); };
            _screenBoost.TextChanged += (_, __) => QueuePreview();
        }

        // Retain precise numeric entry alongside every slider. Local sync avoids
        // recursive events, locale problems and preview restarts during drags.
        private Slider ScreenSlider(Panel body, string label, TextBox field, double minimum, double maximum, double step, Button extra = null)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            row.Children.Add(new TextBlock { Text=label, FontSize=11, VerticalAlignment=VerticalAlignment.Center });
            field.Width=48;field.FontSize=11;field.MinHeight=24;Grid.SetColumn(field,2);row.Children.Add(field);
            if(extra!=null) {Grid.SetColumn(extra,3);row.Children.Add(extra);}body.Children.Add(row);
            var slider = new Slider { Minimum=minimum, Maximum=maximum, TickFrequency=step, IsSnapToTickEnabled=true, Margin=new Thickness(0,1,0,3) };
            System.Windows.Automation.AutomationProperties.SetName(slider,label);
            bool syncing=false;
            bool Parse(out double value) => Double.TryParse((field.Text??"").Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out value) && !Double.IsNaN(value) && !Double.IsInfinity(value);
            if(Parse(out double initial)) slider.Value=Math.Max(minimum,Math.Min(maximum,initial));
            field.TextChanged += (_,__) => { if(syncing)return; if(Parse(out double value)) { syncing=true;slider.Value=Math.Max(minimum,Math.Min(maximum,value));syncing=false; } };
            slider.ValueChanged += (_,__) => { if(syncing)return; syncing=true;field.Text=slider.Value.ToString("0.###",CultureInfo.CurrentCulture);syncing=false; };
            if(extra!=null) _screenDotMaximum = value => {
                syncing=true; slider.Maximum=value;
                if(Parse(out double number)) slider.Value=Math.Max(slider.Minimum,Math.Min(slider.Maximum,number));
                syncing=false;
            };
            Grid.SetColumn(slider,1);slider.Margin=new Thickness(5,0,5,0);row.Children.Add(slider);return slider;
        }

        private void UpdateAutomaticDot()
        {
            if (_preparationSync || _screenDot == null || !DtfOutlineUnits.TryNumber(_screenLpi.Text, out double lpi) || lpi < .5 || lpi > 200) return;
            _screenDotMaximum?.Invoke(Math.Min(250, Math.Max(.02,25.4/lpi*DtfDotGeometry.CoverDiameter((DtfDotShape)Math.Max(0,_screenShape.SelectedIndex)))));
            if(!_automaticDot) return;
            _preparationSync = true;
            _screenDot.Text = DtfOutlineUnits.Format(25.4 / lpi * (_screenMode.SelectedIndex == 0 ? DtfDotGeometry.CoverDiameter((DtfDotShape)Math.Max(0,_screenShape.SelectedIndex)) : .75));
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
            options.KnockoutSoft = _knockoutSoft.IsChecked == true;
            if(!options.KnockoutSoft)options.KnockoutTolerance=Math.Max(0,options.KnockoutTolerance);
            options.Halftone = _halftone.IsChecked == true; options.HalftoneAlphaOnly = _screenMode.SelectedIndex == 0;
            options.HalftoneShape = (DtfDotShape)_screenShape.SelectedIndex;
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
            if (!Int32.TryParse(_screenBoost.Text,out int boost) || boost<0 || boost>100) {
                if(options.Halftone && !options.HalftoneAlphaOnly) {ShowMessage("Колорбуст: 0–100%.",true);return false;} boost=Options.HalftoneColorBoost;
            }
            options.HalftoneColorBoost = boost;
            options.HalftoneRemoveSmall=_removeSmall.IsChecked==true;options.HalftoneGrowSmall=_growSmall.IsChecked==true;
            bool removeValid=DtfOutlineUnits.TryNumber(_removeSmallMm.Text,out double remove),growValid=DtfOutlineUnits.TryNumber(_growSmallMm.Text,out double grow);
            if(options.Halftone && (options.HalftoneRemoveSmall && !removeValid || options.HalftoneGrowSmall && !growValid)) {ShowMessage("Мелкие элементы: укажите размер в мм.",true);return false;}
            options.HalftoneRemoveMm=removeValid?remove:Options.HalftoneRemoveMm;options.HalftoneGrowMm=growValid?grow:Options.HalftoneGrowMm;
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
            _knockoutHex.IsEnabled = _knockoutTolerance.IsEnabled = _knockoutSwatch.IsEnabled = _knockoutPick.IsEnabled = _knockoutSoft.IsEnabled = knockout;
            _screenLpi.IsEnabled = _screenAngle.IsEnabled = _screenDot.IsEnabled = _screenMode.IsEnabled = _screenShape.IsEnabled = _screenAuto.IsEnabled = halftone;
            _screenLpiSlider.IsEnabled = _screenAngleSlider.IsEnabled = _screenDotSlider.IsEnabled = halftone;
            _screenBoost.IsEnabled = _screenBoostSlider.IsEnabled = halftone && _screenMode.SelectedIndex == 1;
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
