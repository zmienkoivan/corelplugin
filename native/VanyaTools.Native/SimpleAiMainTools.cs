using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed partial class SimpleAiTab
    {
        private ComboBox _rasterInsertionMode;
        private Button _colorSummaryButton;
        internal FrameworkElement RasterSettingsView { get; private set; }

        private RasterInsertionMode SelectedRasterInsertionMode =>
            _rasterInsertionMode == null ? RasterInsertionMode.NewAside :
            (RasterInsertionMode)Math.Max(0, Math.Min(2, _rasterInsertionMode.SelectedIndex));

        private FrameworkElement BuildRasterOutputTools()
        {
            var tools = DockerLayout.ToolGroup("Результат обработки");
            _rasterInsertionMode = new ComboBox
            {
                ItemsSource = new[]
                {
                    "Заменить исходник",
                    "На место, исходник вправо",
                    "Результат рядом с исходником"
                },
                SelectedIndex = (int)RasterInsertionSettings.LoadMode(),
                MinHeight = 28,
                MinWidth = 0,
                FontSize = 11,
                Margin = new Thickness(1, 0, 1, 3),
                ToolTip = "Как вставлять результат коррекции, обводки и удаления фона. " +
                    "«Заменить» удаляет исходные объекты после успешной вставки; " +
                    "«На место, исходник вправо» сохраняет их справа. Действие можно отменить в Corel."
            };
            System.Windows.Automation.AutomationProperties.SetName(_rasterInsertionMode,
                "Вставка результата обработки");
            _rasterInsertionMode.SelectionChanged += (_, __) =>
            {
                try { RasterInsertionSettings.SaveMode(SelectedRasterInsertionMode); }
                catch (Exception error)
                {
                    Log.Error("Could not save raster insertion mode.", error);
                    Report("Режим выбран. Не удалось сохранить его для следующего запуска.", true);
                }
            };
            tools.Children.Add(_rasterInsertionMode);
            RasterSettingsView = DockerSettings.Section("Обработка растра", tools);
            _colorSummaryButton = Button("Сводка цветов", async (_, __) => await InsertColorSummary());
            _colorSummaryButton.ToolTip = "Список цветов выделения снизу слева, с отступом 5 мм, 14 пт. " +
                "Вектор: цвета заливок и обводок. Растр: основные оттенки, приблизительно.";
            return _colorSummaryButton;
        }

        private async System.Threading.Tasks.Task InsertColorSummary()
        {
            if (_busy) return;
            SetBusy(true, "Собираю цвета выделения…");
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                var result = new SelectionColorSummaryService().InsertSummary();
                Report(result.Message, false);
            }
            catch (Exception error)
            {
                Log.Error("Selection color summary failed.", error);
                Report(error.GetBaseException().Message, true);
            }
            finally { SetBusy(false, null); }
        }
    }
}
