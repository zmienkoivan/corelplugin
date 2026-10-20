using System;
using System.IO;
using System.Reflection;

namespace VanyaTools.Native
{
    internal static class SportArtworkCapture
    {
        internal static string CaptureObjects()
        {
            dynamic app = CorelApp.Get(), document = app.ActiveDocument;
            if (document == null || Convert.ToInt32(app.ActiveSelectionRange.Count) == 0)
                throw new InvalidOperationException("Выделите рисунок детали: векторные объекты, текст или группу.");
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "sport-patterns");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "artwork-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") +
                "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cdr");
            try
            {
                object options = app.CreateStructSaveAsOptions();
                options.GetType().InvokeMember("Range", BindingFlags.SetProperty, null, options, new object[] { 2 }); // cdrSelection
                options.GetType().InvokeMember("Version", BindingFlags.SetProperty, null, options, new object[] { 0 }); // cdrCurrentVersion
                options.GetType().InvokeMember("Overwrite", BindingFlags.SetProperty, null, options, new object[] { false });
                // Use the current native CDR version, independently of the user's
                // delivery version, so capturing does not downgrade editable art.
                document.GetType().InvokeMember("SaveAsCopy", BindingFlags.InvokeMethod, null,
                    (object)document, new object[] { path, options });
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new InvalidOperationException("Corel не сохранил выделенные объекты в CDR.");
                return path;
            }
            catch
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                throw;
            }
        }
    }
}
