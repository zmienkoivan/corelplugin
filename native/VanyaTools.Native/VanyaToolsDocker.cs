using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VanyaTools.Native
{
    public sealed class VanyaToolsDocker : UserControl
    {
        private readonly TextBlock _status;
        private readonly TextBox _trimPaddingPx;
        private readonly System.Windows.Controls.RadioButton _trimModeTopLeft;
        private readonly System.Windows.Controls.RadioButton _trimModeBotRight;
        private readonly System.Windows.Controls.CheckBox _trimTop;
        private readonly System.Windows.Controls.CheckBox _trimBottom;
        private readonly System.Windows.Controls.CheckBox _trimLeft;
        private readonly System.Windows.Controls.CheckBox _trimRight;
        private readonly TextBox _offsetMm;
        private readonly TextBox _rasterDpi;
        private readonly TextBox _smoothing;
        private readonly TextBox _detail;
        private readonly TextBox _roundSpikesMm;
        private readonly TextBox _simplificationToleranceMm;
        private readonly TextBox _tabWidthMm;
        private readonly TextBox _tabHeightMm;
        private readonly TextBox _tabRadiusMm;

        public VanyaToolsDocker()
            : this(null)
        {
        }

        public VanyaToolsDocker(object app)
        {
            CorelApp.SetHostApplication(app);
            Log.Info("VanyaToolsDocker constructor started.");

            var outer = new StackPanel
            {
                Margin = new Thickness(6),
                Background = new SolidColorBrush(Color.FromRgb(236, 239, 241))
            };

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = outer
            };
            var root = outer;

            root.Children.Add(new TextBlock
            {
                Text = "Vanya Tools",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            });

            _status = new TextBlock
            {
                Text = "Готово · сборка 2026.09.24.0001",
                FontSize = 11,
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
                Background = new SolidColorBrush(Color.FromRgb(223, 229, 232))
            };
            root.Children.Add(_status);

            AddSectionTitle(root, "Обрезка растра");

            AddSmallLabel(root, "По границе");
            AddRadioButton(root, "Прозрачные пиксели", "TrimMode", true);
            _trimModeTopLeft  = AddRadioButton(root, "Цвет верхнего-левого угла",  "TrimMode", false);
            _trimModeBotRight = AddRadioButton(root, "Цвет нижнего-правого угла",  "TrimMode", false);

            AddSmallLabel(root, "Обрезать стороны");
            var sidesGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 5) };
            _trimTop    = AddCheckBox(sidesGrid, "Сверху", true);
            _trimLeft   = AddCheckBox(sidesGrid, "Слева",  true);
            _trimBottom = AddCheckBox(sidesGrid, "Снизу",  true);
            _trimRight  = AddCheckBox(sidesGrid, "Справа", true);
            root.Children.Add(sidesGrid);

            _trimPaddingPx = AddRow(root, "Отступ, px", "2");
            root.Children.Add(Button("Обрезать растр", (_, __) => RunTrim(true)));

            AddSeparator(root);
            AddSectionTitle(root, "Контур реза");

            _offsetMm = AddRow(root, "Отступ, мм", "2");
            _rasterDpi = AddRow(root, "Растр DPI", "300");
            _smoothing = AddRow(root, "Сглаживание", "70");
            _detail = AddRow(root, "Детализация", "35");
            _roundSpikesMm = AddRow(root, "Скругление, мм", "0.7");
            _simplificationToleranceMm = AddRow(root, "Упрощение контура, мм", "0.05");

            root.Children.Add(Button("Создать контур реза", (_, __) => RunCutContour()));
            root.Children.Add(Button("Сгладить выбранный контур", (_, __) => RunSmoothSelectedContour()));

            AddSeparator(root);
            AddSectionTitle(root, "Редактирование пака");
            root.Children.Add(Button("Удалить контуры пака", (_, __) => RunDeletePackContours()));
            root.Children.Add(Button("Удалить маркеры пака", (_, __) => RunDeletePackMarkers()));

            AddSeparator(root);
            AddSectionTitle(root, "Язычки отрыва");
            _tabWidthMm = AddRow(root, "Ширина, мм", "4");
            _tabHeightMm = AddRow(root, "Длина, мм", "12");
            _tabRadiusMm = AddRow(root, "Скругление язычка, мм", "2");
            AddSmallLabel(root, "Язычок пересекает контур пополам.");
            root.Children.Add(Button("Добавить маркер", (_, __) => RunAddPeelMarker()));
            root.Children.Add(Button("Переприкрепить маркеры", (_, __) => RunReattachMarkers()));
            root.Children.Add(Button("Применить язычки", (_, __) => RunApplyPeelTabs()));

            Log.Info("VanyaToolsDocker constructor finished.");
        }

        private void RunTrim(bool useTiles)
        {
            RunSafe(() =>
            {
                var mode = _trimModeTopLeft.IsChecked  == true ? TrimMode.TopLeftColor
                         : _trimModeBotRight.IsChecked == true ? TrimMode.BottomRightColor
                         : TrimMode.TransparentPixels;

                var sides = TrimSides.None;
                if (_trimTop.IsChecked    == true) sides |= TrimSides.Top;
                if (_trimBottom.IsChecked == true) sides |= TrimSides.Bottom;
                if (_trimLeft.IsChecked   == true) sides |= TrimSides.Left;
                if (_trimRight.IsChecked  == true) sides |= TrimSides.Right;

                int padding = ParseInt(_trimPaddingPx.Text, 2);
                var result = new BitmapTrimService().TrimSelected(mode, sides, padding, useTiles);
                string m = useTiles ? "тайлы" : "пиксели";
                SetStatus($"Обрезано ({m}): {result.Trimmed}. Пропущено: {result.Skipped}.", false);
            });
        }

        private void RunCutContour()
        {
            RunSafe(() =>
            {
                double offset = ParseDouble(_offsetMm.Text, 2);
                int dpi = ParseInt(_rasterDpi.Text, 300);
                int smoothing = ParseInt(_smoothing.Text, 70);
                int detail = ParseInt(_detail.Text, 25);
                double roundSpikes = ParseDouble(_roundSpikesMm.Text, 0.7);
                double simplifyToleranceMm = ParseDouble(_simplificationToleranceMm.Text, 0.05);

                new StickerCutService().CreateCutContour(offset, dpi, smoothing, detail, roundSpikes, simplifyToleranceMm);
                SetStatus($"Контур реза создан: {offset:0.###} мм.", false);
            });
        }

        private void RunSmoothSelectedContour()
        {
            RunSafe(() =>
            {
                double roundSpikes = ParseDouble(_roundSpikesMm.Text, 0.7);
                double simplifyToleranceMm = ParseDouble(_simplificationToleranceMm.Text, 0.05);
                int processed = new StickerCutService().SmoothSelectedContours(roundSpikes, simplifyToleranceMm);
                SetStatus($"Сглажено контуров: {processed}.", false);
            });
        }

        private void RunDeletePackContours()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().DeletePackContours();
                SetStatus($"Удалено контуров: {count}. Создайте контур заново.", false);
            });
        }

        private void RunDeletePackMarkers()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().DeletePackMarkers();
                SetStatus($"Удалено маркеров: {count}.", false);
            });
        }

        private void RunReattachMarkers()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().ReattachMarkers(
                    ParseDouble(_tabWidthMm.Text, 4),
                    ParseDouble(_tabHeightMm.Text, 12),
                    ParseDouble(_tabRadiusMm.Text, 2));
                SetStatus($"Переприкреплено маркеров: {count}.", false);
            });
        }

        private void RunAddPeelMarker()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().AddMarker(
                    ParseDouble(_tabWidthMm.Text, 4),
                    ParseDouble(_tabHeightMm.Text, 12),
                    ParseDouble(_tabRadiusMm.Text, 2));
                SetStatus($"Добавлено маркеров: {count}. Переместите и переприкрепите.", false);
            });
        }

        private void RunApplyPeelTabs()
        {
            RunSafe(() =>
            {
                int count = new PeelTabService().ApplyMarkers(
                    ParseDouble(_tabWidthMm.Text, 4),
                    ParseDouble(_tabHeightMm.Text, 12),
                    ParseDouble(_tabRadiusMm.Text, 2));
                SetStatus($"Применено язычков: {count}.", false);
            });
        }

        private void RefreshSelectionStatus()
        {
            RunSafe(() =>
            {
                dynamic app = CorelApp.Get();
                int count = (int)app.ActiveSelectionRange.Count;
                SetStatus(count > 0 ? $"Selected objects: {count}" : "No selection", false);
            });
        }

        private void RunSafe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Command failed.", ex);
                SetStatus(ex.Message, true);
            }
        }

        private void SetStatus(string text, bool isError)
        {
            _status.Text = text;
            _status.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(138, 31, 17))
                : new SolidColorBrush(Color.FromRgb(66, 81, 91));
        }

        private static Button Button(string text, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 11,
                MinHeight = 24,
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(5, 1, 5, 1)
            };
            button.Click += handler;
            return button;
        }

        private static TextBox AddRow(Panel root, string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            var text = new TextBlock
            {
                Text = label,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var input = new TextBox
            {
                Text = value,
                FontSize = 11,
                Height = 22,
                TextAlignment = TextAlignment.Right,
                Padding = new Thickness(3, 1, 3, 1)
            };
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);

            root.Children.Add(grid);
            return input;
        }

        private static System.Windows.Controls.RadioButton AddRadioButton(Panel root, string text, string groupName, bool isChecked)
        {
            var rb = new System.Windows.Controls.RadioButton
            {
                Content   = text,
                FontSize  = 11,
                GroupName = groupName,
                IsChecked = isChecked,
                Margin    = new Thickness(0, 0, 0, 2)
            };
            root.Children.Add(rb);
            return rb;
        }

        private static System.Windows.Controls.CheckBox AddCheckBox(Panel root, string text, bool isChecked)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content   = text,
                FontSize  = 11,
                IsChecked = isChecked,
                Margin    = new Thickness(0, 1, 0, 1)
            };
            root.Children.Add(cb);
            return cb;
        }

        private static void AddSmallLabel(Panel root, string text)
        {
            root.Children.Add(new TextBlock
            {
                Text       = text,
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
                Margin     = new Thickness(0, 3, 0, 2)
            });
        }

        private static void AddSectionTitle(Panel root, string text)
        {
            root.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        private static void AddSeparator(Panel root)
        {
            root.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(200, 206, 211)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 3, 0, 6)
            });
        }

        private static double ParseDouble(string text, double fallback)
        {
            text = (text ?? string.Empty).Replace(",", ".").Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : fallback;
        }

        private static int ParseInt(string text, int fallback)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }
    }
}
