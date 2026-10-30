using System;
using System.IO;
using System.Net;
using System.Text;

namespace VanyaTools.Updater
{
    // Fixed read-only endpoint. Worker jobs cannot request arbitrary URLs or
    // launch an installation; the indicator receives metadata only.
    internal static class UpdateReleaseMetadata
    {
        internal static string Read()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/zmienkoivan/corelplugin/releases/latest");
            request.Method = "GET"; request.Timeout = 4500; request.ReadWriteTimeout = 4500;
            request.UserAgent = "VanyaTools-UpdateIndicator/1.1.97";
            request.Accept = "application/vnd.github+json";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return ReadLimited(reader);
        }

        internal static string ReadLimited(TextReader reader)
        {
            const int limit = 1024 * 1024;
            var text = new StringBuilder(); var block = new char[8192];
            int count;
            while ((count = reader.Read(block, 0, block.Length)) > 0)
            {
                if (text.Length > limit - count) throw new InvalidDataException("Ответ GitHub слишком большой.");
                text.Append(block, 0, count);
            }
            if (String.IsNullOrWhiteSpace(text.ToString())) throw new InvalidDataException("GitHub вернул пустой ответ.");
            return text.ToString();
        }
    }
}
