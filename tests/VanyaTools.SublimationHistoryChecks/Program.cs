using System;
using System.IO;
using System.Linq;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; }
    private static string Key(string pixels, string part, string hint, int width, int height) =>
        SublimationAnalysisCache.PixelKey(File.ReadAllBytes(pixels), 1, 1, part, hint, width, height);

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vanya-SublimationHistoryChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.png");
            File.WriteAllBytes(source, new byte[] { 1, 3, 5, 7 });
            string key = Key(source, "Перед", "Надпись", 650, 850);
            Check(key == Key(source, "Перед", "Надпись", 650, 850), "Identical source/context reuses analysis");
            Check(key != Key(source, "Спинка", "Надпись", 650, 850), "Different selected part invalidates analysis");
            Check(key != Key(source, "Перед", "Другая надпись", 650, 850), "Changed hint invalidates analysis");
            Check(key != Key(source, "Перед", "Надпись", 651, 850), "Changed width invalidates analysis");
            Check(key != Key(source, "Перед", "Надпись", 650, 851), "Changed height invalidates analysis");
            string samePixels = Path.Combine(root, "different-path.png"); File.Copy(source, samePixels);
            Check(key == Key(samePixels, "Перед", "Надпись", 650, 850), "New capture filename does not invalidate identical source");
            File.WriteAllBytes(source, new byte[] { 1, 3, 5, 8 });
            Check(key != Key(source, "Перед", "Надпись", 650, 850), "Changed source invalidates analysis");
            byte[] samples = { 1, 3, 5, 255, 2, 4, 6, 255 };
            string pixelKey = SublimationAnalysisCache.PixelKey(samples, 2, 1, "Перед", "Надпись", 650, 850);
            Check(pixelKey == SublimationAnalysisCache.PixelKey((byte[])samples.Clone(), 2, 1, "Перед", "Надпись", 650, 850),
                "Decoded pixels compare without PNG metadata, path or DPI");
            Check(pixelKey != SublimationAnalysisCache.PixelKey(samples, 1, 2, "Перед", "Надпись", 650, 850),
                "Same bytes in different image dimensions do not match");
            samples[3] = 254;
            Check(pixelKey != SublimationAnalysisCache.PixelKey(samples, 2, 1, "Перед", "Надпись", 650, 850), "Alpha changes invalidate analysis");

            string cache = Path.Combine(root, "cache"), target = Path.Combine(root, "next-model");
            Directory.CreateDirectory(cache); Directory.CreateDirectory(target);
            Check(!SublimationAnalysisCache.Copy(cache, target, false), "Missing analysis cannot be reused");
            File.WriteAllText(Path.Combine(cache, "analysis.txt"), " ");
            Check(!SublimationAnalysisCache.Copy(cache, target, false), "Blank analysis cannot be reused");
            File.WriteAllText(Path.Combine(cache, "analysis.txt"), "Проверенный анализ переда");
            File.WriteAllText(Path.Combine(cache, "generated.png"), "Old model output must not be reused");
            File.WriteAllText(Path.Combine(cache, "upscale-0.png"), "Old image tile must not be reused");
            Check(SublimationAnalysisCache.Copy(cache, target, false), "Body analysis can be reused");
            Check(File.ReadAllText(Path.Combine(target, "analysis.txt")) == "Проверенный анализ переда", "Analysis contents preserved exactly");
            Check(!File.Exists(Path.Combine(target, "generated.png")) && !File.Exists(Path.Combine(target, "upscale-0.png")), "New model must generate its own artwork and tiles");
            Check(!SublimationAnalysisCache.Copy(cache, target, true), "Sleeve requires its prepared reference");
            File.WriteAllBytes(Path.Combine(cache, "sleeve-reference.png"), new byte[] { 2, 4, 6 });
            File.WriteAllText(Path.Combine(cache, "sleeve-focus-v2.txt"), "LEFT_BOX: ...");
            Check(SublimationAnalysisCache.Copy(cache, target, true), "Complete sleeve analysis can be reused");
            Check(File.ReadAllBytes(Path.Combine(target, "sleeve-reference.png")).SequenceEqual(new byte[] { 2, 4, 6 }), "Exact sleeve reference retained");
            Check(File.ReadAllText(Path.Combine(target, "sleeve-focus-v2.txt")) == "LEFT_BOX: ...", "Focus metadata retained");

            string history = Path.Combine(root, "history.json");
            Check(SublimationHistory.Load(history).Count == 0, "New journal empty");
            string final = Path.Combine(root, "final.png"); File.WriteAllText(final, "Complete PNG fixture");
            DateTime created = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
            SublimationHistory.Remember(history, new SublimationResult { Path = final, CreatedUtc = created,
                ModelId = "model-a", Part = "Перед", Width = 650, Height = 850 });
            var rows = SublimationHistory.Load(history);
            Check(rows.Count == 1 && rows[0].Path == final && rows[0].Width == 650, "Completed file retained before insertion");
            Check(!rows[0].InsertedUtc.HasValue, "Ready is distinct from inserted");
            SublimationHistory.Remember(history, new SublimationResult { Path = final, CreatedUtc = DateTime.UtcNow,
                InsertedUtc = created.AddMinutes(1), ModelId = "model-a" });
            rows = SublimationHistory.Load(history);
            Check(rows.Count == 1 && rows[0].CreatedUtc == created, "Repeat insertion updates one result, preserving original time");
            Check(rows[0].InsertedUtc == created.AddMinutes(1), "Successful insertion is tracked separately");
            SublimationHistory.Remember(history, new SublimationResult { Path = final, InsertionError = "Document closed" });
            rows = SublimationHistory.Load(history);
            Check(rows.Count == 1 && rows[0].InsertionError == "Document closed" && rows[0].InsertedUtc.HasValue,
                "Failed repeat retains PNG and previous successful insertion");
            for (int i = 0; i < 15; i++)
            {
                string file = Path.Combine(root, "result-" + i + ".png"); File.WriteAllText(file, "result");
                SublimationHistory.Remember(history, new SublimationResult { Path = file, CreatedUtc = created.AddMinutes(i) });
            }
            rows = SublimationHistory.Load(history);
            Check(rows.Count == SublimationHistory.Limit, "Journal bounded to ten recent results");
            Check(rows[0].Path.EndsWith("result-14.png"), "Newest result first");
            File.Delete(rows[0].Path);
            Check(SublimationHistory.Load(history).Count == SublimationHistory.Limit - 1, "Deleted files do not appear as insertable results");
            File.WriteAllText(history, "broken JSON");
            Check(SublimationHistory.Load(history).Count == 0, "Corrupt journal cannot prevent tab opening");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "Atomic writes clean staging files");
            Console.WriteLine("Sublimation history/cache checks passed: " + _checks + ". No Corel, network or generation.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string expected = Path.Combine(Path.GetTempPath(), "Vanya-SublimationHistoryChecks-");
            if (Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }
}
