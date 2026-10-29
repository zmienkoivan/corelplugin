using System;
using System.IO;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }

    private static int Main()
    {
        try
        {
            const string refusal = "Модель завершилась со статусом failed. The input or output was flagged as sensitive. " +
                "Please try again with different inputs. (E005) (uJl6I3ruRD)";
            Check(SublimationFailure.IsSensitiveError(refusal), "Actual provider refusal is classified");
            Check(SublimationFailure.IsSensitiveError("Provider error (e005)"), "Code is case insensitive");
            Check(SublimationFailure.IsSensitiveError("The output was FLAGGED AS SENSITIVE"), "Provider wording works without code");
            Check(SublimationFailure.IsSensitiveRejection(new InvalidOperationException("stage failed", new IOException(refusal))),
                "Wrapped provider error remains classified");
            Check(!SublimationFailure.IsSensitiveRejection(null), "No exception is not a provider rejection");
            Check(!SublimationFailure.IsSensitiveError(null), "Null error is not classified");
            Check(!SublimationFailure.IsSensitiveError("E0051"), "Longer code is not classified");
            Check(!SublimationFailure.IsSensitiveError("XE005"), "Code embedded in identifier is not classified");
            Check(!SublimationFailure.IsSensitiveError("Модель ещё работает. Повтор продолжит этот же запрос без новой генерации."),
                "Polling timeout remains resumable");
            Check(!SublimationFailure.IsSensitiveError("HTTP 503 Service Unavailable"), "Server fault remains separate");
            Check(!SublimationFailure.IsSensitiveError("Связь оборвалась при проверке запроса."), "Network fault remains separate");

            Check(SublimationFailure.IsSensitivePrediction("{\"status\":\"failed\",\"error\":\"blocked (E005)\"}"),
                "Existing failed prediction restores refusal");
            Check(!SublimationFailure.IsSensitivePrediction("{\"status\":\"succeeded\",\"error\":\"E005\"}"),
                "Only terminal failed prediction blocks resume");
            Check(!SublimationFailure.IsSensitivePrediction("{\"status\":\"processing\",\"error\":null}"),
                "Running prediction remains resumable");
            Check(!SublimationFailure.IsSensitivePrediction("{\"status\":\"failed\",\"error\":\"HTTP 503\",\"input\":{\"prompt\":\"E005 flagged as sensitive\"}}"),
                "Prompt cannot be mistaken for the provider error");
            Check(!SublimationFailure.IsSensitivePrediction("{\"status\":\"failed\",\"error\":null}"), "Null provider error stays generic");
            Check(!SublimationFailure.IsSensitivePrediction("[]"), "Unexpected state shape stays generic");
            Check(!SublimationFailure.IsSensitivePrediction("not json"), "Broken state does not fabricate a refusal");
            Check(!SublimationFailure.IsSensitivePrediction(null), "Missing state stays generic");

            Check(SublimationFailure.BlocksResume(true, false), "Rejected unfinished job cannot rerun through Resume");
            Check(!SublimationFailure.BlocksResume(true, true), "Ready PNG can still be inserted without generation");
            Check(!SublimationFailure.BlocksResume(false, false), "Ordinary interrupted job can continue");
            Check(SublimationFailure.RetryCaption(true).Contains("без новой генерации"), "Ready output action describes insertion only");
            Check(!SublimationFailure.RetryCaption(false).Contains("без новой генерации"), "Unfinished action does not promise a free request");
            Console.WriteLine("Passed " + _checks + " sublimation failure checks.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
