using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace VanyaTools.FilmPdf
{
    internal sealed class PdfTextNotOutlinedException : Exception
    { internal PdfTextNotOutlinedException() : base("PDF contains font-dependent text. Export all text as curves before printing.") { } }

    // Validate actual text-showing operators, not /Font resources: an unused
    // font dictionary does not mean the outlined artwork still needs a font.
    internal sealed class PdfTextCurvesGuard
    {
        private readonly HashSet<PdfObject> _visited = new HashSet<PdfObject>();
        private int _operators;
        internal static void Check(PdfDocument document)
        {
            var guard = new PdfTextCurvesGuard();
            foreach (PdfPage page in document.Pages)
            {
                guard.Scan(ContentReader.ReadContent(page));
                guard.Walk(page.Resources, 0, false);
                guard.Walk(page.Elements["/Annots"], 0, false);
            }
        }

        private void Walk(PdfItem item, int depth, bool appearance)
        {
            item = item is PdfReference reference ? reference.Value : item;
            if (!(item is PdfObject obj) || !_visited.Add(obj)) return;
            if (depth > 128 || _visited.Count > 100000)
                throw new InvalidDataException("PDF text validation exceeded the resource nesting limit.");
            if (obj is PdfDictionary dictionary)
            {
                string subtype = dictionary.Elements.GetName("/Subtype");
                if (subtype == "/PS") throw new InvalidDataException("Cannot verify fonts in a PostScript XObject.");
                bool content = subtype == "/Form" || dictionary.Elements.GetInteger("/PatternType") == 1 || appearance;
                if (content && dictionary.Stream != null) Scan(ContentReader.ReadContent(dictionary.Stream.UnfilteredValue));
                foreach (string key in dictionary.Elements.Keys)
                {
                    // Do not walk page/catalog back-links from annotations.
                    if (key == "/P" || key == "/Parent") continue;
                    Walk(dictionary.Elements[key], depth + 1, key == "/AP" || (appearance && dictionary.Stream == null));
                }
            }
            else if (obj is PdfArray array)
                foreach (PdfItem child in array.Elements) Walk(child, depth + 1, appearance);
        }

        private void Scan(CSequence sequence)
        {
            foreach (CObject item in sequence)
            {
                if (!(item is COperator operation)) continue;
                if (++_operators > 4000000) throw new InvalidDataException("PDF text validation exceeded the operator limit.");
                switch (operation.Name)
                {
                    case "Tj": case "TJ": case "'": case "\"":
                        // Invisible text and text used as clipping must also be
                        // curves. Their glyphs still depend on a font.
                        if (ContainsText(operation.Operands)) throw new PdfTextNotOutlinedException();
                        break;
                }
            }
        }
        private static bool ContainsText(CSequence values)
        {
            foreach (CObject value in values)
            {
                if (value is CString text && text.Value.Length > 0) return true;
                if (value is CArray array && ContainsText(array)) return true;
            }
            return false;
        }
    }
}
