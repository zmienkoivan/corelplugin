using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed class FrameBagTab : UserControl
    {
        private FrameBagSetup _setup = new FrameBagSetup { LeftZipper = BagZipper.Slot };
        private readonly Action<string, bool> _status;
        private readonly TextBlock _message;
        internal FrameBagTab(Action<string, bool> status)
        {
            _status = status;
            var panel = DockerLayout.ToolGroup("Внутрирамная велосумка",
                "Для треугольной рамы нужны три стороны, для четырёхугольной — четыре стороны и диагональ. Высота сумки задаёт верхний сегмент, глубина сверху/снизу — толщину поперёк рамы. " +
                "В окне доступны скругление углов, зазор до рамы, припуск, виды сбоку/спереди и полный комплект для шитья. " +
                "Две боковины и отдельные стенки создаются на новом листе Corel или сохраняются в DXF 1:1.");
            panel.Margin = new Thickness(8);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var button = DockerTheme.Primary(new Button { Content = "Построить выкройку…", MinHeight = 36 });
            button.Click += (_, __) => Open();
            panel.Children.Add(button);
            _message = new TextBlock { Text = "Начальные значения в окне — пример. Введите свои замеры.", TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 9, 0, 0) };
            panel.Children.Add(_message);
        }
        private void Open()
        {
            try
            {
                var window = new FrameBagWindow(_setup);
                var owner = PresentationSource.FromVisual(this) as HwndSource;
                if (owner != null) new WindowInteropHelper(window).Owner = owner.Handle;
                bool? accepted;
                try { accepted = window.ShowDialog(); }
                finally { _setup = window.Setup; }
                if (accepted != true) return;
                int count;
                using (DockerUiWatchdog.Trace(Dispatcher, "Frame bag cutting pattern"))
                    count = FrameBagService.Insert(window.Plan);
                Report("Выкройка создана на новом листе: " + count + " деталей. Для лазера выделены только контуры резки.", false);
            }
            catch (Exception error) { Log.Error("Frame bag pattern failed.", error); Report(error.GetBaseException().Message, true); }
        }
        private void Report(string message, bool error)
        {
            _message.Text = message;
            DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, error ? DockerTheme.ErrorTextBrush : DockerTheme.MutedTextBrush);
            _status(message, error);
        }
    }
}
