using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class ConnectionStatus
    {
        public bool Available { get; set; }
        public string Message { get; set; }
        public string Path { get; set; }
        public long Bytes { get; set; }
        public string Data { get; set; }
    }

    internal static class ConnectivityClient
    {
        internal static Task<ConnectionStatus> Replicate(string token) => String.IsNullOrWhiteSpace(token)
            ? Task.FromResult(new ConnectionStatus { Message = "Replicate: ключ API не задан" })
            : Task.Run(() => Run(new { action = "replicate", token }, 8000));

        internal static Task<ConnectionStatus> Folder(string folder) => Task.Run(() => Run(new { action = "folder", folder }, 8000));

        internal static Task<ConnectionStatus> Deliver(string source, string folder) =>
            Task.Run(() => Run(new { action = "deliver", source, folder }, 120000));

        internal static async Task<string> LatestRelease()
        {
            ConnectionStatus response = await Task.Run(() => Run(new { action = "update-release" }, 8000,
                new Version(1, 1, 97))).ConfigureAwait(false);
            return ReleaseMetadata(response);
        }

        internal static string ReleaseMetadata(ConnectionStatus response)
        {
            if (response == null || !response.Available)
                throw new IOException(response?.Message ?? "Сетевой помощник не вернул результат проверки обновления.");
            if (String.IsNullOrWhiteSpace(response.Data) || response.Data.Length > 1024 * 1024)
                throw new InvalidDataException("Сетевой помощник вернул пустые или слишком большие данные обновления.");
            return response.Data;
        }

        private static ConnectionStatus Run(object job, int timeout, Version minimumVersion = null)
        {
            try
            {
                string exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VanyaTools", "VanyaTools.ReplicateWorker.exe");
                if (!File.Exists(exe)) throw new FileNotFoundException("Обновите сетевой помощник Vanya Tools.");
                var version = FileVersionInfo.GetVersionInfo(exe);
                Version required = minimumVersion ?? new Version(1, 1, 56);
                if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) < required)
                    throw new InvalidOperationException("Сетевой помощник устарел. Установите полный пакет " + required.ToString(3) + " и перезапустите Corel.");
                var start = new ProcessStartInfo(exe, "--connectivity-worker") { UseShellExecute = false,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                var serializer = new JavaScriptSerializer();
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new IOException("Не удалось запустить сетевой помощник.");
                    Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
                    process.StandardInput.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(serializer.Serialize(job))));
                    process.StandardInput.Close();
                    if (!process.WaitForExit(timeout))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException(timeout > 10000 ?
                            "Передача PDF не завершилась за 2 минуты. Проверьте очередь перед повторной отправкой." :
                            "Проверка подключения не завершилась за 8 секунд.");
                    }
                    if (!output.Wait(1000)) throw new IOException("Помощник не завершил ответ.");
                    return serializer.Deserialize<ConnectionStatus>(Encoding.UTF8.GetString(Convert.FromBase64String(output.Result.Trim())))
                        ?? new ConnectionStatus { Message = "Помощник вернул пустой ответ" };
                }
            }
            catch (Exception error) { return new ConnectionStatus { Message = error.GetBaseException().Message }; }
        }
    }
}
