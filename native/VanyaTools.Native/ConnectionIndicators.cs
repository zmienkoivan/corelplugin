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
        private readonly Ellipse _replicateDot, _printerDot, _updateDot;
        private readonly Border _replicateHost, _printerHost, _updateHost;
        private readonly Action _requestUpdate;
        private readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        private readonly DispatcherTimer _updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
        private readonly DispatcherTimer _blinkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly Version _installedVersion = typeof(ConnectionIndicators).Assembly.GetName().Version;
        private bool _busy, _checkingUpdate;
        private UpdateAvailabilityState _updateState = UpdateAvailabilityState.Unknown;
        private Version _latestVersion;

        internal ConnectionIndicators(Func<string> token, Func<Task<ConnectionStatus>> printer, Action requestUpdate = null)
        {
            _token = token; _printer = printer; _requestUpdate = requestUpdate;
            Orientation = Orientation.Horizontal; VerticalAlignment = VerticalAlignment.Center;
            _replicateHost = Dot("Replicate", out _replicateDot);
            _printerHost = Dot("Принтер этикеток", out _printerDot);
            _updateHost = Dot("Обновления Vanya Tools", out _updateDot, UpdateClicked);
            Children.Add(_replicateHost); Children.Add(_printerHost); Children.Add(_updateHost);
            _timer.Tick += async (_, __) => await Refresh();
            _updateTimer.Tick += async (_, __) => await RefreshUpdate();
            _blinkTimer.Tick += (_, __) => _updateDot.Opacity = _updateDot.Opacity < .5 ? 1 : .2;
            Loaded += async (_, __) => { if (IsVisible) { StartTimers(); await Task.WhenAll(Refresh(), RefreshUpdate()); } };
            Unloaded += (_, __) => StopTimers();
            IsVisibleChanged += async (_, __) => {
                if (IsVisible && IsLoaded) { StartTimers(); await Task.WhenAll(Refresh(), RefreshUpdate()); } else StopTimers();
            };
        }

        private Border Dot(string name, out Ellipse dot, Func<Task> click = null)
        {
            dot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.Gray, Stroke = Brushes.White, StrokeThickness = .6 };
            var host = new Border { Child = dot, Background = Brushes.Transparent, Padding = new Thickness(5, 4, 5, 4),
                ToolTip = name + ": проверка подключения…" };
            AutomationProperties.SetName(host, name);
            ToolTipService.SetInitialShowDelay(host, 150);
            host.MouseLeftButtonUp += async (_, __) => await (click == null ? Refresh() : click());
            return host;
        }

        private void StartTimers()
        { _timer.Start(); _updateTimer.Start(); SyncUpdateBlink(); }

        private void StopTimers()
        { _timer.Stop(); _updateTimer.Stop(); _blinkTimer.Stop(); _updateDot.Opacity = 1; }

        private void SyncUpdateBlink()
        {
            if (IsLoaded && IsVisible && _updateState == UpdateAvailabilityState.Available) _blinkTimer.Start();
            else { _blinkTimer.Stop(); _updateDot.Opacity = 1; }
        }

        private async Task UpdateClicked()
        {
            if (_updateState == UpdateAvailabilityState.Available && _requestUpdate != null)
            { _requestUpdate(); return; }
            await RefreshUpdate(true);
        }

        private async Task RefreshUpdate(bool force = false)
        {
            if (_checkingUpdate || !IsLoaded || !IsVisible) return;
            _checkingUpdate = true;
            try
            {
                UpdateAvailabilityStatus result = await UpdateAvailability.CheckAsync(_installedVersion, force);
                if (!IsLoaded) return;
                string message = result.Message;
                // Losing the connection must not hide an already discovered release.
                // A failed first check, however, is gray rather than a false "current".
                if (result.State == UpdateAvailabilityState.Unknown && _updateState == UpdateAvailabilityState.Available)
                    message = "Доступна версия " + _latestVersion.ToString(3) + ".\nПоследняя проверка: " + message;
                else
                { _updateState = result.State; _latestVersion = result.LatestVersion; }
                _updateDot.Fill = _updateState == UpdateAvailabilityState.Available
                    ? new SolidColorBrush(Color.FromRgb(207, 52, 52)) : _updateState == UpdateAvailabilityState.Current
                    ? new SolidColorBrush(Color.FromRgb(43, 160, 71)) : Brushes.Gray;
                string action = _updateState == UpdateAvailabilityState.Available && _requestUpdate != null
                    ? "Нажмите, чтобы обновить." : "Нажмите для проверки.";
                _updateHost.ToolTip = message + "\nУстановлена: " + _installedVersion.ToString(3) +
                    " · проверено " + result.CheckedUtc.ToLocalTime().ToString("HH:mm") + "\n" + action;
                AutomationProperties.SetHelpText(_updateHost, message + " " + action);
                SyncUpdateBlink();
            }
            catch (Exception error) { Log.Error("Update indicator failed.", error); }
            finally { _checkingUpdate = false; }
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
