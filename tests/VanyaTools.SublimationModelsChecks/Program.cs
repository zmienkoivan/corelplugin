using System;
using System.Collections.Generic;
using VanyaTools.Native;

internal static class Program
{
    private const string Reference = "https://example.invalid/synthetic-garment.png";
    private const string Prompt = "Synthetic flat garment artwork. Preserve the reference layout.";
    private static int _checks;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); _checks++; }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidOperationException) { _checks++; return; } throw new Exception(message); }

    private static int Main()
    {
        try
        {
            Check(SublimationModels.StoredModel(null) == SublimationModels.DefaultId, "Legacy metadata must use FLUX");
            Check(SublimationModels.StoredModel("") == SublimationModels.DefaultId, "Empty model must use FLUX");
            Check(SublimationModels.StoredModel(" ") == SublimationModels.DefaultId, "Blank model must use FLUX");
            Check(SublimationModels.Choices.Length == 4, "Four explicit model choices");
            foreach (var choice in SublimationModels.Choices)
                Check(SublimationModels.StoredModel(choice.Id) == choice.Id && SublimationModels.Find(choice.Id) == choice,
                    "Saved model should remain exact, without an automatic fallback");
            Reject(() => SublimationModels.StoredModel("unknown/model"), "Unknown saved model must not silently create another paid request");

            var flux = SublimationModels.Input(SublimationModels.DefaultId, Reference, Prompt, 650, 850);
            Check(flux.Count == 7 && (string)flux["aspect_ratio"] == "custom", "FLUX input contract unchanged");
            Check((int)flux["width"] == 1552 && (int)flux["height"] == 2048, "FLUX keeps legacy model dimensions");
            Check((int)flux["safety_tolerance"] == 2, "Existing FLUX safety tolerance must not be weakened");
            Check((string)flux["output_format"] == "png" && ((string[])flux["input_images"])[0] == Reference, "FLUX reference and PNG format");
            Check(!flux.ContainsKey("image_input") && (string)flux["prompt"] == Prompt, "FLUX source prompt remains unchanged");

            foreach (string id in new[] { SublimationModels.Nano2, SublimationModels.NanoPro })
            {
                var nano = SublimationModels.Input(id, Reference, Prompt, 650, 850);
                Check((string)nano["aspect_ratio"] == "3:4", "Nano uses a supported nearby output ratio");
                Check((string)nano["resolution"] == "2K" && (string)nano["output_format"] == "png", "Nano resolution and PNG");
                Check(((string[])nano["image_input"])[0] == Reference && (string)nano["prompt"] == Prompt, "Nano reference and prompt are not rewritten");
                Check(!nano.ContainsKey("width") && !nano.ContainsKey("height") && !nano.ContainsKey("input_images"), "No FLUX-only inputs sent to Nano");
                Check(!nano.ContainsKey("safety_tolerance") && !nano.ContainsKey("disable_safety_checker"), "Provider defaults retained for Nano filters");
                var sleeve = SublimationModels.Plan(id, 450, 300);
                Check(sleeve.AspectRatio == "3:2" && sleeve.Width == 2048 && sleeve.Height == 1365, "Sleeve output ratio and estimated 2K dimensions");
                Reject(() => SublimationModels.Plan(id, 100, 300), "Severely mismatched Nano format must fail before capture or billing");
            }
            var pro = SublimationModels.Input(SublimationModels.NanoPro, Reference, Prompt, 650, 850);
            Check(pro.ContainsKey("allow_fallback_model") && !(bool)pro["allow_fallback_model"], "Pro never silently switches provider models");
            Check(!SublimationModels.Input(SublimationModels.Nano2, Reference, Prompt, 650, 850).ContainsKey("allow_fallback_model"), "Pro-only fallback flag absent from Nano 2");

            var seedream = SublimationModels.Input(SublimationModels.Seedream, Reference, Prompt, 650, 850);
            Check((string)seedream["size"] == "custom" && (int)seedream["width"] == 1552 && (int)seedream["height"] == 2048, "Seedream custom size preserves target ratio");
            Check((string)seedream["sequential_image_generation"] == "disabled" && (int)seedream["max_images"] == 1, "A run generates one image");
            Check(((string[])seedream["image_input"])[0] == Reference && (string)seedream["prompt"] == Prompt, "Seedream reference and prompt retained");
            Check(!seedream.ContainsKey("output_format") && !seedream.ContainsKey("resolution") && !seedream.ContainsKey("safety_tolerance"), "Unsupported fields are absent from Seedream");
            foreach (var pair in new[] { Tuple.Create(100, 300), Tuple.Create(300, 100), Tuple.Create(550, 750), Tuple.Create(1000, 1000) })
            {
                var plan = SublimationModels.Plan(SublimationModels.Seedream, pair.Item1, pair.Item2);
                Check(plan.Width >= 1024 && plan.Width <= 4096 && plan.Height >= 1024 && plan.Height <= 4096,
                    "Seedream pixel limits remain valid for accepted garment dimensions");
            }
            Check(((string)SublimationModels.Input(SublimationModels.Seedream, Reference, new string('a', 4000), 650, 850)["prompt"]).Length == 4000,
                "Seedream accepts exactly 4000 characters without truncation");
            Reject(() => SublimationModels.Input(SublimationModels.Seedream, Reference, new string('a', 4001), 650, 850), "Seedream prompt limit preflight");
            Reject(() => SublimationModels.Input(SublimationModels.DefaultId, "", Prompt, 650, 850), "Missing reference preflight");
            Reject(() => SublimationModels.Input(SublimationModels.DefaultId, Reference, " ", 650, 850), "Missing prompt preflight");
            Reject(() => SublimationModels.Plan(SublimationModels.DefaultId, 0, 850), "Zero dimensions rejected");
            Reject(() => SublimationModels.Plan(SublimationModels.DefaultId, 1000, 50), "Unsupported extreme ratio rejected");
            Console.WriteLine("Sublimation model checks passed: " + _checks + ". No Corel, network or paid generation.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
