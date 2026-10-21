using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using VanyaTools.Printing;

namespace VanyaTools.Native
{
    internal static class PrintJobExporter
    {
        internal static async Task<SportPrintExporter.Result> Export(string folder, int dpi, int button,
            PrintIccOptions options, Func<string, bool> confirmPage)
        {
            folder = PrintExportSettings.NormalizeFolder(folder);
            if (String.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Укажите папку этой кнопки в настройках печати.");
            options = (options ?? new PrintIccOptions()).Copy();
            string process = button >= 3 ? "UV" : "DTF";

            string job = Guid.NewGuid().ToString("N");
            string workspace = Path.Combine(Path.GetTempPath(), "Vanya-print-icc-" + job);
            string ready = Path.Combine(workspace, "ready.pdf"), profile = Path.Combine(workspace, "input.icc");
            SportPrintExporter.Result result = null;
            Directory.CreateDirectory(workspace);
            try
            {
                // The native publisher restores the source CDR before this
                // method first yields. All modes prepare locally, so no network
                // folder operation can block Corel's dispatcher.
                result = SportPrintExporter.Export(workspace, dpi, true, button != 0, processName: process, confirmPage: confirmPage);
                string prepared = result.Path;
                if (options.Mode != PrintIccMode.Preserve)
                {
                    await Task.Run(() => {
                        options.Validate();
                        if (options.Mode == PrintIccMode.Assign) File.WriteAllBytes(profile, IccProfileFile.Read(options.ProfilePath).Data);
                    });
                    await ProcessPdf(result.Path, ready, result.WidthMm, result.HeightMm, options.Mode, profile);
                    prepared = ready;
                }
                // Keep the exported filename even when the ICC tool used ready.pdf.
                string delivery = Path.Combine(workspace, Path.GetFileName(result.Path));
                if (prepared != delivery) File.Copy(prepared, delivery, true);
                ConnectionStatus sent = await ConnectivityClient.Deliver(delivery, folder);
                if (!sent.Available) throw new IOException(sent.Message);
                string original = result.Path;
                result.Path = sent.Path; result.Bytes = sent.Bytes;
                TryDelete(original);
                Log.Info("Print delivery complete: mode=" + options.Mode + ", file=" + Path.GetFileName(result.Path));
                return result;
            }
            finally
            {
                if (result != null && String.Equals(Path.GetDirectoryName(result.Path), workspace, StringComparison.OrdinalIgnoreCase)) TryDelete(result.Path);
                TryDelete(ready); TryDelete(profile);
                try { Directory.Delete(workspace, false); }
                catch (Exception error) { Log.Error("Could not remove print ICC workspace.", error); }
            }
        }

        private static async Task ProcessPdf(string input, string output, double width, double height, PrintIccMode mode, string profile)
        {
            string tool = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "FilmPdf", "VanyaTools.FilmPdf.exe");
            if (!File.Exists(tool)) throw new InvalidOperationException("Не найден модуль ICC PDF. Установите полный пакет Vanya Tools.");
            string[] arguments = { input, output, width.ToString("R", CultureInfo.InvariantCulture), height.ToString("R", CultureInfo.InvariantCulture),
                mode == PrintIccMode.Assign ? "assign" : "remove", mode == PrintIccMode.Assign ? profile : "-" };
            var command = new StringBuilder();
            foreach (string argument in arguments) { if (command.Length > 0) command.Append(' '); command.Append(Quote(argument)); }
            var start = new ProcessStartInfo(tool) { Arguments = command.ToString(), UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
            using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
            {
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) => completed.TrySetResult(true);
                process.Start();
                Task<string> error = process.StandardError.ReadToEndAsync(), text = process.StandardOutput.ReadToEndAsync();
                await completed.Task;
                string detail = await error; await text;
                if (process.ExitCode != 0 || !File.Exists(output))
                    throw new InvalidOperationException("PDF не отправлен: обработка ICC не завершена. " + detail.Trim());
            }
        }

        // Windows command-line argument quoting; no shell is involved.
        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', 2 * slashes + 1).Append('"');
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', 2 * slashes).Append('"').ToString();
        }
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception error) { Log.Error("Could not remove temporary print ICC file.", error); }
        }
    }
}
