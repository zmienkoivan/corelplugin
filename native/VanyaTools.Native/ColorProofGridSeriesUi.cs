using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed partial class ColorProofTab
    {
        private CheckBox _gridSeriesEnabled;
        private ComboBox _gridSeriesChannel, _gridLevelsMode, _gridPageChoice, _gridWindowPageChoice;
        private TextBox _gridSeriesCount, _gridSeriesStep, _gridSeriesOffsets;
        private FrameworkElement _gridSeriesSettings;
        private TextBlock _gridWindowPageInfo;
        private ColorProofGridSeriesPlan _gridVisibleSeries;
        private ColorProofGridPlan _gridVisiblePlan;
        private int _gridSeriesPageIndex = -1;
        private bool _gridChoosingPage;

        private bool GridSeriesEnabled => _gridSeriesEnabled?.IsChecked == true;
        private int GridSeriesChannel => (_gridSeriesChannel?.SelectedItem as ComboBoxItem)?.Tag is int channel ? channel : -1;

        private FrameworkElement CreateGridSeriesControls()
        {
            var panel = new StackPanel();
            _gridSeriesEnabled = new CheckBox { Content = "Несколько таблиц", Margin = new Thickness(0, 2, 0, 4) };
            panel.Children.Add(_gridSeriesEnabled);
            var settings = new StackPanel();
            _gridSeriesChannel = Combo();
            settings.Children.Add(Row("Между листами", _gridSeriesChannel));
            _gridSeriesCount = Field("5"); _gridSeriesStep = Field("3");
            settings.Children.Add(GridPair("Таблиц · 1–9", _gridSeriesCount, "Шаг канала", _gridSeriesStep));
            _gridSeriesOffsets = Field("");
            _gridSeriesOffsets.ToolTip = "Отклонения в единицах канала, включая 0: например -4 -2 0 +2 +4. Пусто — по числу таблиц и шагу.";
            var advanced = new StackPanel();
            advanced.Children.Add(_gridSeriesOffsets);
            advanced.Children.Add(Note("Включите 0. Пусто — авто вокруг эталона. На границе диапазон сдвигается без повторов цветов."));
            settings.Children.Add(new Expander { Header = "Свои отклонения между листами", Content = advanced,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 4) });
            _gridSeriesSettings = settings; settings.Visibility = Visibility.Collapsed; panel.Children.Add(settings);
            _gridSeriesEnabled.Checked += (_, __) => GridSeriesChanged();
            _gridSeriesEnabled.Unchecked += (_, __) => GridSeriesChanged();
            _gridSeriesChannel.SelectionChanged += (_, __) => {
                if (_gridUpdating) return;
                _gridSeriesPageIndex = -1; UpdateGridRoles(); QueueGridPreview();
            };
            _gridSeriesCount.TextChanged += (_, __) => QueueGridPreview();
            _gridSeriesStep.TextChanged += (_, __) => GridStepChanged();
            _gridSeriesOffsets.TextChanged += (_, __) => {
                bool automatic = String.IsNullOrWhiteSpace(_gridSeriesOffsets.Text);
                _gridSeriesCount.IsEnabled = _gridSeriesStep.IsEnabled = automatic;
                QueueGridPreview();
            };
            return panel;
        }

        private void GridSeriesChanged()
        {
            if (_gridUpdating) return;
            _gridSeriesSettings.Visibility = GridSeriesEnabled ? Visibility.Visible : Visibility.Collapsed;
            _gridSeriesPageIndex = -1;
            RefreshGridSeriesChannels(); UpdateGridRoles(); QueueGridPreview();
        }

        private void RefreshGridSeriesChannels()
        {
            if (_gridUpdating || _gridSeriesChannel == null || _gridX == null || _gridY == null) return;
            int previous = GridSeriesChannel;
            _gridUpdating = true;
            try
            {
                string[] names = GridChannelNames(_model.SelectedIndex == 0);
                _gridSeriesChannel.Items.Clear();
                foreach (int channel in Enumerable.Range(0, names.Length)
                    .Where(channel => channel != _gridX.SelectedIndex && channel != _gridY.SelectedIndex))
                    _gridSeriesChannel.Items.Add(new ComboBoxItem { Content = names[channel], Tag = channel });
                _gridSeriesChannel.SelectedItem = _gridSeriesChannel.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == previous)
                    ?? _gridSeriesChannel.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == 2)
                    ?? _gridSeriesChannel.Items.Cast<ComboBoxItem>().FirstOrDefault();
            }
            finally { _gridUpdating = false; }
        }

        private void ApplySwatchPreset()
        {
            _gridUpdating = true;
            try
            {
                _mode.SelectedIndex = 2;
                _gridX.SelectedIndex = 1; _gridY.SelectedIndex = 0;
                _gridKind.SelectedIndex = _model.SelectedIndex == 0 ? 1 : 0;
                _gridSeriesEnabled.IsChecked = true;
                _gridSeriesCount.Text = "5"; _gridSeriesOffsets.Text = "";
                _gridLevelsMode.SelectedIndex = 0;
                foreach (TextBox field in _gridExtraLevels) field.Text = "";
                _gridSeriesPageIndex = -1;
            }
            finally { _gridUpdating = false; }
            _gridSeriesSettings.Visibility = Visibility.Visible;
            RefreshGridSeriesChannels();
            _gridUpdating = true;
            try { _gridSeriesChannel.SelectedItem = _gridSeriesChannel.Items.Cast<ComboBoxItem>().First(item => (int)item.Tag == 2); }
            finally { _gridUpdating = false; }
            UpdateGridRoles(); UpdateForm(); UpdateGridPreview();
        }

        private ColorProofGridSeriesOptions ReadGridSeriesOptions(ColorProofGridOptions grid)
        {
            int limit = grid.Cmyk ? 100 : 255;
            string[] parts = _gridSeriesOffsets.Text.Replace('−', '-').Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int[] offsets = null;
            if (parts.Length > 0)
            {
                if (parts.Length > ColorProofGridSeriesPlan.MaxPages)
                    throw new InvalidOperationException("Для серии задайте не больше 9 отклонений.");
                offsets = parts.Select(part => {
                    if (!Int32.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < -limit || value > limit)
                        throw new InvalidOperationException("Отклонения между листами: целые числа от −" + limit + " до +" + limit + ", включая 0.");
                    return value;
                }).ToArray();
            }
            return new ColorProofGridSeriesOptions { Grid = grid, Channel = GridSeriesChannel, Offsets = offsets,
                Count = offsets == null ? GridInteger(_gridSeriesCount, 1, 9, "Таблиц") : offsets.Length,
                Step = offsets == null ? GridInteger(_gridSeriesStep, 1, limit, "Шаг канала серии") : 1 };
        }

        private FrameworkElement CreateGridPageChoice(bool window)
        {
            var choice = new ComboBox { Margin = new Thickness(0, 2, 0, 5), MinWidth = 120, Visibility = Visibility.Collapsed };
            if (window) _gridWindowPageChoice = choice; else _gridPageChoice = choice;
            choice.SelectionChanged += (_, __) => {
                if (_gridChoosingPage || choice.SelectedIndex < 0 || _gridVisibleSeries == null) return;
                _gridSeriesPageIndex = choice.SelectedIndex;
                _gridVisiblePlan = _gridVisibleSeries.Pages[_gridSeriesPageIndex];
                UpdateGridPreview();
            };
            return choice;
        }

        private void SyncGridPageChoices()
        {
            _gridChoosingPage = true;
            try
            {
                foreach (ComboBox choice in new[] { _gridPageChoice, _gridWindowPageChoice })
                {
                    if (choice == null) continue;
                    choice.Items.Clear(); choice.Visibility = _gridVisibleSeries == null ? Visibility.Collapsed : Visibility.Visible;
                    if (_gridVisibleSeries == null) continue;
                    string name = GridChannelNames(_gridVisibleSeries.Options.Grid.Cmyk)[_gridVisibleSeries.Options.Channel];
                    for (int index = 0; index < _gridVisibleSeries.Pages.Count; index++)
                        choice.Items.Add("Таблица " + (index + 1) + " / " + _gridVisibleSeries.Pages.Count + " · " + name + ":" +
                            _gridVisibleSeries.ChannelValues[index] + " · Δ" + GridDelta(_gridVisibleSeries.Offsets[index]) +
                            (index == _gridVisibleSeries.ReferencePageIndex ? " · эталон" : ""));
                    choice.SelectedIndex = _gridSeriesPageIndex;
                }
            }
            finally { _gridChoosingPage = false; }
        }

        private void DisplayGridPage()
        {
            if (_gridVisiblePlan == null) return;
            if (_gridSketchExpander.IsExpanded) _gridSketch.Child = GridContextSketch(_gridVisiblePlan);
            if (_gridWindowSketch != null) _gridWindowSketch.Child = GridContextSketch(_gridVisiblePlan);
            if (_gridWindowPageInfo != null) _gridWindowPageInfo.Text = GridPageDescription();
        }

        private string GridPageDescription()
        {
            if (_gridVisiblePlan == null) return "";
            ColorProofGridPlan plan = _gridVisiblePlan;
            string location = "R" + (plan.ReferenceRow + 1) + " C" + (plan.ReferenceColumn + 1) +
                (plan.Options.Compound ? " Q" + (plan.ReferenceQuadrant + 1) : "");
            if (_gridVisibleSeries == null) return "Исходный эталон в сетке: " + location + ".";
            return _gridSeriesPageIndex == _gridVisibleSeries.ReferencePageIndex ? "Исходный эталон в сетке: " + location + "."
                : "На этом листе изменён канал серии. Исходный эталон показан отдельной плашкой для сравнения.";
        }

        private Canvas GridContextSketch(ColorProofGridPlan plan)
        {
            bool containsOriginal = _gridVisibleSeries == null || _gridSeriesPageIndex == _gridVisibleSeries.ReferencePageIndex;
            Canvas grid = GridSketchWithReference(plan, containsOriginal);
            var canvas = new Canvas { Width = grid.Width, Height = grid.Height + 24, Background = Brushes.White };
            int[] original = _gridVisibleSeries?.OriginalBaseChannels ?? plan.BaseChannels;
            string[] names = GridChannelNames(plan.Options.Cmyk);
            var text = new TextBlock { Text = "Эталон · " + String.Join(" ", original.Select((value, channel) => names[channel] + ":" + value)),
                FontSize = 6, Foreground = Brushes.Black, Width = Math.Max(20, grid.Width - 24), TextWrapping = TextWrapping.Wrap };
            canvas.Children.Add(text);
            var patch = new System.Windows.Shapes.Rectangle { Width = 12, Height = 12,
                Fill = new SolidColorBrush(GridScreenColor(original, plan.Options.Cmyk)) };
            Canvas.SetLeft(patch, grid.Width - 12); canvas.Children.Add(patch);
            var description = new TextBlock { Text = GridPageDescription(), FontSize = 4, Foreground = Brushes.Black,
                Width = grid.Width, TextWrapping = TextWrapping.Wrap };
            Canvas.SetTop(description, 13); canvas.Children.Add(description);
            Canvas.SetTop(grid, 24); canvas.Children.Add(grid);
            return canvas;
        }

        private static string GridDelta(int value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);
    }
}
