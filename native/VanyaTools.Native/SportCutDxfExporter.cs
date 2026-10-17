using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace VanyaTools.Native
{
    internal static class SportCutDxfExporter
    {
        internal sealed class Result
        {
            public string Path, Warning;
            public double WidthMm, HeightMm;
            public int CurveCount;
            public long Bytes;
        }

        public static Result ExportCurrentPage(string folder)
        {
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new InvalidOperationException("Выберите доступную папку для листов резки.");
            folder = System.IO.Path.GetFullPath(folder);
            dynamic document = CorelApp.Get().ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте CDR со страницей раскладки резки.");
            dynamic page = document.ActivePage;
            var result = new Result { CurveCount = SportLayoutService.ValidateCuttingPage((object)page) };
            int oldUnit = Convert.ToInt32(document.Unit);
            bool oldDirty = Convert.ToBoolean(document.Dirty);
            string temporary = null, staging = null;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                result.WidthMm = Convert.ToDouble(page.SizeWidth);
                result.HeightMm = Convert.ToDouble(page.SizeHeight);
                if (result.WidthMm <= 0 || result.HeightMm <= 0 ||
                    Double.IsNaN(result.WidthMm + result.HeightMm) || Double.IsInfinity(result.WidthMm + result.HeightMm))
                    throw new InvalidOperationException("У страницы нет корректного размера.");

                string documentName = System.IO.Path.GetFileNameWithoutExtension(Convert.ToString(document.FileName));
                string prefix = SafeName(documentName) + " - резка - лист " +
                    Convert.ToInt32(page.Index).ToString("000", CultureInfo.InvariantCulture) + " - " +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string target = AvailablePath(folder, prefix);
                string job = Guid.NewGuid().ToString("N");
                temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vanya-cut-" + job + ".dxf");
                staging = System.IO.Path.Combine(folder, ".Vanya-cut-" + job + ".partial");

                // Native vector export: current page, independent of selection.
                // Values verified against Corel 2026 FilterDXF.tlb. No dialog,
                // rasterisation, curve simplification or fit-to-page scaling.
                object filter = document.ExportEx(temporary, CorelConstants.CdrDxf,
                    CorelConstants.CdrCurrentPage);
                if (filter == null) throw new InvalidOperationException("CorelDRAW не открыл фильтр экспорта DXF.");
                Set(filter, "Version", 1); // dxfVersion2000
                Set(filter, "Units", 3); // dxfMillimeters
                Set(filter, "TextAsCurves", true);
                Set(filter, "FillUnmapped", false);
                Log.Info("Sport DXF export: page=" + page.Index + ", " + result.WidthMm + "x" +
                    result.HeightMm + " mm, curves=" + result.CurveCount + ", AutoCAD 2000, millimeters.");
                try { filter.GetType().InvokeMember("Finish", BindingFlags.InvokeMethod, null, filter, null); }
                catch (Exception error)
                {
                    throw new InvalidOperationException("CorelDRAW не завершил экспорт DXF: " +
                        error.GetBaseException().Message, error);
                }
                ValidateFile(temporary);
                File.Copy(temporary, staging);
                File.Move(staging, target);
                result.Path = target;
                result.Bytes = new FileInfo(target).Length;
                Log.Info("Sport DXF export complete: " + System.IO.Path.GetFileName(target) + ", bytes=" + result.Bytes);
                return result;
            }
            finally
            {
                try { document.Unit = oldUnit; document.Dirty = oldDirty; }
                catch (Exception error)
                {
                    result.Warning = "DXF сохранён, но прежние единицы документа Corel не удалось восстановить.";
                    Log.Error("Could not restore document state after DXF export.", error);
                }
                DeleteTemporary(temporary);
                DeleteTemporary(staging);
            }
        }

        private static void Set(object filter, string name, object value)
        {
            try { filter.GetType().InvokeMember(name, BindingFlags.SetProperty, null, filter, new[] { value }); }
            catch (Exception error)
            {
                Log.Error("Could not set DXF setting " + name + ".", error);
                throw new InvalidOperationException("Не удалось установить параметр DXF «" + name + "»: " +
                    error.GetBaseException().Message, error);
            }
        }

        private static void ValidateFile(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length < 64) throw new InvalidDataException("CorelDRAW создал пустой или незавершённый DXF.");
                byte[] header = new byte[(int)Math.Min(2048, stream.Length)];
                ReadFully(stream, header);
                string start = Encoding.ASCII.GetString(header);
                if (!start.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal) &&
                    !start.Contains("SECTION"))
                    throw new InvalidDataException("CorelDRAW не создал корректный DXF.");
                byte[] tail = new byte[(int)Math.Min(256, stream.Length)];
                stream.Seek(-tail.Length, SeekOrigin.End);
                ReadFully(stream, tail);
                if (!Encoding.ASCII.GetString(tail).Contains("EOF"))
                    throw new InvalidDataException("CorelDRAW не завершил файл DXF.");
            }
        }

        private static void ReadFully(Stream stream, byte[] buffer)
        {
            int position = 0;
            while (position < buffer.Length)
            {
                int read = stream.Read(buffer, position, buffer.Length - position);
                if (read == 0) throw new InvalidDataException("DXF не удалось прочитать целиком.");
                position += read;
            }
        }

        private static void DeleteTemporary(string path)
        {
            if (path == null) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception error) { Log.Error("Could not remove temporary cutting DXF.", error); }
        }

        private static string SafeName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) name = "Спортформа";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var result = new StringBuilder();
            foreach (char character in name)
            {
                result.Append(Array.IndexOf(invalid, character) < 0 ? character : '_');
                if (result.Length >= 100) break;
            }
            string safe = result.ToString().Trim().TrimEnd('.');
            return safe.Length == 0 ? "Спортформа" : safe;
        }

        private static string AvailablePath(string folder, string prefix)
        {
            string path = System.IO.Path.Combine(folder, prefix + ".dxf");
            for (int copy = 2; File.Exists(path); copy++)
                path = System.IO.Path.Combine(folder, prefix + " - " + copy + ".dxf");
            return path;
        }
    }
}
