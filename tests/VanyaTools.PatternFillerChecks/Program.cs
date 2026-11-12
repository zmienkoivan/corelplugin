using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class Program
{
    internal const string Json = "{\"theme\":\"Морская гравюра\",\"style\":\"Чёрный контур, золотая заливка, штриховка\"," +
        "\"elements\":[\"Капля\",\"Гребень волны\",\"Ромб-чешуйка\",\"Звезда-искра\"]}";
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            Plans(); Pipeline(); Extraction(); Counts(); Viewport();
            Console.WriteLine("Pattern filler analysis checks passed: " + _checks + ". Synthetic images and stub worker only; no network or Corel.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Plans()
    {
        var parsed = PatternFillerBrief.Parse(Json);
        Check(parsed.Theme == "Морская гравюра" && parsed.Elements.Length == 4, "Theme or element set was lost.");
        Check(PatternFillerBrief.Parse("```json\n" + Json + "\n```").Style == parsed.Style, "Fenced JSON was not read.");
        var copy = parsed.Copy(); copy.Elements[0] = "Круг";
        Check(parsed.Elements[0] == "Капля", "Editing a cached plan mutates the original.");
        string prompt = PatternFillerBrief.GenerationPrompt(parsed);
        Check(parsed.Elements.All(prompt.Contains) && prompt.Contains(parsed.Theme) && prompt.Contains(parsed.Style),
            "Generation prompt omits the reviewed theme, style or elements.");
        Check(prompt.Contains("Row 1, column 1: Капля") && prompt.Contains("Row 2, column 2: Звезда-искра") &&
            prompt.Contains("2 columns and 2 rows") && prompt.Contains("#FF00FF") && prompt.Contains("STYLE MASTER") && prompt.Contains("Never tile"), "Sprite layout/matte contract changed.");
        for (int i = 0; i < 3; i++)
        {
            var preset = PatternFillerBrief.Preset(i); preset.Validate();
            Check(preset.Elements.All(PatternFillerBrief.GenerationPrompt(preset).Contains), "A geometric preset was lost.");
        }
        Reject(() => PatternFillerBrief.Parse("not json"));
        Reject(() => PatternFillerBrief.Parse("null"));
        Reject(() => PatternFillerBrief.Parse(Json.Replace("\"theme\":\"Морская гравюра\"", "\"theme\":\"\"")));
        Reject(() => PatternFillerBrief.Parse(Json.Replace("\"Капля\"", "7")));
        Reject(() => PatternFillerBrief.Parse(Json.Replace("\"Гребень волны\"", "\" капля \"")));
        Reject(() => PatternFillerBrief.Parse(Json.Replace(",\"Звезда-искра\"", "")));
        Reject(() => PatternFillerBrief.Parse(Json.Replace("\"Капля\"", new JavaScriptSerializer().Serialize(new string('x', 241)))));
        Reject(() => PatternFillerBrief.Parse(new string('x', 12001)));
        Reject(() => PatternFillerBrief.GenerationPrompt(PatternFillerBrief.Preset(-1)));
    }

    private static void Pipeline()
    {
        var source = new byte[32 * 32 * 4];
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, source, 32 * 4);
        image.Freeze();
        string key = PatternFillerAi.ReferenceKey(image);
        Check(key == PatternFillerAi.ReferenceKey(image), "Unchanged preview invalidates the analysis cache.");
        source[23] = 255;
        var changed = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, source, 32 * 4);
        changed.Freeze();
        Check(key != PatternFillerAi.ReferenceKey(changed), "Changed image reuses stale analysis.");

        ReplicateWorkerClient.Calls = 0;
        var plan = PatternFillerAi.Analyze(image, "local-test", CancellationToken.None);
        Check(ReplicateWorkerClient.Calls == 1 && ReplicateWorkerClient.Model == PatternFillerBrief.AnalysisModel,
            "Analysis started generation or used the wrong model.");
        Check(ReplicateWorkerClient.Input.ContainsKey("image") && !ReplicateWorkerClient.Input.ContainsKey("image_input") &&
            (int)ReplicateWorkerClient.Input["max_tokens"] == 1400, "Analysis request is not the vision-model contract.");
        Check(ReplicateWorkerClient.Input["prompt"].Equals(PatternFillerBrief.AnalysisTask(4, false)), "Wrong analysis task.");
        Check(!File.Exists(AiPrintTab.Upload) && !File.Exists(ReplicateWorkerClient.Output), "Analysis left input/output files.");

        var generated = PatternFillerAi.Generate(image, "local-test", plan, CancellationToken.None);
        var sprites = generated.Cells.Where(value => value != null).ToArray();
        Check(ReplicateWorkerClient.Calls == 2 && ReplicateWorkerClient.Model == SublimationModels.Nano2 &&
            sprites.Length == 4 && sprites.All(sprite => sprite.IsFrozen), "Generation did not produce four frozen sprites.");
        Check(ReplicateWorkerClient.Input.ContainsKey("image_input") &&
            ReplicateWorkerClient.Input["prompt"].Equals(PatternFillerBrief.GenerationPrompt(plan)), "Generation ignored the reviewed plan.");
        Check(!File.Exists(AiPrintTab.Upload) && !File.Exists(ReplicateWorkerClient.Output), "Generation left temporary files.");

        int beforeMini = ReplicateWorkerClient.Calls;
        var mini = PatternFillerAi.Analyze(image, "local-test", PatternFillerBrief.GptMiniModel, CancellationToken.None);
        Check(ReplicateWorkerClient.Calls == beforeMini + 1 && ReplicateWorkerClient.Model == PatternFillerBrief.GptMiniModel &&
            mini.Theme == plan.Theme, "GPT mini selection does not perform exactly one analysis.");
        Check(ReplicateWorkerClient.Input.ContainsKey("image_input") && !ReplicateWorkerClient.Input.ContainsKey("image") &&
            !ReplicateWorkerClient.Input.ContainsKey("max_tokens") && !ReplicateWorkerClient.Input.ContainsKey("max_image_resolution"),
            "GPT mini received Claude vision parameters.");
        Check((int)ReplicateWorkerClient.Input["max_completion_tokens"] == 3000 &&
            ReplicateWorkerClient.Input["reasoning_effort"].Equals("minimal") &&
            ReplicateWorkerClient.Input["verbosity"].Equals("low"), "GPT mini completion controls changed.");
        Check(!File.Exists(AiPrintTab.Upload) && !File.Exists(ReplicateWorkerClient.Output), "GPT mini left preview/output files.");
        int beforeInvalid = ReplicateWorkerClient.Calls;
        bool rejected = false;
        try { PatternFillerAi.Analyze(image, "local-test", "unknown/model", CancellationToken.None); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && ReplicateWorkerClient.Calls == beforeInvalid, "Invalid model was submitted.");

        string jwt = "Модель завершилась со статусом failed. RefreshError: invalid_grant: Invalid JWT Signature.";
        ReplicateWorkerClient.Failure = new InvalidOperationException(jwt);
        int beforeFailure = ReplicateWorkerClient.Calls;
        bool failed = false;
        try { PatternFillerAi.Analyze(image, "local-test", PatternFillerBrief.GptMiniModel, CancellationToken.None); }
        catch (InvalidOperationException error)
        {
            failed = true;
            Check(ReplicateWorkerClient.Calls == beforeFailure + 1 && ReplicateWorkerClient.Model == PatternFillerBrief.GptMiniModel,
                "Provider failure automatically resubmits or switches the model.");
            Check(PatternFillerBrief.FailureMessage(error, true).Contains("подтверждения") &&
                !PatternFillerBrief.FailureMessage(error, true).Contains("RefreshError"), "Provider failure is not readable.");
            Check(!File.Exists(AiPrintTab.Upload), "Failed analysis left its preview.");
        }
        finally { ReplicateWorkerClient.Failure = null; }
        Check(failed, "Provider failure was swallowed.");
        Check(PatternFillerBrief.AnalysisModels[0].Id == PatternFillerBrief.AnalysisModel &&
            PatternFillerBrief.FindAnalysisModel(PatternFillerBrief.AnalysisModel).Name.Contains("Claude"),
            "Claude is no longer the default analyzer.");
        Check(PatternFillerBrief.FailureMessage(new Exception("ordinary error"), true) == "ordinary error",
            "Unrelated failures are misclassified as provider credentials.");
        Check(PatternFillerBrief.FailureMessage(new Exception(new string('x', 500)), true).Length <= 400,
            "Very long diagnostics overflow the window.");

        ReplicateWorkerClient.Failure = new OperationCanceledException();
        try
        {
            try { PatternFillerAi.Analyze(image, "local-test", CancellationToken.None); }
            catch (OperationCanceledException) { Check(!File.Exists(AiPrintTab.Upload), "Cancellation left an uploaded preview file."); return; }
            throw new Exception("Cancellation was swallowed.");
        }
        finally { ReplicateWorkerClient.Failure = null; }
    }


    private static void Extraction()
    {
        const int width = 320, height = 320;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = pixels[i + 2] = pixels[i + 3] = 255; }
        void Fill(int left, int top, int w, int h, byte b = 0, byte g = 0, byte r = 0)
        {
            for (int y = top; y < top + h; y++) for (int x = left; x < left + w; x++)
            { int i = (y * width + x) * 4; pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; }
        }
        Fill(30, 35, 45, 60); Fill(100, 45, 12, 20); // One disconnected ornament.
        Fill(30, 159, 90, 1); // Antialiasing strip from the next cell.
        Fill(0, 50, 3, 3, 189, 31, 231); // Colored matte noise on the cell boundary.
        Fill(200, 35, 50, 70, 80, 180, 230);
        Fill(220, 0, 3, 2, 247, 21, 250);
        Fill(0, 190, 20, 40); Fill(35, 190, 40, 40); Fill(105, 190, 40, 40); // Tiled cell.
        Fill(300, 200, 20, 40); // A genuinely cropped motif, no whole copy.
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        var plan = PatternFillerBrief.Parse(Json);
        var result = PatternFillerExtraction.Extract(image, plan, CancellationToken.None);
        Check(result.Count == 3 && result.Cells[3] == null, "Missing/cropped cell was hidden or fabricated.");
        Check(result.Cells[0].PixelWidth > 80 && !result.Representative[0],
            "Disconnected ornament or one-pixel strip was mistaken for a repeated pattern.");
        Check(result.Cells[1] != null, "Small colored matte residue discarded a whole figure.");
        Check(result.Representative[2] && result.Cells[2].PixelWidth < 60 && result.Cells[2].PixelHeight < 60,
            "A repeating cell was returned as several copies or lost entirely.");
        Check(result.Names.SequenceEqual(plan.Elements) && ReferenceEquals(result.Sheet, image),
            "Cell identities or the full original sheet were lost.");
        var after = new byte[pixels.Length]; image.CopyPixels(after, width * 4, 0);
        Check(after.SequenceEqual(pixels), "Extraction mutated the original paid sheet.");
    }

    private static void Counts()
    {
        for (int count = 4; count <= 10; count++)
        {
            var plan = new PatternFillerPlan { Theme = "Эталон", Style = "Гравюра",
                Elements = Enumerable.Range(1, count).Select(i => "Рисунок " + i).ToArray(), SmallFillers = count % 2 == 0 };
            plan.Validate();
            string json = new JavaScriptSerializer().Serialize(new { theme = plan.Theme, style = plan.Style, elements = plan.Elements });
            Check(PatternFillerBrief.Parse(json, count, plan.SmallFillers).Elements.SequenceEqual(plan.Elements),
                "Variable motif count was not parsed.");
            Check(plan.Copy().SmallFillers == plan.SmallFillers, "Plan copy lost the motif mode.");
            Reject(() => PatternFillerBrief.Parse(json, count == 10 ? 4 : count + 1));
            int columns = PatternFillerBrief.Columns(count), rows = (count + columns - 1) / columns;
            const int cell = 80;
            int width = columns * cell, height = rows * cell;
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4) pixels[i] = pixels[i + 2] = pixels[i + 3] = 255;
            for (int i = 0; i < count; i++)
                for (int y = i / columns * cell + 20; y < i / columns * cell + 60; y++)
                    for (int x = i % columns * cell + 20; x < i % columns * cell + 60; x++)
                    { int at = (y * width + x) * 4; pixels[at] = pixels[at + 2] = 0; }
            var sheet = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); sheet.Freeze();
            var extracted = PatternFillerExtraction.Extract(sheet, plan, CancellationToken.None);
            Check(extracted.Count == count && extracted.Cells.Length == count && extracted.Cells.All(image => image.IsFrozen),
                "Lost or fabricated cells in a non-square sheet.");
            Check(extracted.Names.SequenceEqual(plan.Elements) && extracted.SmallFillers == plan.SmallFillers,
                "Grid extraction lost identity or full/filler mode.");
            Check(PatternFillerBrief.GenerationPrompt(plan).Contains("exactly " + count) &&
                plan.Elements.All(PatternFillerBrief.GenerationPrompt(plan).Contains),
                "Variable-count prompt omitted a subject.");
            Check(PatternFillerBrief.Resolution(count) == (count > 4 ? "4K" : "2K"),
                "Insufficient resolution for a larger sheet.");
            var input = PatternFillerBrief.AnalysisInput(PatternFillerBrief.AnalysisModel, "local", count, false);
            Check(input["prompt"].ToString().Contains("exactly " + count) &&
                input["prompt"].ToString().Contains("SINGLE main print"), "Analysis does not use the hero/count contract.");
        }
        Reject(() => new PatternFillerPlan { Elements = Enumerable.Range(0, 11).Select(i => i.ToString()).ToArray() }.Validate());
    }

    private static void Viewport()
    {
        var view = new PatternViewport();
        var bounds = new System.Windows.Rect(-10, -20, 200, 100);
        view.Update(bounds, new System.Windows.Size(840, 640));
        Check(Math.Abs(view.Percent - 100) < .001 && view.Center == new System.Windows.Point(90, 30), "Initial fit failed.");
        var cursor = new System.Windows.Point(123, 217);
        var anchor = view.World(cursor);
        view.ZoomAt(3, cursor);
        Check((view.World(cursor) - anchor).Length < 1e-9, "Zoom lost the point under the cursor.");
        double scale = view.Scale;
        var center = view.Center;
        view.Update(new System.Windows.Rect(-40, -30, 230, 150), new System.Windows.Size(900, 700));
        Check(view.Scale == scale && view.Center == center, "Recalculation or resize reset the inspected area.");
        view.Pan(new System.Windows.Vector(75, -30));
        Check((view.Center - new System.Windows.Point(center.X - 75 / scale, center.Y + 30 / scale)).Length < 1e-9,
            "Pan moves by the wrong physical distance.");
        view.ZoomAt(1e20, cursor);
        Check(!Double.IsInfinity(view.Scale) && view.Scale > 0, "Unbounded zoom.");
        view.Fit();
        Check(Math.Abs(view.Percent - 100) < .001 && view.Center == new System.Windows.Point(75, 45),
            "Fit did not restore the latest outline.");
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { _checks++; return; }
        throw new Exception("Invalid plan accepted.");
    }

    private static void Check(bool value, string message)
    { _checks++; if (!value) throw new Exception(message); }
}

namespace VanyaTools.Native
{
    internal static class SublimationModels { internal const string Nano2 = "google/nano-banana-2"; }

    internal static class AiPrintTab
    {
        internal static string Upload;
        internal static string DataUri(BitmapSource image, int maxDimension = 2048, int maxPixels = 4000000)
        {
            Upload = Path.Combine(Path.GetTempPath(), "Vanya-Filler-Test-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllText(Upload, "synthetic"); return "replicate-file:" + Upload;
        }
        internal static BitmapSource Bitmap(string path)
        {
            const int width = 160, height = 160;
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i + 3] = 255;
                if (x % 80 > 15 && x % 80 < 65 && y % 80 > 15 && y % 80 < 65) continue;
                pixels[i] = pixels[i + 2] = 255;
            }
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze(); return image;
        }
    }

    internal static class ReplicateWorkerClient
    {
        internal static int Calls;
        internal static string Model, Output;
        internal static Dictionary<string, object> Input;
        internal static Exception Failure;
        internal static void Run(string model, string token, Dictionary<string, object> input, string outputPath,
            Action<string> progress = null, CancellationToken cancellation = default)
        {
            Calls++; Model = model; Input = input; Output = outputPath;
            cancellation.ThrowIfCancellationRequested();
            if (Failure != null) throw Failure;
            File.WriteAllText(outputPath, outputPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? Program.Json : "synthetic image");
        }
    }
}
