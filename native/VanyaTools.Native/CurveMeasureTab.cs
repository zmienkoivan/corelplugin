using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class CurveMeasureTab : UserControl
    {
        private readonly Action<string, bool> _status;
        private readonly TextBlock _mode, _context, _message;
        private readonly ValueRow _first, _second;
        private readonly Button _copyAll;
        private readonly ObservableCollection<HistoryRow> _history = new ObservableCollection<HistoryRow>();
        private readonly DataGrid _historyGrid;
        private readonly Button _clearHistory;
        private CurveMeasurement _measurement;
        private bool _measuring;

        internal CurveMeasureTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(8) };
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            panel.Children.Add(new TextBlock { Text = "Рулетка · курвиметр", FontWeight = FontWeights.SemiBold,
                FontSize = 16, Margin = new Thickness(0, 0, 0, 7) });
            DockerHelp.Add(panel, "В Corel выберите кривую инструментом «Форма» (F10) и выделите ровно два её узла. " +
                "Нажмите «Измерить». Для замкнутого контура показаны меньший и больший участки между узлами, " +
                "для открытого — один участок. Узлы должны принадлежать одному непрерывному подконтуру. " +
                "Если объект в группе, выберите саму кривую внутри группы. Если точка ещё не является узлом, добавьте узел в Corel. " +
                "Считается длина вдоль линий и Безье; результат выводится в миллиметрах с двумя знаками после запятой. " +
                "Измерение выполняется по нажатию кнопки. Успешные замеры добавляются в историю внизу. " +
                "История хранится в текущем сеансе панели; документ, кривая и узлы указаны в подсказке строки. " +
                "Для удаления строк нажмите «Очистить историю».");
            var measure = new Button { Content = "Измерить между двумя узлами (F10)", MinHeight = 36,
                Margin = new Thickness(0, 0, 0, 12), Focusable = false, IsTabStop = false };
            measure.Click += (_, __) => Measure();
            panel.Children.Add(measure);
            _mode = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14,
                Margin = new Thickness(0, 0, 0, 5) };
            panel.Children.Add(_mode);
            _first = new ValueRow("Между узлами", CopyNumber);
            _second = new ValueRow("Больший участок", CopyNumber) { Visibility = Visibility.Collapsed };
            panel.Children.Add(_first); panel.Children.Add(_second);
            _context = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 8), Visibility = Visibility.Collapsed };
            DockerTheme.UseTheme(_context, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
            panel.Children.Add(_context);
            _copyAll = new Button { Content = "Копировать измерение", IsEnabled = false,
                MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
            _copyAll.Click += (_, __) => CopyMeasurement();
            panel.Children.Add(_copyAll);
            _message = new TextBlock { Text = "Выберите два узла одной кривой (F10).", TextWrapping = TextWrapping.Wrap };
            DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
            panel.Children.Add(_message);

            var historyHeader = new Grid { Margin = new Thickness(0, 14, 0, 6) };
            historyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            historyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            historyHeader.Children.Add(new TextBlock { Text = "История замеров", FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center });
            _clearHistory = new Button { Content = "Очистить историю", IsEnabled = false, MinHeight = 28,
                Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2),
                ToolTip = "Удалить все строки истории. Последний результат останется сверху." };
            _clearHistory.Click += (_, __) => { _history.Clear(); _clearHistory.IsEnabled = false; };
            Grid.SetColumn(_clearHistory, 1); historyHeader.Children.Add(_clearHistory);
            panel.Children.Add(historyHeader);

            _historyGrid = new DataGrid { ItemsSource = _history, AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false,
                CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
                SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
                Height = 210, MinHeight = 110, FontSize = 11, RowHeaderWidth = 0,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader };
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(HistoryRow.Detail))));
            _historyGrid.RowStyle = rowStyle;
            _historyGrid.Columns.Add(new DataGridTextColumn { Header = "№", Binding = new Binding(nameof(HistoryRow.Number)), Width = 28 });
            _historyGrid.Columns.Add(new DataGridTextColumn { Header = "Кривая", Binding = new Binding(nameof(HistoryRow.Curve)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 60 });
            _historyGrid.Columns.Add(new DataGridTextColumn { Header = "Длина /\nменьший, мм",
                Binding = new Binding(nameof(HistoryRow.Smaller)), Width = 78 });
            _historyGrid.Columns.Add(new DataGridTextColumn { Header = "Больший,\nмм",
                Binding = new Binding(nameof(HistoryRow.Larger)), Width = 70 });
            panel.Children.Add(_historyGrid);
        }

        private static string Millimeters(double value) => value.ToString("0.00", CultureInfo.CurrentCulture);

        private void Measure()
        {
            if (_measuring) return;
            _measuring = true;
            var cursor = Mouse.OverrideCursor;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                _status("Измерение длины по кривой…", false);
                var result = CurveMeasureService.MeasureSelection();
                _measurement = result;
                var historyRow = new HistoryRow(_history.Count + 1, result);
                _history.Add(historyRow);
                _clearHistory.IsEnabled = true;
                _historyGrid.ScrollIntoView(historyRow);
                _mode.Text = result.Closed ? "Замкнутая кривая" : "Открытая кривая";
                _first.Label.Text = (result.Closed ? "Меньший участок" : "Между узлами") + ", мм";
                _first.SetValue(Millimeters(result.SmallerMm));
                _second.Visibility = result.Closed ? Visibility.Visible : Visibility.Collapsed;
                if (result.Closed) _second.SetValue(Millimeters(result.LargerMm));
                else _second.Clear();
                _context.Text = result.DocumentName + " · " + result.ShapeName + "\nПодконтур " +
                    result.SubPathIndex + " · узлы " + result.FirstNode + " — " + result.SecondNode;
                _context.Visibility = Visibility.Visible;
                _copyAll.IsEnabled = true;
                _message.Text = "Измерено. Выберите другую пару узлов для следующего измерения.";
                DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
                _status(ResultText(result), false);
                Log.Info("Curve measurement: subpath=" + result.SubPathIndex + ", nodes=" + result.FirstNode +
                    "-" + result.SecondNode + ", closed=" + result.Closed + ", mm=" +
                    result.SmallerMm.ToString("R", CultureInfo.InvariantCulture) + ", otherMm=" +
                    result.LargerMm.ToString("R", CultureInfo.InvariantCulture) + ".");
            }
            catch (Exception error)
            {
                _measurement = null;
                _first.Clear(); _second.Clear();
                _second.Visibility = Visibility.Collapsed;
                _context.Visibility = Visibility.Collapsed;
                _mode.Text = ""; _copyAll.IsEnabled = false;
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                _message.Text = error.Message;
                DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush);
                _status("Рулетка: " + error.Message, true);
                if (!(error is InvalidOperationException)) Log.Error("Curve measurement failed", error);
            }
            finally { Mouse.OverrideCursor = cursor; _measuring = false; }
        }

        private static string ResultText(CurveMeasurement result) => result.Closed
            ? "Меньший участок: " + Millimeters(result.SmallerMm) + " мм; больший: " + Millimeters(result.LargerMm) + " мм."
            : "Между узлами: " + Millimeters(result.SmallerMm) + " мм.";

        private void CopyMeasurement()
        {
            if (_measurement != null) CopyNumber(ResultText(_measurement));
        }

        private void CopyNumber(string value)
        {
            try { Clipboard.SetText(value); _status("Измерение скопировано.", false); }
            catch (Exception error) { _status("Не удалось скопировать: " + error.Message, true); }
        }

        private sealed class HistoryRow
        {
            public int Number { get; }
            public string Curve { get; }
            public string Smaller { get; }
            public string Larger { get; }
            public string Detail { get; }

            internal HistoryRow(int number, CurveMeasurement measurement)
            {
                Number = number;
                Curve = measurement.ShapeName + "\n" + (measurement.Closed ? "Замкнутая" : "Открытая");
                Smaller = Millimeters(measurement.SmallerMm);
                Larger = measurement.Closed ? Millimeters(measurement.LargerMm) : "—";
                Detail = measurement.DocumentName + " · " + measurement.ShapeName + "\nПодконтур " +
                    measurement.SubPathIndex + " · узлы " + measurement.FirstNode + " — " + measurement.SecondNode;
            }
        }

        private sealed class ValueRow : Grid
        {
            internal readonly TextBlock Label;
            private readonly TextBox _value;
            private readonly Button _copy;

            internal ValueRow(string title, Action<string> copy)
            {
                Margin = new Thickness(0, 0, 0, 8);
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Label = new TextBlock { Text = title + ", мм", Margin = new Thickness(0, 0, 0, 3) };
                Children.Add(Label);
                _value = new TextBox { IsReadOnly = true, Text = "—", FontSize = 23,
                    HorizontalContentAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 3, 6, 3) };
                Grid.SetRow(_value, 1); Children.Add(_value);
                _copy = new Button { Content = "Копировать", MinHeight = 32, IsEnabled = false,
                    Margin = new Thickness(5, 0, 0, 0), ToolTip = "Копировать число в миллиметрах" };
                _copy.Click += (_, __) => copy(_value.Text);
                Grid.SetColumn(_copy, 1); Grid.SetRow(_copy, 1); Children.Add(_copy);
            }

            internal void SetValue(string value) { _value.Text = value; _copy.IsEnabled = true; }
            internal void Clear() { _value.Text = "—"; _copy.IsEnabled = false; }
        }
    }
}
