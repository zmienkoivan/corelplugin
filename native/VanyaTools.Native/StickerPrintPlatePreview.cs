using System;

namespace VanyaTools.Native
{
    internal static class StickerPrintPlatePreview
    {
        internal static bool Read()=>Read((object)CorelApp.Get());
        internal static bool Read(object application)
        {
            dynamic app=application;
            return app.ActiveDocument!=null && Convert.ToBoolean(app.ActiveWindow.ActiveView.SimulateOverprints);
        }
        internal static void Set(bool enabled)=>Set((object)CorelApp.Get(),enabled);
        internal static void Set(object application,bool enabled)
        {
            dynamic app=application;
            if(app.ActiveDocument==null)throw new InvalidOperationException("Откройте документ для просмотра наложений.");
            app.ActiveWindow.ActiveView.SimulateOverprints=enabled;
            if(Convert.ToBoolean(app.ActiveWindow.ActiveView.SimulateOverprints)!=enabled)
                throw new InvalidOperationException("Corel не изменил просмотр наложений.");
            app.ActiveWindow.Refresh();
        }
    }
}
