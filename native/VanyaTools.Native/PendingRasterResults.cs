using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class DeferredRasterResult
    {
        public string Path { get; set; }
        public string Operation { get; set; }
        public DateTime DeferredUtc { get; set; }
        public bool Active { get; set; }
    }

    // Only the small pointer files are updated. The ready PNG, insertion data
    // and native color-channel companion are never removed by deferring a job.
    internal static class PendingRasterResults
    {
        internal const int Limit = 50;
        private const int MaximumHistoryBytes = 1000000;
        internal static string HistoryPath(string pendingPath) => pendingPath + ".deferred.json";

        internal static List<DeferredRasterResult> Load(string pendingPath)
        {
            string history = HistoryPath(pendingPath);
            if (!File.Exists(history)) return new List<DeferredRasterResult>();
            if (new FileInfo(history).Length > MaximumHistoryBytes)
                throw new InvalidDataException("Список отложенных результатов слишком большой. Готовые PNG сохранены.");
            try
            {
                return Normalize(new JavaScriptSerializer().Deserialize<List<DeferredRasterResult>>(File.ReadAllText(history)));
            }
            catch (ArgumentException error) { throw new InvalidDataException("Не удалось прочитать список отложенных результатов. Готовые PNG сохранены.", error); }
            catch (InvalidOperationException error) { throw new InvalidDataException("Не удалось прочитать список отложенных результатов. Готовые PNG сохранены.", error); }
        }

        internal static void Defer(string pendingPath, string output, string operation)
        {
            if (String.IsNullOrWhiteSpace(output) || !File.Exists(output))
                throw new FileNotFoundException("Готовый PNG не найден.", output);
            var rows = Load(pendingPath);
            rows.RemoveAll(row => SamePath(row.Path, output));
            rows.Insert(0, new DeferredRasterResult { Path = System.IO.Path.GetFullPath(output),
                Operation = operation, DeferredUtc = DateTime.UtcNow });
            WriteAtomic(HistoryPath(pendingPath), new JavaScriptSerializer().Serialize(Normalize(rows)));
            // Persistence is complete. An obsolete or locked pending pointer
            // must not prevent this docker from accepting the next operation.
            ClearMatchingMarker(pendingPath, output);
        }

        internal static bool ClearMatchingMarker(string pendingPath, string output)
        {
            try
            {
                if (!File.Exists(pendingPath)) return true;
                if (!SamePath(File.ReadAllText(pendingPath).Trim(), output)) return false;
                File.Delete(pendingPath); return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static bool IsDeferred(string pendingPath, string output) =>
            Load(pendingPath).Any(row => !row.Active && SamePath(row.Path, output));

        internal static bool Contains(string pendingPath, string output) =>
            Load(pendingPath).Any(row => SamePath(row.Path, output));

        internal static void Restore(string pendingPath, string output)
        {
            if (String.IsNullOrWhiteSpace(output) || !File.Exists(output))
                throw new FileNotFoundException("Отложенный PNG больше не найден.", output);
            var rows = Load(pendingPath);
            foreach (var row in rows) if (SamePath(row.Path, output)) row.Active = true;
            // Do not hide the deferred row until the active pointer is durable.
            // If that pointer is locked, the result must remain discoverable.
            WriteAtomic(pendingPath, System.IO.Path.GetFullPath(output));
            WriteAtomic(HistoryPath(pendingPath), new JavaScriptSerializer().Serialize(rows));
        }

        private static List<DeferredRasterResult> Normalize(IEnumerable<DeferredRasterResult> rows)
        {
            var result = new List<DeferredRasterResult>();
            foreach (var row in rows ?? Enumerable.Empty<DeferredRasterResult>())
            {
                if (row == null || String.IsNullOrWhiteSpace(row.Path) || !File.Exists(row.Path) ||
                    result.Any(other => SamePath(other.Path, row.Path))) continue;
                result.Add(row); if (result.Count == Limit) break;
            }
            return result;
        }

        private static bool SamePath(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second)) return false;
            try { return String.Equals(System.IO.Path.GetFullPath(first), System.IO.Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        private static void WriteAtomic(string path, string contents)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staging, contents);
                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
            }
            finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { } }
        }
    }
}
