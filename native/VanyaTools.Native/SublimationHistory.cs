using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class SublimationResult
    {
        public string Path { get; set; }
        public string MetadataPath { get; set; }
        public string Part { get; set; }
        public string ModelId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? InsertedUtc { get; set; }
        public string InsertionError { get; set; }
        public bool CanReinsert { get; set; }
    }

    // Stores only completed local files. Re-insertion never calls the image provider.
    internal static class SublimationHistory
    {
        internal const int Limit = 10;

        internal static List<SublimationResult> Load(string path)
        {
            if (!File.Exists(path)) return new List<SublimationResult>();
            try
            {
                if (new FileInfo(path).Length > 1000000) return new List<SublimationResult>();
                return Normalize(new JavaScriptSerializer().Deserialize<List<SublimationResult>>(File.ReadAllText(path)));
            }
            catch (ArgumentException) { return new List<SublimationResult>(); }
            catch (InvalidOperationException) { return new List<SublimationResult>(); }
            catch (IOException) { return new List<SublimationResult>(); }
        }

        internal static List<SublimationResult> Remember(string historyPath, SublimationResult result)
        {
            if (result == null || String.IsNullOrWhiteSpace(result.Path) || !File.Exists(result.Path))
                throw new FileNotFoundException("Готовая развёртка не найдена.", result?.Path);
            var rows = Load(historyPath);
            var previous = rows.FirstOrDefault(row => SamePath(row.Path, result.Path));
            if (previous != null)
            {
                result.CreatedUtc = previous.CreatedUtc;
                if (!result.InsertedUtc.HasValue) result.InsertedUtc = previous.InsertedUtc;
            }
            rows.RemoveAll(row => SamePath(row.Path, result.Path));
            rows.Insert(0, result);
            rows = Normalize(rows);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(historyPath));
            string staging = historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staging, new JavaScriptSerializer().Serialize(rows));
                if (File.Exists(historyPath)) File.Replace(staging, historyPath, null);
                else File.Move(staging, historyPath);
            }
            finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { } }
            return rows;
        }

        private static List<SublimationResult> Normalize(IEnumerable<SublimationResult> rows)
        {
            var result = new List<SublimationResult>();
            foreach (var row in rows ?? Enumerable.Empty<SublimationResult>())
            {
                if (row == null || String.IsNullOrWhiteSpace(row.Path) || !File.Exists(row.Path) ||
                    result.Any(previous => SamePath(previous.Path, row.Path))) continue;
                result.Add(row);
                if (result.Count == Limit) break;
            }
            return result;
        }

        private static bool SamePath(string first, string second) =>
            String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    internal static class SublimationAnalysisCache
    {
        // The image model and upscaling choice are deliberately absent: neither affects analysis.
        // All values used by AnalysisPrompt/SleeveFocusPrompt are included.
        internal static string PixelKey(byte[] bgra, int pixelWidth, int pixelHeight,
            string part, string hint, int width, int height)
        {
            if (bgra == null || pixelWidth <= 0 || pixelHeight <= 0 ||
                (long)pixelWidth * pixelHeight * 4 != bgra.Length)
                throw new ArgumentException("Некорректные пиксели исходника.");
            using (var hash = SHA256.Create())
            {
                string pixels = Convert.ToBase64String(hash.ComputeHash(bgra));
                string parameters = new JavaScriptSerializer().Serialize(new object[]
                    { "sublimation-pixels-v3", pixelWidth, pixelHeight, pixels, part ?? "", hint ?? "", width, height });
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(parameters)));
            }
        }

        internal static bool Copy(string sourceDirectory, string targetDirectory, bool sleeve)
        {
            string analysis = System.IO.Path.Combine(sourceDirectory, "analysis.txt");
            string reference = System.IO.Path.Combine(sourceDirectory, "sleeve-reference.png");
            if (!File.Exists(analysis) || new FileInfo(analysis).Length == 0 ||
                String.IsNullOrWhiteSpace(File.ReadAllText(analysis)) ||
                (sleeve && (!File.Exists(reference) || new FileInfo(reference).Length == 0))) return false;
            File.Copy(analysis, System.IO.Path.Combine(targetDirectory, "analysis.txt"), true);
            if (sleeve)
            {
                File.Copy(reference, System.IO.Path.Combine(targetDirectory, "sleeve-reference.png"), true);
                string focus = System.IO.Path.Combine(sourceDirectory, "sleeve-focus-v2.txt");
                if (File.Exists(focus)) File.Copy(focus, System.IO.Path.Combine(targetDirectory, "sleeve-focus-v2.txt"), true);
            }
            return true;
        }
    }
}
