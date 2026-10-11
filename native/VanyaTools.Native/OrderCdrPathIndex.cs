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

        internal static void Remember(string server, int orderId, string cdrPath, string yearFolder)
        {
            if (orderId <= 0 || !Path.IsPathRooted(cdrPath)) return;
            string name = FileName(server, orderId);
            Write(Path.Combine(LocalDirectory, name), cdrPath);
            string shared = SharedDirectory(yearFolder);
            if (shared != null) Write(Path.Combine(shared, name), cdrPath);
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

        private static void Write(string indexPath, string cdrPath)
        {
            string temporary = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(indexPath));
                if (File.Exists(indexPath) &&
                    String.Equals(File.ReadAllText(indexPath, Encoding.UTF8).Trim(), cdrPath,
                        StringComparison.OrdinalIgnoreCase)) return;
                File.WriteAllText(temporary, cdrPath, Encoding.UTF8);
                if (File.Exists(indexPath)) File.Delete(indexPath);
                File.Move(temporary, indexPath);
            }
            catch (Exception error) { Log.Error("Order CDR link write failed", error); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }
}
