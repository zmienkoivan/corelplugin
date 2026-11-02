using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace VanyaTools.Native
{
    internal static class PrintPdfText
    {
        // Apply last: another PDF option/preset must not silently turn this off.
        // Corel outlines during publishing; editable source text is untouched.
        internal static void RequireCurves(object settings)
        {
            try
            {
                Type type = settings.GetType();
                type.InvokeMember("TextAsCurves", BindingFlags.SetProperty, null, settings, new object[] { true });
                if (!Convert.ToBoolean(type.InvokeMember("TextAsCurves", BindingFlags.GetProperty, null, settings, null)))
                    throw new InvalidOperationException("Corel не применил перевод текста в кривые.");
            }
            catch (Exception error)
            { throw new InvalidOperationException("PDF не отправлен: не удалось включить обязательный перевод текста в кривые. " + error.GetBaseException().Message, error); }
        }

        // Used by ordinary sport-sheet PDF export. Queue jobs and film PDFs
        // use the same guard inside their existing ICC helper before delivery.
        internal static async Task CheckFileAsync(string path)
        {
            string tool = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "FilmPdf", "VanyaTools.FilmPdf.exe");
            if (!File.Exists(tool)) throw new InvalidOperationException("Не найден модуль проверки PDF. Установите полный пакет Vanya Tools.");
            var start = new ProcessStartInfo(tool) { Arguments = "--require-curves \"" + Path.GetFullPath(path) + "\"",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
            using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
            {
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) => completed.TrySetResult(true);
                process.Start();
                Task<string> error = process.StandardError.ReadToEndAsync(), text = process.StandardOutput.ReadToEndAsync();
                await completed.Task;
                string detail = await error; await text;
                if (process.ExitCode != 0) throw new InvalidOperationException(process.ExitCode == 3
                    ? "PDF не отправлен: в нём остался текст, не переведённый в кривые. " + detail.Trim()
                    : "PDF не отправлен: проверка текста не завершена. " + detail.Trim());
            }
        }
    }
}
