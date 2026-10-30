using System;
using System.Threading;
using System.Threading.Tasks;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); _checks++; }
    private static string Release(string tag = "v1.1.96", string draft = "false", string prerelease = "false", string state = "uploaded", string size = "100")
        => "{\"tag_name\":\"" + tag + "\",\"draft\":" + draft + ",\"prerelease\":" + prerelease +
            ",\"assets\":[{\"name\":\"VanyaToolsNative.zip\",\"state\":\"" + state + "\",\"size\":" + size +
            ",\"browser_download_url\":\"https://github.com/zmienkoivan/corelplugin/releases/download/" + tag + "/VanyaToolsNative.zip\"}]}";
    private static void Reject(string json)
    { try { UpdateAvailabilityProbe.ReleaseVersion(json); } catch { _checks++; return; } throw new Exception("Invalid release was accepted"); }
    private static int Main()
    {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("Update availability checks passed: " + _checks + ". No network, Corel or downloads."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Run()
    {
        Check(UpdateAvailabilityProbe.Normalize(new Version(1, 1, 95)) == new Version(1, 1, 95, 0), "Three and four version parts compare equally");
        Check(UpdateAvailabilityProbe.Normalize(new Version(1, 1)) == new Version(1, 1, 0, 0), "Absent version parts normalize to zero");
        Check(UpdateAvailabilityProbe.ReleaseVersion(Release()) == new Version(1, 1, 96, 0), "Latest stable asset yields version");
        Check(UpdateAvailabilityProbe.ReleaseVersion(Release("V1.2.3.4")) == new Version(1, 2, 3, 4), "Revision and uppercase prefix retained");
        Reject(Release(draft: "true")); Reject(Release(prerelease: "true")); Reject(Release(state: "new"));
        Reject(Release(size: "0")); Reject(Release(size: "-1")); Reject(Release(tag: "v1.2.3-beta"));
        Reject("{}"); Reject(Release().Replace("VanyaToolsNative.zip", "Other.zip"));
        Reject(Release().Replace("https://", "file://")); Reject("invalid"); Reject("");

        DateTime now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        int fetches = 0; string data = Release(); bool fail = false;
        var probe = new UpdateAvailabilityProbe(() => { Interlocked.Increment(ref fetches); if (fail) throw new Exception("offline"); return Task.FromResult(data); }, () => now);
        var status = await probe.CheckAsync(new Version(1, 1, 95, 0));
        Check(status.State == UpdateAvailabilityState.Available && status.LatestVersion == new Version(1, 1, 96, 0), "New upload lights update state");
        Check(status.Message.Contains("1.1.96") && status.CheckedUtc == now, "Tooltip includes version and check timestamp");
        status = await probe.CheckAsync(new Version(1, 1, 96, 0));
        Check(status.State == UpdateAvailabilityState.Current && fetches == 1, "Same version is current using cached metadata");
        status = await probe.CheckAsync(new Version(1, 2, 0));
        Check(status.State == UpdateAvailabilityState.Current && fetches == 1, "Newer local build remains current");
        now = now.AddMinutes(14); await probe.CheckAsync(new Version(1, 1, 95));
        Check(fetches == 1, "Normal check respects fifteen-minute cache");
        await probe.CheckAsync(new Version(1, 1, 95), true);
        Check(fetches == 2, "Manual check bypasses existing cache");
        now = now.AddMinutes(16); fail = true;
        status = await probe.CheckAsync(new Version(1, 1, 95));
        Check(fetches == 3 && status.State == UpdateAvailabilityState.Available && status.Message.Contains("offline"), "Known update survives network failure with explanation");
        status = await probe.CheckAsync(new Version(1, 1, 96));
        Check(status.State == UpdateAvailabilityState.Unknown, "Network failure cannot show false green");
        now = now.AddSeconds(59); await probe.CheckAsync(new Version(1, 1, 96));
        Check(fetches == 3, "Failures have bounded one-minute cache");
        now = now.AddSeconds(2); fail = false; data = Release("v1.1.97");
        status = await probe.CheckAsync(new Version(1, 1, 96));
        Check(fetches == 4 && status.State == UpdateAvailabilityState.Available && status.LatestVersion.Build == 97, "Expired failed check retries and recovers");

        var gate = new TaskCompletionSource<string>(); int shared = 0;
        var concurrent = new UpdateAvailabilityProbe(() => { Interlocked.Increment(ref shared); return gate.Task; });
        Task<UpdateAvailabilityStatus> first = concurrent.CheckAsync(new Version(1, 1, 95));
        Task<UpdateAvailabilityStatus> second = concurrent.CheckAsync(new Version(1, 1, 96), true);
        Task<UpdateAvailabilityStatus> third = concurrent.CheckAsync(new Version(1, 1, 95), true);
        gate.SetResult(Release());
        var statuses = await Task.WhenAll(first, second, third);
        Check(shared == 1, "Concurrent normal and manual checks share one network request");
        Check(statuses[0].State == UpdateAvailabilityState.Available && statuses[1].State == UpdateAvailabilityState.Current && statuses[2].State == UpdateAvailabilityState.Available,
            "Coalesced results compare against each caller's installed version");
        var invalid = new UpdateAvailabilityProbe(() => Task.FromResult(Release(draft: "true")));
        status = await invalid.CheckAsync(new Version(1, 1, 95));
        Check(status.State == UpdateAvailabilityState.Unknown && status.LatestVersion == null, "Unpublished release is unknown rather than green/red");
        var timeout = new UpdateAvailabilityProbe(() => { throw new TaskCanceledException(); });
        status = await timeout.CheckAsync(new Version(1, 1, 95));
        Check(status.State == UpdateAvailabilityState.Unknown && status.Message.Contains("время ожидания"), "Timeout is understandable and not up-to-date");
    }
}
