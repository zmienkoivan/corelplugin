using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal static class PreviewAdminWorkerClient
    {
        public static List<Dictionary<string, object>> List(string server, string key)
        {
            string data = Request("list", server, key, null);
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }
                .Deserialize<List<Dictionary<string, object>>>(data)
                ?? new List<Dictionary<string, object>>();
        }

        public static void Delete(string server, string key, string id)
        {
            Request("delete", server, key, id);
        }

        private static string Request(string action, string server, string key, string id)
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "VanyaTools.ReplicateWorker.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("Не найден помощник публикации. Обновите Vanya Tools и перезапустите CorelDRAW.", exe);
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var job = new Dictionary<string, object>
            {
                ["action"] = action, ["server"] = server, ["key"] = key
            };
            if (id != null) job["id"] = id;
            var start = new ProcessStartInfo
            {
                FileName = exe, Arguments = "--preview-publish-worker",
                WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Не удалось запустить помощник публикации.");
                process.StandardInput.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(serializer.Serialize(job))));
                process.StandardInput.Close();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(45000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Сервер превью не ответил за 45 секунд.");
                }
                string responseText;
                try { responseText = Encoding.UTF8.GetString(Convert.FromBase64String(outputTask.GetAwaiter().GetResult().Trim())); }
                catch (FormatException) { throw new InvalidDataException("Помощник публикации вернул неизвестный ответ: " + errorTask.GetAwaiter().GetResult()); }
                var response = serializer.Deserialize<Dictionary<string, object>>(responseText);
                if (response == null || !response.ContainsKey("ok") || !Convert.ToBoolean(response["ok"]))
                    throw new InvalidOperationException(response != null && response.ContainsKey("error")
                        ? Convert.ToString(response["error"]) : "Сервер превью не ответил.");
                return response.ContainsKey("data") ? Convert.ToString(response["data"]) : "";
            }
        }
    }
}
