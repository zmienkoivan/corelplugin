using System;
using System.IO;
using System.Security;

namespace VanyaTools.Native
{
    internal static class VanyaToolsDataFolder
    {
        // A leading dot alone does not hide a folder in Windows Explorer.
        // Apply the attribute on access, including folders from older releases.
        internal static void HideForPath(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) return;
            try
            {
                for (var folder = new DirectoryInfo(directory); folder != null; folder = folder.Parent)
                {
                    if (!String.Equals(folder.Name, ".VanyaTools", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!folder.Exists) return;
                    var attributes = File.GetAttributes(folder.FullName);
                    if ((attributes & FileAttributes.Hidden) == 0)
                        File.SetAttributes(folder.FullName, attributes | FileAttributes.Hidden);
                    return;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SecurityException)
            {
                // Shares may deny attribute changes while still allowing data writes.
                Log.Error("Could not hide the VanyaTools data folder", error);
            }
        }
    }
}
