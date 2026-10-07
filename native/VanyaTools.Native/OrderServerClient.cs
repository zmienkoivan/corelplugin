using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal static class OrderServerClient
    {
        internal static Task Check(string server, string key)
        {
            return Task.Run(async () =>
            {
                using (var client = Client(key))
                using (var response = await client.GetAsync(Address(server, "/api/orders")).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Dictionary<string, object> data = null;
                        try { data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body); }
                        catch { }
                        throw new InvalidOperationException(data != null && data.ContainsKey("error")
                            ? Convert.ToString(data["error"]) : "Сервер заказов: HTTP " + (int)response.StatusCode);
                    }
                }
            });
        }

        internal static Task<Dictionary<string, object>> Publish(string server, string key,
            Dictionary<string, object> details, IList<byte[]> photos, int orderId = 0)
        {
            return Task.Run(async () =>
            {
                using (var client = Client(key))
                using (var form = new MultipartFormDataContent())
                {
                    form.Add(new StringContent(new JavaScriptSerializer().Serialize(details), Encoding.UTF8), "details");
                    for (int i = 0; i < photos.Count; i++)
                    {
                        var content = new ByteArrayContent(photos[i]);
                        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                        form.Add(content, "photo" + i, "order-" + (i + 1) + ".jpg");
                    }
                    string endpoint = Address(server, orderId == 0 ? "/api/orders" : "/api/orders/" + orderId);
                    using (var response = await (orderId == 0
                        ? client.PostAsync(endpoint, form)
                        : client.PutAsync(endpoint, form)).ConfigureAwait(false))
                        return await Read(response).ConfigureAwait(false);
                }
            });
        }

        internal static Task<Dictionary<string, object>> Get(string server, string key, int orderId)
        {
            return Task.Run(async () =>
            {
                using (var client = Client(key))
                using (var response = await client.GetAsync(Address(server, "/api/orders/" + orderId)).ConfigureAwait(false))
                    return await Read(response).ConfigureAwait(false);
            });
        }

        internal static Task<Dictionary<string, object>> SetState(string server, string key,
            int orderId, string state, int revision)
        {
            return Task.Run(async () =>
            {
                using (var client = Client(key))
                using (var request = new HttpRequestMessage(new HttpMethod("PATCH"),
                    Address(server, "/api/orders/" + orderId + "/state")))
                {
                    request.Content = new StringContent(new JavaScriptSerializer().Serialize(
                        new { state, revision }), Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request).ConfigureAwait(false))
                        return await Read(response).ConfigureAwait(false);
                }
            });
        }

        private static HttpClient Client(string key)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Укажите ключ сервера заказов.");
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-admin-key", key.Trim());
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        }

        private static string Address(string server, string path)
        {
            if (!Uri.TryCreate(server?.TrimEnd('/'), UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Host != "localhost" && uri.Host != "127.0.0.1"))
                throw new InvalidOperationException("Укажите HTTPS-адрес сервера заказов.");
            return uri.GetLeftPart(UriPartial.Authority) + path;
        }

        private static async Task<Dictionary<string, object>> Read(HttpResponseMessage response)
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Dictionary<string, object> data;
            try { data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body); }
            catch { throw new InvalidOperationException("Сервер заказов вернул непонятный ответ."); }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(data != null && data.ContainsKey("error")
                    ? Convert.ToString(data["error"]) : "Сервер заказов: HTTP " + (int)response.StatusCode);
            return data;
        }
    }
}
