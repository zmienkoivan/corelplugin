using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace VanyaTools.FilmPdf
{
    // This is a structural print-content check, not a screen-colour test. White
    // ink, named spot colours, patterns and transparent artwork are valid jobs.
    // In particular, an empty focus-mode PDF can have a valid page and streams
    // containing clipping/positioning commands without any painting commands.
    internal sealed class PdfPrintContentGuard
    {
        private const int MaxFormDepth = 128;
        private const int MaxOperators = 1000000;
        private readonly HashSet<PdfDictionary> _activeForms = new HashSet<PdfDictionary>();
        private readonly Dictionary<PdfDictionary, CSequence> _forms = new Dictionary<PdfDictionary, CSequence>();
        private int _operators;

        internal static void Check(PdfPage page)
        {
            var guard = new PdfPrintContentGuard();
            if (!guard.Paints(ContentReader.ReadContent(page), page.Resources, 0, 0))
                throw new InvalidDataException("PDF has no printable paths, text, images or shading. The file was not sent to the print queue.");
        }

        private bool Paints(CSequence content, PdfDictionary resources, int depth, int inheritedTextMode)
        {
            bool path = false, textObject = false;
            int textMode = inheritedTextMode;
            var savedTextModes = new Stack<int>();
            foreach (CObject item in content)
            {
                if (!(item is COperator operation)) continue;
                if (++_operators > MaxOperators)
                    throw new InvalidDataException("PDF print-content validation exceeded the operator limit.");
                switch (operation.Name)
                {
                    case "q": savedTextModes.Push(textMode); break;
                    case "Q": if (savedTextModes.Count > 0) textMode = savedTextModes.Pop(); break;
                    case "l": case "c": case "v": case "y": case "re":
                        path = true;
                        break;
                    case "S": case "s": case "f": case "F": case "f*":
                    case "B": case "B*": case "b": case "b*":
                        if (path) return true;
                        path = false;
                        break;
                    case "n": path = false; break;
                    case "BT": textObject = true; break;
                    case "ET": textObject = false; break;
                    case "Tr":
                        if (operation.Operands.Count == 1 && operation.Operands[0] is CInteger mode)
                            textMode = mode.Value;
                        break;
                    case "Tj": case "TJ": case "'": case "\"":
                        // Modes 3 and 7 only position/clip text. Numeric-only TJ
                        // arrays and empty strings do not draw any glyphs.
                        if (textObject && textMode != 3 && textMode != 7 && ContainsText(operation.Operands)) return true;
                        break;
                    case "Do":
                        if (operation.Operands.Count == 1 && operation.Operands[0] is CName name &&
                            PaintsXObject(Resource(resources, "/XObject", name.Name), resources, depth, textMode)) return true;
                        break;
                    case "sh":
                        if (operation.Operands.Count == 1 && operation.Operands[0] is CName shading &&
                            Resource(resources, "/Shading", shading.Name) != null) return true;
                        break;
                    case "BI":
                        // ContentReader parses/skips the inline image data as a
                        // single BI operator, so its binary data is not mistaken
                        // for vector or text operators.
                        return true;
                }
            }
            return false;
        }

        private bool PaintsXObject(PdfDictionary item, PdfDictionary inheritedResources, int depth, int textMode)
        {
            if (item == null) return false;
            string subtype = item.Elements.GetName("/Subtype");
            if (subtype == "/Image")
                return item.Elements.GetInteger("/Width") > 0 && item.Elements.GetInteger("/Height") > 0 &&
                    item.Stream != null && item.Stream.Value.Length > 0;
            if (subtype != "/Form" || item.Stream == null || !_activeForms.Add(item)) return false;
            try
            {
                if (depth >= MaxFormDepth)
                    throw new InvalidDataException("PDF print-content validation exceeded the Form nesting limit.");
                if (!_forms.TryGetValue(item, out CSequence content))
                {
                    content = ContentReader.ReadContent(item.Stream.UnfilteredValue);
                    _forms.Add(item, content);
                }
                var resources = Value(item.Elements["/Resources"]) as PdfDictionary ?? inheritedResources;
                return Paints(content, resources, depth + 1, textMode);
            }
            finally { _activeForms.Remove(item); }
        }

        private static bool ContainsText(CSequence sequence)
        {
            foreach (CObject item in sequence)
            {
                if (item is CString text && text.Value.Length > 0) return true;
                if (item is CArray array && ContainsText(array)) return true;
            }
            return false;
        }

        private static PdfDictionary Resource(PdfDictionary resources, string kind, string name)
        {
            var group = resources == null ? null : Value(resources.Elements[kind]) as PdfDictionary;
            return group == null ? null : Value(group.Elements[name]) as PdfDictionary;
        }
        private static PdfItem Value(PdfItem value) => value is PdfReference reference ? reference.Value : value;
    }
}
