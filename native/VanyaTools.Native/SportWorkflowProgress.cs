using System;
using System.Reflection;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class SportWorkflowProgress
    {
        private const string Owner = "A6519696-E3C8-4904-A2AD-36CD6B7E933D";
        public int SchemaVersion { get; set; } = 1;
        public string DocumentId { get; set; }
        public string[] Inputs { get; set; }
        public bool[] Completed { get; set; }
        public string[] Details { get; set; }

        internal static SportWorkflowProgress Read(object document)
        {
            dynamic doc = document;
            dynamic properties = doc.Properties;
            if (!Convert.ToBoolean(properties.Exists(Owner, 1))) return null;
            string json = Convert.ToString(properties.GetType().InvokeMember("Item", BindingFlags.GetProperty,
                null, (object)properties, new object[] { Owner, 1 }));
            if (String.IsNullOrWhiteSpace(json)) return null;
            SportWorkflowProgress state;
            try { state = new JavaScriptSerializer { MaxJsonLength = 16777216 }.Deserialize<SportWorkflowProgress>(json); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            if (state == null || state.SchemaVersion != 1 || !Guid.TryParse(state.DocumentId, out _) ||
                state.Inputs == null || state.Inputs.Length != 6 || state.Completed == null || state.Completed.Length != 6 ||
                state.Details == null || state.Details.Length != 6)
                return null;
            return state;
        }

        internal string Serialize() => new JavaScriptSerializer { MaxJsonLength = 16777216 }.Serialize(this);

        internal static void Write(object document, string json)
        {
            dynamic doc = document;
            object properties = doc.Properties;
            properties.GetType().InvokeMember("Item", BindingFlags.SetProperty, null, properties,
                new object[] { Owner, 1, json });
        }

        internal static string SessionKey(object document)
        {
            dynamic doc = document;
            // These document properties are stable across repeated COM wrappers.
            // Index also distinguishes two open copies with identical metadata.
            return Convert.ToString(doc.CodeName) + "|" + Convert.ToString(doc.Name) + "|" + Convert.ToString(doc.Index);
        }
    }
}
