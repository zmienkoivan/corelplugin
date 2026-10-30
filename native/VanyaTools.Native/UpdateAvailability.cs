using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal enum UpdateAvailabilityState { Unknown, Current, Available }

    internal sealed class UpdateAvailabilityStatus
    {
        internal UpdateAvailabilityState State { get; set; }
        internal Version LatestVersion { get; set; }
        internal string Message { get; set; }
        internal DateTime CheckedUtc { get; set; }
    }

    // This probe only reads public release metadata. Installation stays in the
    // existing updater and is never triggered by a timer or a network response.
    internal static class UpdateAvailability
    {
        // Corel's process can be denied sockets by Windows while the installed
        // network helper is allowed. Use the same helper as the other LEDs.
        private static readonly UpdateAvailabilityProbe Probe = new UpdateAvailabilityProbe(ConnectivityClient.LatestRelease);

        internal static Task<UpdateAvailabilityStatus> CheckAsync(Version installed, bool force = false)
            => Probe.CheckAsync(installed, force);

    }

    // Injectable transport/clock keep the cache and comparison checks entirely
    // offline. A snapshot is independent of the installed assembly revision.
    internal sealed class UpdateAvailabilityProbe
    {
        private sealed class Snapshot
        {
            internal Version Latest;
            internal string Error;
            internal DateTime CheckedUtc, ConfirmedUtc;
        }

        private readonly Func<Task<string>> _fetch;
        private readonly Func<DateTime> _utcNow;
        private readonly object _gate = new object();
        private Snapshot _cached;
        private DateTime _expiresUtc;
        private Task<Snapshot> _inFlight;

        internal UpdateAvailabilityProbe(Func<Task<string>> fetch, Func<DateTime> utcNow = null)
        { _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch)); _utcNow = utcNow ?? (() => DateTime.UtcNow); }

        internal async Task<UpdateAvailabilityStatus> CheckAsync(Version installed, bool force = false)
        {
            installed = Normalize(installed);
            Task<Snapshot> work;
            lock (_gate)
            {
                if (_inFlight != null) work = _inFlight;
                else if (!force && _cached != null && _utcNow() < _expiresUtc) work = Task.FromResult(_cached);
                else work = _inFlight = Task.Run(ReadSnapshot);
            }
            Snapshot result = await work.ConfigureAwait(false);
            bool available = result.Latest != null && result.Latest > installed;
            string latest = result.Latest?.ToString(3);
            return new UpdateAvailabilityStatus
            {
                State = available ? UpdateAvailabilityState.Available : result.Error == null
                    ? UpdateAvailabilityState.Current : UpdateAvailabilityState.Unknown,
                LatestVersion = result.Latest, CheckedUtc = result.CheckedUtc,
                Message = result.Error == null
                    ? available ? "Доступно обновление Vanya Tools " + latest + "." : "Vanya Tools обновлён · версия " + installed.ToString(3) + "."
                    : available ? "Доступно обновление Vanya Tools " + latest + ". Подтверждено: " + result.ConfirmedUtc.ToLocalTime().ToString("dd.MM HH:mm") +
                        ". Сейчас проверить не удалось: " + result.Error
                    : "Не удалось проверить обновления: " + result.Error
            };
        }

        private async Task<Snapshot> ReadSnapshot()
        {
            var snapshot = new Snapshot { CheckedUtc = _utcNow() };
            try
            {
                string json = await _fetch().ConfigureAwait(false);
                snapshot.Latest = ReleaseVersion(json);
                snapshot.ConfirmedUtc = snapshot.CheckedUtc;
            }
            catch (Exception error)
            {
                snapshot.Error = error is OperationCanceledException ? "превышено время ожидания" : error.GetBaseException().Message;
                lock (_gate)
                {
                    snapshot.Latest = _cached?.Latest;
                    snapshot.ConfirmedUtc = _cached?.ConfirmedUtc ?? default(DateTime);
                }
            }
            lock (_gate)
            {
                _cached = snapshot;
                _expiresUtc = snapshot.CheckedUtc + (snapshot.Error == null ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(1));
                _inFlight = null;
            }
            return snapshot;
        }

        internal static Version Normalize(Version version)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }

        internal static Version ReleaseVersion(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024)
                throw new InvalidDataException("GitHub вернул пустые или слишком большие данные релиза.");
            var release = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (release == null || !release.TryGetValue("draft", out object draft) || !(draft is bool) || (bool)draft ||
                !release.TryGetValue("prerelease", out object prerelease) || !(prerelease is bool) || (bool)prerelease)
                throw new InvalidDataException("Стабильный опубликованный выпуск не найден.");
            string tag = release.TryGetValue("tag_name", out object tagValue) ? Convert.ToString(tagValue).Trim() : "";
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag.Substring(1);
            if (!Version.TryParse(tag, out Version version)) throw new InvalidDataException("Не удалось прочитать номер версии релиза.");
            if (release.TryGetValue("assets", out object values) && values is IEnumerable assets)
                foreach (object value in assets)
                {
                    if (!(value is Dictionary<string, object> asset)) continue;
                    string name = asset.TryGetValue("name", out object file) ? Convert.ToString(file) : "";
                    string state = asset.TryGetValue("state", out object status) ? Convert.ToString(status) : "";
                    string url = asset.TryGetValue("browser_download_url", out object link) ? Convert.ToString(link) : "";
                    bool sized = asset.TryGetValue("size", out object size) && Int64.TryParse(Convert.ToString(size), out long length) && length > 0;
                    if (String.Equals(name, "VanyaToolsNative.zip", StringComparison.OrdinalIgnoreCase) && state == "uploaded" && sized &&
                        Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps)
                        return Normalize(version);
                }
            throw new InvalidDataException("Пакет VanyaToolsNative.zip ещё не опубликован.");
        }
    }
}
