using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Updater
{
    internal static class OrderWorker
    {
        internal static int Run()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            try
            {
                var job = serializer.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(Console.In.ReadToEnd().Trim())));
                if (job == null) throw new InvalidDataException("Пустой запрос заказа.");
                string server = Required(job, "server").TrimEnd('/');
                string key = Required(job, "key");
                string action = Required(job, "action");
                if (!Uri.TryCreate(server, UriKind.Absolute, out Uri root) ||
                    (root.Scheme != Uri.UriSchemeHttps && !IsLocalHttp(root)) ||
                    !String.IsNullOrEmpty(root.UserInfo) || root.AbsolutePath != "/")
                    throw new InvalidDataException("Укажите HTTPS-адрес или HTTP-адрес сервера в локальной сети.");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
                string route = "/api/orders";
                string method = "GET";
                if (action == "publish")
                {
                    int id = Convert.ToInt32(job.ContainsKey("id") ? job["id"] : 0);
                    route += id > 0 ? "/" + id : "";
                    method = id > 0 ? "PUT" : "POST";
                }
                else if (action == "get")
                {
                    int id = Convert.ToInt32(job["id"]);
                    if (id < 1) throw new InvalidDataException("Неверный номер заказа.");
                    route += "/" + id;
                }
                else if (action == "get-number")
                {
                    string number = Required(job, "number");
                    var match = System.Text.RegularExpressions.Regex.Match(number, @"^(?:(\d{4})/)?(\d{5})$");
                    if (!match.Success)
                        throw new InvalidDataException("Номер заказа: две цифры месяца и три цифры счётчика.");
                    route += "/by-number/" + match.Groups[2].Value;
                    if (match.Groups[1].Success) route += "?year=" + match.Groups[1].Value;
                }
                else if (action == "state")
                {
                    int id = Convert.ToInt32(job["id"]);
                    if (id < 1) throw new InvalidDataException("Неверный номер заказа.");
                    route += "/" + id + "/state";
                    method = "PATCH";
                }
                else if (action == "unassigned") route += "/unassigned";
                else if (action == "assign")
                {
                    int id = Convert.ToInt32(job["id"]);
                    if (id < 1) throw new InvalidDataException("Неверный номер заказа.");
                    route += "/" + id + "/assign";
                    method = "POST";
                }
                else if (action != "check" && action != "list")
                    throw new InvalidDataException("Неизвестная команда заказа.");

                var request = (HttpWebRequest)WebRequest.Create(new Uri(root, route));
                request.Method = method;
                request.Headers["X-Admin-Key"] = key;
                request.Timeout = 8 * 60 * 1000;
                request.ReadWriteTimeout = 8 * 60 * 1000;
                request.KeepAlive = false;
                if (action == "publish") WriteMultipart(request, Required(job, "details"), job["photos"] as IEnumerable);
                if (action == "state" || action == "assign") WriteJson(request, Required(job, "details"));
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    if (action == "unassigned" || action == "list")
                        body = "{\"orders\":" + body + "}";
                    Reply(serializer, true, body, null);
                }
                return 0;
            }
            catch (Exception error)
            {
                Reply(serializer, false, null, Describe(error));
                return 1;
            }
        }

        private static bool IsLocalHttp(Uri root)
        {
            if (root.Scheme != Uri.UriSchemeHttp) return false;
            if (root.IsLoopback) return true;
            if (!IPAddress.TryParse(root.Host, out IPAddress address)) return false;
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && (bytes[0] == 10 ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31));
        }

        private static void WriteMultipart(HttpWebRequest request, string details, IEnumerable images)
        {
            var files = new List<string>();
            if (images != null) foreach (object item in images) files.Add(Convert.ToString(item));
            if (files.Count > 10) throw new InvalidDataException("Слишком много фото.");
            foreach (string file in files)
                if (!File.Exists(file) || new FileInfo(file).Length > 9_500_000)
                    throw new InvalidDataException("Фото заказа не найдено или превышает 9,5 МБ.");
            string boundary = "----VanyaOrder" + Guid.NewGuid().ToString("N");
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            var heads = new List<byte[]>();
            heads.Add(Encoding.UTF8.GetBytes("--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"details\"\r\n\r\n" + details + "\r\n"));
            for (int i = 0; i < files.Count; i++)
                heads.Add(Encoding.UTF8.GetBytes("--" + boundary + "\r\n" +
                    "Content-Disposition: form-data; name=\"photo" + i + "\"; filename=\"order-" + (i + 1) + ".jpg\"\r\n" +
                    "Content-Type: image/jpeg\r\n\r\n"));
            byte[] line = Encoding.ASCII.GetBytes("\r\n");
            byte[] ending = Encoding.ASCII.GetBytes("--" + boundary + "--\r\n");
            long length = heads[0].Length + ending.Length;
            for (int i = 0; i < files.Count; i++)
                length += heads[i + 1].Length + new FileInfo(files[i]).Length + line.Length;
            request.ContentLength = length;
            using (Stream output = request.GetRequestStream())
            {
                output.Write(heads[0], 0, heads[0].Length);
                for (int i = 0; i < files.Count; i++)
                {
                    output.Write(heads[i + 1], 0, heads[i + 1].Length);
                    using (Stream input = File.OpenRead(files[i])) input.CopyTo(output);
                    output.Write(line, 0, line.Length);
                }
                output.Write(ending, 0, ending.Length);
            }
        }

        private static void WriteJson(HttpWebRequest request, string details)
        {
            byte[] body = Encoding.UTF8.GetBytes(details);
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = body.Length;
            using (Stream output = request.GetRequestStream()) output.Write(body, 0, body.Length);
        }

        private static string Required(Dictionary<string, object> values, string name)
        {
            string value = values.ContainsKey(name) ? Convert.ToString(values[name]) : null;
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Не указан параметр: " + name + ".");
            return value;
        }

        private static string Describe(Exception error)
        {
            if (error is WebException web && web.Response != null)
            {
                try
                {
                    using (var reader = new StreamReader(web.Response.GetResponseStream(), Encoding.UTF8))
                    {
                        var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        if (data != null && data.ContainsKey("error")) return Convert.ToString(data["error"]);
                    }
                }
                catch { }
            }
            return error.GetBaseException().Message;
        }

        private static void Reply(JavaScriptSerializer serializer, bool ok, string data, string error)
        {
            string json = serializer.Serialize(new Dictionary<string, object>
                { ["ok"] = ok, ["data"] = data, ["error"] = error });
            Console.Out.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        }
    }
}
