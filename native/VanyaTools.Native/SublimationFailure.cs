using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    // A provider moderation rejection is terminal, not a network retry.
    // Keep this separate from the shared worker's prediction/retry policy.
    internal static class SublimationFailure
    {
        private static readonly Regex SensitiveCode = new Regex(@"(?<![A-Za-z0-9])E005(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal const string SensitiveMessage = "Модель отклонила изображение или результат (E005). Развёртка не создана. " +
            "Исходник и готовые этапы сохранены. Повтор будет новым запросом; автоматическое продолжение отключено. " +
            "Для нового задания выберите исходник и нажмите «Создать развёртку из выделения».";

        internal static bool IsSensitiveRejection(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
                if (IsSensitiveError(current.Message)) return true;
            return false;
        }

        internal static bool IsSensitiveError(string message)
        {
            return !String.IsNullOrEmpty(message) && (SensitiveCode.IsMatch(message) ||
                message.IndexOf("flagged as sensitive", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool IsSensitivePrediction(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var prediction = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (prediction == null || !prediction.TryGetValue("status", out object status) ||
                    !String.Equals(Convert.ToString(status), "failed", StringComparison.OrdinalIgnoreCase)) return false;
                return prediction.TryGetValue("error", out object error) && IsSensitiveError(Convert.ToString(error));
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal static bool BlocksResume(bool providerBlocked, bool hasReadyOutput)
        {
            return providerBlocked && !hasReadyOutput;
        }

        internal static string RetryCaption(bool hasReadyOutput)
        {
            return hasReadyOutput ? "Повторить вставку · без новой генерации" : "Продолжить с готового этапа";
        }
    }
}
