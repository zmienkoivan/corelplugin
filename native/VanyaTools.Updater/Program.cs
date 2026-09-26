using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VanyaTools.Updater
{
    internal static class Program
    {
        private const string DefaultRepository = "zmienkoivan/corelplugin";

        private static int Main(string[] args)
        {
            if (args != null && args.Length == 1 && args[0] == "--replicate-worker")
                return RunReplicateWorker();

            Console.OutputEncoding = Encoding.UTF8;
            string installedVersionText = GetArgument(args, "--installed-version");
            Version installedVersion = null;
            if (!String.IsNullOrWhiteSpace(installedVersionText) &&
                !Version.TryParse(installedVersionText, out installedVersion))
                installedVersion = null;
            string workDir = Path.Combine(Path.GetTempPath(), "VanyaToolsUpdate-" + Guid.NewGuid().ToString("N"));
            int exitCode = 1;

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string repository = ReadRepository();
                Console.WriteLine("Vanya Tools — проверка обновления");
                ReleaseInfo release;
                using (var spinner = new ConsoleSpinner("Проверяю последнюю версию GitHub"))
                    release = GetLatestRelease(repository);

                Version latestVersion = ParseReleaseVersion(release.TagName);
                if (installedVersion != null && latestVersion <= installedVersion)
                {
                    Console.WriteLine("У вас уже установлена последняя версия " + installedVersion + ". Архив не скачивался.");
                    exitCode = 0;
                }
                else
                {

                Directory.CreateDirectory(workDir);
                string zipPath = Path.Combine(workDir, "VanyaToolsNative.zip");
                string packageDir = Path.Combine(workDir, "package");
                Directory.CreateDirectory(packageDir);

                Console.WriteLine("Найдено обновление " + release.TagName + ". Загружаю пакет...");
                using (var client = new WebClient())
                using (var spinner = new ConsoleSpinner("Загрузка пакета обновления"))
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "VanyaTools-Updater/1.0.20";
                    client.DownloadProgressChanged += (_, e) => spinner.SetMessage("Загрузка пакета: " + e.ProgressPercentage + "%");
                    client.DownloadFile(release.AssetUrl, zipPath);
                }

                using (var spinner = new ConsoleSpinner("Распаковываю пакет обновления"))
                    ZipFile.ExtractToDirectory(zipPath, packageDir);
                string installer = Path.Combine(packageDir, "Install.ps1");
                string addonDll = Path.Combine(packageDir, "VanyaToolsNative", "VanyaTools.Native.dll");
                if (!File.Exists(installer) || !File.Exists(addonDll))
                    throw new InvalidDataException("Загруженный архив не содержит установщик Vanya Tools.");

                SaveRepository(repository);
                Console.WriteLine("Загрузка завершена. Сохраните документы и закройте CorelDRAW.");

                using (var spinner = new ConsoleSpinner("Ожидаю закрытия CorelDRAW для установки"))
                    while (IsCorelRunning()) Thread.Sleep(2000);

                Console.WriteLine("CorelDRAW закрыт. Запускаю установщик...");
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(installer),
                    WorkingDirectory = packageDir,
                    UseShellExecute = true,
                    Verb = IsAdministrator() ? "open" : "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                        throw new InvalidOperationException("Не удалось запустить установщик.");
                    using (var spinner = new ConsoleSpinner("Устанавливаю обновление"))
                        process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("Установщик завершился с кодом " + process.ExitCode + ".");
                }

                Console.WriteLine("Обновление установлено из релиза " + release.TagName + ". Перезапустите CorelDRAW.");
                exitCode = 0;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Обновление не выполнено: " + ex.Message);
                Console.ResetColor();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workDir))
                        Directory.Delete(workDir, true);
                }
                catch { }
            }

            Pause();
            return exitCode;
        }

        // Network requests run in this standalone process instead of inside CorelDRAW.
        // Some desktop security policies deny sockets to CorelDRW.exe while allowing tools.
        private static int RunReplicateWorker()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string encodedJob = Console.In.ReadToEnd().Trim();
                var job = serializer.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(encodedJob)));
                if (job == null) throw new InvalidDataException("Пустое задание Replicate.");
                string token = Convert.ToString(job["token"]);
                string model = Convert.ToString(job["model"]);
                string outputPath = Convert.ToString(job["output_path"]);
                var input = job["input"] as Dictionary<string, object>;
                if (String.IsNullOrWhiteSpace(token) || String.IsNullOrWhiteSpace(model) ||
                    String.IsNullOrWhiteSpace(outputPath) || input == null)
                    throw new InvalidDataException("В задании Replicate отсутствуют обязательные поля.");

                LogWorker("Replicate request started. Model=" + model + ".");
                var timer = Stopwatch.StartNew();
                string outputUrl = CreatePrediction(token, model, input, serializer);
                ReportWorkerProgress("downloading");
                DownloadWithSystemProxy(outputUrl, outputPath);
                LogWorker("Replicate request completed in " + timer.ElapsedMilliseconds + " ms.");
                WriteWorkerResponse(serializer, new Dictionary<string, object> { ["ok"] = true });
                return 0;
            }
            catch (Exception ex)
            {
                LogWorker("Replicate request failed: " + ex);
                WriteWorkerResponse(serializer, new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = ex.Message
                });
                return 1;
            }
        }

        private static void WriteWorkerResponse(JavaScriptSerializer serializer, Dictionary<string, object> response)
        {
            string json = serializer.Serialize(response);
            Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        }

        private static void ReportWorkerProgress(string stage)
        {
            LogWorker("Replicate stage: " + stage + ".");
            Console.Error.WriteLine("PROGRESS:" + stage);
        }

        private static void LogWorker(string message)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "VanyaTools.Native.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [AI-WORKER] " + message + Environment.NewLine);
            }
            catch { }
        }

        private static string CreatePrediction(string token, string model,
            Dictionary<string, object> input, JavaScriptSerializer serializer)
        {
            var request = CreateHttpRequest("https://api.replicate.com/v1/models/" + model + "/predictions");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            // Return the prediction handle immediately; waiting on the create request
            // can hit short gateway timeouts before polling has a chance to begin.
            ReportWorkerProgress("sending");
            var requestTimer = Stopwatch.StartNew();
            byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(new Dictionary<string, object> { ["input"] = input }));
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            Dictionary<string, object> result = ReadJson(request, serializer);
            string status = Convert.ToString(result["status"]);
            LogWorker("Prediction create returned status=" + status + " after " + requestTimer.ElapsedMilliseconds + " ms.");
            if (status == "starting" || status == "processing") ReportWorkerProgress("processing");

            for (int i = 0; i < 90 && (status == "starting" || status == "processing"); i++)
            {
                var urls = result["urls"] as Dictionary<string, object>;
                if (urls == null || !urls.ContainsKey("get")) break;
                Thread.Sleep(2000);
                var poll = CreateHttpRequest(Convert.ToString(urls["get"]));
                poll.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                result = ReadJson(poll, serializer);
                status = Convert.ToString(result["status"]);
            }
            if (status != "succeeded")
                throw new InvalidOperationException(Convert.ToString(result.ContainsKey("error")
                    ? result["error"] : "Модель завершилась со статусом " + status));

            object output = result["output"];
            var list = output as ArrayList;
            if (list != null && list.Count > 0) output = list[0];
            var array = output as object[];
            if (array != null && array.Length > 0) output = array[0];
            string url = Convert.ToString(output);
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Replicate вернул некорректную ссылку на результат.");
            return url;
        }

        private static HttpWebRequest CreateHttpRequest(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            try
            {
                IWebProxy proxy = WebRequest.GetSystemWebProxy();
                if (proxy != null)
                {
                    proxy.Credentials = CredentialCache.DefaultCredentials;
                    request.Proxy = proxy;
                }
            }
            catch { }
            return request;
        }

        private static Dictionary<string, object> ReadJson(HttpWebRequest request, JavaScriptSerializer serializer)
        {
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return serializer.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string body = reader.ReadToEnd();
                        if (body.Length > 2000) body = body.Substring(0, 2000) + "…";
                        if (String.IsNullOrWhiteSpace(body)) body = "(пустое тело ответа)";
                        throw new InvalidOperationException("Replicate вернул HTTP " +
                            (int)response.StatusCode + " " + response.StatusDescription + ". Ответ API: " + body);
                    }
                }
                throw;
            }
        }

        private static void DownloadWithSystemProxy(string url, string outputPath)
        {
            var timer = Stopwatch.StartNew();
            string directory = Path.GetDirectoryName(outputPath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var client = new WebClient())
            {
                try
                {
                    IWebProxy proxy = WebRequest.GetSystemWebProxy();
                    if (proxy != null)
                    {
                        proxy.Credentials = CredentialCache.DefaultCredentials;
                        client.Proxy = proxy;
                    }
                }
                catch { }
                client.DownloadFile(url, outputPath);
            }
            long size = new FileInfo(outputPath).Length;
            LogWorker("Output downloaded in " + timer.ElapsedMilliseconds + " ms; bytes=" + size + ".");
        }

        private static string ReadRepository()
        {
            string repoFile = GetRepositoryFile();
            string repository = File.Exists(repoFile) ? File.ReadAllText(repoFile).Trim() : "";
            if (string.IsNullOrEmpty(repository))
                repository = DefaultRepository;
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                throw new InvalidOperationException("Адрес GitHub репозитория должен иметь формат владелец/имя.");
            return repository;
        }

        private static string GetArgument(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static Version ParseReleaseVersion(string tag)
        {
            Version version;
            string value = (tag ?? "").Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            if (!Version.TryParse(value, out version))
                throw new InvalidDataException("Невозможно определить номер версии релиза " + tag + ".");
            return version;
        }

        private static void SaveRepository(string repository)
        {
            string repoFile = GetRepositoryFile();
            Directory.CreateDirectory(Path.GetDirectoryName(repoFile));
            File.WriteAllText(repoFile, repository, Encoding.ASCII);
        }

        private static string GetRepositoryFile()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools",
                "github-repository.txt");
        }

        private static ReleaseInfo GetLatestRelease(string repository)
        {
            string uri = "https://api.github.com/repos/" + repository + "/releases/latest";
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "VanyaTools-Updater/1.0.20";
            request.Accept = "application/vnd.github+json";

            string json;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                json = reader.ReadToEnd();

            var serializer = new JavaScriptSerializer();
            var release = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (release == null)
                throw new InvalidDataException("GitHub вернул ответ с неверным форматом.");

            string tag = release.ContainsKey("tag_name") ? Convert.ToString(release["tag_name"]) : "";
            var assets = release.ContainsKey("assets") ? release["assets"] as IEnumerable : null;
            if (assets != null)
            {
                foreach (object item in assets)
                {
                    var asset = item as Dictionary<string, object>;
                    if (asset == null || !asset.ContainsKey("name") || !asset.ContainsKey("browser_download_url"))
                        continue;
                    if (string.Equals(Convert.ToString(asset["name"]), "VanyaToolsNative.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ReleaseInfo
                        {
                            TagName = tag,
                            AssetUrl = Convert.ToString(asset["browser_download_url"])
                        };
                    }
                }
            }

            throw new InvalidDataException("В последнем GitHub Release нет файла VanyaToolsNative.zip.");
        }

        private static bool IsCorelRunning()
        {
            Process[] processes = Process.GetProcessesByName("CorelDRW");
            try { return processes.Length > 0; }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        private sealed class ConsoleSpinner : IDisposable
        {
            private readonly object _sync = new object();
            private readonly Thread _thread;
            private volatile bool _running;
            private string _message;
            private int _frame;
            private static readonly char[] Frames = { '|', '/', '-', '\\' };

            public ConsoleSpinner(string message)
            {
                _message = message;
                _thread = new Thread(Spin) { IsBackground = true };
                _running = true;
                _thread.Start();
            }

            public void SetMessage(string message)
            {
                lock (_sync) _message = message;
            }

            private void Spin()
            {
                while (_running)
                {
                    lock (_sync)
                    {
                        try { Console.Write("\r" + _message + " " + Frames[_frame++ % Frames.Length]); }
                        catch { }
                    }
                    Thread.Sleep(120);
                }
            }

            public void Dispose()
            {
                _running = false;
                try { _thread.Join(500); } catch { }
                lock (_sync)
                {
                    try { Console.Write("\r" + new string(' ', Math.Min(140, (_message ?? "").Length + 4)) + "\r"); }
                    catch { }
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void Pause()
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine("Это окно закроется через 8 секунд.");
                Thread.Sleep(8000);
            }
            catch { }
        }

        private sealed class ReleaseInfo
        {
            public string TagName { get; set; }
            public string AssetUrl { get; set; }
        }
    }
}
