using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal static class OrderServerClient
    {
        internal static Task Check(string server, string key)
        {
            return Task.Run(() => RunWorker("check", server, key, 0, null, null));
        }

        internal static Task<Dictionary<string, object>> Publish(string server, string key,
            Dictionary<string, object> details, IList<byte[]> photos, int orderId = 0)
        {
            return Task.Run(() => RunWorker("publish", server, key, orderId, null,
                new JavaScriptSerializer().Serialize(details), photos));
        }

        internal static Task<Dictionary<string, object>> Get(string server, string key, int orderId)
        {
            return Task.Run(() => RunWorker("get", server, key, orderId, null, null));
        }

        internal static Task<Dictionary<string, object>> GetByNumber(string server, string key, string number) =>
            Task.Run(() => RunWorker("get-number", server, key, 0, number, null));

        internal static Task<Dictionary<string, object>> Unassigned(string server, string key) =>
            Task.Run(() => RunWorker("unassigned", server, key, 0, null, null));

        internal static Task<Dictionary<string, object>> List(string server, string key) =>
            Task.Run(() => RunWorker("list", server, key, 0, null, null));

        internal static Task<Dictionary<string, object>> Assign(string server, string key, int id, string clientKey) =>
            Task.Run(() => RunWorker("assign", server, key, id, null,
                new JavaScriptSerializer().Serialize(new { clientKey })));

        internal static Task<Dictionary<string, object>> SetState(string server, string key,
            int orderId, string state, int revision)
        {
            return Task.Run(() => RunWorker("state", server, key, orderId, null,
                new JavaScriptSerializer().Serialize(new { state, revision })));
        }

        private static Dictionary<string, object> RunWorker(string action, string server, string key,
            int orderId, string number, string details, IList<byte[]> photos = null)
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "VanyaTools.ReplicateWorker.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Помощник заказов не найден. Обновите Vanya Tools.", exe);
            string directory = null;
            var files = new List<string>();
            try
            {
                if (photos != null && photos.Count > 0)
                {
                    directory = Path.Combine(Path.GetTempPath(), "VanyaOrder-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    for (int i = 0; i < photos.Count; i++)
                    {
                        string file = Path.Combine(directory, "photo-" + i + ".jpg");
                        File.WriteAllBytes(file, photos[i]);
                        files.Add(file);
                    }
                }
                var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                var job = new Dictionary<string, object> {
                    ["action"] = action, ["server"] = server, ["key"] = key,
                    ["id"] = orderId, ["number"] = number, ["details"] = details,
                    ["photos"] = files
                };
                var start = new ProcessStartInfo {
                    FileName = exe, Arguments = "--order-worker",
                    WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("Не удалось запустить помощник заказов.");
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    process.StandardInput.Write(Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(serializer.Serialize(job))));
                    process.StandardInput.Close();
                    if (!process.WaitForExit(8 * 60 * 1000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("Сервер заказов не ответил за 8 минут. Проверьте канал перед повтором.");
                    }
                    Dictionary<string, object> reply;
                    try
                    {
                        string json = Encoding.UTF8.GetString(Convert.FromBase64String(output.GetAwaiter().GetResult().Trim()));
                        reply = serializer.Deserialize<Dictionary<string, object>>(json);
                    }
                    catch (Exception error) when (error is FormatException || error is ArgumentException)
                    {
                        throw new InvalidDataException("Помощник заказов не вернул ответ: " +
                            errors.GetAwaiter().GetResult());
                    }
                    if (reply == null || !reply.ContainsKey("ok") || !Convert.ToBoolean(reply["ok"]))
                        throw new InvalidOperationException(reply != null && reply.ContainsKey("error")
                            ? Convert.ToString(reply["error"]) : "Сервер заказов не ответил.");
                    if (action == "check") return new Dictionary<string, object>();
                    string data = reply.ContainsKey("data") ? Convert.ToString(reply["data"]) : null;
                    return serializer.Deserialize<Dictionary<string, object>>(data);
                }
            }
            finally
            {
                foreach (string file in files) try { File.Delete(file); } catch { }
                if (directory != null) try { Directory.Delete(directory); } catch { }
            }
        }

    }
}
