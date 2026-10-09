using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace VanyaTools.Native
{
    // Corel's plugin commands are real customizable commands. The WPF toolbar
    // hosts are only controls and do not appear as commands in Corel's list.
    internal static class CorelPluginCommands
    {
        internal const string OpenDocker = "VanyaTools.OpenDocker";
        internal const string TrimRaster = "VanyaTools.TrimRaster";
        internal const string FitFrame = "VanyaTools.FitFrame";

        private const string DockerId = "e75f8206-5448-4578-ae13-ee8d9a4f15c9";
        private const int PluginCommandDispId = 20;
        private static readonly Guid ApplicationEventsId =
            new Guid("b05800c5-9aa4-44fd-9547-4f91eb757ac4");
        private static readonly Action<string> CommandHandler = HandleCommand;
        private static object _registeredApp;

        internal static void EnsureRegistered(object app)
        {
            if (app == null || ReferenceEquals(_registeredApp, app)) return;
            try
            {
                ComEventsHelper.Combine(app, ApplicationEventsId,
                    PluginCommandDispId, CommandHandler);
                dynamic corel = app;
                corel.AddPluginCommand(OpenDocker, "Vanya Tools: открыть панель",
                    "Открыть панель Vanya Tools");
                corel.AddPluginCommand(TrimRaster, "Vanya Tools: обрезать растр",
                    "Обрезать выбранный растр");
                corel.AddPluginCommand(FitFrame, "Vanya Tools: подогнать рамку",
                    "Подогнать рамку под принт");
                _registeredApp = app;
                Log.Info("Corel plugin commands registered.");
            }
            catch (Exception error)
            {
                Log.Error("Could not register Corel plugin commands.", error);
                try { ComEventsHelper.Remove(app, ApplicationEventsId,
                    PluginCommandDispId, CommandHandler); }
                catch { }
            }
        }

        private static void HandleCommand(string commandId)
        {
            try
            {
                if (String.Equals(commandId, OpenDocker, StringComparison.OrdinalIgnoreCase))
                {
                    CorelApp.Get().FrameWork.ShowDocker(DockerId);
                }
                else if (String.Equals(commandId, TrimRaster, StringComparison.OrdinalIgnoreCase))
                {
                    if (!VanyaToolsDocker.TryRunToolbarTrim())
                        new BitmapTrimService().TrimSelected(TrimMode.TransparentPixels,
                            TrimSides.All, 2, true);
                }
                else if (String.Equals(commandId, FitFrame, StringComparison.OrdinalIgnoreCase))
                {
                    if (!VanyaToolsDocker.TryRunToolbarFitFrame())
                        PrintFrameFitService.FitSelected(PrintFrameAnchor.TopCenter);
                }
            }
            catch (Exception error)
            {
                Log.Error("Corel plugin command failed: " + commandId, error);
                MessageBox.Show(error.Message, "Vanya Tools", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
