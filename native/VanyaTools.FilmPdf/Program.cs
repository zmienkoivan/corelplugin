using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using VanyaTools.Printing;

namespace VanyaTools.FilmPdf
{
    // Runs separately from Corel so PDFsharp and its dependencies never enter
    // the host's AppDomain. Only Corel's newly generated temporary PDF is read.
    internal static class Program
    {
        private sealed class FilmSizeException : Exception
        { internal FilmSizeException(string message) : base(message) { } }

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                bool printJob = args.Length == 6;
                if (args.Length != 4 && !printJob) throw new ArgumentException("Expected input PDF, output PDF, width and height in mm, optionally ICC mode and profile.");
                string mode = printJob ? args[4] : "remove";
                if (mode != "remove" && mode != "assign") throw new ArgumentException("Unsupported ICC mode.");
                IccProfileFile selected = mode == "assign" ? IccProfileFile.Read(args[5]) : null;
                string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
                if (String.Equals(input, output, StringComparison.OrdinalIgnoreCase) || File.Exists(output))
                    throw new IOException("The output must be a new file distinct from the input.");
                double width = Double.Parse(args[2], CultureInfo.InvariantCulture) * 72 / 25.4;
                double height = Double.Parse(args[3], CultureInfo.InvariantCulture) * 72 / 25.4;
                using (PdfDocument document = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
                {
                    if (document.PageCount != 1) throw new InvalidDataException("Expected one film page.");
                    CheckSize(document.Pages[0], width, height);
                    if (selected != null) ProfileAttachment.Apply(document, selected);
                    else
                    {
                        var visited = new HashSet<PdfObject>();
                        foreach (PdfObject item in document.Internals.GetAllObjects()) Rewrite(item, visited);
                    }
                    document.Version = 17;
                    document.Options.CompressContentStreams = printJob;
                    document.Save(output);
                }
                // Read back the finished graph, not the discarded ICC stream bytes.
                using (PdfDocument document = PdfReader.Open(output, printJob ? PdfDocumentOpenMode.Modify : PdfDocumentOpenMode.Import))
                {
                    if (document.PageCount != 1) throw new InvalidDataException("Film page count changed.");
                    CheckSize(document.Pages[0], width, height);
                    if (selected != null) ProfileAttachment.Check(document, selected);
                    else
                    {
                        var visited = new HashSet<PdfObject>();
                        foreach (PdfObject item in document.Internals.GetAllObjects()) Check(item, visited);
                    }
                }
                return 0;
            }
            catch (FilmSizeException error) { Console.Error.WriteLine(error.Message); return 2; }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }

        private static void CheckSize(PdfPage page, double width, double height)
        {
            double unit = page.Elements.ContainsKey("/UserUnit") ? page.Elements.GetReal("/UserUnit") : 1;
            int rotation = ((page.Rotate % 360) + 360) % 360;
            bool swapped = rotation == 90 || rotation == 270;
            double actualWidth = (swapped ? page.MediaBox.Height : page.MediaBox.Width) * unit;
            double actualHeight = (swapped ? page.MediaBox.Width : page.MediaBox.Height) * unit;
            double cropWidth = page.HasCropBox ? (swapped ? page.CropBox.Height : page.CropBox.Width) * unit : actualWidth;
            double cropHeight = page.HasCropBox ? (swapped ? page.CropBox.Width : page.CropBox.Height) * unit : actualHeight;
            // Compare physical dimensions after PDF rotation and UserUnit. Accept
            // up to 0.2 mm of page-box rounding; never resize content to make it fit.
            double tolerance = .2 * 72 / 25.4;
            if (unit <= 0 || Double.IsNaN(unit) || Double.IsInfinity(unit) || rotation % 90 != 0 ||
                Double.IsNaN(actualWidth + actualHeight + cropWidth + cropHeight) ||
                Double.IsInfinity(actualWidth + actualHeight + cropWidth + cropHeight) ||
                Math.Min(Math.Min(actualWidth, actualHeight), Math.Min(cropWidth, cropHeight)) <= 0 ||
                Math.Abs(actualWidth - width) > tolerance || Math.Abs(actualHeight - height) > tolerance ||
                Math.Abs(cropWidth - width) > tolerance || Math.Abs(cropHeight - height) > tolerance)
                throw new FilmSizeException(String.Format(CultureInfo.InvariantCulture,
                    "Expected {0:0.###} x {1:0.###} mm; PDF {2:0.###} x {3:0.###} mm; crop {4:0.###} x {5:0.###} mm; rotation {6}; UserUnit {7:0.######}.",
                    width * 25.4 / 72, height * 25.4 / 72, actualWidth * 25.4 / 72, actualHeight * 25.4 / 72,
                    cropWidth * 25.4 / 72, cropHeight * 25.4 / 72, rotation, unit));
        }

        private static PdfItem Value(PdfItem item) => item is PdfReference reference ? reference.Value : item;

        private static bool Icc(PdfItem item) => Value(item) is PdfArray array && array.Elements.Count > 0 &&
            Value(array.Elements[0]) is PdfName name && name.Value == "/ICCBased";

        private static PdfName Device(PdfItem item)
        {
            var array = (PdfArray)Value(item);
            if (array.Elements.Count != 2 || !(Value(array.Elements[1]) is PdfDictionary profile))
                throw new InvalidDataException("Invalid ICC color space in Corel PDF.");
            // Change only the color-space declaration. The numeric channel values,
            // image samples, vector operators and page geometry remain untouched.
            switch (profile.Elements.GetInteger("/N"))
            {
                case 1: return new PdfName("/DeviceGray");
                case 3: return new PdfName("/DeviceRGB");
                case 4: return new PdfName("/DeviceCMYK");
                default: throw new InvalidDataException("Unsupported ICC channel count in Corel PDF.");
            }
        }

        private static void Rewrite(PdfItem item, HashSet<PdfObject> visited)
        {
            item = Value(item);
            if (!(item is PdfObject obj) || !visited.Add(obj)) return;
            if (item is PdfDictionary dictionary)
            {
                dictionary.Elements.Remove("/OutputIntents");
                dictionary.Elements.Remove("/DestOutputProfile");
                // Default device spaces must not silently reactivate color management.
                dictionary.Elements.Remove("/DefaultGray");
                dictionary.Elements.Remove("/DefaultRGB");
                dictionary.Elements.Remove("/DefaultCMYK");
                var keys = new List<string>(dictionary.Elements.Keys);
                foreach (string key in keys)
                {
                    PdfItem child = dictionary.Elements[key];
                    if (Icc(child)) dictionary.Elements[key] = Device(child);
                    else Rewrite(child, visited);
                }
            }
            else if (item is PdfArray array)
                for (int i = 0; i < array.Elements.Count; i++)
                {
                    PdfItem child = array.Elements[i];
                    if (Icc(child)) array.Elements[i] = Device(child);
                    else Rewrite(child, visited);
                }
        }

        private static void Check(PdfItem item, HashSet<PdfObject> visited)
        {
            if (Icc(item)) throw new InvalidDataException("ICC color space remains in the finished PDF.");
            item = Value(item);
            if (!(item is PdfObject obj) || !visited.Add(obj)) return;
            if (item is PdfDictionary dictionary)
            {
                if (dictionary.Elements.ContainsKey("/OutputIntents") || dictionary.Elements.ContainsKey("/DestOutputProfile"))
                    throw new InvalidDataException("ICC output profile remains in the finished PDF.");
                foreach (PdfItem child in dictionary.Elements.Values) Check(child, visited);
            }
            else if (item is PdfArray array)
                foreach (PdfItem child in array.Elements) Check(child, visited);
        }
    }
}
