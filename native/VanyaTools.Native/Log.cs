using System;
using System.IO;

namespace VanyaTools.Native
{
    internal static class Log
    {
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "VanyaTools.Native.log");

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}{ex}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
