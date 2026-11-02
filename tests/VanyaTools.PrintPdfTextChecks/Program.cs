using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PdfSharp.Pdf.IO;
using VanyaTools.FilmPdf;
using VanyaTools.Native;

internal static class Checks
{
    private static int _checks;
    private const string Paint = "0 0 0 1 k 10 10 20 20 re f\n";
    private const string Fonts = "/Font << /F1 5 0 R >>";
    private const string Live = "BT /F1 12 Tf 10 10 Td (Hello) Tj ET\n";
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static int Helper(params string[] args)
    {
        var stderr = Console.Error;
        try
        {
            Console.SetError(new StringWriter());
            return (int)typeof(VanyaTools.FilmPdf.Program).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { args });
        }
        finally { Console.SetError(stderr); }
    }
    private static string Stream(string content, string dictionary = "")
        => "<< " + dictionary + " /Length " + Encoding.ASCII.GetByteCount(content) + " >>\nstream\n" + content + "\nendstream";
    private static string Form(string content, string resources = Fonts)
        => Stream(content, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << " + resources + " >>");
    private static string Pdf(string root, string name, string content = Paint, string resources = Fonts,
        string annotation = "", params string[] extras)
    {
        var objects = new List<string> {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 283.464566929 141.732283465] /Resources << " + resources +
                " >> /Contents 4 0 R " + annotation + " >>", Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>" };
        objects.AddRange(extras);
        string path = Path.Combine(root, name + ".pdf");
        using (var output = File.Create(path))
        {
            Action<string> write = text => { byte[] bytes = Encoding.ASCII.GetBytes(text); output.Write(bytes, 0, bytes.Length); };
            write("%PDF-1.7\n"); var offsets = new List<long>();
            for (int i = 0; i < objects.Count; i++) { offsets.Add(output.Position); write((i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n"); }
            long xref = output.Position; write("xref\n0 " + (objects.Count + 1) + "\n0000000000 65535 f \n");
            foreach (long offset in offsets) write(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
            write("trailer\n<< /Size " + (objects.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        }
        return path;
    }
    private static void Accept(string path, string context)
    {
        byte[] original = File.ReadAllBytes(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import)) PdfTextCurvesGuard.Check(document);
        Check(Helper("--require-curves", path) == 0, "Standalone guard rejected " + context);
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Guard rewrote " + context);
    }
    private static void Reject(string path, string context)
    {
        byte[] original = File.ReadAllBytes(path); bool rejected = false;
        try { using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import)) PdfTextCurvesGuard.Check(document); }
        catch (PdfTextNotOutlinedException) { rejected = true; }
        Check(rejected && Helper("--require-curves", path) == 3, "Guard accepted " + context);
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Rejected input was changed: " + context);
    }
    private static string Icc(string root)
    {
        var data = new byte[152]; data[3] = 152; data[8] = 4;
        foreach (var item in new[] { Tuple.Create(12, "mntr"), Tuple.Create(16, "RGB "), Tuple.Create(20, "XYZ "), Tuple.Create(36, "acsp"), Tuple.Create(132, "desc"), Tuple.Create(144, "text") })
            Buffer.BlockCopy(Encoding.ASCII.GetBytes(item.Item2), 0, data, item.Item1, 4);
        data[131] = 1; data[139] = 144; data[143] = 8;
        string path = Path.Combine(root, "synthetic.icc"); File.WriteAllBytes(path, data); return path;
    }
    private static void MainSettings()
    {
        var settings = new GoodSettings(); PrintPdfText.RequireCurves(settings);
        Check(settings.TextAsCurves, "Publisher did not enable curves from disabled settings");
        bool failed = false;
        try { PrintPdfText.RequireCurves(new IgnoredSettings()); } catch (InvalidOperationException) { failed = true; }
        Check(failed, "Publisher accepted ignored TextAsCurves setter");
        failed = false;
        try { PrintPdfText.RequireCurves(new UnreadableSettings()); } catch (InvalidOperationException) { failed = true; }
        Check(failed, "Publisher accepted unreadable TextAsCurves value");
    }
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vanya-PdfTextChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            MainSettings();
            string clean = Pdf(root, "curves"); Accept(clean, "outlined paths with unused font resource");
            Accept(Pdf(root, "empty-text", Paint + "BT /F1 12 Tf () Tj [12 -7] TJ ET\n"), "empty text operators");
            string live = Pdf(root, "live", Live); Reject(live, "ordinary live text");
            Reject(Pdf(root, "array", "BT /F1 12 Tf [(H) -20 (i)] TJ ET\n"), "text array");
            Reject(Pdf(root, "single-quote", "BT /F1 12 Tf (Hello) ' ET\n"), "single-quote text operator");
            Reject(Pdf(root, "double-quote", "BT /F1 12 Tf 1 2 (Hello) \" ET\n"), "double-quote text operator");
            Reject(Pdf(root, "hidden", Paint + "BT /F1 12 Tf 3 Tr (hidden) Tj ET\n"), "invisible text behind painted path");
            Reject(Pdf(root, "text-clip", Paint + "BT /F1 12 Tf 7 Tr (clip) Tj ET\n"), "text clipping");
            Reject(Pdf(root, "form", "/Fm Do\n", "/XObject << /Fm 6 0 R >>", "", Form(Live)), "nested Form");
            Reject(Pdf(root, "deep-form", "/Fm Do\n", "/XObject << /Fm 6 0 R >>", "",
                Form("/Child Do\n", "/XObject << /Child 7 0 R >>"), Form(Live)), "nested Form resources");
            Reject(Pdf(root, "pattern", Paint, "/Pattern << /Pat 6 0 R >>", "",
                Stream(Live, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100 /Resources << " + Fonts + " >>")), "tiling pattern");
            Reject(Pdf(root, "soft-mask", Paint, "/ExtGState << /GS 6 0 R >>", "",
                "<< /Type /ExtGState /SMask << /S /Luminosity /G 7 0 R >> >>", Form(Live)), "soft-mask Form");
            Reject(Pdf(root, "annotation", Paint, Fonts, "/Annots [6 0 R]",
                "<< /Type /Annot /Subtype /Stamp /Rect [0 0 50 50] /P 3 0 R /AP << /N 7 0 R >> >>", Form(Live)), "annotation appearance");
            Accept(Pdf(root, "outlined-form", "/Fm Do\n", "/XObject << /Fm 6 0 R >>", "", Form(Paint)), "outlined Form");
            Accept(Pdf(root, "cycle", Paint, "/XObject << /Fm 6 0 R >>", "", Form(Paint, "/XObject << /Self 6 0 R >>")), "cyclic resource graph");
            Accept(Pdf(root, "image", "/Im Do\n", "/XObject << /Im 6 0 R >>", "",
                Stream("TjX", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8")), "RGB image with text-like sample bytes");
            string profile = Icc(root);
            foreach (string mode in new[] { "preserve", "remove", "assign", "film" })
            {
                string output = Path.Combine(root, mode + "-ready.pdf");
                string[] args = mode == "film" ? new[] { clean, output, "100", "50" } : new[] { clean, output, "100", "50", mode, profile };
                Check(Helper(args) == 0 && File.Exists(output), "Outlined PDF rejected by " + mode);
                Accept(output, mode + " output");
                if (mode == "preserve") Check(File.ReadAllBytes(clean).SequenceEqual(File.ReadAllBytes(output)), "ICC preserve changed PDF bytes");
                string blocked = Path.Combine(root, mode + "-blocked.pdf"); args[0] = live; args[1] = blocked;
                Check(Helper(args) == 3 && !File.Exists(blocked), "Live text delivered by " + mode);
            }
            Console.WriteLine("Print PDF text checks passed: " + _checks + ". Real synthetic PDFs, native setting doubles; no Corel or customer files."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
public sealed class GoodSettings { public bool TextAsCurves { get; set; } }
public sealed class IgnoredSettings { public bool TextAsCurves { get => false; set { } } }
public sealed class UnreadableSettings { public bool TextAsCurves { get => throw new InvalidOperationException("Synthetic read error"); set { } } }
