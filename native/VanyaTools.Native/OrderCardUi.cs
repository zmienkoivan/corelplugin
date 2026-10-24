using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed partial class OrderCardTab
    {
        private Expander _orderSelectionExpander;
        private TextBlock _orderSelectionLabel;
        private TextBlock _cardNumberCaption;
        private Expander _unassignedOpenExpander;
        private int? _unassignedKnownCount;
        private int _unassignedSnapshotVersion;
        private string _unassignedContextServer, _unassignedContextKey, _lastUnassignedError;
        private DateTime _nextUnassignedRefreshUtc = DateTime.MinValue, _lastUnassignedErrorUtc;
        private Window _unassignedPicker;

        private static void CompactCardField(Panel parent, string caption, Control editor, bool right)
        {
            editor.MinWidth = 0; editor.FontSize = 11; editor.Margin = new Thickness(0);
            AutomationProperties.SetName(editor, caption);
            editor.SetBinding(ToolTipProperty, new Binding("Text") { Source = editor });
            var field = DockerLayout.LabeledField(caption, editor);
            field.Margin = right ? new Thickness(3, 0, 0, 0) : new Thickness(0, 0, 3, 0);
            parent.Children.Add(field);
        }

        private static void UseCompactChoiceCaption(ContentControl choice, string caption)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.TextProperty, caption);
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            choice.ContentTemplate = new DataTemplate { VisualTree = text };
            AutomationProperties.SetName(choice, Convert.ToString(choice.Content));
        }

        private static ControlTemplate AlwaysOpenFramesTemplate()
        {
            var root = new FrameworkElementFactory(typeof(DockPanel));
            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(FrameworkElement.HeightProperty, 27.0);
            header.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.SetValue(FrameworkElement.MarginProperty, new Thickness(1, 0, 125, 0));
            header.SetValue(DockPanel.DockProperty, Dock.Top);
            root.AppendChild(header);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Content"); root.AppendChild(content);
            return new ControlTemplate(typeof(Expander)) { VisualTree = root };
        }

        private void OwnCardWindow(Window window)
        {
            var owner = Window.GetWindow(this);
            if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else
            {
                var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                if (source != null) new System.Windows.Interop.WindowInteropHelper(window).Owner = source.Handle;
            }
        }

        private FrameworkElement CreateCardNumberRow()
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 5, 2) };
            var number = Input(); number.Margin = new Thickness(1, 0, 1, 0);
            number.MaxLength = 10;
            number.ToolTip = "Открыть карточку по номеру, например 10001 или 2026/10001.";
            AutomationProperties.SetName(number, "Номер заказа для загрузки карточки");
            var open = Button("↵", async (_, __) => await OpenServerOrder(number.Text.Trim()));
            open.Width = 27; open.Margin = new Thickness(1, 0, 0, 0);
            open.Padding = new Thickness(0);
            open.ToolTip = "Загрузить карточку заказа по номеру";
            DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
            _cardNumberCaption = Label("Заказ №"); _cardNumberCaption.VerticalAlignment = VerticalAlignment.Center;
            _cardNumberCaption.Margin = new Thickness(1, 0, 4, 0);
            DockPanel.SetDock(_cardNumberCaption, Dock.Left); row.Children.Add(_cardNumberCaption);
            row.Children.Add(number);
            number.KeyDown += async (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter || _busy) return;
                e.Handled = true; await OpenServerOrder(number.Text.Trim());
            };
            return row;
        }

        private async Task ShowUnassignedPicker(StackPanel content)
        {
            if (_busy || _unassignedPicker != null) return;
            await RefreshUnassignedIfDue();
            if (_busy || _unassignedKnownCount == 0) return;
            var window = new Window { Title = "Подтянуть существующий заказ", Width = 490,
                SizeToContent = SizeToContent.Height, MinWidth = 330,
                MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 80),
                WindowStartupLocation = WindowStartupLocation.CenterScreen };
            double height = _unassignedTable.Height;
            _unassignedPicker = window;
            try
            {
                _unassignedOpenExpander.Content = null;
                _unassignedTable.Height = 280;
                content.Margin = new Thickness(8);
                window.Content = content; DockerTheme.Apply(window); OwnCardWindow(window); window.ShowDialog();
            }
            catch (Exception error) { Log.Error("Orders without cards picker failed.", error); _status(error.Message, true); }
            finally
            {
                window.Content = null; content.Margin = new Thickness(0);
                _unassignedTable.Height = height; _unassignedOpenExpander.Content = content; _unassignedPicker = null;
            }
        }

        private void ResetUnassignedContext(string server, string key)
        {
            if (String.Equals(server, _unassignedContextServer, StringComparison.Ordinal) &&
                String.Equals(key, _unassignedContextKey, StringComparison.Ordinal)) return;
            _unassignedContextServer = server; _unassignedContextKey = key;
            _unassignedKnownCount = null; _unassignedSnapshotVersion++;
            _nextUnassignedRefreshUtc = DateTime.MinValue; _lastUnassignedError = null;
            _unassignedTable.ItemsSource = null; _unassignedTable.Visibility = Visibility.Collapsed;
            _unassignedOpenExpander.Visibility = Visibility.Collapsed;
            _unassignedCount.Text = "";
        }

        private async Task RefreshUnassignedIfDue()
        {
            // Quick document fields and the standalone orders holder also own
            // this timer. Only the actual visible card may poll this endpoint.
            if (!IsLoaded || !IsVisible || _busy || _unassignedLoading) return;
            string server = _server.Text.Trim(), key = _key.Password.Trim();
            ResetUnassignedContext(server, key);
            if (server.Length == 0 || key.Length == 0)
            { _unassignedOpenExpander.Visibility = Visibility.Collapsed; return; }
            if (DateTime.UtcNow < _nextUnassignedRefreshUtc) return;
            await LoadUnassignedTable(true);
        }

        private void ApplyUnassignedRows(List<OrderListItem> rows)
        {
            _unassignedKnownCount = rows.Count;
            _unassignedTable.ItemsSource = rows;
            _unassignedTable.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _unassignedOpenExpander.Header = UnassignedHeader("Подтянуть заказ · " + rows.Count);
            _unassignedOpenExpander.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _unassignedOpenExpander.ToolTip = rows.Count > 0
                ? "Откройте список и выберите заказ для привязки к текущему CDR." : null;
            _unassignedCount.Text = rows.Count > 0 ? "Выберите заказ для текущего CDR." : "";
            _lastUnassignedError = null;
        }

        private void ShowUnassignedFailure(Exception error)
        {
            // A failed request is not an empty list. Keep usable cached rows
            // and a manual refresh control even when this is the first check.
            string message = error.GetBaseException().Message;
            if (_lastUnassignedError != message || DateTime.UtcNow - _lastUnassignedErrorUtc >= TimeSpan.FromMinutes(5))
            {
                Log.Error("Orders without cards could not be refreshed.", error);
                _lastUnassignedError = message; _lastUnassignedErrorUtc = DateTime.UtcNow;
            }
            _unassignedOpenExpander.Header = UnassignedHeader(_unassignedKnownCount > 0
                ? "Подтянуть заказ · " + _unassignedKnownCount.Value : "Подтянуть заказ · проверить");
            _unassignedOpenExpander.Visibility = Visibility.Visible;
            _unassignedOpenExpander.ToolTip = "Список не обновлён. Нажмите ↻, чтобы повторить проверку.";
            _unassignedCount.Text = "Не удалось обновить список: " + message;
            _unassignedTable.Visibility = _unassignedKnownCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static TextBlock UnassignedHeader(string text) => new TextBlock { Text = text, FontSize = 11,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

        private static StackPanel CardGroup(Panel parent, string title, string help = null)
        {
            var group = DockerLayout.ToolGroup(title, help);
            parent.Children.Add(group); return group;
        }

        private static StackPanel CardFold(Panel parent, string title, bool expanded = false)
        {
            var content = new StackPanel { Margin = new Thickness(3, 4, 3, 5) };
            parent.Children.Add(new Expander { Header = new TextBlock { Text = title, FontSize = 11,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                IsExpanded = expanded, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(1, 2, 1, 5) });
            return content;
        }

        private static void CardField(Panel parent, string caption, Control editor)
        {
            editor.MinWidth = 0; editor.FontSize = 11;
            editor.Margin = new Thickness(0, 0, 0, 3);
            AutomationProperties.SetName(editor, caption);
            parent.Children.Add(DockerLayout.LabeledField(caption, editor));
        }

        private static void CardHideEmpty(TextBlock text)
        {
            var style = new Style(typeof(TextBlock));
            var empty = new DataTrigger { Binding = new Binding("Text") { Source = text }, Value = "" };
            empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
            style.Triggers.Add(empty); text.Style = style;
        }

        private static void CardActionRows(Panel parent, params Button[] buttons)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < buttons.Length; i++)
            {
                if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(buttons[i], i / 2); Grid.SetColumn(buttons[i], i % 2);
                if (i == buttons.Length - 1 && buttons.Length % 2 == 1) Grid.SetColumnSpan(buttons[i], 2);
                buttons[i].Margin = new Thickness(1, 1, 1, 3); grid.Children.Add(buttons[i]);
            }
            parent.Children.Add(grid);
        }

        private FrameworkElement CreateOrdersView(StackPanel toolbar, StackPanel details, bool standalone)
        {
            var root = new Grid { Margin = new Thickness(5), MinWidth = 0 };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.Margin = new Thickness(0, 0, 0, 3); root.Children.Add(toolbar);
            var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _ordersTable.Height = Double.NaN; _ordersTable.MinHeight = 80;
            _ordersTable.EnableRowVirtualization = true; _ordersTable.EnableColumnVirtualization = true;
            _ordersTable.FontSize = 11; _ordersTable.FrozenColumnCount = 1;
            body.Children.Add(_ordersTable);
            var detailScroll = new ScrollViewer { Content = details,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            details.Margin = new Thickness(2, 4, 2, 2);
            _orderSelectionLabel = new TextBlock { Text = "Выбранный заказ", FontSize = 11,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            _orderSelectionExpander = new Expander { Header = _orderSelectionLabel, Content = detailScroll,
                IsExpanded = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch, Margin = new Thickness(1, 4, 1, 1) };
            body.Children.Add(_orderSelectionExpander);
            _linkOrder.Margin = new Thickness(1, 5, 1, 0); DockerTheme.Primary(_linkOrder);
            Grid.SetRow(_linkOrder, 2); root.Children.Add(_linkOrder);
            bool? wasWide = null;
            Action arrange = () =>
            {
                bool wide = root.ActualWidth >= 730;
                detailScroll.MaxHeight = wide ? Double.PositiveInfinity : Math.Max(65, Math.Min(330, body.ActualHeight * 0.5));
                if (wasWide == wide) return;
                wasWide = wide;
                body.ColumnDefinitions[1].Width = wide ? new GridLength(290) : new GridLength(0);
                Grid.SetRow(_orderSelectionExpander, wide ? 0 : 1);
                Grid.SetColumn(_orderSelectionExpander, wide ? 1 : 0);
                _orderSelectionExpander.Margin = wide ? new Thickness(8, 0, 0, 0) : new Thickness(1, 4, 1, 1);
                _orderSelectionExpander.IsExpanded = wide;
                _linkOrder.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
                _linkOrder.MinWidth = wide ? 220 : 0;
            };
            root.SizeChanged += (_, __) => arrange();
            body.SizeChanged += (_, __) => arrange();
            root.Loaded += (_, __) => arrange();
            return root;
        }

        private void RefreshOrderSelectionUi()
        {
            if (_orderSelectionExpander == null) return;
            var order = _ordersTable.SelectedItem as OrderListItem;
            _orderSelectionLabel.Text = order == null ? "Фото и сведения о заказе" :
                "№" + order.Number + " · " + order.Customer;
            if (!_restoringOrderSelection && order != null) _orderSelectionExpander.IsExpanded = true;
        }
    }
}
