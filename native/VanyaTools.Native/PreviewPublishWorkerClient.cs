using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal static class PreviewPublishWorkerClient
    {
        public static string Run(string server, string key, string source, string optionsJson,
            Action<string> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string cancelPath = source + ".cancel";
            if (File.Exists(cancelPath)) File.Delete(cancelPath);
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "VanyaTools.ReplicateWorker.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("Не найден помощник публикации. Обновите Vanya Tools и перезапустите CorelDRAW.", exe);
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string payload = serializer.Serialize(new Dictionary<string, object>
            {
                ["server"] = server, ["key"] = key, ["source"] = source,
                ["options_json"] = optionsJson, ["cancel_path"] = cancelPath
            });
            var start = new ProcessStartInfo
            {
                FileName = exe, Arguments = "--preview-publish-worker",
                WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (cancellation.Register(() => { try { File.WriteAllText(cancelPath, "cancel"); } catch { } }))
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить помощник публикации.");
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null && e.Data.StartsWith("PROGRESS:", StringComparison.Ordinal))
                        try { progress?.Invoke(e.Data.Substring(9)); } catch { }
                };
                process.BeginErrorReadLine();
                process.StandardInput.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)));
                process.StandardInput.Close();
                var responseTask = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(16 * 60 * 1000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Публикация не завершилась за 16 минут.");
                }
                cancellation.ThrowIfCancellationRequested();
                string responseText;
                try { responseText = Encoding.UTF8.GetString(Convert.FromBase64String(responseTask.GetAwaiter().GetResult().Trim())); }
                catch (FormatException) { throw new InvalidDataException("Помощник публикации вернул неизвестный ответ."); }
                var response = serializer.Deserialize<Dictionary<string, object>>(responseText);
                if (response == null || !response.ContainsKey("ok") || !Convert.ToBoolean(response["ok"]))
                {
                    string error = response != null && response.ContainsKey("error")
                        ? Convert.ToString(response["error"]) : "Сервер не вернул результат.";
                    if (response != null && response.ContainsKey("cancelled") && Convert.ToBoolean(response["cancelled"]))
                        throw new OperationCanceledException(error);
                    throw new InvalidOperationException(error);
                }
                string link = response.ContainsKey("link") ? Convert.ToString(response["link"]) : null;
                if (String.IsNullOrWhiteSpace(link)) throw new InvalidDataException("Сервер не вернул ссылку.");
                return link;
            }
        }
    }
}
