using System;
using System.Reflection;

namespace VanyaTools.Native
{
    internal static class SportProjectState
    {
        private const string Owner = "C0C17479-9777-48E3-835B-10A41EB9CC20";
        internal static string Read(object document)
        {
            object properties = ((dynamic)document).Properties;
            if (!Convert.ToBoolean(properties.GetType().InvokeMember("Exists", BindingFlags.InvokeMethod,
                null, properties, new object[] { Owner, 1 }))) return null;
            return Convert.ToString(properties.GetType().InvokeMember("Item", BindingFlags.GetProperty,
                null, properties, new object[] { Owner, 1 }));
        }
        internal static void Write(object document, string json)
        {
            object properties = ((dynamic)document).Properties;
            properties.GetType().InvokeMember("Item", BindingFlags.SetProperty, null, properties,
                new object[] { Owner, 1, json });
            ((dynamic)document).Dirty = true;
        }
    }
}
