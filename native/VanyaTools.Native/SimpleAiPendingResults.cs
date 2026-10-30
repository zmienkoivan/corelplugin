using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VanyaTools.Native
{
    internal sealed partial class SimpleAiTab
    {
        private UniformGrid _pendingActions;
        private Button _insertBeside, _openReady, _deferReady;
        private StackPanel _deferredRows;
        private Expander _deferredResults;

        private void AddPendingResultControls(Panel panel)
        {
            _pendingActions = new UniformGrid { Columns = 3, Visibility = Visibility.Collapsed };
            _insertBeside = Button("Вставить рядом", async (_, __) => await InsertPendingBeside());
            _insertBeside.ToolTip = "Вставить готовый результат в исходные документ и страницу. Исходник не изменяется; новая генерация не запускается.";
            _openReady = Button("PNG ↗", (_, __) => OpenReadyFile(_readyOutput));
            _deferReady = Button("Отложить", (_, __) => DeferReadyResult());
            _deferReady.ToolTip = "Сохранить готовый PNG в отложенных и освободить инструменты для новой обработки.";
            foreach (var button in new[] { _insertBeside, _openReady, _deferReady })
            { button.FontSize = 10; button.MinWidth = 0; button.Padding = new Thickness(3, 2, 3, 2); _pendingActions.Children.Add(button); }
            panel.Children.Add(_pendingActions);
            _deferredRows = new StackPanel();
            _deferredResults = new Expander { Header = "Отложенные результаты", IsExpanded = false,
                Content = new ScrollViewer { Content = _deferredRows, MaxHeight = 140,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Visibility = Visibility.Collapsed };
            panel.Children.Add(_deferredResults);
            RefreshDeferredResults();
        }

        private bool ReadyResultExists => !String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput);
        private bool CanInsertReadyBeside => ReadyResultExists && _mode == SimpleAiMode.Raster && RasterResultInsertion.HasPlacement(_readyOutput);
        private bool RestoredReadyNeedsBeside => CanInsertReadyBeside && _rasterInsertionSource == null;

        private void UpdatePendingResultControls()
        {
            if (_pendingActions == null) return;
            _pendingActions.Visibility = ReadyResultExists ? Visibility.Visible : Visibility.Collapsed;
            _insertBeside.IsEnabled = !_busy && CanInsertReadyBeside;
            _openReady.IsEnabled = _deferReady.IsEnabled = !_busy && ReadyResultExists;
            if (_deferredResults != null) _deferredResults.IsEnabled = !_busy;
        }

        private async Task InsertPendingBeside()
        {
            if (_busy || !CanInsertReadyBeside) return;
            SetBusy(true, "Вставляю готовый результат рядом с исходником…");
            try
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                _importWarning = null; InsertReady(true, true); ReportOutcome();
            }
            catch (Exception error) { Log.Error("Pending raster safe insertion failed.", error); Report(error.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private void DeferReadyResult()
        {
            if (_busy || !ReadyResultExists) return;
            try
            {
                string output = _readyOutput;
                PendingRasterResults.Defer(PendingPath(), output, OperationName(Path.GetFileName(output)));
                _readyOutput = null; _rasterInsertionSource = null; _lastInput = null; _lastSource = null;
                _lastOutput = null; _lastModel = null; _importWarning = null; _resultPreview.Source = null;
                SetBusy(false, null); RefreshDeferredResults();
                Report("Результат отложен. Можно обрабатывать новое выделение; PNG доступен в «Отложенных результатах».", false);
            }
            catch (Exception error) { Log.Error("Could not defer pending raster.", error); Report(error.GetBaseException().Message, true); }
        }

        private void RefreshDeferredResults()
        {
            if (_deferredRows == null) return;
            _deferredRows.Children.Clear();
            try
            {
                var rows = PendingRasterResults.Load(PendingPath()).FindAll(row => !row.Active);
                _deferredResults.Header = "Отложенные результаты (" + rows.Count + ")";
                _deferredResults.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                foreach (var row in rows)
                {
                    var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2), ToolTip = row.Path };
                    var restore = Button("Вернуть", (_, __) => RestoreDeferredResult(row.Path));
                    restore.FontSize = 10; DockPanel.SetDock(restore, Dock.Right); line.Children.Add(restore);
                    var open = Button("PNG ↗", (_, __) => OpenReadyFile(row.Path));
                    open.FontSize = 10; DockPanel.SetDock(open, Dock.Right); line.Children.Add(open);
                    line.Children.Add(Note(row.DeferredUtc.ToLocalTime().ToString("dd.MM HH:mm") + " · " + row.Operation));
                    _deferredRows.Children.Add(line);
                }
            }
            catch (Exception error)
            {
                Log.Error("Could not read deferred raster results.", error);
                _deferredResults.Visibility = Visibility.Visible;
                _deferredRows.Children.Add(Note(error.GetBaseException().Message));
            }
        }

        private void RestoreDeferredResult(string path)
        {
            if (_busy) return;
            try
            {
                var preview = PreviewBitmap(path);
                if (ReadyResultExists && !String.Equals(_readyOutput, path, StringComparison.OrdinalIgnoreCase))
                    PendingRasterResults.Defer(PendingPath(), _readyOutput, OperationName(Path.GetFileName(_readyOutput)));
                PendingRasterResults.Restore(PendingPath(), path);
                _readyOutput = path; _rasterInsertionSource = null; _lastInput = null; _lastSource = null;
                _resultPreview.Source = preview; SetBusy(false, null); RefreshDeferredResults();
                Report("Готовый PNG возвращён. Можно вставить его рядом на исходной странице без новой обработки.", false);
            }
            catch (Exception error) { Log.Error("Could not restore deferred raster result.", error); Report(error.GetBaseException().Message, true); }
        }

        private void OpenReadyFile(string path)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path)) throw new FileNotFoundException("Готовый PNG не найден.", path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error) { Report(error.GetBaseException().Message, true); }
        }
    }
}
