using System;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
                "Измерение выполняется по нажатию кнопки. Значения относятся к последнему измерению.");
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
            _context = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 8), Visibility = Visibility.Collapsed };
            panel.Children.Add(_context);
            _copyAll = new Button { Content = "Копировать измерение", IsEnabled = false,
                MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
            _copyAll.Click += (_, __) => CopyMeasurement();
            panel.Children.Add(_copyAll);
            _message = new TextBlock { Text = "Выберите два узла одной кривой (F10).", TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray };
            panel.Children.Add(_message);
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
                _message.Foreground = Brushes.DimGray;
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
                _message.Foreground = Brushes.Firebrick;
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
