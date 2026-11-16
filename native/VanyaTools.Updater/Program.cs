using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
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
            if (args != null && args.Length == 1 && args[0] == "--connectivity-worker")
                return ConnectivityWorker.Run();
            if (args != null && args.Length == 1 && args[0] == "--replicate-worker")
                return RunReplicateWorker();
            if (args != null && args.Length == 1 && args[0] == "--openai-image-worker")
                return OpenAiImageWorker.Run();
            if (args != null && args.Length == 1 && args[0] == "--preview-publish-worker")
                return PreviewPublishWorker.Run();
            if (args != null && args.Length == 1 && args[0] == "--telegram-worker")
                return TelegramWorker.Run();
            if (args != null && args.Length == 1 && args[0] == "--order-worker")
                return OrderWorker.Run();

            Console.OutputEncoding = Encoding.UTF8;
            string installedVersionText = GetArgument(args, "--installed-version");
            Version installedVersion = null;
            if (!String.IsNullOrWhiteSpace(installedVersionText) &&
                !Version.TryParse(installedVersionText, out installedVersion))
                installedVersion = null;
            string targetAddonsPath = GetArgument(args, "--corel-addons-path");
            string workDir = Path.Combine(Path.GetTempPath(), "VanyaToolsUpdate-" + Guid.NewGuid().ToString("N"));
            int exitCode = 1;

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                targetAddonsPath = ValidateCorelAddonsPath(targetAddonsPath);
                string repository = ReadRepository();
                Console.WriteLine("Vanya Tools — проверка обновления");
                if (targetAddonsPath != null) Console.WriteLine("Папка текущего CorelDRAW: " + targetAddonsPath);
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
                    client.Headers[HttpRequestHeader.UserAgent] = "VanyaTools-Updater/1.0.24";
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
                    while (IsCorelRunning(targetAddonsPath)) Thread.Sleep(2000);

                Console.WriteLine("CorelDRAW закрыт. Запускаю установщик...");
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(installer) +
                        (targetAddonsPath == null ? "" : " -CorelAddonsPath " + QuoteArgument(targetAddonsPath)),
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
                string cancelPath = job.ContainsKey("cancel_path") ? Convert.ToString(job["cancel_path"]) : outputPath + ".cancel";
                var input = job["input"] as Dictionary<string, object>;
                if (String.IsNullOrWhiteSpace(token) || String.IsNullOrWhiteSpace(model) ||
                    String.IsNullOrWhiteSpace(outputPath) || input == null)
                    throw new InvalidDataException("В задании Replicate отсутствуют обязательные поля.");

                LogWorker("Replicate request started. Model=" + model + ".");
                var timer = Stopwatch.StartNew();
                bool textOutput = Path.GetExtension(outputPath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
                var localInputFiles = CollectLocalInputFiles(input);
                string outputUrl = CreatePrediction(token, model, input, serializer, outputPath, cancelPath, textOutput);
                if (File.Exists(cancelPath)) throw new OperationCanceledException("Вставка отменена. Модель уже завершилась; результат можно получить повтором без новой генерации.");
                ReportWorkerProgress("downloading");
                if (textOutput)
                {
                    File.WriteAllText(outputPath + ".part", outputUrl, Encoding.UTF8);
                    if (File.Exists(outputPath)) File.Replace(outputPath + ".part", outputPath, null);
                    else File.Move(outputPath + ".part", outputPath);
                }
                else DownloadWithSystemProxy(outputUrl, outputPath, cancelPath);
                foreach (string inputFile in localInputFiles)
                    try { if (File.Exists(inputFile)) File.Delete(inputFile); } catch { }
                try{File.Delete(outputPath+".submission.json");}catch(IOException){}catch(UnauthorizedAccessException){}
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
                    ["error"] = ex.Message,
                    ["cancelled"] = ex is OperationCanceledException
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
            Dictionary<string, object> input, JavaScriptSerializer serializer, string outputPath, string cancelPath, bool textOutput)
        {
            string statePath = outputPath + ".prediction.json";
            Dictionary<string, object> result = File.Exists(statePath)
                ? serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath)) : null;
            bool recoveredSubmission=false;
            if(result==null && File.Exists(outputPath+".submitted"))
            {
                result=RecoverSubmission(token,model,input,serializer,outputPath,cancelPath);
                if(result==null)throw new InvalidOperationException("Сервер мог принять запрос, но его номер не получен. Поиск в истории не дал однозначного совпадения; автоматический дубль заблокирован. Проверьте историю Replicate.");
                SavePrediction(statePath,result,serializer);recoveredSubmission=true;
            }
            string previousStatus = result == null ? "" : Convert.ToString(result["status"]);
            if (!recoveredSubmission && (result == null || previousStatus == "failed" || previousStatus == "canceled"))
            {
                if (File.Exists(cancelPath)) throw new OperationCanceledException("Операция отменена до отправки в Replicate.");
                if (result == null && File.Exists(outputPath + ".submitted"))
                    throw new InvalidOperationException("Сервер мог принять запрос, но его номер не получен. Проверьте запрос в Replicate перед новым платным запуском; автоматический дубль заблокирован.");
                var ignoreIds=new List<string>();
                if(result!=null)
                {
                    string priorId=ReplicateSubmissionRecovery.Value(result,"id");if(!String.IsNullOrEmpty(priorId))ignoreIds.Add(priorId);
                    try{var prior=serializer.Deserialize<ReplicateSubmissionStamp>(File.ReadAllText(outputPath+".submission.json"));if(prior?.IgnoreIds!=null)ignoreIds.AddRange(prior.IgnoreIds);}catch(IOException){}catch(ArgumentException){}
                    if(File.Exists(outputPath+".submitted"))File.Delete(outputPath+".submitted");
                }
                if (File.Exists(statePath)) File.Delete(statePath);
                result=null;
                var stamp=ReplicateSubmissionRecovery.Create(model,input,DateTime.UtcNow);stamp.IgnoreIds=ignoreIds.Distinct().Take(100).ToArray();
                // The generic prediction endpoint supports official owner/name
                // identifiers as well as pinned community model versions.
                var request = CreateHttpRequest("https://api.replicate.com/v1/predictions");
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Accept = "application/json";
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                request.Headers["Cancel-After"] = ReplicatePredictionLifetime.CancelAfterHeader;
                // The server deadline survives HTTP 500, lost IDs and Corel closing.
                // Return the prediction handle immediately; waiting on the create request
                // can hit short gateway timeouts before polling has a chance to begin.
                ReportWorkerProgress("sending");
                input = ResolveUploadedInputs(input, token, cancelPath, serializer) as Dictionary<string, object>;
                if (input == null) throw new InvalidDataException("Не удалось подготовить входные данные для Replicate.");
                ReportWorkerProgress("creating");
                var requestTimer = Stopwatch.StartNew();
                var body = new Dictionary<string, object> { ["input"] = input };
                body["version"] = model;
                byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(body));
                request.ContentLength=bytes.Length;request.SendChunked=false;request.ServicePoint.Expect100Continue=false;
                if(File.Exists(cancelPath))throw new OperationCanceledException("Операция отменена до запуска модели.");
                stamp.SentUtc=DateTime.UtcNow.ToString("o");SaveSubmissionStamp(outputPath,stamp,serializer);
                try
                {
                    using (var stream = request.GetRequestStream())
                    {
                        // A connection failure before obtaining the request stream
                        // cannot have submitted a paid prediction. Mark it only when
                        // the request body is about to be written.
                        File.WriteAllText(outputPath + ".submitted", "sent");
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    result = ReadJson(request, serializer);
                }
                catch (ReplicateApiException ex)
                {
                    if (ReplicateSubmissionFailure.DefinitelyRejected(ex.StatusCode))
                    {File.Delete(outputPath + ".submitted");throw;}
                    if(File.Exists(outputPath+".submitted"))result=RecoverSubmission(token,model,input,serializer,outputPath,cancelPath);
                    if(result==null)throw new InvalidOperationException(ReplicateSubmissionFailure.DescribeUnknown(ex.StatusCode,ex.Message),ex);
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    bool submitted = File.Exists(outputPath + ".submitted");
                    if(submitted)result=RecoverSubmission(token,model,input,serializer,outputPath,cancelPath);
                    if(result==null)throw new InvalidOperationException(submitted
                        ? "Соединение оборвалось при создании запроса. Однозначно восстановить его номер не удалось; автоматический дубль заблокирован. Проверьте историю Replicate."
                        : "Соединение оборвалось до отправки запроса. Нажмите «Повторить запрос».", ex);
                }
                SavePrediction(statePath, result, serializer);
                LogWorker("Prediction create returned status=" + Convert.ToString(result["status"]) + " after " + requestTimer.ElapsedMilliseconds + " ms.");
            }
            string status = Convert.ToString(result["status"]);
            if (status == "starting" || status == "processing") ReportWorkerProgress("processing");

            bool cancelSent=false,automaticCancellation=false;
            DateTime startedUtc=PredictionStartedUtc(result,outputPath,serializer,DateTime.UtcNow);
            var cancellationWait=new Stopwatch();
            while(status=="starting" || status=="processing")
            {
                var urls=result["urls"] as Dictionary<string,object>;
                if(urls==null || !urls.ContainsKey("get"))break;
                bool manual=File.Exists(cancelPath),expired=ReplicatePredictionLifetime.Expired(startedUtc,DateTime.UtcNow);
                if((manual || expired) && !cancelSent)
                {
                    // Check once more before cancellation: a completed result must
                    // win the race with our deadline and remain downloadable.
                    automaticCancellation=!manual;
                    if(expired && !manual)
                    {
                        string id=ReplicateSubmissionRecovery.Value(result,"id");
                        try{result=GetCancellationStatus(id,token,serializer);}
                        catch(Exception error){LogWorker("Could not refresh status before deadline cancellation: "+error.GetType().Name+". Sending cancellation for the saved prediction ID.");}
                        SavePrediction(statePath,result,serializer);status=Convert.ToString(result["status"]);
                        if(status!="starting" && status!="processing")break;
                        startedUtc=PredictionStartedUtc(result,outputPath,serializer,startedUtc);
                        if(!ReplicatePredictionLifetime.Expired(startedUtc,DateTime.UtcNow)){automaticCancellation=false;continue;}
                    }
                    ReportWorkerProgress(automaticCancellation?"auto-cancelling":"cancelling");
                    try
                    {
                        result=CancelPrediction(token,result,serializer);
                        SavePrediction(statePath,result,serializer);status=Convert.ToString(result["status"]);
                        cancelSent=true;cancellationWait.Restart();
                        if(status!="starting" && status!="processing")break;
                    }
                    catch(Exception error)
                    {
                        // A failed HTTP response can still mean the server accepted
                        // the cancellation. Confirm via GET; never send a new model.
                        try
                        {
                            string id=ReplicateSubmissionRecovery.Value(result,"id");
                            var latest=GetCancellationStatus(id,token,serializer);
                            SavePrediction(statePath,latest,serializer);result=latest;status=Convert.ToString(result["status"]);
                            if(status!="starting" && status!="processing")break;
                        }
                        catch(Exception refresh){LogWorker("Could not confirm cancel response: "+refresh.GetType().Name+".");}
                        throw new InvalidOperationException("Не удалось подтвердить "+(automaticCancellation?"автоматическую ":"")+"отмену в Replicate. Номер запроса сохранён; новый запуск автоматически не отправлялся. Проверьте этот запрос в истории. "+error.Message,error);
                    }
                }
                if(cancelSent && ReplicatePredictionLifetime.ConfirmationExpired(cancellationWait.Elapsed))
                    throw new TimeoutException("Отмена отправлена в Replicate, но сервис ещё не подтвердил остановку. Номер запроса сохранён; повторная генерация автоматически не создаётся.");
                for(int delay=0;delay<20;delay++)
                {if(!cancelSent && File.Exists(cancelPath))break;Thread.Sleep(100);}
                if(!cancelSent && File.Exists(cancelPath))continue;
                if(cancelSent)
                {
                    try{result=GetCancellationStatus(ReplicateSubmissionRecovery.Value(result,"id"),token,serializer);}
                    catch(Exception error){throw new InvalidOperationException("Отмена отправлена в Replicate, но сервис ещё не подтвердил остановку. Номер запроса сохранён; новый запуск автоматически не отправлялся.",error);}
                }
                else result=GetPredictionWithRetry(Convert.ToString(urls["get"]),token,serializer);
                SavePrediction(statePath,result,serializer);status=Convert.ToString(result["status"]);
            }
            if(status=="canceled")
            {
                if(automaticCancellation)throw new TimeoutException("Replicate подтвердил автоматическую отмену: превышен лимит 10 минут. Результаты предыдущих этапов сохранены. Новая попытка потребует отдельного подтверждения.");
                throw new OperationCanceledException("Replicate подтвердил отмену.");
            }
            if (status != "succeeded")
                throw new InvalidOperationException("Модель завершилась со статусом " + status + ". " +
                    (result.ContainsKey("error") ? Convert.ToString(result["error"]) : ""));

            object output = result["output"];
            if (textOutput)
            {
                var chunks = output as System.Collections.IEnumerable;
                if (output is string) return (string)output;
                if (chunks == null) throw new InvalidDataException("Модель анализа не вернула текст.");
                var text = new StringBuilder();
                foreach (object chunk in chunks) text.Append(Convert.ToString(chunk));
                if (text.Length == 0) throw new InvalidDataException("Модель анализа вернула пустой текст.");
                return text.ToString();
            }
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

        private static DateTime PredictionStartedUtc(Dictionary<string,object> prediction,string outputPath,JavaScriptSerializer serializer,DateTime fallbackUtc)
        {
            string submitted=null;
            try
            {
                string receipt=outputPath+".submission.json";
                if(File.Exists(receipt))submitted=serializer.Deserialize<ReplicateSubmissionStamp>(File.ReadAllText(receipt))?.SentUtc;
                if(String.IsNullOrEmpty(submitted) && File.Exists(outputPath+".submitted"))submitted=File.GetLastWriteTimeUtc(outputPath+".submitted").ToString("o");
            }
            catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
            return ReplicatePredictionLifetime.Started(prediction,submitted,fallbackUtc);
        }
        private static Dictionary<string,object> GetCancellationStatus(string id,string token,JavaScriptSerializer serializer)
        {
            // Deadline checks and cancellation confirmation must fit inside the
            // worker's remaining lifetime even if the provider stops responding.
            var request=CreateHttpRequest("https://api.replicate.com/v1/predictions/"+Uri.EscapeDataString(id));
            request.Timeout=15000;request.ReadWriteTimeout=15000;
            request.Headers[HttpRequestHeader.Authorization]="Bearer "+token;
            return ReadJson(request,serializer);
        }
        private static Dictionary<string,object> CancelPrediction(string token,Dictionary<string,object> prediction,JavaScriptSerializer serializer)
        {
            string id=ReplicateSubmissionRecovery.Value(prediction,"id");
            if(String.IsNullOrWhiteSpace(id))throw new InvalidDataException("Нет номера запроса для отмены.");
            var request=CreateHttpRequest("https://api.replicate.com/v1/predictions/"+Uri.EscapeDataString(id)+"/cancel");
            request.Method="POST";request.ContentLength=0;request.Timeout=20000;request.ReadWriteTimeout=20000;
            request.Headers[HttpRequestHeader.Authorization]="Bearer "+token;
            return ReadJson(request,serializer);
        }

        private static Dictionary<string, object> GetPredictionWithRetry(string url, string token, JavaScriptSerializer serializer)
        {
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    var poll = CreateHttpRequest(url);
                    poll.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                    return ReadJson(poll, serializer);
                }
                catch (Exception ex) when (attempt < 4 && IsTransientNetworkError(ex))
                {
                    LogWorker("Prediction status attempt " + attempt + "/4 failed: " + ex.GetType().Name + ". Retrying the same prediction.");
                    ReportWorkerProgress("retrying-status");
                    Thread.Sleep(attempt * 2000);
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    throw new InvalidOperationException("Связь оборвалась при проверке запроса. Нажмите «Повторить запрос»: продолжится тот же запуск без новой оплаты.", ex);
                }
            }
            throw new InvalidOperationException("Не удалось получить статус запроса Replicate.");
        }

        private static object ResolveUploadedInputs(object value, string token, string cancelPath, JavaScriptSerializer serializer)
        {
            int remaining=ReplicateInputEncoding.InlineBudget;
            return ResolveUploadedInputs(value,token,cancelPath,serializer,ref remaining);
        }
        private static object ResolveUploadedInputs(object value,string token,string cancelPath,JavaScriptSerializer serializer,ref int remaining)
        {
            var text = value as string;
            if (text != null)
            {
                if (text.StartsWith("replicate-file:", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(cancelPath)) throw new OperationCanceledException("Операция отменена до загрузки изображения.");
                    string path = text.Substring("replicate-file:".Length);
                    var info = new FileInfo(path);
                    if (!info.Exists) throw new FileNotFoundException("Не найден временный PNG для отправки в Replicate.", path);
                    if (info.Length > 100L * 1024 * 1024) throw new InvalidDataException("Изображение превышает лимит загрузки Replicate 100 МБ.");
                    byte[] bytes=File.ReadAllBytes(path);
                    if(ReplicateInputEncoding.TryInline(bytes,"image/png",ref remaining,out string inline))
                    {LogWorker("Input kept inline without a file-host fetch; bytes="+bytes.Length+".");return inline;}
                    return UploadReplicateFile(token, bytes, info.Name, "image/png", serializer, cancelPath);
                }
                if (text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = text.IndexOf(',');
                    if (comma > 0)
                    {
                        byte[] bytes = Convert.FromBase64String(text.Substring(comma + 1));
                        string mime = text.Substring(5, text.IndexOf(';') - 5);
                        if(ReplicateInputEncoding.TryInline(bytes,mime,ref remaining,out _))return text;
                        return UploadReplicateFile(token, bytes, "input", mime, serializer, cancelPath);
                    }
                }
                return text;
            }

            var dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
            {
                var keys = new List<string>(dictionary.Keys);
                foreach (string key in keys) dictionary[key] = ResolveUploadedInputs(dictionary[key], token, cancelPath, serializer,ref remaining);
                return dictionary;
            }
            var list = value as IList;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++) list[i] = ResolveUploadedInputs(list[i], token, cancelPath, serializer,ref remaining);
                return list;
            }
            return value;
        }

        private static List<string> CollectLocalInputFiles(object value)
        {
            var files = new List<string>();
            CollectLocalInputFiles(value, files);
            return files;
        }

        private static void CollectLocalInputFiles(object value, List<string> files)
        {
            var text = value as string;
            if (text != null)
            {
                if (text.StartsWith("replicate-file:", StringComparison.OrdinalIgnoreCase))
                {
                    string path = text.Substring("replicate-file:".Length);
                    if (!files.Contains(path)) files.Add(path);
                }
                return;
            }
            var dictionary = value as Dictionary<string, object>;
            if (dictionary != null) { foreach (object item in dictionary.Values) CollectLocalInputFiles(item, files); return; }
            var list = value as IList;
            if (list != null) foreach (object item in list) CollectLocalInputFiles(item, files);
        }

        private static string UploadReplicateFile(string token, byte[] content, string filename, string contentType,
            JavaScriptSerializer serializer, string cancelPath)
        {
            // Uploading a temporary input file does not start or bill a model.
            // Retry transient connection failures before creating the prediction.
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                if (File.Exists(cancelPath)) throw new OperationCanceledException("Операция отменена до загрузки изображения.");
                try { return UploadReplicateFileOnce(token, content, filename, contentType, serializer); }
                catch (Exception ex) when (attempt < 4 && IsTransientNetworkError(ex))
                {
                    LogWorker("Input upload attempt " + attempt + "/4 failed: " + ex.GetType().Name + ". Retrying without submitting a prediction.");
                    ReportWorkerProgress("retrying-upload");
                    int delayMs = attempt * 2000;
                    for (int waited = 0; waited < delayMs; waited += 250)
                    {
                        if (File.Exists(cancelPath)) throw new OperationCanceledException("Операция отменена до загрузки изображения.");
                        Thread.Sleep(250);
                    }
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    throw new InvalidOperationException("Не удалось загрузить входной фрагмент в Replicate после нескольких попыток. Готовые этапы сохранены; нажмите «Продолжить» позже.", ex);
                }
            }
            throw new InvalidOperationException("Не удалось загрузить временный файл в Replicate.");
        }

        private static string UploadReplicateFileOnce(string token, byte[] content, string filename, string contentType, JavaScriptSerializer serializer)
        {
            const string endpoint = "https://api.replicate.com/v1/files";
            string boundary = "----VanyaTools" + Guid.NewGuid().ToString("N");
            string safeName = Path.GetFileName(filename ?? "input.png").Replace("\"", "");
            byte[] header = Encoding.UTF8.GetBytes("--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"content\"; filename=\"" + safeName + "\"\r\n" +
                "Content-Type: " + (String.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType) + "\r\n\r\n");
            byte[] footer = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
            var request = CreateHttpRequest(endpoint);
            request.Method = "POST";
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            request.Timeout = 180000;
            request.ReadWriteTimeout = 180000;
            request.KeepAlive = false;
            request.ContentLength = header.Length + (long)content.Length + footer.Length;
            using (var stream = request.GetRequestStream())
            {
                stream.Write(header, 0, header.Length);
                stream.Write(content, 0, content.Length);
                stream.Write(footer, 0, footer.Length);
            }
            var response = ReadJson(request, serializer);
            var urls = response.ContainsKey("urls") ? response["urls"] as Dictionary<string, object> : null;
            if (urls == null || !urls.ContainsKey("get")) throw new InvalidDataException("Replicate загрузил файл без ссылки urls.get.");
            string url = Convert.ToString(urls["get"]);
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Replicate вернул некорректную ссылку для загруженного файла.");
            LogWorker("Input uploaded to temporary Replicate storage; bytes=" + content.Length + ".");
            return url;
        }

        private static void SavePrediction(string path, Dictionary<string, object> result, JavaScriptSerializer serializer)
        {
            // Store only resume metadata, never the API token or input image.
            var state = new Dictionary<string, object>();
            foreach (string key in new[] { "id", "status", "urls", "output", "error", "metrics", "created_at", "started_at", "completed_at" })
                if (result.ContainsKey(key)) state[key] = result[key];
            File.WriteAllText(path + ".tmp", serializer.Serialize(state));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);
        }

        private static void SaveSubmissionStamp(string output,ReplicateSubmissionStamp stamp,JavaScriptSerializer serializer)
        {
            string path=output+".submission.json";File.WriteAllText(path+".tmp",serializer.Serialize(stamp));
            if(File.Exists(path))File.Replace(path+".tmp",path,null);else File.Move(path+".tmp",path);
        }
        private static Dictionary<string,object> RecoverSubmission(string token,string model,Dictionary<string,object> input,JavaScriptSerializer serializer,string output,string cancelPath)
        {
            ReportWorkerProgress("recovering");
            try
            {
                ReplicateSubmissionStamp stamp;
                string receipt=output+".submission.json";
                if(File.Exists(receipt))
                {
                    if(new FileInfo(receipt).Length>262144)throw new InvalidDataException("Submission receipt too large.");
                    stamp=serializer.Deserialize<ReplicateSubmissionStamp>(File.ReadAllText(receipt));
                    ReplicateSubmissionRecovery.Validate(stamp);
                    if(stamp.Model!=model)throw new InvalidDataException("Submission model mismatch.");
                }
                else stamp=ReplicateSubmissionRecovery.Create(model,input,File.GetLastWriteTimeUtc(output+".submitted"));
                var fingerprints=new Dictionary<string,string>();
                for(int attempt=0;attempt<3;attempt++)
                {
                    if(attempt>0 && !File.Exists(cancelPath))Thread.Sleep(attempt*500);
                    var candidates=new List<Dictionary<string,object>>();string next="https://api.replicate.com/v1/predictions";
                    for(int page=0;page<3 && !String.IsNullOrEmpty(next);page++)
                    {
                        var uri=new Uri(next);
                        if(uri.Scheme!="https" || uri.Host!="api.replicate.com" || uri.AbsolutePath!="/v1/predictions")throw new InvalidDataException("Unexpected prediction history URL.");
                        var response=ReadRecoveryJson(next,token,serializer);
                        if(response.ContainsKey("results") && response["results"] is IList list)
                            foreach(object item in list)if(item is Dictionary<string,object> candidate)candidates.Add(candidate);
                        next=response.ContainsKey("next")?Convert.ToString(response["next"]):null;
                    }
                    if(!String.IsNullOrEmpty(next))
                    {
                        var floor=DateTime.Parse(stamp.SentUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime().AddMinutes(-5);
                        bool passedWindow=candidates.Exists(candidate=>DateTime.TryParse(ReplicateSubmissionRecovery.Value(candidate,"created_at"),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var time) && time.ToUniversalTime()<floor);
                        if(!passedWindow){LogWorker("Prediction history window is incomplete; recovery remains blocked.");return null;}
                    }
                    var matches=ReplicateSubmissionRecovery.Match(stamp,candidates,url=>{
                        if(!fingerprints.TryGetValue(url,out string hash))fingerprints[url]=hash=RecoveryFileHash(url,token,serializer);
                        return hash;
                    });
                    if(matches.Count>1){LogWorker("Prediction recovery found multiple exact matches; no new prediction submitted.");return null;}
                    if(matches.Count==1)
                    {
                        LogWorker("Recovered existing prediction " + ReplicateSubmissionRecovery.Value(matches[0],"id") + "; no new prediction submitted.");
                        return matches[0];
                    }
                }
            }
            catch(Exception error){LogWorker("Prediction recovery could not establish a unique match: "+error.GetType().Name+". No new prediction submitted.");}
            return null;
        }
        private static Dictionary<string,object> ReadRecoveryJson(string url,string token,JavaScriptSerializer serializer)
        {
            var request=CreateHttpRequest(url);request.Headers[HttpRequestHeader.Authorization]="Bearer "+token;
            request.Timeout=request.ReadWriteTimeout=15000;return ReadJson(request,serializer);
        }
        private static string RecoveryFileHash(string url,string token,JavaScriptSerializer serializer)
        {
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || !uri.IsDefaultPort)throw new InvalidDataException("Unexpected input file URL.");
            if(uri.Host=="api.replicate.com" && uri.AbsolutePath.StartsWith("/v1/files/",StringComparison.Ordinal))
            {
                var file=ReadRecoveryJson(url,token,serializer);
                var checksums=file.ContainsKey("checksums")?file["checksums"] as Dictionary<string,object>:null;
                return ReplicateSubmissionRecovery.Value(checksums,"sha256");
            }
            if(uri.Host!="replicate.delivery" && !uri.Host.EndsWith(".replicate.delivery",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected input file host.");
            var request=CreateHttpRequest(url);request.Timeout=request.ReadWriteTimeout=15000;request.AllowAutoRedirect=false;
            using(var response=request.GetResponse())
            using(var stream=response.GetResponseStream())
            using(var memory=new MemoryStream())
            {
                if(response.ContentLength>100L*1024*1024)throw new InvalidDataException("Input file too large.");
                var buffer=new byte[65536];int read;
                while((read=stream.Read(buffer,0,buffer.Length))>0){if(memory.Length+read>100L*1024*1024)throw new InvalidDataException("Input file too large.");memory.Write(buffer,0,read);}
                memory.Position=0;return ReplicateSubmissionRecovery.Digest(memory);
            }
        }

        private static HttpWebRequest CreateHttpRequest(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            request.KeepAlive = false;
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
                        throw new ReplicateApiException((int)response.StatusCode, "Replicate вернул HTTP " +
                            (int)response.StatusCode + " " + response.StatusDescription + ". Ответ API: " + body);
                    }
                }
                throw;
            }
        }

        private static void DownloadWithSystemProxy(string url, string outputPath, string cancelPath)
        {
            var timer = Stopwatch.StartNew();
            string directory = Path.GetDirectoryName(outputPath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // The prediction is already paid for and its URL is stored on disk.
            // Retry only the file transfer; never submit a second prediction here.
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                if (File.Exists(cancelPath)) throw new OperationCanceledException();
                try
                {
                    var request = CreateHttpRequest(url);
                    request.KeepAlive = false;
                    using (var response = request.GetResponse())
                    using (var source = response.GetResponseStream())
                    using (var destination = File.Create(outputPath + ".part"))
                    {
                        byte[] buffer = new byte[65536];
                        int count;
                        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (File.Exists(cancelPath)) throw new OperationCanceledException();
                            destination.Write(buffer, 0, count);
                        }
                    }
                    if (File.Exists(cancelPath)) throw new OperationCanceledException();
                    if (File.Exists(outputPath)) File.Replace(outputPath + ".part", outputPath, null);
                    else File.Move(outputPath + ".part", outputPath);
                    long size = new FileInfo(outputPath).Length;
                    LogWorker("Output downloaded in " + timer.ElapsedMilliseconds + " ms; bytes=" + size + ".");
                    return;
                }
                catch (Exception ex) when (attempt < 4 && IsTransientNetworkError(ex))
                {
                    LogWorker("Output download attempt " + attempt + "/4 failed: " + ex.GetType().Name + ". Retrying the same finished prediction.");
                    ReportWorkerProgress("retrying-download");
                    int delayMs = attempt * 2000;
                    for (int waited = 0; waited < delayMs; waited += 250)
                    {
                        if (File.Exists(cancelPath)) throw new OperationCanceledException();
                        Thread.Sleep(250);
                    }
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    throw new InvalidOperationException("Не удалось скачать готовый результат Replicate после нескольких попыток. Новый запрос к модели не нужен; нажмите «Продолжить» позже.", ex);
                }
                finally { if (File.Exists(outputPath + ".part")) File.Delete(outputPath + ".part"); }
            }
        }

        private static bool IsTransientNetworkError(Exception error)
        {
            var api = error as ReplicateApiException;
            if (api != null) return api.StatusCode >= 500 || api.StatusCode == 408 || api.StatusCode == 429;
            var web = error as WebException;
            if (web != null)
            {
                if (web.Status == WebExceptionStatus.ConnectFailure || web.Status == WebExceptionStatus.Timeout ||
                    web.Status == WebExceptionStatus.ReceiveFailure || web.Status == WebExceptionStatus.SendFailure ||
                    web.Status == WebExceptionStatus.ConnectionClosed || web.Status == WebExceptionStatus.KeepAliveFailure ||
                    web.Status == WebExceptionStatus.NameResolutionFailure) return true;
                var response = web.Response as HttpWebResponse;
                if (response != null && ((int)response.StatusCode >= 500 ||
                    response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode == 429)) return true;
            }
            var socket = error as SocketException;
            if (socket != null)
                return socket.SocketErrorCode == SocketError.ConnectionReset ||
                    socket.SocketErrorCode == SocketError.ConnectionAborted ||
                    socket.SocketErrorCode == SocketError.TimedOut ||
                    socket.SocketErrorCode == SocketError.NetworkDown ||
                    socket.SocketErrorCode == SocketError.NetworkUnreachable ||
                    socket.SocketErrorCode == SocketError.HostUnreachable;
            return error.InnerException != null && IsTransientNetworkError(error.InnerException);
        }

        private sealed class ReplicateApiException : InvalidOperationException
        {
            public int StatusCode { get; private set; }
            public ReplicateApiException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
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

        private static string ValidateCorelAddonsPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string programsPath = Path.GetDirectoryName(path);
            if (!String.Equals(Path.GetFileName(path), "Addons", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetFileName(programsPath), "Programs64", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(programsPath, "CorelDRW.exe")))
                throw new InvalidDataException("Путь обновления не указывает на Programs64\\Addons запущенного CorelDRAW.");
            return path;
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
            request.UserAgent = "VanyaTools-Updater/1.0.26";
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

        private static bool IsCorelRunning(string targetAddonsPath)
        {
            Process[] processes = Process.GetProcessesByName("CorelDRW");
            try
            {
                if (targetAddonsPath == null) return processes.Length > 0;
                string targetExe = Path.Combine(Path.GetDirectoryName(targetAddonsPath), "CorelDRW.exe");
                foreach (Process process in processes)
                {
                    try
                    {
                        if (String.Equals(process.MainModule.FileName, targetExe, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { return true; }
                }
                return false;
            }
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
