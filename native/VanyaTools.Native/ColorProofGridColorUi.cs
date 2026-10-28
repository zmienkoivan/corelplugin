using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed partial class ColorProofTab
    {
        // QA injects managed delegates before Loaded. Static GridSketch never uses native conversion.
        internal Func<bool, int[], bool, int[]> GridComponentConverter = null;
        internal Func<ColorProofReference, bool, int[]> GridReferenceComponents = null;
        internal Func<bool, IReadOnlyList<int[]>, CancellationToken, Action<int, int>, Task<ColorProofPreviewResult>> GridPreviewConverter = null;
        internal bool GridNativePreviewEnabled = true;
        private bool _gridColorHooks, _gridChangingModel, _gridWritingBase;
        private int _gridActiveModel = -1;
        private bool _gridCanonicalCmyk;
        private int[] _gridCanonicalBase;
        private readonly Dictionary<bool, int[]> _gridModelComponents = new Dictionary<bool, int[]>();
        private readonly Dictionary<string, int[]> _gridDisplayColors = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private CancellationTokenSource _gridColorCancellation;
        private int _gridColorVersion;
        private string _gridColorContextDescription = "", _gridColorBaseTip = "";

        private sealed class GridPatchColor
        {
            internal readonly bool Cmyk;
            internal readonly int[] Values;
            internal GridPatchColor(int[] values, bool cmyk) { Values = (int[])values.Clone(); Cmyk = cmyk; }
        }

        private void EnsureGridColorState()
        {
            if (_gridCanonicalBase == null)
            {
                bool cmyk = _model.SelectedIndex == 0;
                SetGridCanonical(cmyk, ReadGridBaseFields(cmyk));
                _gridActiveModel = _model.SelectedIndex;
            }
            if (_gridColorHooks) return;
            _gridColorHooks = true;
            foreach (TextBox field in _gridBase) field.TextChanged += (_, __) => GridBaseEdited();
            Loaded += (_, __) => { if (_mode.SelectedIndex == 2) UpdateGridPreview(); else BeginGridManagedPreview(); };
            _mode.SelectionChanged += (_, __) => { if (!_gridUpdating && !_gridChangingModel) { if (_mode.SelectedIndex == 2) UpdateGridPreview(); else BeginGridManagedPreview(); } };
            _gridSketchExpander.Collapsed += (_, __) => BeginGridManagedPreview();
            Unloaded += (_, __) => CancelGridColorPreview(true);
            IsVisibleChanged += (_, __) => {
                if (!IsVisible) CancelGridColorPreview(true);
                else if (IsLoaded) { if (_mode.SelectedIndex == 2) UpdateGridPreview(); else BeginGridManagedPreview(); }
            };
        }
        private static int[] CheckedGridComponents(int[] values, bool cmyk)
        {
            int limit = cmyk ? 100 : 255, channels = cmyk ? 4 : 3;
            if (values == null || values.Length != channels || values.Any(value => value < 0 || value > limit))
                throw new InvalidOperationException("Некорректные каналы эталона " + (cmyk ? "CMYK." : "RGB."));
            return (int[])values.Clone();
        }
        private int[] ReadGridBaseFields(bool cmyk)
        {
            string[] names = GridChannelNames(cmyk);
            return Enumerable.Range(0, names.Length).Select(channel => GridInteger(_gridBase[channel], 0, cmyk ? 100 : 255, names[channel])).ToArray();
        }
        private void SetGridCanonical(bool cmyk, int[] values)
        {
            _gridCanonicalBase = CheckedGridComponents(values, cmyk); _gridCanonicalCmyk = cmyk;
            _gridModelComponents.Clear(); _gridModelComponents[cmyk] = (int[])_gridCanonicalBase.Clone();
        }
        private void GridBaseEdited()
        {
            if (_gridUpdating || _gridWritingBase || _gridChangingModel || _gridActiveModel < 0) return;
            // Even an incomplete edit invalidates the selected native color as the source of truth.
            _reference = null;
            try { SetGridCanonical(_gridActiveModel == 0, ReadGridBaseFields(_gridActiveModel == 0)); }
            catch (InvalidOperationException) { }
            CancelGridColorPreview(true); RefreshGridDisplayColors();
            UpdateGridReferenceLabel();
        }
        private int[] GridComponentsForModel(bool cmyk)
        {
            EnsureGridColorState();
            if (_gridModelComponents.TryGetValue(cmyk, out int[] cached)) return (int[])cached.Clone();
            int[] values = _reference != null
                ? (GridReferenceComponents != null ? GridReferenceComponents(_reference, cmyk) : _service.Components(_reference, cmyk))
                : (GridComponentConverter != null ? GridComponentConverter(_gridCanonicalCmyk, (int[])_gridCanonicalBase.Clone(), cmyk)
                    : new ColorProofPreviewService().ConvertComponents(_gridCanonicalCmyk, _gridCanonicalBase, cmyk));
            values = CheckedGridComponents(values, cmyk);
            _gridModelComponents[cmyk] = values;
            return (int[])values.Clone();
        }
        private int[] CurrentGridBaseComponents()
        { return GridComponentsForModel(_model.SelectedIndex == 0); }

        private void HandleGridModelChanged()
        {
            if (_gridChangingModel || _gridUpdating || _gridPanel == null) return;
            EnsureGridColorState();
            int previous = _gridActiveModel, target = _model.SelectedIndex;
            if (target < 0 || target > 1 || target == previous) return;
            int[] previousValues = null;
            try
            {
                previousValues = ReadGridBaseFields(previous == 0);
                // A converted view is refreshed on every explicit model change: the CDR/ICC may differ.
                // Only the canonical numeric recipe remains exact and reusable across contexts.
                if ((target == 0) != _gridCanonicalCmyk) _gridModelComponents.Remove(target == 0);
                int[] values = GridComponentsForModel(target == 0);
                _gridActiveModel = target;
                _gridChangingModel = true;
                try { UpdateChannels(); DefaultOffsets(); RefreshGridModel(); }
                finally { _gridChangingModel = false; }
                if (_mode.SelectedIndex == 2) QueueGridPreview(); else BeginGridManagedPreview();
            }
            catch (Exception error)
            {
                _gridChangingModel = true;
                try
                {
                    _model.SelectedIndex = previous; _gridActiveModel = previous;
                    if (previousValues != null) _gridModelComponents[previous == 0] = (int[])previousValues.Clone();
                    UpdateChannels();
                    if (previousValues != null) WriteGridBaseValues(previousValues, previous == 0);
                }
                finally { _gridChangingModel = false; }
                _status("Модель не изменена: " + error.GetBaseException().Message, true);
            }
        }
        private void AdoptGridReference(ColorProofReference reference)
        {
            if (reference == null) throw new InvalidOperationException("Выберите эталонный цвет.");
            bool cmyk = _model.SelectedIndex == 0;
            int[] values = CheckedGridComponents(GridReferenceComponents != null ? GridReferenceComponents(reference, cmyk)
                : _service.Components(reference, cmyk), cmyk);
            EnsureGridColorState();
            _reference = reference; SetGridCanonical(cmyk, values); _gridActiveModel = _model.SelectedIndex;
            RefreshGridModel(); UpdateGridReferenceLabel();
            if (_mode.SelectedIndex != 2) BeginGridManagedPreview();
        }
        // Catalog restore calls this first, then restores axes/steps/series and calls UpdateGridPreview.
        // No COM: the restored numeric recipe becomes the exact canonical reference.
        internal void RestoreGridColorBase(bool cmyk, int[] exactValues)
        {
            int[] values = CheckedGridComponents(exactValues, cmyk);
            EnsureGridColorState();
            _gridChangingModel = true;
            try
            {
                _reference = null; SetGridCanonical(cmyk, values);
                _model.SelectedIndex = cmyk ? 0 : 1; _gridActiveModel = _model.SelectedIndex;
                UpdateChannels(); DefaultOffsets(); RefreshGridModel();
            }
            finally { _gridChangingModel = false; }
            if (_mode.SelectedIndex == 2) QueueGridPreview(); else BeginGridManagedPreview();
        }
        private void WriteGridBaseValues(int[] values, bool cmyk)
        {
            bool updating = _gridUpdating; _gridUpdating = true; _gridWritingBase = true;
            try
            {
                string[] names = GridChannelNames(cmyk);
                for (int channel = 0; channel < 4; channel++)
                {
                    _gridBaseFields[channel].Visibility = channel < names.Length ? Visibility.Visible : Visibility.Collapsed;
                    _gridBaseLabels[channel].Text = channel < names.Length ? names[channel] : "";
                    _gridBase[channel].Text = channel < values.Length ? values[channel].ToString(CultureInfo.InvariantCulture) : "";
                }
            }
            finally { _gridWritingBase = false; _gridUpdating = updating; }
        }
        private void UpdateGridReferenceLabel()
        {
            if (_referenceLabel == null || _gridCanonicalBase == null) return;
            bool cmyk = _model.SelectedIndex == 0;
            if (!_gridModelComponents.TryGetValue(cmyk, out int[] values)) return;
            string[] names = GridChannelNames(cmyk);
            _referenceLabel.Text = (_reference?.IsSpot == true ? "Плашечный · " : "") + "Эталон " + (cmyk ? "CMYK" : "RGB");
            _referenceLabel.ToolTip = String.Join(" ", values.Select((value, channel) => names[channel] + ":" + value));
            _preview.Background = new SolidColorBrush(GetDisplayColor(values, cmyk));
        }

        // Pure lookup only. Live UI shows a neutral placeholder until ICC conversion succeeds.
        internal Color GetDisplayColor(int[] values, bool cmyk)
        {
            if (!GridNativePreviewEnabled && GridPreviewConverter == null) return GridScreenColor(values, cmyk);
            return _gridDisplayColors.TryGetValue(GridColorKey(values, cmyk), out int[] rgb)
                ? Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]) : Color.FromRgb(218, 222, 225);
        }
        private static string GridColorKey(int[] values, bool cmyk) => (cmyk ? "CMYK:" : "RGB:") + String.Join(",", values);
        private void CancelGridColorPreview(bool clear)
        {
            ++_gridColorVersion; _gridColorCancellation?.Cancel();
            if (clear) { _gridDisplayColors.Clear(); _gridColorContextDescription = ""; }
        }
        private void ApplyGridDisplayColors(DependencyObject element)
        {
            if (element == null) return;
            if (element is System.Windows.Shapes.Rectangle rectangle && rectangle.Tag is GridPatchColor tagged)
                rectangle.Fill = new SolidColorBrush(GetDisplayColor(tagged.Values, tagged.Cmyk));
            if (element is Panel panel) foreach (UIElement child in panel.Children) ApplyGridDisplayColors(child);
            else if (element is Decorator decorator) ApplyGridDisplayColors(decorator.Child);
        }
        private Canvas CreateManagedGridSketch(ColorProofGridPlan plan, bool showReference, int firstNumber = 1)
        {
            Canvas canvas = GridSketchWithReference(plan, showReference, firstNumber);
            ApplyGridDisplayColors(canvas);
            return canvas;
        }
        private void RefreshGridDisplayColors()
        {
            bool cmyk = _model.SelectedIndex == 0;
            int[] original = _mode.SelectedIndex == 2 && _gridVisiblePlan != null
                ? (_gridVisibleSeries?.OriginalBaseChannels ?? _gridVisiblePlan.BaseChannels)
                : (_gridModelComponents.TryGetValue(cmyk, out int[] cached) ? cached : null);
            if (original != null) _preview.Background = new SolidColorBrush(GetDisplayColor(original, cmyk));
            ApplyGridDisplayColors(_gridSketch?.Child); ApplyGridDisplayColors(_gridWindowSketch?.Child);
        }
        private void BeginGridManagedPreview()
        {
            CancelGridColorPreview(true); RefreshGridDisplayColors();
            if (_gridChangingModel || !IsLoaded || !IsVisible) return;
            if (!GridNativePreviewEnabled && GridPreviewConverter == null) return;
            bool grid = _mode.SelectedIndex == 2 && _gridVisiblePlan != null;
            bool cmyk = _model.SelectedIndex == 0;
            int[] original = grid ? (_gridVisibleSeries?.OriginalBaseChannels ?? _gridVisiblePlan.BaseChannels)
                : (_gridModelComponents.TryGetValue(cmyk, out int[] cached) ? cached : null);
            if (original == null) return;
            var recipes = new List<int[]> { (int[])original.Clone() };
            if (grid && (_gridSketchExpander.IsExpanded || _gridWindowSketch != null))
                recipes.AddRange(_gridVisiblePlan.Patches.Select(patch => (int[])patch.Values.Clone()));
            var unique = recipes.GroupBy(values => GridColorKey(values, cmyk)).Select(group => group.First()).ToList();
            int version = _gridColorVersion;
            var cancellation = new CancellationTokenSource(); _gridColorCancellation = cancellation;
            _ = RunGridManagedPreview(cmyk, unique, version, cancellation);
        }
        private async Task RunGridManagedPreview(bool cmyk, IReadOnlyList<int[]> recipes,
            int version, CancellationTokenSource cancellation)
        {
            string baseTip = _mode.SelectedIndex == 2 ? _gridColorBaseTip : "";
            Action<int, int> progress = (done, count) => {
                Action show = () => { if (version == _gridColorVersion) SetGridColorTip(baseTip, "ICC: " + done + " / " + count); };
                if (Dispatcher.CheckAccess()) show(); else Dispatcher.BeginInvoke(show);
            };
            try
            {
                progress(0, recipes.Count);
                Task<ColorProofPreviewResult> task = GridPreviewConverter != null
                    ? GridPreviewConverter(cmyk, recipes, cancellation.Token, progress)
                    : new ColorProofPreviewService().ConvertBatchAsync(cmyk, recipes, cancellation.Token, progress);
                ColorProofPreviewResult result = await task.ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (version != _gridColorVersion || cancellation.IsCancellationRequested || !IsLoaded || !IsVisible) return;
                    if (result?.Rgb == null || result.Rgb.Count != recipes.Count)
                        throw new InvalidOperationException("Преобразователь вернул неверное число экранных цветов.");
                    for (int index = 0; index < recipes.Count; index++)
                        _gridDisplayColors[GridColorKey(recipes[index], cmyk)] = CheckedGridComponents(result.Rgb[index], false);
                    _gridColorContextDescription = result.ContextDescription ?? "";
                    if (_mode.SelectedIndex == 2) DisplayGridPage(); RefreshGridDisplayColors();
                    SetGridColorTip(baseTip, "ICC CDR → sRGB. " + _gridColorContextDescription);
                }).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                await Dispatcher.InvokeAsync(() => {
                    if (version != _gridColorVersion || cancellation.IsCancellationRequested) return;
                    _gridDisplayColors.Clear(); RefreshGridDisplayColors();
                    _gridSummary.Text = "Предпросмотр недоступен: " + error.GetBaseException().Message;
                    DockerTheme.UseTheme(_gridSummary, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush);
                    SetGridColorTip(baseTip, "Предпросмотр ICC недоступен: " + error.GetBaseException().Message);
                }).Task.ConfigureAwait(false);
            }
            finally
            {
                await Dispatcher.InvokeAsync(() => {
                    if (ReferenceEquals(_gridColorCancellation, cancellation)) _gridColorCancellation = null;
                    cancellation.Dispose();
                }).Task.ConfigureAwait(false);
            }
        }
        private void SetGridColorTip(string baseTip, string message)
        {
            string tip = baseTip + "\n" + message;
            _gridSummary.ToolTip = tip; _preview.ToolTip = message; _gridSketch.ToolTip = message;
            if (_gridWindowSketch != null) _gridWindowSketch.ToolTip = message;
        }
    }
}
