using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VanyaTools.Native
{
    internal static class OrderPhotoDiskCache
    {
        private const long MaximumBytes = 1024L * 1024 * 1024;
        private static readonly object Gate = new object();
        private static int _writes;
        private static string DirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "order-photo-cache");

        private static string FilePath(string key, bool original)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    (original ? "original:" : "preview:") + key));
                return Path.Combine(DirectoryPath,
                    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".jpg");
            }
        }

        internal static byte[] Read(string key, bool original)
        {
            string path = FilePath(key, original);
            try
            {
                if (!File.Exists(path)) return null;
                var info = new FileInfo(path);
                long limit = original ? 20L * 1024 * 1024 : 4L * 1024 * 1024;
                if (info.Length <= 0 || info.Length > limit) { File.Delete(path); return null; }
                byte[] bytes = File.ReadAllBytes(path);
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
                return bytes;
            }
            catch (Exception error)
            {
                Log.Error("Order photo disk cache read failed", error);
                return null;
            }
        }

        internal static void Write(string key, bool original, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 20 * 1024 * 1024) return;
            string path = FilePath(key, original);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    File.WriteAllBytes(temporary, bytes);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temporary, path);
                    if (++_writes == 1 || _writes % 16 == 0) Prune();
                }
            }
            catch (Exception error) { Log.Error("Order photo disk cache write failed", error); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        internal static void Remove(string key)
        {
            lock (Gate)
            {
                foreach (bool original in new[] { false, true })
                    try { File.Delete(FilePath(key, original)); }
                    catch (Exception error) { Log.Error("Order photo disk cache removal failed", error); }
            }
        }

        private static void Prune()
        {
            var files = new DirectoryInfo(DirectoryPath).GetFiles("*.jpg")
                .OrderBy(file => file.LastAccessTimeUtc).ToArray();
            long total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total <= MaximumBytes) break;
                try { long length = file.Length; file.Delete(); total -= length; }
                catch (Exception error) { Log.Error("Order photo disk cache prune failed", error); }
            }
        }
    }
}
