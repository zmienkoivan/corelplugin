using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal static class FilmShapeSearch
    {
        // Resolve StaticID from live objects, including selected children and art
        // outside the active page. No name or page-bounds filter is involved;
        // this traversal does not change the document.
        internal static Dictionary<int, object> Find(object document, IEnumerable<int> ids)
        {
            var wanted = new HashSet<int>(ids);
            var found = new Dictionary<int, object>(); var seen = new HashSet<int>();
            if (wanted.Count == 0) return found;
            Scan(() => (object)CorelApp.Get().ActiveSelectionRange, wanted, found, seen);
            if (found.Count == wanted.Count) return found;
            dynamic doc = document;
            foreach (object page in doc.Pages)
            {
                ScanPage(page, wanted, found, seen);
                if (found.Count == wanted.Count) return found;
            }
            try { ScanPage((object)doc.MasterPage, wanted, found, seen); }
            catch (Exception error) { Log.Error("Could not inspect film artwork on master layers.", error); }
            return found;
        }

        private static void ScanPage(object value, HashSet<int> wanted, Dictionary<int, object> found, HashSet<int> seen)
        {
            dynamic page = value;
            Scan(() => (object)page.Shapes, wanted, found, seen);
            if (found.Count == wanted.Count) return;
            // Include the desktop explicitly: artwork is not limited to the page rectangle.
            Scan(() => (object)page.DesktopLayer.Shapes, wanted, found, seen);
            if (found.Count == wanted.Count) return;
            try
            {
                foreach (dynamic layer in page.Layers)
                {
                    Scan(() => (object)layer.Shapes, wanted, found, seen);
                    if (found.Count == wanted.Count) return;
                }
            }
            catch (Exception error) { Log.Error("Could not enumerate film artwork layers.", error); }
        }

        private static void Scan(Func<object> collection, HashSet<int> wanted, Dictionary<int, object> found, HashSet<int> seen)
        {
            try
            {
                foreach (object value in (dynamic)collection())
                {
                    if (found.Count == wanted.Count) return;
                    try
                    {
                        dynamic shape = value; int id = Convert.ToInt32(shape.StaticID);
                        if (!seen.Add(id)) continue;
                        if (wanted.Contains(id)) found[id] = value;
                        if (found.Count == wanted.Count) return;
                        if (Convert.ToInt32(shape.Type) == CorelConstants.CdrGroupShape)
                            Scan(() => (object)shape.Shapes, wanted, found, seen);
                        if (found.Count == wanted.Count) return;
                        object clip = null;
                        try { clip = (object)shape.PowerClip; } catch (COMException) { }
                        if (clip != null) Scan(() => (object)((dynamic)clip).Shapes, wanted, found, seen);
                    }
                    catch (Exception error) { Log.Error("Could not inspect an object during film shape lookup.", error); }
                }
            }
            catch (Exception error) { Log.Error("Could not enumerate shapes during film shape lookup.", error); }
        }
    }
}
