using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed class Apparel3DTab : UserControl
    {
        private Apparel3DWindow _window;
        public Apparel3DTab(Action<string, bool> status)
        {
            var panel = DockerLayout.ToolGroup("3D-примерка футболки",
                "Используйте готовый CDR заказа с двумя видами футболки. Перед и спинка определяются по группам корпуса и рукавов. " +
                "В окне нажмите «Взять заказ из Corel», затем правьте дизайн в Corel и обновляйте просмотр. " +
                "Для нестандартной раскладки можно сначала выделить две группы нужных видов. Это примерка размещения принта, не расчёт посадки и лекал.");
            panel.Margin = new Thickness(8); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var button = DockerTheme.Primary(new Button { Content = "Открыть 3D-примерку…", MinHeight = 36 }); panel.Children.Add(button);
            button.Click += (_, __) =>
            {
                try
                {
                    if (_window != null) { _window.Activate(); return; }
                    _window = new Apparel3DWindow(status);
                    var owner = PresentationSource.FromVisual(this) as HwndSource;
                    if (owner != null) new WindowInteropHelper(_window).Owner = owner.Handle;
                    _window.Closed += (s, e) => _window = null;
                    _window.Show();
                }
                catch (Exception error) { _window = null; Log.Error("Could not open apparel preview.", error); status(error.GetBaseException().Message, true); }
            };
        }
    }
}
