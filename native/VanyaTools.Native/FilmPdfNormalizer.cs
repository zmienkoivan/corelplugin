using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace VanyaTools.Native
{
    internal static class FilmPdfNormalizer
    {
        internal static async Task Normalize(string input, string output, double widthMm, double heightMm)
        {
            string tool = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "FilmPdf", "VanyaTools.FilmPdf.exe");
            if (!File.Exists(tool)) throw new InvalidOperationException("Не найден модуль PDF фотовывода. Установите полный пакет Vanya Tools.");
            var start = new ProcessStartInfo(tool) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true,
                Arguments = "\"" + input + "\" \"" + output + "\" " + widthMm.ToString("R", CultureInfo.InvariantCulture) +
                    " " + heightMm.ToString("R", CultureInfo.InvariantCulture)
            };
            using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
            {
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) => completed.TrySetResult(true);
                process.Start();
                Task<string> error = process.StandardError.ReadToEndAsync(), text = process.StandardOutput.ReadToEndAsync();
                await completed.Task;
                string detail = await error; await text;
                if (process.ExitCode != 0 || !File.Exists(output))
                {
                    Log.Info("Film PDF normalization failed: " + detail);
                    if (process.ExitCode == 2)
                    {
                        string diagnostic = "";
                        try
                        {
                            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "Diagnostics");
                            Directory.CreateDirectory(directory);
                            diagnostic = Path.Combine(directory, "FilmPdfSizeError.pdf");
                            File.Copy(input, diagnostic, true);
                            Log.Info("Film PDF size diagnostic: " + diagnostic);
                        }
                        catch (Exception errorCopy) { Log.Error("Could not retain film size diagnostic.", errorCopy); diagnostic = ""; }
                        throw new InvalidOperationException("Размер страницы PDF не совпадает с фотовыводом 1:1. " + detail.Trim() +
                            (diagnostic.Length == 0 ? "" : "\nPDF для диагностики: " + diagnostic));
                    }
                    throw new InvalidOperationException("Не удалось подготовить PDF без ICC-профилей: " + detail.Trim());
                }
            }
        }
    }
}
