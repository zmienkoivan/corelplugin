using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal static class OperationTiming
    {
        private const int Window = 12;
        private static readonly object Gate = new object();
        private static readonly string HistoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "operation-times.json");
        private static readonly Lazy<Dictionary<string, double[]>> History = new Lazy<Dictionary<string, double[]>>(Load);
        private static bool _saveQueued, _dirty;

        internal static string Display(string stage, TimeSpan elapsed, string key)
        {
            string text = stage + " · " + Format(elapsed.TotalSeconds);
            if (String.IsNullOrEmpty(key)) return text;
            lock (Gate)
            {
                return text + (History.Value.TryGetValue(key, out var samples) && samples.Length != 0
                    ? " · в среднем " + Format(samples.Average()) : " · среднее пока —");
            }
        }

        internal static void Record(string key, TimeSpan elapsed)
        {
            double seconds = elapsed.TotalSeconds;
            if (String.IsNullOrEmpty(key) || seconds <= 0 || seconds > 3600) return;
            lock (Gate)
            {
                var history = History.Value;
                if (!history.TryGetValue(key, out var samples)) samples = Array.Empty<double>();
                history[key] = samples.Skip(Math.Max(0, samples.Length - Window + 1)).Concat(new[] { seconds }).ToArray();
                _dirty = true;
                if (_saveQueued) return;
                _saveQueued = true;
            }
            _ = Task.Run(Save);
        }

        private static string Format(double seconds) =>
            seconds < 59.5 ? seconds.ToString("0.#", CultureInfo.CurrentCulture) + " с" :
                TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3599.5 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

        private static Dictionary<string, double[]> Load()
        {
            try
            {
                if (File.Exists(HistoryPath) && new FileInfo(HistoryPath).Length <= 262144)
                {
                    var history = new JavaScriptSerializer().Deserialize<Dictionary<string, double[]>>(File.ReadAllText(HistoryPath));
                    if (history != null)
                        return history.Where(pair => pair.Value != null).ToDictionary(pair => pair.Key,
                            pair => pair.Value.Where(seconds => seconds > 0 && seconds <= 3600).Take(Window).ToArray());
                }
            }
            catch (Exception error) { Log.Error("Could not read local operation times.", error); }
            return new Dictionary<string, double[]>();
        }

        private static void Save()
        {
            while (true)
            {
                Dictionary<string, double[]> snapshot;
                lock (Gate)
                {
                    if (!_dirty)
                    {
                        _saveQueued = false;
                        return;
                    }
                    _dirty = false;
                    snapshot = new Dictionary<string, double[]>(History.Value);
                }

                string temporary = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath));
                    File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(snapshot));
                    if (File.Exists(HistoryPath)) File.Replace(temporary, HistoryPath, null);
                    else File.Move(temporary, HistoryPath);
                }
                catch (Exception error) { Log.Error("Could not save local operation times.", error); }
                finally
                {
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
