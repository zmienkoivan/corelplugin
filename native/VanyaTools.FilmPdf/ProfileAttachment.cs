using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using VanyaTools.Printing;

namespace VanyaTools.FilmPdf
{
    internal static class ProfileAttachment
    {
        private static PdfItem Value(PdfItem item) => item is PdfReference reference ? reference.Value : item;

        internal static void Apply(PdfDocument document, IccProfileFile profile)
        {
            var stream = new PdfDictionary(document);
            stream.Elements.SetInteger("/N", profile.Channels);
            stream.Elements.SetName("/Alternate", profile.DeviceSpace);
            stream.CreateStream(profile.Data); stream.Stream.Zip();
            document.Internals.AddObject(stream);
            var space = new PdfArray(document);
            space.Elements.Add(new PdfName("/ICCBased")); space.Elements.Add(stream.Reference);
            var visited = new HashSet<PdfObject>();
            var models = new Dictionary<PdfDictionary, bool> { [stream] = true };
            foreach (PdfObject item in document.Internals.GetAllObjects()) Rewrite(item, profile, space, visited, models);
            foreach (PdfPage page in document.Pages) AddDefault(page.Resources, profile, space);
        }

        private static PdfItem ColorSpace(PdfItem item, IccProfileFile profile, PdfArray chosen, Dictionary<PdfDictionary, bool> models)
        {
            PdfItem value = Value(item);
            if (value is PdfName direct) return direct.Value == profile.DeviceSpace ? chosen : item;
            if (!(value is PdfArray array) || array.Elements.Count == 0 || !(Value(array.Elements[0]) is PdfName kind)) return item;
            if (kind.Value == "/ICCBased")
            {
                if (array.Elements.Count != 2 || !(Value(array.Elements[1]) is PdfDictionary embedded))
                    throw new InvalidDataException("Invalid ICC colour space in PDF.");
                if (embedded.Elements.GetInteger("/N") != profile.Channels) return item;
                if (!models.TryGetValue(embedded, out bool matches))
                {
                    byte[] bytes = embedded.Stream?.UnfilteredValue;
                    string signature = profile.Channels == 3 ? "RGB " : "CMYK";
                    matches = bytes != null && bytes.Length >= 20 && Encoding.ASCII.GetString(bytes, 16, 4) == signature;
                    models.Add(embedded, matches);
                }
                return matches ? chosen : item;
            }
            if (kind.Value == "/CalRGB" && profile.Channels == 3) return chosen;
            // ICC tags may also be used as an indexed base or a spot colour's
            // alternate. Spot names and tint transforms themselves are retained.
            int index = kind.Value == "/Indexed" || kind.Value == "/Pattern" ? 1 :
                kind.Value == "/Separation" || kind.Value == "/DeviceN" ? 2 : -1;
            if (index > 0 && index < array.Elements.Count)
                array.Elements[index] = ColorSpace(array.Elements[index], profile, chosen, models);
            return item;
        }

        private static void AddDefault(PdfDictionary resources, IccProfileFile profile, PdfArray chosen)
        {
            var spaces = Value(resources.Elements["/ColorSpace"]) as PdfDictionary;
            if (spaces == null)
            {
                spaces = new PdfDictionary(resources.Owner);
                resources.Elements["/ColorSpace"] = spaces;
            }
            // Default spaces apply to device colour operators in vector
            // content and inline images. Channel values are never rewritten.
            spaces.Elements[profile.DefaultSpace] = chosen;
        }

        private static void Rewrite(PdfItem item, IccProfileFile profile, PdfArray chosen, HashSet<PdfObject> visited,
            Dictionary<PdfDictionary, bool> models)
        {
            item = Value(item);
            if (!(item is PdfObject obj) || !visited.Add(obj)) return;
            if (item is PdfDictionary dictionary)
            {
                PdfItem color = dictionary.Elements["/ColorSpace"];
                if (Value(color) is PdfDictionary spaces)
                {
                    foreach (string key in new List<string>(spaces.Elements.Keys))
                        spaces.Elements[key] = ColorSpace(spaces.Elements[key], profile, chosen, models);
                }
                else if (color != null) dictionary.Elements["/ColorSpace"] = ColorSpace(color, profile, chosen, models);
                // Leave transparency-group /CS and grey opacity masks in
                // their original blending spaces; they are not input inks.
                if (Value(dictionary.Elements["/Resources"]) is PdfDictionary resources) AddDefault(resources, profile, chosen);
                foreach (PdfItem child in new List<PdfItem>(dictionary.Elements.Values)) Rewrite(child, profile, chosen, visited, models);
            }
            else if (item is PdfArray array)
                foreach (PdfItem child in array.Elements) Rewrite(child, profile, chosen, visited, models);
        }

        internal static void Check(PdfDocument document, IccProfileFile profile)
        {
            foreach (PdfPage page in document.Pages)
            {
                var spaces = Value(page.Resources.Elements["/ColorSpace"]) as PdfDictionary;
                var array = spaces == null ? null : Value(spaces.Elements[profile.DefaultSpace]) as PdfArray;
                if (array == null || array.Elements.Count != 2 || !(Value(array.Elements[0]) is PdfName kind) ||
                    kind.Value != "/ICCBased" || !(Value(array.Elements[1]) is PdfDictionary embedded) ||
                    embedded.Elements.GetInteger("/N") != profile.Channels || embedded.Stream == null ||
                    !embedded.Stream.UnfilteredValue.SequenceEqual(profile.Data))
                    throw new InvalidDataException("The selected input ICC profile is missing or changed in the finished PDF.");
            }
        }
    }
}
