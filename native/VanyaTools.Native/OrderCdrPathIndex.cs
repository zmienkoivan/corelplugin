using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VanyaTools.Native
{
    internal static class OrderCdrPathIndex
    {
        private static string LocalDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "order-cdr-links");

        private static string FileName(string server, int orderId)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(server.Trim().ToLowerInvariant()));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant()
                    + "-" + orderId + ".txt";
            }
        }

        private static string SharedDirectory(string yearFolder) =>
            String.IsNullOrWhiteSpace(yearFolder) ? null :
            Path.Combine(yearFolder, ".VanyaTools", "order-cdr-links");

        private static readonly OrderCdrLinkQueue RememberQueue = new OrderCdrLinkQueue(RememberSnapshot,
            error => Log.Info("Background order CDR link failed: " + error.GetType().Name),
            (milliseconds, success) => {
                if (!success || milliseconds >= 2000)
                    Log.Info("Background order CDR link: success=" + success + ", elapsed=" + milliseconds + " ms.");
            });

        internal static void QueueRemember(string server, int orderId, string cdrPath, string yearFolder,
            bool resolveDocumentYear = true) => RememberQueue.Enqueue(server, orderId, cdrPath, yearFolder, resolveDocumentYear);

        private static bool RememberSnapshot(OrderCdrLinkQueue.Snapshot value)
        {
            Log.Info("Background order CDR link started.");
            if (!Path.IsPathRooted(value.CdrPath) || !File.Exists(value.CdrPath)) return false;
            string year = value.YearFolder;
            bool yearRequired = !String.IsNullOrWhiteSpace(year);
            if (value.ResolveDocumentYear)
            {
                string customer = Path.GetDirectoryName(value.CdrPath);
                string month = customer == null ? null : Path.GetDirectoryName(customer);
                string candidate = month == null ? null : Path.GetDirectoryName(month);
                bool expectedYear = candidate != null && Path.GetFileName(candidate).Length == 4 &&
                    Int32.TryParse(Path.GetFileName(candidate), out int number) && number >= 2000 && number <= 2100;
                yearRequired |= expectedYear;
                year = expectedYear && Directory.Exists(candidate) ? candidate : OrderCdrFolders.YearFolder(year, null);
            }
            // A local link remains useful while the share is down. Keep retrying
            // the shared link if its year could not yet be resolved.
            bool written = Remember(value.Server, value.OrderId, value.CdrPath, year);
            return written && (!yearRequired || !String.IsNullOrWhiteSpace(year));
        }

        internal static bool Remember(string server, int orderId, string cdrPath, string yearFolder)
        {
            if (orderId <= 0 || !Path.IsPathRooted(cdrPath)) return false;
            string name = FileName(server, orderId);
            bool local = Write(Path.Combine(LocalDirectory, name), cdrPath);
            string shared = SharedDirectory(yearFolder);
            bool mirrored = shared == null || Write(Path.Combine(shared, name), cdrPath);
            return local && mirrored;
        }

        internal static string Find(string server, int orderId, string yearFolder)
        {
            if (orderId <= 0) return null;
            string name = FileName(server, orderId);
            string shared = SharedDirectory(yearFolder);
            string sharedPath = shared == null ? null : Read(Path.Combine(shared, name));
            return sharedPath ?? Read(Path.Combine(LocalDirectory, name));
        }

        private static string Read(string indexPath)
        {
            try
            {
                if (!File.Exists(indexPath)) return null;
                string cdrPath = File.ReadAllText(indexPath, Encoding.UTF8).Trim();
                return Path.IsPathRooted(cdrPath) &&
                    String.Equals(Path.GetExtension(cdrPath), ".cdr", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(cdrPath) ? cdrPath : null;
            }
            catch (Exception error)
            {
                Log.Error("Order CDR link read failed", error);
                return null;
            }
        }

        private static bool Write(string indexPath, string cdrPath)
        {
            string temporary = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(indexPath));
                if (File.Exists(indexPath) &&
                    String.Equals(File.ReadAllText(indexPath, Encoding.UTF8).Trim(), cdrPath,
                        StringComparison.OrdinalIgnoreCase)) return true;
                File.WriteAllText(temporary, cdrPath, Encoding.UTF8);
                if (File.Exists(indexPath)) File.Delete(indexPath);
                File.Move(temporary, indexPath);
                return true;
            }
            catch (Exception error) { Log.Error("Order CDR link write failed", error); return false; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }
}
