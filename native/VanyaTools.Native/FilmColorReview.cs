using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace VanyaTools.Native
{
    internal sealed class FilmColorReview : UserControl
    {
        private readonly DataGrid _grid;
        private readonly Expander _expander;
        private readonly Button _selectErrors, _selectRows, _black;
        private readonly Func<Func<Task>, Task> _run;
        private readonly Action<string, bool> _status;
        private readonly Func<FilmColorScope, Task<FilmColorReport>> _recheck;
        private FilmColorScope _scope;
        private FilmColorReport _report;

        internal FilmColorReview(Func<Func<Task>, Task> run, Action<string, bool> status,
            Func<FilmColorScope, Task<FilmColorReport>> recheck)
        {
            _run = run; _status = status; _recheck = recheck;
            var panel = new StackPanel();
            _grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow, HeadersVisibility = DataGridHeadersVisibility.Column,
                FontSize = 11, MinHeight = 75, MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _grid.Columns.Add(new DataGridTextColumn { Header = "", Binding = new Binding(nameof(FilmColorIssue.Level)), Width = 26 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Объект", Binding = new Binding(nameof(FilmColorIssue.ObjectName)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 65 });
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FilmColorIssue.Problem)));
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(FrameworkElement.MarginProperty, new Thickness(3));
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "Проблема", CellTemplate = new DataTemplate { VisualTree = text },
                Width = new DataGridLength(2.2, DataGridLengthUnitType.Star), MinWidth = 100 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Правка", Binding = new Binding(nameof(FilmColorIssue.Action)), Width = 72 });
            panel.Children.Add(_grid);
            var buttons = DockerLayout.Cards(140, 2); panel.Children.Add(buttons);
            _selectErrors = Add(buttons, "Выделить все ошибки", () => Select(false));
            _selectRows = Add(buttons, "Выделить строки", () => Select(true));
            _black = Add(buttons, "Ошибочные цвета → K100", async () => {
                // Read the current colors first: edits made in Corel are never overwritten from a stale report.
                FilmColorReport current = await _recheck(_scope);
                int changed = FilmColorCorrections.Black(_scope, current);
                _scope.Restore();
                FilmColorReport remaining = await _recheck(_scope);
                _status("Исправлено объектов: " + changed + ". Осталось ошибок: " + remaining.Errors +
                    (remaining.Errors > 0 ? ". Объекты «Вручную» выберите в списке." : "."), remaining.Errors > 0);
            });
            Add(buttons, "Проверить снова", async () => {
                _scope.Restore(); FilmColorReport report = await _recheck(_scope);
                _status("Повторная проверка: ошибок " + report.Errors + ", предупреждений " + report.Warnings + ".", report.Errors > 0);
            });
            Add(panel, "Вернуть исходное выделение", () => { _scope.Restore(); _status("Выделен весь проверенный фотовывод.", false); return Task.CompletedTask; });
            DockerHelp.Add(panel, "Выделить все ошибки — выбрать ошибочные объекты в Corel; предупреждения о полутонах в этот выбор не входят. " +
                "Можно выбрать несколько строк через Ctrl/Shift и выделить только их. K100 заменяет только недопустимые цвета векторных заливок, " +
                "обводок, букв и точек градиента. Белый, нейтральные полутона и геометрия сохраняются. Вся правка отменяется одним Ctrl+Z. " +
                "Растры, сложные заливки, эффекты и ошибки чтения помечены «Вручную»; их можно выделить и исправить в Corel. " +
                "Повторная проверка относится ко всему исходному выводу и возвращает его выделение. Перед экспортом выбор ошибок также заменяется " +
                "исходным выводом, если вы не выбрали другие объекты самостоятельно. При смене CDR требуется новая проверка.");
            _expander = new Expander { Header = "Ошибки и предупреждения цветов", Content = panel, Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 5) }; Content = _expander;
        }

        internal void Show(FilmColorScope scope, FilmColorReport report)
        {
            _scope = scope; _report = report; _grid.ItemsSource = report.Issues;
            int objects = report.Issues.Where(i => i.Error).Select(i => i.ShapeId).Distinct().Count();
            _expander.Header = "Цвета: ошибок " + report.Errors + " (объектов " + objects + "), предупреждений " + report.Warnings;
            _expander.Visibility = report.Issues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _expander.IsExpanded = report.Errors > 0;
            _selectErrors.IsEnabled = report.Errors > 0; _selectRows.IsEnabled = report.Issues.Count > 0;
            _black.IsEnabled = report.Issues.Any(i => i.CanBlack);
            if (report.Errors > 0) Dispatcher.BeginInvoke(new Action(() => BringIntoView()));
        }

        internal void Clear()
        { _scope = null; _report = null; _grid.ItemsSource = null; _expander.Visibility = Visibility.Collapsed; }

        internal void RestoreIfHighlighted() => _scope?.RestoreIfHighlighted();

        private Task Select(bool rows)
        {
            var issues = rows ? _grid.SelectedItems.Cast<FilmColorIssue>().ToList() : _report.Issues.Where(i => i.Error).ToList();
            int count = _scope.Select(issues);
            _status("Выделено проблемных объектов: " + count + ". После правки нажмите «Проверить снова».", false);
            return Task.CompletedTask;
        }
        private Button Add(Panel panel, string title, Func<Task> action)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 2, 2, 2) };
            button.Click += async (_, __) => await _run(action); panel.Children.Add(button); return button;
        }
    }
}
