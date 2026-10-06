using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Updater
{
    internal static class TelegramWorker
    {
        internal static int Run()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string token = null;
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(Console.In.ReadToEnd().Trim()));
                var job = serializer.Deserialize<Dictionary<string, object>>(json);
                if (job == null) throw new InvalidDataException("Пустой запрос Telegram.");
                string action = Required(job, "action");
                token = Required(job, "token");
                string channel = Required(job, "channel");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                object result;
                if (action == "check")
                    result = new Dictionary<string, object> { ["description"] = Check(token, channel) };
                else if (action == "send")
                    result = new Dictionary<string, object> {
                        ["message_id"] = Send(token, channel, Required(job, "caption"), job["photos"] as IEnumerable)
                    };
                else throw new InvalidDataException("Неизвестная команда Telegram.");
                Reply(serializer, true, result, null);
                return 0;
            }
            catch (Exception error)
            {
                Exception cause = error.GetBaseException();
                string message = error is InvalidOperationException || error is InvalidDataException
                    ? error.Message : "Сбой соединения с Telegram (" + cause.GetType().Name + ": " + cause.Message + ").";
                if (!String.IsNullOrEmpty(token)) message = message.Replace(token, "[скрыто]");
                if (message.Length > 300) message = message.Substring(0, 300) + "…";
                Reply(serializer, false, null, message);
                return 1;
            }
        }

        private static string Check(string token, string channel)
        {
            using (var client = NewClient(TimeSpan.FromSeconds(20)))
            using (var form = new MultipartFormDataContent())
            {
                form.Add(new StringContent(channel), "chat_id");
                ReadResponse(client.PostAsync(Api(token, "getChat"), form).GetAwaiter().GetResult(), token);
                return "бот и канал доступны.";
            }
        }

        private static int Send(string token, string channel, string caption, IEnumerable images)
        {
            var files = new List<string>();
            if (images != null)
                foreach (object item in images) files.Add(Convert.ToString(item));
            if (files.Count < 1 || files.Count > 10)
                throw new InvalidDataException("Для карточки нужно от 1 до 10 кадров.");
            foreach (string file in files)
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 9500000)
                    throw new InvalidDataException("Файл кадра не найден или превышает размер фото Telegram.");
            }
            bool album = files.Count > 1;
            using (var client = NewClient(TimeSpan.FromMinutes(5)))
            using (var form = new MultipartFormDataContent())
            {
                form.Add(new StringContent(channel), "chat_id");
                if (album)
                {
                    var media = new List<Dictionary<string, string>>();
                    for (int i = 0; i < files.Count; i++)
                    {
                        var item = new Dictionary<string, string> {
                            ["type"] = "photo", ["media"] = "attach://photo" + i
                        };
                        if (i == 0) item["caption"] = caption;
                        media.Add(item);
                    }
                    form.Add(new StringContent(new JavaScriptSerializer().Serialize(media), Encoding.UTF8), "media");
                }
                else form.Add(new StringContent(caption, Encoding.UTF8), "caption");
                for (int i = 0; i < files.Count; i++)
                {
                    var content = new StreamContent(File.OpenRead(files[i]));
                    content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    form.Add(content, album ? "photo" + i : "photo", "order-card-" + (i + 1) + ".jpg");
                }
                try
                {
                    string body = ReadResponse(client.PostAsync(Api(token,
                        album ? "sendMediaGroup" : "sendPhoto"), form).GetAwaiter().GetResult(), token);
                    var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                    object message = data["result"];
                    if (album)
                    {
                        var messages = message as object[];
                        if (messages == null || messages.Length == 0)
                            throw new InvalidDataException("Telegram не вернул сообщения альбома.");
                        message = messages[0];
                    }
                    return Convert.ToInt32(((Dictionary<string, object>)message)["message_id"]);
                }
                catch (Exception error) when (error is HttpRequestException || error is System.Threading.Tasks.TaskCanceledException)
                {
                    throw new InvalidOperationException("Сбой соединения с Telegram. Проверьте канал перед повторной отправкой, чтобы не создать дубль.", error);
                }
            }
        }

        private static HttpClient NewClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        }

        private static string Api(string token, string method)
        {
            return "https://api.telegram.org/bot" + token + "/" + method;
        }

        private static string ReadResponse(HttpResponseMessage response, string token)
        {
            using (response)
            {
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Dictionary<string, object> data;
                try { data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body); }
                catch { throw new InvalidDataException("Не удалось прочитать ответ Telegram. Проверьте канал перед повторной отправкой."); }
                if (data == null || !data.ContainsKey("ok"))
                    throw new InvalidDataException("Telegram вернул неполный ответ. Проверьте канал перед повторной отправкой.");
                if (!response.IsSuccessStatusCode || !Convert.ToBoolean(data["ok"]))
                {
                    string reason = data.ContainsKey("description")
                        ? Convert.ToString(data["description"]) : response.ReasonPhrase;
                    throw new InvalidOperationException("Telegram: " + reason.Replace(token, "[скрыто]"));
                }
                return body;
            }
        }

        private static string Required(Dictionary<string, object> job, string key)
        {
            object value;
            if (!job.TryGetValue(key, out value) || value == null || String.IsNullOrWhiteSpace(Convert.ToString(value)))
                throw new InvalidDataException("Не задан параметр Telegram: " + key + ".");
            return Convert.ToString(value);
        }

        private static void Reply(JavaScriptSerializer serializer, bool ok, object result, string error)
        {
            var data = new Dictionary<string, object> { ["ok"] = ok };
            if (result != null)
                foreach (var field in (Dictionary<string, object>)result) data[field.Key] = field.Value;
            if (error != null) data["error"] = error;
            Console.Out.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(serializer.Serialize(data))));
        }
    }
}
