using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal static class OpenAiImageWorkerClient
    {
        public static void Run(string model, string token, string prompt, string imagePath,
            string stylePath, string outputPath, Action<string> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string cancelPath = outputPath + ".cancel";
            if (File.Exists(cancelPath)) File.Delete(cancelPath);
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "VanyaTools.ReplicateWorker.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Не найден сетевой помощник Vanya Tools. Обновите плагин и перезапустите CorelDRAW.", exe);
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string payload = serializer.Serialize(new Dictionary<string, object>
            {
                ["token"] = token, ["model"] = model, ["prompt"] = prompt,
                ["image_path"] = imagePath, ["style_path"] = stylePath,
                ["output_path"] = outputPath, ["cancel_path"] = cancelPath
            });
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            var start = new ProcessStartInfo
            {
                FileName = exe, Arguments = "--openai-image-worker",
                WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (cancellation.Register(() => { try { File.WriteAllText(cancelPath, "cancel"); } catch { } }))
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить помощник OpenAI.");
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null && e.Data.StartsWith("PROGRESS:", StringComparison.Ordinal))
                        try { progress?.Invoke(e.Data.Substring(9)); } catch { }
                };
                process.BeginErrorReadLine();
                process.StandardInput.Write(encoded);
                process.StandardInput.Close();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(12 * 60 * 1000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("OpenAI не завершил обработку за 12 минут.");
                }
                string responseText;
                try { responseText = Encoding.UTF8.GetString(Convert.FromBase64String(outputTask.GetAwaiter().GetResult().Trim())); }
                catch (FormatException) { throw new InvalidOperationException("Помощник OpenAI вернул ответ в неизвестном формате."); }
                var response = serializer.Deserialize<Dictionary<string, object>>(responseText);
                if (response == null || !response.ContainsKey("ok") || !Convert.ToBoolean(response["ok"]))
                {
                    string error = response != null && response.ContainsKey("error")
                        ? Convert.ToString(response["error"]) : "OpenAI не вернул результат.";
                    if (response != null && response.ContainsKey("cancelled") && Convert.ToBoolean(response["cancelled"]))
                        throw new OperationCanceledException(error);
                    throw new InvalidOperationException(error);
                }
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    throw new InvalidDataException("Запрос завершился, но изображение не появилось.");
            }
        }
    }
}
