using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Updater
{
    internal static class ConnectivityWorker
    {
        internal static int Run()
        {
            var serializer = new JavaScriptSerializer();
            var reply = new Dictionary<string, object>();
            string service = "Replicate";
            try
            {
                var job = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(
                    Convert.FromBase64String(Console.In.ReadToEnd().Trim())));
                string action = Value(job, "action");
                if (action == "update-release")
                {
                    service = "GitHub";
                    reply["Data"] = UpdateReleaseMetadata.Read();
                    reply["Available"] = true;
                    reply["Message"] = "GitHub: сведения об обновлении получены";
                }
                else if (action == "replicate")
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    var request = (HttpWebRequest)WebRequest.Create("https://api.replicate.com/v1/account");
                    request.Method = "GET"; request.Timeout = 4500; request.ReadWriteTimeout = 4500;
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + Value(job, "token");
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        reply["Available"] = response.StatusCode == HttpStatusCode.OK;
                        reply["Message"] = "Replicate: связь с API и ключ работают";
                    }
                }
                else if (action == "folder" || action == "deliver")
                {
                    string folder = Path.GetFullPath(Value(job, "folder"));
                    if (!Directory.Exists(folder)) throw new IOException("Папка печати недоступна: " + folder);
                    if (action == "deliver")
                    {
                        string source = Value(job, "source");
                        string name = Path.GetFileName(source), target = Path.Combine(folder, name);
                        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ожидается PDF для печати.");
                        for (int copy = 2; File.Exists(target); copy++)
                            target = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + " - " + copy + ".pdf");
                        string staging = Path.Combine(folder, ".Vanya-print-" + Guid.NewGuid().ToString("N") + ".partial");
                        try
                        {
                            long bytes = new FileInfo(source).Length;
                            File.Copy(source, staging); File.Move(staging, target);
                            reply["Path"] = target; reply["Bytes"] = bytes;
                        }
                        finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { } }
                    }
                    reply["Available"] = true; reply["Message"] = "Папка печати доступна";
                }
                else throw new InvalidOperationException("Неизвестная проверка подключения.");
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                reply["Available"] = false;
                reply["Message"] = response != null ? service + ": HTTP " + (int)response.StatusCode +
                    (service == "Replicate" && response.StatusCode == HttpStatusCode.Unauthorized ? " · проверьте ключ API" : "") :
                    service + ": " + (error.Status == WebExceptionStatus.Timeout ? "сервер не ответил вовремя" : error.Message);
                response?.Dispose();
            }
            catch (Exception error) { reply["Available"] = false; reply["Message"] = error.Message; }
            Console.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(serializer.Serialize(reply))));
            return 0;
        }
        private static string Value(Dictionary<string, object> value, string key) =>
            value != null && value.TryGetValue(key, out object result) ? Convert.ToString(result) : "";
    }
}
