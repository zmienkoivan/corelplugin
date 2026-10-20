using System;
using System.IO;
using VanyaTools.Printing;

namespace VanyaTools.Native
{
    internal enum PrintIccMode { Preserve = 0, Remove = 1, Assign = 2 }

    internal sealed class PrintIccOptions
    {
        public PrintIccMode Mode { get; set; }
        public string ProfilePath { get; set; } = "";
        internal PrintIccOptions Copy() => new PrintIccOptions { Mode = Mode, ProfilePath = ProfilePath ?? "" };
        internal void Validate()
        {
            if (!Enum.IsDefined(typeof(PrintIccMode), Mode)) throw new InvalidOperationException("Выберите режим ICC.");
            if (Mode != PrintIccMode.Assign) return;
            string path = PrintExportSettings.NormalizeFolder(ProfilePath);
            IccProfileFile.Read(path); ProfilePath = path;
        }
        internal string Description
        {
            get
            {
                if (Mode == PrintIccMode.Remove) return "ICC: удалить";
                if (Mode != PrintIccMode.Assign) return "ICC: как в Corel";
                try { return "ICC входной: " + Path.GetFileName(ProfilePath ?? ""); }
                catch { return "ICC входной: проверьте путь в настройках"; }
            }
        }
    }
}
