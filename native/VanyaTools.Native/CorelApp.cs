using System;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal static class CorelApp
    {
        private static object _hostApplication;

        public static void SetHostApplication(object app)
        {
            if (app != null)
            {
                _hostApplication = app;
                Log.Info("Corel host application object received.");
            }
        }

        public static dynamic Get()
        {
            if (_hostApplication != null)
            {
                return _hostApplication;
            }

            foreach (var progId in GetProgIds())
            {
                try
                {
                    var app = Marshal.GetActiveObject(progId);
                    Log.Info("Corel application found by ProgID: " + progId);
                    return app;
                }
                catch (COMException)
                {
                }
            }

            throw new InvalidOperationException("CorelDRAW application was not found.");
        }

        private static string[] GetProgIds()
        {
            return new[]
            {
                "CorelDRAW.Application.27",
                "CorelDRAW.Application.26",
                "CorelDRAW.Application.25",
                "CorelDRAW.Application.24",
                "CorelDRAW.Application.23",
                "CorelDRAW.Application.22",
                "CorelDRAW.Application.21",
                "CorelDRAW.Application.20",
                "CorelDRAW.Application.19",
                "CorelDRAW.Application.18",
                "CorelDRAW.Application.17",
                "CorelDRAW.Application"
            };
        }
    }
}
