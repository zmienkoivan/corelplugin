using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    /// <summary>
    /// Sends Replicate work to the standalone updater process so CorelDRAW's
    /// process-level network restrictions do not block the API request.
    /// </summary>
    internal static class ReplicateWorkerClient
    {
        public static void Run(string model, string token, Dictionary<string, object> input, string outputPath, Action<string> progress = null)
        {
            string exe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "VanyaTools.ReplicateWorker.exe");
            if (!File.Exists(exe))
                throw new InvalidOperationException("Не найден сетевой помощник Vanya Tools. Завершите обновление до версии 1.0.52 и перезапустите CorelDRAW.");

            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string payload = serializer.Serialize(new Dictionary<string, object>
            {
                ["token"] = token,
                ["model"] = model,
                ["input"] = input,
                ["output_path"] = outputPath
            });
            string encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--replicate-worker",
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить сетевой помощник Vanya Tools.");
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null || !e.Data.StartsWith("PROGRESS:", StringComparison.Ordinal)) return;
                    try { if (progress != null) progress(e.Data.Substring("PROGRESS:".Length)); } catch { }
                };
                process.BeginErrorReadLine();
                process.StandardInput.Write(encodedPayload);
                process.StandardInput.Close();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(12 * 60 * 1000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Replicate не завершил обработку за 12 минут.");
                }
                string encodedResponse = outputTask.GetAwaiter().GetResult().Trim();
                string responseText;
                try { responseText = Encoding.UTF8.GetString(Convert.FromBase64String(encodedResponse)); }
                catch (FormatException) { throw new InvalidOperationException("Сетевой помощник вернул ответ в неизвестном формате."); }
                var response = serializer.Deserialize<Dictionary<string, object>>(responseText);
                bool ok = response != null && response.ContainsKey("ok") && Convert.ToBoolean(response["ok"]);
                if (!ok)
                {
                    string error = response != null && response.ContainsKey("error")
                        ? Convert.ToString(response["error"]) : "Сетевой помощник завершился без результата.";
                    throw new InvalidOperationException(error);
                }
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    throw new InvalidDataException("Помощник завершил запрос, но файл результата не появился.");
            }
        }
    }
}
