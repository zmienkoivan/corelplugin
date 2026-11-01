using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class WorkProjectInfo
    {
        public string Id { get; set; }
        public string[] Folders { get; set; } = new string[0];
    }
    internal static class WorkTimeProjectMetadata
    {
        private const string Owner = "9BEF77F0-5718-4670-855F-64DAC1BFF133";
        internal static WorkProjectInfo Read(object document, string path)
        {
            try
            {
                dynamic properties = ((dynamic)document).Properties;
                if (Convert.ToBoolean(properties.Exists(Owner, 1)))
                {
                    string text = Convert.ToString(((object)properties).GetType().InvokeMember("Item", BindingFlags.GetProperty, null, (object)properties, new object[] { Owner, 1 }));
                    if (text != null && text.Length <= 16000)
                    {
                        var value = new JavaScriptSerializer().Deserialize<WorkProjectInfo>(text);
                        if (value != null && Guid.TryParse(value.Id, out _)) return value;
                    }
                }
            }
            catch (Exception error) { Log.Error("Could not read CDR work-history ID.", error); }
            if (String.IsNullOrWhiteSpace(path)) return new WorkProjectInfo { Id = Guid.NewGuid().ToString("N") };
            using (var sha = SHA256.Create()) return new WorkProjectInfo { Id = new Guid(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())).Take(16).ToArray()).ToString("N") };
        }
        internal static void Write(object document, WorkProjectInfo project)
        {
            object properties = ((dynamic)document).Properties;
            properties.GetType().InvokeMember("Item", BindingFlags.SetProperty, null, properties,
                new object[] { Owner, 1, new JavaScriptSerializer().Serialize(project) });
        }
    }
}
