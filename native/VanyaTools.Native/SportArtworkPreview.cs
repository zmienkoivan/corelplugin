using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class SportArtworkPreview
    {
        private const int MaximumSide = 640;
        private const long MaximumEmbeddedBytes = 8 * 1024 * 1024;

        // Only captured CDRs have a sidecar; never write next to a user's source file.
        internal static string Sidecar(string artwork) => artwork + ".preview.png";

        internal static void CaptureSelection(object application, object document, string artwork)
        {
            string path = Sidecar(artwork);
            try
            {
                dynamic app = application, selection = app.ActiveSelectionRange;
                double width = Convert.ToDouble(selection.SizeWidth), height = Convert.ToDouble(selection.SizeHeight);
                double side = Math.Max(width, height);
                if (width <= 0 || height <= 0 || Double.IsNaN(side) || Double.IsInfinity(side))
                    throw new InvalidOperationException("Не удалось определить пропорции миниатюры.");
                object options = app.CreateStructExportOptions();
                Set(options, "SizeX", Math.Max(1, (int)Math.Round(MaximumSide * width / side)));
                Set(options, "SizeY", Math.Max(1, (int)Math.Round(MaximumSide * height / side)));
                Set(options, "ResolutionX", 96);
                Set(options, "ResolutionY", 96);
                Set(options, "ImageType", CorelConstants.CdrRgbColorImage);
                Set(options, "AntiAliasingType", CorelConstants.CdrNormalAntiAliasing);
                Set(options, "Transparent", true);
                Set(options, "Overwrite", true);
                object palette = app.CreateStructPaletteOptions();
                object filter = document.GetType().InvokeMember("ExportEx", BindingFlags.InvokeMethod,
                    null, document, new object[] { new BStrWrapper(path), CorelConstants.CdrPng,
                        CorelConstants.CdrSelection, options, palette });
                if (filter == null) throw new InvalidOperationException("Corel не открыл фильтр PNG.");
                filter.GetType().InvokeMember("Finish", BindingFlags.InvokeMethod, null, filter, null);
            }
            catch (Exception error)
            {
                // The editable CDR is already saved. A preview failure must not discard it.
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                Log.Error("Could not capture sport artwork thumbnail; using embedded CDR preview.", error);
            }
        }

        internal static BitmapSource Load(string path)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Файл рисунка не найден.", path);
            if (!String.Equals(Path.GetExtension(path), ".cdr", StringComparison.OrdinalIgnoreCase))
                return LoadImage(path);

            string sidecar = Sidecar(path);
            if (File.Exists(sidecar) && File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(path))
            {
                try { return LoadImage(sidecar); }
                catch (Exception error) { Log.Error("Sport artwork thumbnail sidecar is unavailable.", error); }
            }

            // Modern CDR files contain an embedded raster preview. Do not open a
            // drawing or call Corel COM in this background file reader.
            try
            {
                using (var file = OpenRead(path))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    var entries = archive.Entries.Where(entry => IsPreview(entry.FullName) &&
                        entry.Length > 0 && entry.Length <= MaximumEmbeddedBytes)
                        .OrderBy(entry => entry.FullName.IndexOf("thumbnail.png", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                        .ThenByDescending(entry => entry.Length).Take(8);
                    foreach (var entry in entries)
                    {
                        try
                        {
                            using (var source = entry.Open())
                            using (var bytes = new MemoryStream())
                            {
                                source.CopyTo(bytes);
                                bytes.Position = 0;
                                return Decode(bytes);
                            }
                        }
                        catch (Exception error) { Log.Error("Could not decode embedded sport artwork preview.", error); }
                    }
                }
            }
            catch (InvalidDataException) { } // Older CDRs may not have a ZIP container.
            throw new InvalidOperationException("В CDR нет доступной миниатюры. Возьмите рисунок выделением из Corel.");
        }

        private static bool IsPreview(string name)
        {
            string extension = Path.GetExtension(name);
            return (name.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0) &&
                new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static BitmapSource LoadImage(string path)
        {
            using (var source = OpenRead(path)) return Decode(source);
        }

        private static FileStream OpenRead(string path) => new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        private static BitmapSource Decode(Stream source)
        {
            // Read dimensions without decoding full-size artwork (up to 250 MP).
            var frame = BitmapFrame.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int width = frame.PixelWidth, height = frame.PixelHeight;
            source.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // IgnoreImageCache expects a URI cache key. These previews use a
            // StreamSource, so that flag makes WPF query its cache with null.
            // OnLoad already detaches the pixels from the source file/stream.
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (width >= height) image.DecodePixelWidth = Math.Min(MaximumSide, width);
            else image.DecodePixelHeight = Math.Min(MaximumSide, height);
            image.StreamSource = source;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
    }
}
