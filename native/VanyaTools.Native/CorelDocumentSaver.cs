using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace VanyaTools.Native
{
    internal static class CorelDocumentSaver
    {
        private static string SettingPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "cdr-save-version.txt");

        internal static int InstalledVersion
        {
            get
            {
                try { return Process.GetCurrentProcess().MainModule.FileVersionInfo.FileMajorPart; }
                catch { return 0; }
            }
        }

        internal static int SelectedVersion
        {
            get
            {
                try
                {
                    if (!File.Exists(SettingPath)) return 0;
                    if (Int32.TryParse(File.ReadAllText(SettingPath).Trim(),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) &&
                        IsSupported(version)) return version;
                }
                catch (Exception error) { Log.Error("Could not read CDR save version.", error); }
                return 0;
            }
        }

        internal static bool IsSupported(int version)
        {
            int installed = InstalledVersion;
            return version == 0 || installed >= 6 && version >= 5 && version < installed;
        }

        internal static string VersionLabel(int version)
        {
            switch (version)
            {
                case 27: return "CorelDRAW 2026";
                case 26: return "CorelDRAW 2025";
                case 25: return "CorelDRAW 2024";
                case 24: return "CorelDRAW 2022–2023";
                case 23: return "CorelDRAW 2021";
                case 22: return "CorelDRAW 2020";
                case 21: return "CorelDRAW 2019";
                case 20: return "CorelDRAW 2018";
                case 19: return "CorelDRAW 2017";
                case 18: return "CorelDRAW X8";
                case 17: return "CorelDRAW X7";
                case 16: return "CorelDRAW X6";
                case 15: return "CorelDRAW X5";
                case 14: return "CorelDRAW X4";
                case 13: return "CorelDRAW X3";
                default: return "CorelDRAW v" + version.ToString(CultureInfo.InvariantCulture);
            }
        }

        internal static void SelectVersion(int version)
        {
            if (!IsSupported(version))
                throw new InvalidOperationException("Эта версия CDR недоступна в установленном CorelDRAW.");
            Directory.CreateDirectory(Path.GetDirectoryName(SettingPath));
            File.WriteAllText(SettingPath, version.ToString(CultureInfo.InvariantCulture));
        }

        internal static void Save(dynamic document)
        {
            int version = SelectedVersion;
            if (version == 0)
            {
                document.Save();
                return;
            }
            string path = Convert.ToString(document.FullFileName);
            if (String.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Сначала укажите путь для нового CDR.");
            SaveAs(document, path, true, version);
        }

        internal static void SaveAs(dynamic document, string path, bool overwrite = false)
        {
            SaveAs(document, path, overwrite, SelectedVersion);
        }

        private static void SaveAs(dynamic document, string path, bool overwrite, int version)
        {
            object options = CorelApp.Get().CreateStructSaveAsOptions();
            if (version != 0)
                options.GetType().InvokeMember("Version", BindingFlags.SetProperty,
                    null, options, new object[] { version });
            if (overwrite)
                options.GetType().InvokeMember("Overwrite", BindingFlags.SetProperty,
                    null, options, new object[] { true });
            document.GetType().InvokeMember("SaveAs", BindingFlags.InvokeMethod,
                null, (object)document, new object[] { path, options });
        }
    }
}
