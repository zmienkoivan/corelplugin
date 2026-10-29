using System;
using System.Collections.Generic;
using System.Globalization;

namespace VanyaTools.Native
{
    // Input fields are verified against each model's public Replicate API schema.
    // Each choice is explicit; a provider rejection never selects another model.
    internal static class SublimationModels
    {
        internal const string DefaultId = "black-forest-labs/flux-2-pro";
        internal const string Nano2 = "google/nano-banana-2", NanoPro = "google/nano-banana-pro";
        internal const string Seedream = "bytedance/seedream-4.5";

        internal sealed class Choice
        {
            internal readonly string Id, Name;
            internal Choice(string id, string name) { Id = id; Name = name; }
            public override string ToString() => Name;
        }

        internal sealed class ImagePlan
        {
            internal int Width, Height;
            internal string AspectRatio;
            internal string Description => AspectRatio == "custom" ? Width + " × " + Height + " px" :
                "2K · " + AspectRatio;
        }

        internal static readonly Choice[] Choices = {
            new Choice(DefaultId, "FLUX.2 Pro"), new Choice(Nano2, "Nano Banana 2"),
            new Choice(NanoPro, "Nano Banana Pro"), new Choice(Seedream, "Seedream 4.5")
        };
        private static readonly string[] StandardRatios = { "1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9" };
        private static readonly string[] Nano2Ratios = { "1:1", "1:4", "1:8", "2:3", "3:2", "3:4", "4:1", "4:3", "4:5", "5:4", "8:1", "9:16", "16:9", "21:9" };

        internal static Choice Find(string id)
        {
            foreach (Choice model in Choices) if (String.Equals(model.Id, id, StringComparison.Ordinal)) return model;
            throw new InvalidOperationException("Неизвестная модель развёртки. Выберите модель и создайте новое задание.");
        }

        internal static string StoredModel(string id)
        {
            string resolved = String.IsNullOrWhiteSpace(id) ? DefaultId : id;
            return Find(resolved).Id;
        }

        internal static ImagePlan Plan(string modelId, int widthMm, int heightMm)
        {
            Find(modelId);
            if (widthMm <= 0 || heightMm <= 0 || (double)widthMm / heightMm < .33 || (double)widthMm / heightMm > 3)
                throw new InvalidOperationException("Укажите допустимый размер прямоугольника для развёртки.");
            double ratio = (double)widthMm / heightMm;
            ImagePlan plan = LegacySize(ratio);
            if (modelId == DefaultId) return plan;
            if (modelId == Seedream)
            {
                double scale = Math.Max(1, 1024.0 / Math.Min(plan.Width, plan.Height));
                plan.Width = (int)Math.Round(plan.Width * scale / 16) * 16;
                plan.Height = (int)Math.Round(plan.Height * scale / 16) * 16;
                if (plan.Width < 1024 || plan.Height < 1024 || plan.Width > 4096 || plan.Height > 4096)
                    throw new InvalidOperationException("Для Seedream нужны стороны от 1024 до 4096 px.");
                return plan;
            }
            string closest = null; double difference = Double.PositiveInfinity;
            foreach (string item in modelId == Nano2 ? Nano2Ratios : StandardRatios)
            {
                string[] pair = item.Split(':');
                double candidate = Double.Parse(pair[0], CultureInfo.InvariantCulture) / Double.Parse(pair[1], CultureInfo.InvariantCulture);
                double distance = Math.Abs(Math.Log(candidate / ratio));
                if (distance >= difference) continue;
                closest = item; difference = distance;
            }
            if (Math.Exp(difference) > 1.1)
                throw new InvalidOperationException("Модель " + Find(modelId).Name + " не поддерживает близкий формат этого листа. " +
                    "Выберите FLUX.2 Pro или Seedream 4.5 с произвольным размером.");
            string[] selected = closest.Split(':');
            double selectedRatio = Double.Parse(selected[0], CultureInfo.InvariantCulture) / Double.Parse(selected[1], CultureInfo.InvariantCulture);
            plan.Width = selectedRatio >= 1 ? 2048 : Math.Max(1, (int)Math.Round(2048 * selectedRatio));
            plan.Height = selectedRatio >= 1 ? Math.Max(1, (int)Math.Round(2048 / selectedRatio)) : 2048;
            plan.AspectRatio = closest;
            return plan;
        }

        private static ImagePlan LegacySize(double ratio)
        {
            double longSide = Math.Min(2048, Math.Sqrt(3800000 * Math.Max(ratio, 1 / ratio)));
            return new ImagePlan { Width = Math.Max(256, (int)Math.Floor((ratio >= 1 ? longSide : longSide * ratio) / 16) * 16),
                Height = Math.Max(256, (int)Math.Floor((ratio >= 1 ? longSide / ratio : longSide) / 16) * 16), AspectRatio = "custom" };
        }

        internal static Dictionary<string, object> Input(string modelId, string reference, string prompt, int widthMm, int heightMm)
        {
            ImagePlan plan = Plan(modelId, widthMm, heightMm);
            if (String.IsNullOrWhiteSpace(reference) || String.IsNullOrWhiteSpace(prompt))
                throw new InvalidOperationException("Для развёртки нужны исходное изображение и описание.");
            if (modelId == Seedream && prompt.Length > 4000)
                throw new InvalidOperationException("Seedream 4.5 принимает описание до 4000 символов. " +
                    "Сократите уточнение или выберите другую модель для нового задания; готовый анализ сохранён.");
            var input = new Dictionary<string, object> { ["prompt"] = prompt };
            if (modelId == DefaultId)
            {
                input["input_images"] = new[] { reference }; input["aspect_ratio"] = "custom";
                input["width"] = plan.Width; input["height"] = plan.Height;
                input["output_format"] = "png"; input["safety_tolerance"] = 2;
            }
            else
            {
                input["image_input"] = new[] { reference };
                if (modelId == Seedream)
                {
                    input["size"] = "custom"; input["width"] = plan.Width; input["height"] = plan.Height;
                    input["sequential_image_generation"] = "disabled"; input["max_images"] = 1;
                }
                else
                {
                    input["aspect_ratio"] = plan.AspectRatio; input["resolution"] = "2K"; input["output_format"] = "png";
                    if (modelId == NanoPro) input["allow_fallback_model"] = false;
                }
            }
            return input;
        }
    }
}
