using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal static class TelegramWorkerClient
    {
        internal static Task<string> Check(string token, string channel)
        {
            return Task.Run(() =>
            {
                if (String.IsNullOrWhiteSpace(token) || String.IsNullOrWhiteSpace(channel))
                    throw new InvalidOperationException("Укажите канал и токен бота.");
                var result = Run(new Dictionary<string, object>
                {
                    ["action"] = "check", ["token"] = token, ["channel"] = channel
                }, 30000);
                return Convert.ToString(result["description"]);
            });
        }

        internal static Task<int> Send(string token, string channel, string caption, IList<byte[]> photos)
        {
            return Task.Run(() =>
            {
                if (photos == null || photos.Count < 1 || photos.Count > 10)
                    throw new InvalidOperationException("Для карточки нужно от 1 до 10 кадров.");
                string directory = Path.Combine(Path.GetTempPath(), "VanyaTelegram-" + Guid.NewGuid().ToString("N"));
                var files = new List<string>();
                Directory.CreateDirectory(directory);
                try
                {
                    for (int i = 0; i < photos.Count; i++)
                    {
                        string file = Path.Combine(directory, "photo-" + i + ".jpg");
                        File.WriteAllBytes(file, photos[i]);
                        files.Add(file);
                    }
                    var result = Run(new Dictionary<string, object>
                    {
                        ["action"] = "send", ["token"] = token, ["channel"] = channel,
                        ["caption"] = caption, ["photos"] = files
                    }, 6 * 60 * 1000);
                    return Convert.ToInt32(result["message_id"]);
                }
                finally
                {
                    foreach (string file in files)
                        try { File.Delete(file); } catch { }
                    try { Directory.Delete(directory); } catch { }
                }
            });
        }

        private static Dictionary<string, object> Run(Dictionary<string, object> job, int timeoutMs)
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "VanyaTools.ReplicateWorker.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("Помощник Telegram не найден. Обновите Vanya Tools и перезапустите CorelDRAW.", exe);
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(serializer.Serialize(job)));
            var start = new ProcessStartInfo
            {
                FileName = exe, Arguments = "--telegram-worker",
                WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить помощник Telegram.");
                process.ErrorDataReceived += (_, __) => { };
                process.BeginErrorReadLine();
                process.StandardInput.Write(payload);
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Telegram не ответил вовремя. Проверьте канал перед повторной отправкой, чтобы не создать дубль.");
                }
                Dictionary<string, object> result;
                try
                {
                    string json = Encoding.UTF8.GetString(Convert.FromBase64String(output.GetAwaiter().GetResult().Trim()));
                    result = serializer.Deserialize<Dictionary<string, object>>(json);
                }
                catch (Exception error) when (error is FormatException || error is ArgumentException)
                {
                    throw new InvalidDataException("Помощник Telegram вернул неизвестный ответ. Проверьте канал перед повторной отправкой.");
                }
                if (result == null || !result.ContainsKey("ok") || !Convert.ToBoolean(result["ok"]))
                {
                    string message = result != null && result.ContainsKey("error")
                        ? Convert.ToString(result["error"]) : "Помощник Telegram не вернул результат.";
                    throw new InvalidOperationException(message);
                }
                return result;
            }
        }
    }
}
