using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal sealed partial class OrderCardTab
    {
        private static readonly object CardVersionsGate = new object();
        private static readonly Dictionary<long, long> CardVersions = new Dictionary<long, long>();
        private long _documentCardVersion;

        // Track writes between the main docker and the separate Orders docker
        // without reading the complete CDR property on every timer tick.
        private static long DocumentCardVersion(object document, bool advance = false)
        {
            if (document == null) return 0;
            IntPtr identity = Marshal.GetIUnknownForObject(document);
            try
            {
                lock (CardVersionsGate)
                {
                    long key = identity.ToInt64();
                    CardVersions.TryGetValue(key, out long version);
                    if (advance) CardVersions[key] = ++version;
                    return version;
                }
            }
            finally { Marshal.Release(identity); }
        }
    }
}
