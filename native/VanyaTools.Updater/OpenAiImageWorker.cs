using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VanyaTools.Updater
{
    // Keep API credentials and network requests out of the CorelDRAW process.
    internal static class OpenAiImageWorker
    {
        public static int Run()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            string cancelPath = null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var job = serializer.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(Console.In.ReadToEnd().Trim())));
                if (job == null) throw new InvalidDataException("Пустой запрос OpenAI.");
                string token = Required(job, "token");
                string model = Required(job, "model");
                string prompt = Required(job, "prompt");
                string imagePath = Required(job, "image_path");
                string outputPath = Required(job, "output_path");
                cancelPath = Required(job, "cancel_path");
                string stylePath = job.ContainsKey("style_path") ? Convert.ToString(job["style_path"]) : null;
                if (!File.Exists(imagePath)) throw new FileNotFoundException("Не найден PNG выделения.", imagePath);
                if (!String.IsNullOrEmpty(stylePath) && !File.Exists(stylePath))
                    throw new FileNotFoundException("Не найден образец стиля.", stylePath);
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                { Reply(serializer, true, null, false); return 0; }
                CheckCancelled(cancelPath);
                Console.Error.WriteLine("PROGRESS:sending");
                var request = (HttpWebRequest)WebRequest.Create("https://api.openai.com/v1/images/edits");
                request.Method = "POST";
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                request.Timeout = 10 * 60 * 1000;
                request.ReadWriteTimeout = 10 * 60 * 1000;
                string boundary = "VanyaTools" + Guid.NewGuid().ToString("N");
                request.ContentType = "multipart/form-data; boundary=" + boundary;
                using (var cancelTimer = new Timer(_ =>
                    { if (File.Exists(cancelPath)) request.Abort(); }, null, 500, 500))
                {
                    using (Stream body = request.GetRequestStream())
                    {
                        Field(body, boundary, "model", model);
                        Field(body, boundary, "prompt", prompt);
                        Field(body, boundary, "quality", "high");
                        Field(body, boundary, "size", "auto");
                        Field(body, boundary, "output_format", "png");
                        Image(body, boundary, imagePath, "source.png");
                        if (!String.IsNullOrEmpty(stylePath)) Image(body, boundary, stylePath, "style.png");
                        Write(body, "--" + boundary + "--\r\n");
                    }
                    CheckCancelled(cancelPath);
                    Console.Error.WriteLine("PROGRESS:processing");
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        CheckCancelled(cancelPath);
                        var data = serializer.Deserialize<Dictionary<string, object>>(json);
                        var images = data != null && data.ContainsKey("data") ? data["data"] as IList : null;
                        var image = images != null && images.Count > 0 ? images[0] as Dictionary<string, object> : null;
                        string encoded = image != null && image.ContainsKey("b64_json")
                            ? Convert.ToString(image["b64_json"]) : null;
                        if (String.IsNullOrWhiteSpace(encoded))
                            throw new InvalidDataException("OpenAI не вернул изображение.");
                        Console.Error.WriteLine("PROGRESS:downloading");
                        byte[] bytes = Convert.FromBase64String(encoded);
                        if (bytes.Length == 0) throw new InvalidDataException("OpenAI вернул пустое изображение.");
                        File.WriteAllBytes(outputPath + ".part", bytes);
                        if (File.Exists(outputPath)) File.Replace(outputPath + ".part", outputPath, null);
                        else File.Move(outputPath + ".part", outputPath);
                    }
                }
                Reply(serializer, true, null, false);
                return 0;
            }
            catch (Exception ex)
            {
                bool cancelled = !String.IsNullOrEmpty(cancelPath) && File.Exists(cancelPath);
                string message = cancelled ? "Операция отменена." : ApiError(ex);
                Reply(serializer, false, message, cancelled);
                return 1;
            }
        }

        private static string Required(Dictionary<string, object> values, string key)
        {
            string value = values.ContainsKey(key) ? Convert.ToString(values[key]) : null;
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Не заполнено поле " + key + ".");
            return value;
        }

        private static void CheckCancelled(string path)
        {
            if (File.Exists(path)) throw new OperationCanceledException("Операция отменена.");
        }

        private static string ApiError(Exception error)
        {
            var web = error as WebException;
            if (web == null || web.Response == null) return error.Message;
            try
            {
                using (var reader = new StreamReader(web.Response.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                    var details = parsed != null && parsed.ContainsKey("error")
                        ? parsed["error"] as Dictionary<string, object> : null;
                    string message = details != null && details.ContainsKey("message")
                        ? Convert.ToString(details["message"]) : web.Message;
                    var response = web.Response as HttpWebResponse;
                    return "OpenAI HTTP " + (response == null ? "ошибка" : ((int)response.StatusCode).ToString()) + ": " + message;
                }
            }
            catch { return web.Message; }
        }

        private static void Field(Stream stream, string boundary, string name, string value)
        {
            Write(stream, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name +
                "\"\r\n\r\n" + value + "\r\n");
        }

        private static void Image(Stream stream, string boundary, string path, string filename)
        {
            Write(stream, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"image[]\"; filename=\"" + filename + "\"\r\n" +
                "Content-Type: image/png\r\n\r\n");
            using (var file = File.OpenRead(path)) file.CopyTo(stream);
            Write(stream, "\r\n");
        }

        private static void Write(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void Reply(JavaScriptSerializer serializer, bool ok, string error, bool cancelled)
        {
            string payload = serializer.Serialize(new Dictionary<string, object>
                { ["ok"] = ok, ["error"] = error, ["cancelled"] = cancelled });
            Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)));
        }
    }
}
