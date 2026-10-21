using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class ConnectionIndicators : StackPanel
    {
        private readonly Func<string> _token;
        private readonly Func<Task<ConnectionStatus>> _printer;
        private readonly Ellipse _replicateDot, _printerDot;
        private readonly Border _replicateHost, _printerHost;
        private readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        private bool _busy;

        internal ConnectionIndicators(Func<string> token, Func<Task<ConnectionStatus>> printer)
        {
            _token = token; _printer = printer;
            Orientation = Orientation.Horizontal; VerticalAlignment = VerticalAlignment.Center;
            _replicateHost = Dot("Replicate", out _replicateDot);
            _printerHost = Dot("Принтер этикеток", out _printerDot);
            Children.Add(_replicateHost); Children.Add(_printerHost);
            _timer.Tick += async (_, __) => await Refresh();
            Loaded += async (_, __) => { if (IsVisible) { _timer.Start(); await Refresh(); } };
            Unloaded += (_, __) => _timer.Stop();
            IsVisibleChanged += async (_, __) => {
                if (IsVisible && IsLoaded) { _timer.Start(); await Refresh(); } else _timer.Stop();
            };
        }

        private Border Dot(string name, out Ellipse dot)
        {
            dot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.Gray, Stroke = Brushes.White, StrokeThickness = .6 };
            var host = new Border { Child = dot, Background = Brushes.Transparent, Padding = new Thickness(5, 4, 5, 4),
                ToolTip = name + ": проверка подключения…" };
            AutomationProperties.SetName(host, name);
            ToolTipService.SetInitialShowDelay(host, 150);
            host.MouseLeftButtonUp += async (_, __) => await Refresh();
            return host;
        }

        private async Task Refresh()
        {
            if (_busy || !IsLoaded || !IsVisible) return;
            _busy = true;
            try
            {
                // Snapshot the token on the UI thread. All network/process work
                // then runs off the dispatcher; one probe cannot hold the other.
                Task<ConnectionStatus> replicate = ConnectivityClient.Replicate(_token());
                Task<ConnectionStatus> printer = _printer();
                await Task.WhenAll(Update(replicate, _replicateDot, _replicateHost), Update(printer, _printerDot, _printerHost));
            }
            catch (Exception error) { Log.Error("Connection indicators failed.", error); }
            finally { _busy = false; }
        }

        private async Task Update(Task<ConnectionStatus> check, Ellipse dot, Border host)
        {
            ConnectionStatus result;
            try { result = await check; }
            catch (Exception error) { result = new ConnectionStatus { Message = error.GetBaseException().Message }; }
            if (!IsLoaded) return;
            dot.Fill = result.Available ? new SolidColorBrush(Color.FromRgb(43, 160, 71)) : new SolidColorBrush(Color.FromRgb(207, 52, 52));
            host.ToolTip = result.Message + "\nПроверено: " + DateTime.Now.ToString("HH:mm:ss") + " · нажмите для проверки";
            AutomationProperties.SetHelpText(host, result.Message);
        }
    }
}
