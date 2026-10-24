using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed partial class SportLayoutTab
    {
        private System.Windows.Controls.Button _arrangeReprint;

        private async void ArrangeReprint(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SaveDraft(); _busy = true; SetActionButtons(false);
            try
            {
                var options = ReadOptions();
                object reprintDocument = (object)CorelApp.Get().ActiveDocument;
                _progress.Text = "Читаю текущие объекты рабочего листа допечатки…";
                await Dispatcher.Yield(DispatcherPriority.Render);
                if (!SportProjectPlayers.SameDocument(reprintDocument, (object)CorelApp.Get().ActiveDocument))
                    throw new InvalidOperationException("Активный CDR изменился. Откройте нужный проект и повторите расстановку допечатки.");
                Func<IList<SportSheetNesting.Item>, IList<string>, SportNestingChoice> preview = (items, labels) =>
                {
                    var window = new SportNestingWindow(items, labels, options, false);
                    Window owner = Window.GetWindow(this);
                    if (owner != null) window.Owner = owner;
                    else
                    {
                        var source = PresentationSource.FromVisual(this) as HwndSource;
                        if (source != null) new WindowInteropHelper(window).Owner = source.Handle;
                    }
                    _progress.Text = "Подберите раскладку всех деталей допечатки. Рабочий лист пока сохраняется.";
                    return window.ShowDialog() == true ? window.Choice : null;
                };
                var result = new SportLayoutService().ArrangeReprintWorkspace(options, preview);
                ApplyNestingOptions(options, false);
                ClearCompletedSteps(4);
                _progress.Text = "Допечатка расставлена: " + result.PieceCount + " деталей, страниц: " + result.PageCount +
                    ", общая длина " + (result.SheetHeightMm / 1000).ToString("0.###") + " м. Сохраните CDR.";
                _status("Расставлены существующие детали допечатки с вашими правками.", false);
            }
            catch (OperationCanceledException)
            {
                _progress.Text = "Подбор закрыт. Детали рабочего листа допечатки сохранены без изменений.";
                _status(_progress.Text, false);
            }
            catch (Exception error)
            {
                Log.Error("Sport reprint workspace arrangement failed.", error);
                _progress.Text = "Допечатка не расставлена.";
                _status(error.GetBaseException().Message, true);
            }
            finally { _busy = false; SetActionButtons(true); }
        }
    }
}
