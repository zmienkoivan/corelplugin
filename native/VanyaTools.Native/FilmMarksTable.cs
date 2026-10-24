using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class FilmMarkTableRow : INotifyPropertyChanged
    {
        private string _color, _number;
        private int _order;
        internal FilmMarkReference Source;
        public string Color { get => _color; set { _color = value; Changed(nameof(Color)); } }
        public string Number { get => _number; set { _number = value; Changed(nameof(Number)); } }
        public int Order { get => _order; set { _order = value; Changed(nameof(Order)); } }
        public int Page => Source?.PageIndex ?? 0;
        internal bool Dirty => Source != null && (Color != Source.Caption.Color || Number != Source.Caption.Number.ToString(CultureInfo.InvariantCulture));
        internal void Read(FilmMarkReference source)
        {
            Source = source; Color = source.Caption.Color; Number = source.Caption.Number.ToString(CultureInfo.InvariantCulture); Changed(nameof(Page));
        }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    internal sealed class FilmMarksTable : UserControl
    {
        private readonly ObservableCollection<FilmMarkTableRow> _rows = new ObservableCollection<FilmMarkTableRow>();
        private readonly DataGrid _grid;
        private readonly TextBlock _summary, _message;
        private readonly Action<string, bool> _status;
        private readonly DispatcherTimer _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        private string _context = "";
        private bool _working;
        private FilmMarkTableRow _editing;

        internal FilmMarksTable(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel(); Content = panel;
            var title = new TextBlock { Text = "Пары меток документа", FontWeight = FontWeights.SemiBold, FontSize = 13,
                Margin = new Thickness(0, 5, 0, 3) }; panel.Children.Add(title);
            _summary = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3) };
            DockerTheme.UseTheme(_summary, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
            panel.Children.Add(_summary);
            _grid = new DataGrid { ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false,
                CanUserDeleteRows = false, CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 28, FontSize = 11,
                MinHeight = 80, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _grid.Columns.Add(new DataGridTextColumn { Header = "Пара", Binding = new Binding(nameof(FilmMarkTableRow.Order)), IsReadOnly = true, Width = 42 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Стр.", Binding = new Binding(nameof(FilmMarkTableRow.Page)), IsReadOnly = true, Width = 36 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Слой", Binding = EditBinding(nameof(FilmMarkTableRow.Number)), Width = 54 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Название цвета", Binding = EditBinding(nameof(FilmMarkTableRow.Color)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 90 });
            _grid.BeginningEdit += (_, e) => _editing = (FilmMarkTableRow)e.Row.Item;
            _grid.CellEditEnding += (_, e) => {
                var row = (FilmMarkTableRow)e.Row.Item;
                if (e.EditAction == DataGridEditAction.Commit)
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                        if (!_working && IsEnabled && _editing != row && _rows.Contains(row) && row.Dirty) Apply(new[] { row });
                    }));
                _editing = null;
            };
            panel.Children.Add(_grid);
            var buttons = DockerLayout.Cards(95, 3);
            buttons.Children.Add(Button("Выделить пару", () => {
                if (!CommitPending()) return;
                if (_grid.SelectedItem is FilmMarkTableRow row) FilmMarkPairs.Select(_context, row.Source.Id);
            }));
            buttons.Children.Add(Button("Применить", () => CommitPending()));
            buttons.Children.Add(Button("Обновить", Refresh)); panel.Children.Add(buttons);
            var discard = Button("Сбросить правки в таблице", () => {
                _grid.CancelEdit(DataGridEditingUnit.Cell); _grid.CancelEdit(DataGridEditingUnit.Row); _editing = null;
                _rows.Clear(); Refresh(); Show("Таблица перечитана из CDR.", false, false);
            }); panel.Children.Add(discard);
            _message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 10, Margin = new Thickness(0, 3, 0, 3) };
            panel.Children.Add(_message);
            DockerHelp.Add(panel, "Список пар Vanya Tools на всех страницах открытого CDR обновляется автоматически, пока вкладка открыта. " +
                "Каждая скопированная пара — отдельная строка, даже если её цвет и номер совпадают с исходной. " +
                "Цвет и номер слоя применяются по Enter или после выхода из ячейки; положение меток сохраняется. " +
                "«Выделить пару» выбирает обе метки вместе с подписью для обычного копирования в Corel. " +
                "У новых меток подпись входит в группу пары. Старые комплекты тоже поддерживаются. " +
                "Если скопированы только старые метки без подписи, при правке строки подпись будет создана у этой копии. " +
                "Изменения сохраняются в CDR и отменяются обычной командой Corel. «Сбросить правки» перечитывает подписи из CDR.");
            _watch.Tick += (_, __) => Refresh();
            Loaded += (_, __) => Watch(); IsVisibleChanged += (_, __) => Watch();
            Unloaded += (_, __) => _watch.Stop();
        }

        private static Binding EditBinding(string path) => new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
        private Button Button(string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 1, 3, 2) };
            button.Click += (_, __) => { try { action(); } catch (Exception error) { Show(error.Message, true); Log.Error("Film marks table action failed.", error); } };
            return button;
        }
        private bool Commit() => _grid.CommitEdit(DataGridEditingUnit.Cell, true) && _grid.CommitEdit(DataGridEditingUnit.Row, true);
        internal bool CommitPending() => Commit() && Apply(_rows.Where(r => r.Dirty).ToList());
        private void Watch()
        {
            if (IsLoaded && IsVisible) { Refresh(); _watch.Start(); }
            else _watch.Stop();
        }

        internal void Refresh()
        {
            if (_working || !IsVisible || !IsEnabled) return;
            try
            {
                if (Convert.ToBoolean(CorelApp.Get().Optimization)) return;
                FilmMarkSnapshot snapshot = FilmMarkPairs.Scan();
                if (_message.Text.StartsWith("Таблица временно недоступна:", StringComparison.Ordinal)) Show("", false, false);
                if (_context != snapshot.Context)
                {
                    _grid.CancelEdit(DataGridEditingUnit.Cell); _grid.CancelEdit(DataGridEditingUnit.Row);
                    _editing = null; _rows.Clear(); _context = snapshot.Context;
                    Show("", false, false);
                }
                var ids = new HashSet<int>(snapshot.Pairs.Select(p => p.Id));
                foreach (FilmMarkTableRow row in _rows.Where(r => !ids.Contains(r.Source.Id)).ToList()) _rows.Remove(row);
                for (int i = 0; i < snapshot.Pairs.Count; i++)
                {
                    FilmMarkReference source = snapshot.Pairs[i];
                    FilmMarkTableRow row = _rows.FirstOrDefault(r => r.Source.Id == source.Id);
                    if (row == null) { row = new FilmMarkTableRow(); row.Read(source); _rows.Add(row); }
                    else if (row != _editing && !row.Dirty) row.Read(source);
                    row.Order = i + 1;
                    // Do not move DataGrid containers under an editor's caret.
                    if (_editing == null && _rows.IndexOf(row) != i) _rows.Move(_rows.IndexOf(row), i);
                }
                _summary.Text = snapshot.Title + " · пар: " + _rows.Count;
            }
            catch (Exception error) { Show("Таблица временно недоступна: " + error.Message, true, false); }
        }

        private bool Apply(IList<FilmMarkTableRow> rows)
        {
            if (_working) return false;
            if (rows.Count == 0) return true;
            var captions = new List<FilmCaption>();
            foreach (FilmMarkTableRow row in rows)
            {
                if (!Int32.TryParse(row.Number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                { Show("Номер слоя должен быть целым числом от 1 до 9999.", true); return false; }
                captions.Add(new FilmCaption { Color = row.Color, Number = number });
            }
            _working = true;
            try
            {
                FilmMarkPairs.Apply(_context, rows.Select(r => r.Source).ToList(), captions);
                // Discard only drafts that were actually written. Re-read the new
                // native references (including wrappers created for legacy copies).
                foreach (FilmMarkTableRow row in rows)
                {
                    row.Source.Caption.Color = row.Color.Trim();
                    row.Source.Caption.Number = Int32.Parse(row.Number, CultureInfo.InvariantCulture);
                    row.Color = row.Source.Caption.Color;
                    row.Number = row.Source.Caption.Number.ToString(CultureInfo.InvariantCulture);
                }
                Show("Подписи обновлены: " + rows.Count + ".", false);
            }
            catch (Exception error) { Show(error.Message, true); Log.Error("Could not update film mark captions.", error); return false; }
            finally { _working = false; }
            Refresh();
            return true;
        }

        private void Show(string message, bool error, bool notify = true)
        {
            if (_message != null) { _message.Text = message; DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, error ? DockerTheme.ErrorTextBrush : DockerTheme.TextBrush); }
            if (notify && !String.IsNullOrWhiteSpace(message)) _status(message, error);
        }
    }
}
