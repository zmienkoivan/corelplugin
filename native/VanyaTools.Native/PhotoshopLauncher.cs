using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal static class PhotoshopLauncher
    {
        internal static string NormalizeExecutable(string value)
        {
            string path = Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"'));
            if (path.Length == 0) return "";
            if (!Path.IsPathRooted(path) || !String.Equals(Path.GetFileName(path), "Photoshop.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выберите файл Photoshop.exe установленного Photoshop.");
            return Path.GetFullPath(path);
        }

        internal static string Find(string saved)
        {
            if (!String.IsNullOrWhiteSpace(saved))
            {
                try
                {
                    string path = NormalizeExecutable(saved);
                    if (File.Exists(path)) return path;
                }
                catch (Exception error) { Log.Error("Could not use saved Photoshop path.", error); }
            }
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Photoshop.exe"))
                        {
                            string path = NormalizeExecutable(key?.GetValue(null) as string);
                            if (path.Length > 0 && File.Exists(path)) return path;
                        }
                    }
                    catch (Exception error) { Log.Error("Could not inspect Photoshop App Paths.", error); }
            foreach (string programFiles in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
            {
                if (String.IsNullOrWhiteSpace(programFiles)) continue;
                string adobe = Path.Combine(programFiles, "Adobe");
                try
                {
                    if (!Directory.Exists(adobe)) continue;
                    foreach (string directory in Directory.GetDirectories(adobe, "Adobe Photoshop*").OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        string path = Path.Combine(directory, "Photoshop.exe");
                        if (File.Exists(path)) return path;
                    }
                }
                catch (Exception error) { Log.Error("Could not inspect Adobe installation folders.", error); }
            }
            return "";
        }

        internal static ProcessStartInfo StartInfo(string executable, string image)
        {
            executable = NormalizeExecutable(executable);
            if (executable.Length == 0 || !File.Exists(executable))
                throw new InvalidOperationException("Photoshop не найден. Укажите Photoshop.exe в настройках.");
            if (String.IsNullOrWhiteSpace(image) || !Path.IsPathRooted(image) || image.IndexOf('"') >= 0 ||
                !String.Equals(Path.GetExtension(image), ".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(image))
                throw new InvalidOperationException("Не найден готовый PNG для Photoshop.");
            return new ProcessStartInfo {
                FileName = executable, Arguments = "\"" + Path.GetFullPath(image) + "\"",
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false
            };
        }
    }
}
