using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VanyaTools.Native
{
    public sealed class VanyaOrdersDocker : UserControl
    {
        public const string DockerGuid = "6f479847-f853-4cf0-8071-189529d141be";

        private readonly TextBlock _status;
        private readonly OrderCardTab _orders;

        public VanyaOrdersDocker() : this(null) { }

        public VanyaOrdersDocker(object app)
        {
            CorelApp.SetHostApplication(app);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;

            var root = new Grid { Margin = new Thickness(6) };
            DockerTheme.UseTheme(root, Panel.BackgroundProperty, DockerTheme.WindowBackground);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            DockerTheme.Apply(this);
            Content = root;

            root.Children.Add(new TextBlock {
                Text = "Vanya Tools · Заказы",
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            });
            _status = new TextBlock {
                Text = "Готово", FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(5, 3, 5, 3)
            };
            DockerTheme.UseTheme(_status, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
            var statusFrame = new Border {
                CornerRadius = new CornerRadius(5), Child = _status
            };
            DockerTheme.UseTheme(statusFrame, Border.BackgroundProperty, DockerTheme.StatusBackground);
            Grid.SetRow(statusFrame, 1);
            root.Children.Add(statusFrame);

            _orders = new OrderCardTab(SetStatus, standaloneOrders: true);
            _orders.OrdersView.HorizontalAlignment = HorizontalAlignment.Stretch;
            _orders.OrdersView.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(_orders.OrdersView, 2);
            root.Children.Add(_orders.OrdersView);
        }

        private void SetStatus(string message, bool isError)
        {
            _status.Text = message;
            DockerTheme.UseTheme(_status, TextBlock.ForegroundProperty,
                isError ? DockerTheme.ErrorTextBrush : DockerTheme.MutedTextBrush);
        }
    }
}
