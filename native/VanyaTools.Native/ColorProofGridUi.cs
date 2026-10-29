using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed partial class ColorProofTab
    {
        private StackPanel _gridPanel, _gridExtras, _gridAdvanced;
        private ComboBox _gridKind, _gridX, _gridY, _gridDifference;
        private TextBox _gridColumns, _gridRows, _gridCell, _gridTile, _gridGap, _gridStepX, _gridStepY, _gridExtraStep;
        private readonly TextBox[] _gridBase = new TextBox[4], _gridExtraLevels = new TextBox[4];
        private readonly TextBlock[] _gridBaseLabels = new TextBlock[4], _gridExtraLabels = new TextBlock[4];
        private readonly FrameworkElement[] _gridBaseFields = new FrameworkElement[4], _gridExtraRows = new FrameworkElement[4];
        private FrameworkElement _gridCellField, _gridTileField;
        private TextBlock _gridSummary;
        private Expander _gridSketchExpander;
        private Viewbox _gridSketch, _gridWindowSketch;
        private Window _gridPreviewWindow;
        private bool _gridUpdating;
        private readonly DispatcherTimer _gridDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };

        private FrameworkElement CreateGridControls()
        {
            _gridUpdating = true;
            _gridPanel = new StackPanel();
            _gridAdvanced = new StackPanel();
            _gridKind = Combo("Один цвет в клетке", "4 цвета · блок 2×2");
            _gridAdvanced.Children.Add(Row("Плашки", _gridKind));
            _gridDifference = Combo("Мягко", "Средне", "Контрастно", "Свой шаг");
            _gridDifference.SelectedIndex = 1;
            _gridDifference.ToolTip = "Шаги X, Y, автоуровней и серии: CMYK — 1 / 3 / 5; RGB — 3 / 8 / 16. Введённые вручную уровни сохраняются.";
            _gridAdvanced.Children.Add(CreateGridSeriesControls());

            var bases = new UniformGrid { Columns = 4 };
            for (int channel = 0; channel < 4; channel++)
            {
                _gridBase[channel] = Field(channel == 3 ? "0" : "50");
                _gridBaseLabels[channel] = GridCaption(new[] { "C", "M", "Y", "K" }[channel]);
                var field = new StackPanel { Margin = new Thickness(1, 0, 3, 3) };
                field.Children.Add(_gridBaseLabels[channel]); field.Children.Add(_gridBase[channel]);
                _gridBaseFields[channel] = field; bases.Children.Add(field);
                _gridBase[channel].TextChanged += (_, __) => QueueGridPreview();
            }
            _gridPanel.Children.Add(bases);

            _gridX = Combo(); _gridY = Combo();
            _gridAdvanced.Children.Add(GridPair("Горизонталь X", _gridX, "Вертикаль Y", _gridY));
            _gridStepX = Field("3"); _gridStepY = Field("3");
            _gridStepX.ToolTip = _gridStepY.ToolTip = "Целый шаг: процентные пункты CMYK или значения RGB 0–255.";
            _gridAdvanced.Children.Add(GridPair("Шаг X", _gridStepX, "Шаг Y", _gridStepY));

            var dimensions = new StackPanel();
            _gridColumns = Field("15"); _gridRows = Field("15");
            dimensions.Children.Add(GridPair("Столбцов", _gridColumns, "Строк", _gridRows));
            _gridCell = Field("20"); _gridTile = Field("30"); _gridGap = Field("1");
            _gridCellField = DockerLayout.LabeledField("Плашка, мм", _gridCell);
            _gridTileField = DockerLayout.LabeledField("Блок 2×2, мм", _gridTile);
            var sizes = new Grid { Margin = new Thickness(0, 2, 0, 3) };
            sizes.ColumnDefinitions.Add(new ColumnDefinition()); sizes.ColumnDefinitions.Add(new ColumnDefinition());
            var difference = DockerLayout.LabeledField("Различия", _gridDifference);
            difference.Margin = new Thickness(0, 0, 3, 0); sizes.Children.Add(difference);
            _gridCellField.Margin = _gridTileField.Margin = new Thickness(3, 0, 0, 0);
            Grid.SetColumn(_gridCellField, 1); Grid.SetColumn(_gridTileField, 1);
            sizes.Children.Add(_gridCellField); sizes.Children.Add(_gridTileField);
            _gridPanel.Children.Add(sizes);
            dimensions.Children.Add(Row("Между плашками, мм", _gridGap));
            _gridAdvanced.Children.Add(dimensions);

            _gridExtras = new StackPanel();
            _gridExtras.Children.Add(GridCaption("Каналы внутри блока"));
            _gridLevelsMode = Combo("Отклонения от эталона", "Абсолютные значения");
            _gridExtras.Children.Add(Row("Уровни", _gridLevelsMode));
            _gridExtraStep = Field("3"); _gridExtras.Children.Add(Row("Шаг автоуровней", _gridExtraStep));
            for (int channel = 0; channel < 4; channel++)
            {
                _gridExtraLabels[channel] = GridCaption("");
                _gridExtraLevels[channel] = Field("");
                _gridExtraLevels[channel].ToolTip = "Абсолютные значения через пробел, включая базовое. Пусто — подобрать автоматически.";
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
                row.Children.Add(_gridExtraLabels[channel]); Grid.SetColumn(_gridExtraLevels[channel], 1); row.Children.Add(_gridExtraLevels[channel]);
                _gridExtraRows[channel] = row; _gridExtras.Children.Add(row);
                _gridExtraLevels[channel].TextChanged += (_, __) => QueueGridPreview();
            }
            _gridExtras.Children.Add(Note("Пусто — авто. Отклонения прибавляются к эталону, например −2 0 +2 +4. В серии один канал меняется между листами, второй — в четырёх частях клетки."));
            _gridAdvanced.Children.Add(_gridExtras);
            _gridSummary = Note(""); _gridSummary.Margin = new Thickness(0, 3, 0, 5); _gridPanel.Children.Add(_gridSummary);
            _gridSketch = new Viewbox { Height = 180, Stretch = Stretch.Uniform,
                ToolTip = "Экранные цвета ориентировочные. Эталон выделен рамкой; окончательное сравнение — по печати." };
            var sketchContent = new StackPanel();
            sketchContent.Children.Add(CreateGridPageChoice(false));
            sketchContent.Children.Add(new Border { Background = Brushes.White, Child = _gridSketch, Padding = new Thickness(4) });
            sketchContent.Children.Add(Button("Крупнее…", (_, __) => ShowGridSketch()));
            _gridSketchExpander = new Expander { Header = "Предпросмотр", Content = sketchContent, IsExpanded = true,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 3) };
            _gridSketchExpander.Expanded += (_, __) => UpdateGridPreview();
            _gridPanel.Children.Add(_gridSketchExpander);
            foreach (var field in new[] { _gridColumns, _gridRows, _gridCell, _gridTile, _gridGap, _sheetWidth })
                field.TextChanged += (_, __) => QueueGridPreview();
            foreach (var field in new[] { _gridStepX, _gridStepY, _gridExtraStep })
                field.TextChanged += (_, __) => GridStepChanged();
            _gridDifference.SelectionChanged += (_, __) => {
                if (_gridUpdating) return;
                ApplyGridDifferencePreset(); QueueGridPreview();
            };
            _gridKind.SelectionChanged += (_, __) => { UpdateGridRoles(); QueueGridPreview(); };
            _gridLevelsMode.SelectionChanged += (_, __) => { UpdateGridRoles(); QueueGridPreview(); };
            _gridX.SelectionChanged += (_, __) => { RefreshGridSeriesChannels(); UpdateGridRoles(); QueueGridPreview(); };
            _gridY.SelectionChanged += (_, __) => { RefreshGridSeriesChannels(); UpdateGridRoles(); QueueGridPreview(); };
            _gridDelay.Tick += (_, __) => { _gridDelay.Stop(); UpdateGridPreview(); };
            Unloaded += (_, __) => { _gridDelay.Stop(); _gridPreviewWindow?.Close(); _pantoneWindow?.Close(); _colorProofNumberWindow?.Close(); };
            ApplyGridDifferencePreset();
            _gridUpdating = false;
            return _gridPanel;
        }

        private void RefreshGridModel()
        {
            if (_gridPanel == null) return;
            EnsureGridColorState();
            bool cmyk = _model.SelectedIndex == 0;
            int[] values = GridComponentsForModel(cmyk);
            _gridUpdating = true;
            try
            {
                string[] names = GridChannelNames(cmyk);
                int x = _gridX.SelectedIndex, y = _gridY.SelectedIndex;
                _gridX.Items.Clear(); _gridY.Items.Clear();
                foreach (string name in names) { _gridX.Items.Add(name); _gridY.Items.Add(name); }
                _gridX.SelectedIndex = x >= 0 && x < names.Length ? x : 0;
                _gridY.SelectedIndex = y >= 0 && y < names.Length && y != _gridX.SelectedIndex ? y : (_gridX.SelectedIndex == 0 ? 1 : 0);
                WriteGridBaseValues(values, cmyk);
                for (int channel = 0; channel < 4; channel++) _gridExtraLevels[channel].Text = "";
                _gridActiveModel = _model.SelectedIndex;
                ApplyGridDifferencePreset();
            }
            finally { _gridUpdating = false; }
            _gridSeriesPageIndex = -1;
            RefreshGridSeriesChannels(); UpdateGridRoles(); UpdateGridPreview();
        }

        private void UpdateGridRoles()
        {
            if (_gridPanel == null) return;
            bool compound = _gridKind.SelectedIndex == 1, cmyk = _model.SelectedIndex == 0;
            if (GridSeriesEnabled && !cmyk && compound)
            {
                bool updating = _gridUpdating; _gridUpdating = true;
                try { _gridKind.SelectedIndex = 0; compound = false; }
                finally { _gridUpdating = updating; }
            }
            string[] names = GridChannelNames(cmyk);
            int seriesChannel = GridSeriesEnabled ? GridSeriesChannel : -1;
            _gridCellField.Visibility = compound ? Visibility.Collapsed : Visibility.Visible;
            _gridTileField.Visibility = compound ? Visibility.Visible : Visibility.Collapsed;
            _gridExtras.Visibility = compound ? Visibility.Visible : Visibility.Collapsed;
            for (int channel = 0; channel < 4; channel++)
            {
                bool extra = channel < names.Length && channel != _gridX.SelectedIndex && channel != _gridY.SelectedIndex && channel != seriesChannel;
                _gridExtraRows[channel].Visibility = extra ? Visibility.Visible : Visibility.Collapsed;
                _gridExtraLabels[channel].Text = channel < names.Length ? (_gridLevelsMode.SelectedIndex == 0 ? "Δ" : "") + names[channel] +
                    (cmyk && !GridSeriesEnabled ? " · 1 / 2 / 4 уровня" : " · 4 уровня") : "";
                _gridExtraLevels[channel].ToolTip = _gridLevelsMode.SelectedIndex == 0
                    ? "Целые отклонения от эталона, включая 0. Например: -2 0 +2 +4. Пусто — авто."
                    : "Абсолютные значения, включая базовое. Пусто — авто.";
                if (channel < names.Length) _gridBase[channel].ToolTip = names[channel] + (channel == _gridX.SelectedIndex
                    ? " — базовое значение горизонтальной оси" : channel == _gridY.SelectedIndex
                    ? " — базовое значение вертикальной оси" : compound ? " — база дополнительного канала" : " — фиксированное значение");
            }
        }

        private void QueueGridPreview()
        {
            if (_gridUpdating || _gridPanel == null) return;
            _gridDelay.Stop(); _gridDelay.Start();
        }

        private void ApplyGridDifferencePreset()
        {
            int strength = _gridDifference?.SelectedIndex ?? -1;
            if (strength < 0 || strength > 2) return;
            int step = (_model.SelectedIndex == 0 ? new[] { 1, 3, 5 } : new[] { 3, 8, 16 })[strength];
            bool updating = _gridUpdating; _gridUpdating = true;
            try
            {
                foreach (TextBox field in new[] { _gridStepX, _gridStepY, _gridExtraStep, _gridSeriesStep })
                    if (field != null) field.Text = step.ToString(CultureInfo.InvariantCulture);
            }
            finally { _gridUpdating = updating; }
        }

        private void GridStepChanged()
        {
            if (_gridUpdating) return;
            bool updating = _gridUpdating; _gridUpdating = true;
            try { _gridDifference.SelectedIndex = 3; }
            finally { _gridUpdating = updating; }
            QueueGridPreview();
        }

        private ColorProofGridOptions ReadGridOptions()
        {
            bool cmyk = _model.SelectedIndex == 0, compound = _gridKind.SelectedIndex == 1;
            int limit = cmyk ? 100 : 255; string[] names = GridChannelNames(cmyk);
            int seriesChannel = GridSeriesEnabled ? GridSeriesChannel : -1;
            var values = new int[names.Length]; var levels = new int[names.Length][];
            for (int channel = 0; channel < names.Length; channel++)
            {
                values[channel] = GridInteger(_gridBase[channel], 0, limit, names[channel]);
                if (!compound || channel == _gridX.SelectedIndex || channel == _gridY.SelectedIndex) continue;
                if (channel == seriesChannel) { levels[channel] = new[] { values[channel] }; continue; }
                string[] parts = _gridExtraLevels[channel].Text.Replace('−', '-').Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (parts.Length != 1 && parts.Length != 2 && parts.Length != 4)
                    throw new InvalidOperationException(names[channel] + ": укажите 1, 2 или 4 уровня через пробел либо оставьте поле пустым.");
                levels[channel] = parts.Select(part => {
                    bool relative = _gridLevelsMode.SelectedIndex == 0;
                    if (!Int32.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < (relative ? -limit : 0) || value > limit)
                        throw new InvalidOperationException(names[channel] + ": укажите допустимые целые " + (relative ? "отклонения." : "значения от 0 до " + limit + "."));
                    int actual = relative ? values[channel] + value : value;
                    if (actual < 0 || actual > limit) throw new InvalidOperationException(names[channel] + ": после отклонения получается " + actual +
                        ", за пределами 0–" + limit + ". Измените отклонения.");
                    return actual;
                }).ToArray();
            }
            return new ColorProofGridOptions { Cmyk = cmyk, Compound = compound, BaseChannels = values,
                HorizontalChannel = _gridX.SelectedIndex, VerticalChannel = _gridY.SelectedIndex,
                Columns = GridInteger(_gridColumns, 1, 31, "Столбцов"), Rows = GridInteger(_gridRows, 1, 31, "Строк"),
                StepX = GridInteger(_gridStepX, 1, limit, "Шаг X"), StepY = GridInteger(_gridStepY, 1, limit, "Шаг Y"),
                ExtraStep = compound ? GridInteger(_gridExtraStep, 1, limit, "Шаг автоуровней") : 1,
                CellMm = compound ? 20 : Number(_gridCell, 5, 100, "Размер квадрата, мм"),
                TileMm = compound ? Number(_gridTile, 10, 200, "Размер блока, мм") : 30,
                GapMm = Number(_gridGap, 0, 10, "Между клетками, мм"),
                SheetWidthMm = Number(_sheetWidth, 100, 4000, "Ширина листа, мм"), ExtraLevels = levels };
        }

        private void UpdateGridPreview()
        {
            if (_gridUpdating || _gridSummary == null || _mode.SelectedIndex != 2) return;
            try
            {
                ColorProofGridOptions input = ReadGridOptions();
                _gridVisibleSeries = GridSeriesEnabled ? ColorProofGridSeriesPlan.Build(ReadGridSeriesOptions(input)) : null;
                if (_gridVisibleSeries != null)
                {
                    if (_gridSeriesPageIndex < 0 || _gridSeriesPageIndex >= _gridVisibleSeries.Pages.Count)
                        _gridSeriesPageIndex = _gridVisibleSeries.ReferencePageIndex;
                    _gridVisiblePlan = _gridVisibleSeries.Pages[_gridSeriesPageIndex];
                }
                else _gridVisiblePlan = ColorProofGridPlan.Build(input);
                ColorProofGridPlan plan = _gridVisiblePlan;
                var options = plan.Options; string[] names = GridChannelNames(options.Cmyk);
                int[] remaining = Enumerable.Range(0, plan.BaseChannels.Length)
                    .Where(channel => channel != options.HorizontalChannel && channel != options.VerticalChannel).ToArray();
                string channels = options.Compound
                    ? String.Join("; ", remaining.Select(channel => names[channel] + ": " + String.Join(" / ", plan.ExtraLevels[channel])))
                    : "Фиксированы " + String.Join(" ", remaining.Select(channel => names[channel] + ":" + plan.BaseChannels[channel]));
                int[] original = _gridVisibleSeries?.OriginalBaseChannels ?? plan.BaseChannels;
                _referenceLabel.Text = "Эталон " + (options.Cmyk ? "CMYK" : "RGB");
                _referenceLabel.ToolTip = String.Join(" ", original.Select((value, channel) => names[channel] + ":" + value));
                _preview.Background = new SolidColorBrush(GetDisplayColor(original, options.Cmyk));
                _preview.ToolTip = "ICC-предпросмотр использует профили текущего CDR и вывод sRGB. В печатных плашках сохраняются точные каналы.";
                _gridSummary.Text = (_gridVisibleSeries == null ? plan.Patches.Count + " плашек" :
                    _gridVisibleSeries.Pages.Count + " таблиц · " + _gridVisibleSeries.TotalPatchCount + " плашек") + " · " +
                    GridMm(plan.WidthMm) + " × " + GridMm(plan.HeightMm) + " мм";
                _gridSummary.ToolTip = options.Columns + " × " + options.Rows + ". " +
                    "X " + names[options.HorizontalChannel] + ": " + plan.AxisX.First() + "…" + plan.AxisX.Last() +
                    "; Y " + names[options.VerticalChannel] + ": " + plan.AxisY.First() + "…" + plan.AxisY.Last() + ".\n" + channels;
                _gridColorBaseTip = _gridSummary.ToolTip as string ?? "";
                DockerTheme.UseTheme(_gridSummary, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
                if (_createProof != null) { _createProof.IsEnabled = true; _createProof.Content = _gridVisibleSeries == null
                    ? "Создать таблицу" : "Создать " + _gridVisibleSeries.Pages.Count + " таблиц"; }
                SyncGridPageChoices(); DisplayGridPage(); BeginGridManagedPreview();
            }
            catch (Exception error)
            {
                _gridSummary.Text = error.GetBaseException().Message;
                DockerTheme.UseTheme(_gridSummary, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush);
                _gridVisiblePlan = null; _gridVisibleSeries = null; CancelGridColorPreview(true); SyncGridPageChoices();
                if (_createProof != null) _createProof.IsEnabled = false;
                _gridSketch.Child = null;
                if (_gridWindowSketch != null) _gridWindowSketch.Child = null;
                if (_gridWindowPageInfo != null) _gridWindowPageInfo.Text = _gridSummary.Text;
            }
        }

        private void CreateGridSheet()
        {
            ColorProofGridOptions options = ReadGridOptions();
            ColorProofGridSeriesPlan series = GridSeriesEnabled ? ColorProofGridSeriesPlan.Build(ReadGridSeriesOptions(options)) : null;
            ColorProofGridPlan plan = series == null ? ColorProofGridPlan.Build(options) : series.Pages[0];
            var renderer = new ColorProofGridService();
            int count = series == null ? renderer.Create(_reference, options) : renderer.CreateSeries(_reference, series.Options);
            try { Directory.CreateDirectory(Path.GetDirectoryName(WidthPath)); File.WriteAllText(WidthPath, options.SheetWidthMm.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception error) { Log.Error("Could not save color proof sheet width.", error); }
            _status("Создана " + renderer.LastCatalogId + ": " + count + " образцов · " + (options.Cmyk ? "CMYK" : "RGB") +
                ". Сохраните CDR — в нём записаны номера и точные составы цветов.", false);
        }

        private void ShowGridSketch()
        {
            try
            {
                UpdateGridPreview();
                if (_gridVisiblePlan == null) throw new InvalidOperationException(_gridSummary.Text);
                if (_gridPreviewWindow != null) { _gridPreviewWindow.Activate(); return; }
                var window = new Window { Title = "Цветопроба · Vanya Tools", Width = Math.Min(1000, SystemParameters.WorkArea.Width - 50),
                    Height = Math.Min(850, SystemParameters.WorkArea.Height - 50), MinWidth = 420, MinHeight = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner };
                DockerTheme.Apply(window);
                var root = new DockPanel { Margin = new Thickness(10) }; window.Content = root;
                var note = Note("Эталон отмечен рамкой. Экранные цвета ориентировочные.");
                DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
                FrameworkElement pages = CreateGridPageChoice(true); DockPanel.SetDock(pages, Dock.Top); root.Children.Add(pages);
                _gridWindowPageInfo = Note(""); DockPanel.SetDock(_gridWindowPageInfo, Dock.Top); root.Children.Add(_gridWindowPageInfo);
                _gridWindowSketch = new Viewbox { Stretch = Stretch.Uniform };
                root.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(8), Child = _gridWindowSketch });
                Window owner = Window.GetWindow(this);
                if (owner != null) window.Owner = owner;
                else if (PresentationSource.FromVisual(this) is HwndSource host) new WindowInteropHelper(window).Owner = host.Handle;
                _gridPreviewWindow = window;
                window.Closed += (_, __) => { _gridPreviewWindow = null; _gridWindowSketch = null; _gridWindowPageChoice = null; _gridWindowPageInfo = null; BeginGridManagedPreview(); };
                SyncGridPageChoices(); DisplayGridPage();
                window.Show(); BeginGridManagedPreview();
            }
            catch (Exception error) { Fail("Could not preview color proof grid.", error); }
        }

        private static Canvas GridSketch(ColorProofGridPlan plan) => GridSketchWithReference(plan, true);

        private static Canvas GridSketchWithReference(ColorProofGridPlan plan, bool showReference, int firstNumber = 1)
        {
            const double left = 8, top = 14;
            var canvas = new Canvas { Width = plan.RequiredWidthMm,
                Height = ColorProofGridLegendPlan.ContentHeight(plan) + 30, Background = Brushes.White };
            var options = plan.Options; string[] names = GridChannelNames(options.Cmyk);
            GridSketchText(canvas, left, 0, "Образцы №" + firstNumber + "–" + (firstNumber + plan.Patches.Count - 1), 5);
            int number = firstNumber;
            foreach (ColorProofGridPatch patch in plan.Patches.OrderBy(item => item.Ymm).ThenBy(item => item.Xmm))
            {
                var rectangle = new System.Windows.Shapes.Rectangle { Width = patch.WidthMm, Height = patch.HeightMm,
                    Fill = new SolidColorBrush(GridScreenColor(patch.Values, options.Cmyk)), Tag = new GridPatchColor(patch.Values, options.Cmyk),
                    ToolTip = "№" + number + " · " + (options.Cmyk ? "CMYK · " : "RGB · ") +
                        String.Join(" ", patch.Values.Select((value, channel) => names[channel] + ":" + value)) };
                RenderOptions.SetEdgeMode(rectangle, EdgeMode.Aliased);
                Canvas.SetLeft(rectangle, left + patch.Xmm); Canvas.SetTop(rectangle, top + patch.Ymm); canvas.Children.Add(rectangle);
                var label = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 2.3,
                    FontFamily = new FontFamily("Arial"), LineHeight = 2.8, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Height = ColorProofGridPlan.NumberBandMm - .4, ClipToBounds = true,
                    Width = patch.WidthMm, TextAlignment = TextAlignment.Center, Foreground = Brushes.Black, Tag = "PatchNumber" };
                Canvas.SetLeft(label, left + patch.Xmm); Canvas.SetTop(label, top + patch.Ymm + patch.HeightMm + .4);
                canvas.Children.Add(label); number++;
            }
            if (showReference)
            {
                ColorProofGridPatch reference = plan.Patches.First(patch => patch.Row == plan.ReferenceRow &&
                    patch.Column == plan.ReferenceColumn && patch.Quadrant == plan.ReferenceQuadrant);
                foreach (ColorProofReferenceFrameRect part in ColorProofReferenceFrame.Build(reference.Xmm, reference.Ymm,
                    reference.WidthMm, reference.HeightMm))
                {
                    var marker = new System.Windows.Shapes.Rectangle { Width = part.WidthMm, Height = part.HeightMm,
                        Fill = part.White ? Brushes.White : Brushes.Black, IsHitTestVisible = false, Tag = "ReferenceFrame" };
                    RenderOptions.SetEdgeMode(marker, EdgeMode.Aliased);
                    Canvas.SetLeft(marker, left + part.Xmm); Canvas.SetTop(marker, top + part.Ymm); canvas.Children.Add(marker);
                }
                // Emphasize the selection in a small on-screen overview. The
                // printed frame keeps its actual millimetre geometry above.
                double accent = Math.Min(2, Math.Min(reference.WidthMm, reference.HeightMm) / 8);
                foreach (bool white in new[] { true, false })
                {
                    var marker = new System.Windows.Shapes.Rectangle { Width = reference.WidthMm - 2 * accent,
                        Height = reference.HeightMm - 2 * accent, Fill = Brushes.Transparent,
                        Stroke = white ? Brushes.White : Brushes.Black, StrokeThickness = white ? 2 * accent : accent,
                        IsHitTestVisible = false, SnapsToDevicePixels = true, Tag = "ReferenceFrameAccent" };
                    Canvas.SetLeft(marker, left + reference.Xmm + accent); Canvas.SetTop(marker, top + reference.Ymm + accent);
                    canvas.Children.Add(marker);
                }
            }
            ColorProofGridLegendPlan legend = ColorProofGridLegendPlan.Build(plan, firstNumber);
            double legendLeft = left + plan.WidthMm + ColorProofGridLegendPlan.GapMm;
            GridSketchText(canvas, legendLeft, top, "Рецептуры " + (options.Cmyk ? "CMYK" : "RGB"), 8 * 25.4 / 72);
            foreach (ColorProofGridLegendPlan.Entry entry in legend.Entries)
                GridSketchText(canvas, legendLeft + entry.Xmm, top + entry.Ymm, entry.Text,
                    ColorProofGridLegendPlan.FontPoints * 25.4 / 72);
            var footer = new TextBlock { Text = "Выберите цвет и сообщите номер под образцом.", FontSize = 3.5, Foreground = Brushes.Black,
                Width = Math.Max(60, plan.WidthMm), TextWrapping = TextWrapping.Wrap };
            Canvas.SetLeft(footer, left); Canvas.SetTop(footer, top + ColorProofGridLegendPlan.ContentHeight(plan) + 4); canvas.Children.Add(footer);
            return canvas;
        }

        private static Color GridScreenColor(int[] values, bool cmyk)
        {
            if (!cmyk) return Color.FromRgb((byte)values[0], (byte)values[1], (byte)values[2]);
            double black = 1 - values[3] / 100.0;
            return Color.FromRgb((byte)Math.Round(255 * (1 - values[0] / 100.0) * black),
                (byte)Math.Round(255 * (1 - values[1] / 100.0) * black), (byte)Math.Round(255 * (1 - values[2] / 100.0) * black));
        }
        private static void GridSketchText(Canvas canvas, double x, double y, string value, double size)
        { var text = new TextBlock { Text = value, FontSize = size, Foreground = Brushes.Black }; Canvas.SetLeft(text, x); Canvas.SetTop(text, y); canvas.Children.Add(text); }
        private static string[] GridChannelNames(bool cmyk) => cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" };
        private static string GridMm(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
        private static int GridInteger(TextBox field, int min, int max, string label)
        { if (Int32.TryParse(field.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max) return value;
            throw new InvalidOperationException(label + ": целое число от " + min + " до " + max + "."); }
        private static TextBlock GridCaption(string text) => new TextBlock { Text = text, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        private static FrameworkElement GridPair(string first, FrameworkElement firstField, string second, FrameworkElement secondField)
        { var row = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 3) };
            var a = DockerLayout.LabeledField(first, firstField); a.Margin = new Thickness(0, 0, 3, 0);
            var b = DockerLayout.LabeledField(second, secondField); b.Margin = new Thickness(3, 0, 0, 0); row.Children.Add(a); row.Children.Add(b); return row; }
    }
}
