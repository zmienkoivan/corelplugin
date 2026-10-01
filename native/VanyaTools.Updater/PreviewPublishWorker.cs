using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VanyaTools.Updater
{
    internal static class PreviewPublishWorker
    {
        public static int Run()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string cancelPath = null;
            try
            {
                var job = serializer.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(Console.In.ReadToEnd().Trim())));
                if (job == null) throw new InvalidDataException("Пустой запрос публикации.");
                string server = Required(job, "server").TrimEnd('/');
                string key = Required(job, "key");
                Uri root;
                if (!Uri.TryCreate(server, UriKind.Absolute, out root) ||
                    (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps) ||
                    !String.IsNullOrEmpty(root.UserInfo) || root.AbsolutePath != "/")
                    throw new InvalidDataException("Введите адрес сервера вида http://127.0.0.1:4173.");
                if (root.Scheme != Uri.UriSchemeHttps && !root.IsLoopback)
                    throw new InvalidOperationException("Для удалённого сервера требуется HTTPS.");
                string action = job.ContainsKey("action") ? Convert.ToString(job["action"]) : "publish";
                if (action == "list" || action == "delete")
                {
                    string id = action == "delete" ? Required(job, "id") : null;
                    string data = AdminRequest(root, key, action, id);
                    Reply(serializer, true, null, null, false, data);
                    return 0;
                }
                if (action != "publish") throw new InvalidDataException("Неизвестная команда публикации.");
                string source = Required(job, "source");
                cancelPath = Required(job, "cancel_path");
                if (!File.Exists(source)) throw new FileNotFoundException("PNG выделения не найден.", source);
                string optionsJson = Required(job, "options_json");
                string encodedOptions = Convert.ToBase64String(Encoding.UTF8.GetBytes(optionsJson))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (encodedOptions.Length > 4096) throw new InvalidDataException("Параметры публикации слишком длинные.");
                CheckCancelled(cancelPath);
                Console.Error.WriteLine("PROGRESS:uploading");
                var request = (HttpWebRequest)WebRequest.Create(new Uri(root, "/api/previews"));
                request.Method = "POST";
                request.ContentType = "image/png";
                request.Headers["X-Admin-Key"] = key;
                request.Headers["X-Preview-Options"] = encodedOptions;
                request.ContentLength = new FileInfo(source).Length;
                request.Timeout = 15 * 60 * 1000;
                request.ReadWriteTimeout = 15 * 60 * 1000;
                using (var timer = new Timer(_ =>
                    { if (File.Exists(cancelPath)) request.Abort(); }, null, 500, 500))
                {
                    using (Stream body = request.GetRequestStream())
                    using (Stream image = File.OpenRead(source)) image.CopyTo(body);
                    CheckCancelled(cancelPath);
                    Console.Error.WriteLine("PROGRESS:processing");
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        CheckCancelled(cancelPath);
                        var published = serializer.Deserialize<Dictionary<string, object>>(content);
                        string link = published != null && published.ContainsKey("link")
                            ? Convert.ToString(published["link"]) : null;
                        if (String.IsNullOrWhiteSpace(link))
                            throw new InvalidDataException("Сервер не вернул ссылку на превью.");
                        Reply(serializer, true, link, null, false);
                    }
                }
                return 0;
            }
            catch (Exception error)
            {
                bool cancelled = cancelPath != null && File.Exists(cancelPath);
                Reply(serializer, false, null, cancelled ? "Публикация отменена." : ApiError(error), cancelled);
                return 1;
            }
        }

        private static string Required(Dictionary<string, object> values, string key)
        {
            string value = values.ContainsKey(key) ? Convert.ToString(values[key]) : null;
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Не заполнено поле " + key + ".");
            return value;
        }

        private static string AdminRequest(Uri root, string key, string action, string id)
        {
            if (id != null && !Guid.TryParseExact(id, "D", out _))
                throw new InvalidDataException("Некорректный идентификатор превью.");
            string endpoint = action == "list" ? "/api/previews" : "/api/previews/" + id;
            var request = (HttpWebRequest)WebRequest.Create(new Uri(root, endpoint));
            request.Method = action == "list" ? "GET" : "DELETE";
            request.Headers["X-Admin-Key"] = key;
            request.Timeout = 30000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static void CheckCancelled(string path)
        {
            if (File.Exists(path)) throw new OperationCanceledException("Публикация отменена.");
        }

        private static string ApiError(Exception error)
        {
            var web = error as WebException;
            if (web == null || web.Response == null) return error.GetBaseException().Message;
            try
            {
                using (var reader = new StreamReader(web.Response.GetResponseStream(), Encoding.UTF8))
                {
                    var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    return data != null && data.ContainsKey("error") ? Convert.ToString(data["error"]) : web.Message;
                }
            }
            catch { return web.Message; }
        }

        private static void Reply(JavaScriptSerializer serializer, bool ok, string link, string error, bool cancelled, string data = null)
        {
            string payload = serializer.Serialize(new Dictionary<string, object>
                { ["ok"] = ok, ["link"] = link, ["error"] = error, ["cancelled"] = cancelled, ["data"] = data });
            Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)));
        }
    }
}
