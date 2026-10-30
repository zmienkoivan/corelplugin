using System;
using System.IO;
using System.Linq;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); _checks++; }
    private static void Reject(Action work)
    { try { work(); } catch (IOException) { _checks++; return; } catch (InvalidDataException) { _checks++; return; } throw new Exception("Expected safe I/O rejection"); }
    private static int Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pending-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string marker = Path.Combine(root, "pending.txt"), output = Path.Combine(root, "ready.png");
            byte[] png = { 1, 5, 9, 20 }, native = { 2, 6, 10, 21 }, placement = { 3, 7, 11, 22 };
            File.WriteAllBytes(output, png); File.WriteAllBytes(output + ".native-raster", native);
            File.WriteAllBytes(output + ".insertion.json", placement); File.WriteAllText(marker, output);
            PendingRasterResults.Defer(marker, output, "Коррекция");
            Check(!File.Exists(marker), "Deferring releases the matching pending pointer");
            Check(File.ReadAllBytes(output).SequenceEqual(png), "PNG remains byte-for-byte intact");
            Check(File.ReadAllBytes(output + ".native-raster").SequenceEqual(native), "Native channels remain byte-for-byte intact");
            Check(File.ReadAllBytes(output + ".insertion.json").SequenceEqual(placement), "Original placement provenance remains intact");
            var rows = PendingRasterResults.Load(marker);
            Check(rows.Count == 1 && rows[0].Operation == "Коррекция" && !rows[0].Active, "Deferred result remains discoverable");
            Check(PendingRasterResults.IsDeferred(marker, output), "Leftover pointer is suppressed on restart");
            PendingRasterResults.Defer(marker, output, "Обновлённая подпись");
            Check(PendingRasterResults.Load(marker).Count == 1 && PendingRasterResults.Load(marker)[0].Operation == "Обновлённая подпись", "Repeated defer is deduplicated");
            PendingRasterResults.Restore(marker, output);
            Check(File.ReadAllText(marker) == output, "Restoring writes the original ready PNG pointer");
            Check(!PendingRasterResults.IsDeferred(marker, output), "Restored pointer is active across restart");
            Check(PendingRasterResults.Contains(marker, output), "Restored result retains native companion ownership for later recovery");
            Check(!PendingRasterResults.Contains(marker, Path.Combine(root, "unrelated.png")), "Unrelated outputs keep normal cleanup policy");
            Check(File.ReadAllBytes(output + ".native-raster").SequenceEqual(native), "Restore never alters native color data");
            using (var locked = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PendingRasterResults.Defer(marker, output, "Locked marker");
                Check(PendingRasterResults.IsDeferred(marker, output), "Locked pointer cannot block a durably deferred result");
                Check(File.Exists(marker), "Locked pointer was not forced away");
                Reject(() => PendingRasterResults.Restore(marker, output));
                Check(PendingRasterResults.IsDeferred(marker, output), "Failed restore leaves the row visible in deferred results");
                Check(File.ReadAllBytes(output + ".native-raster").SequenceEqual(native), "Failed restore preserves native data");
            }
            string another = Path.Combine(root, "another.png"); File.WriteAllText(another, "another ready raster");
            File.WriteAllText(marker, another);
            PendingRasterResults.Defer(marker, output, "Keep other job");
            Check(File.ReadAllText(marker) == another, "A different job's pending pointer is preserved");
            Check(!PendingRasterResults.ClearMatchingMarker(marker, output), "Only matching pending marker may be cleared");
            string corruptedMarker = Path.Combine(root, "corrupt-pending.txt");
            string badHistory = PendingRasterResults.HistoryPath(corruptedMarker);
            File.WriteAllText(corruptedMarker, output); File.WriteAllText(badHistory, "{invalid");
            Reject(() => PendingRasterResults.Defer(corruptedMarker, output, "No history loss"));
            Check(File.ReadAllText(corruptedMarker) == output && File.ReadAllText(badHistory) == "{invalid", "Corrupt journal never overwrites old records or pending pointer");
            string failedMarker = Path.Combine(root, "write-fails.txt"); File.WriteAllText(failedMarker, output);
            Directory.CreateDirectory(PendingRasterResults.HistoryPath(failedMarker));
            Reject(() => PendingRasterResults.Defer(failedMarker, output, "Publication failure"));
            Check(File.ReadAllText(failedMarker) == output, "Failed archive publication retains pending result");
            Check(!Directory.GetFiles(root, "*.tmp").Any(), "Atomic journal publication cleans staging files");
            Reject(() => PendingRasterResults.Defer(marker, Path.Combine(root, "missing.png"), "Missing"));
            Check(File.ReadAllText(marker) == another, "Missing PNG cannot clear another pending pointer");
            string limitMarker = Path.Combine(root, "bounded.txt");
            for (int i = 0; i <= PendingRasterResults.Limit; i++)
            {
                string path = Path.Combine(root, "bounded-" + i + ".png"); File.WriteAllText(path, "ready " + i);
                PendingRasterResults.Defer(limitMarker, path, "Item " + i);
            }
            rows = PendingRasterResults.Load(limitMarker);
            Check(rows.Count == PendingRasterResults.Limit, "Deferred index remains bounded");
            Check(rows[0].Operation == "Item 50", "Newest deferred result appears first");
            Check(Directory.GetFiles(root, "bounded-*.png").Length == PendingRasterResults.Limit + 1, "Index limit never removes ready raster files");
            File.Delete(rows[0].Path);
            Check(PendingRasterResults.Load(limitMarker).Count == PendingRasterResults.Limit - 1, "Deleted external PNG is omitted without deleting other files");
            Console.WriteLine("Pending raster recovery checks passed: " + _checks + ". No Corel, network or image processing.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
