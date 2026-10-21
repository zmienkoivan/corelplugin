using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class SportCutDxfExporter
    {
        internal sealed class Result
        {
            public string Path, Warning;
            public double WidthMm, HeightMm;
            public int CurveCount, PartCount;
            public long Bytes;
        }

        private sealed class CurveCopy
        {
            internal object Curve;
            internal object Color;
            internal long PartId;
            internal string Name, PartName;
            internal double OutlineWidth, Left, Bottom, Width, Height;
        }

        public static Result ExportCurrentPage(string folder, Color? singleColor = null)
        {
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                throw new InvalidOperationException("Выберите доступную папку для листов резки.");
            folder = System.IO.Path.GetFullPath(folder);
            dynamic application = CorelApp.Get(), document = application.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте CDR со страницей раскладки резки.");
            dynamic page = document.ActivePage;
            IList<object> cuttingCurves = SportLayoutService.CuttingPageCurves((object)page);
            var result = new Result { CurveCount = cuttingCurves.Count };
            int oldUnit = Convert.ToInt32(document.Unit);
            bool oldDirty = Convert.ToBoolean(document.Dirty);
            string temporary = null, groupedTemporary = null, staging = null;
            dynamic colorDocument = null;
            List<object> selection = null;
            bool? oldOptimization = null;
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
                groupedTemporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vanya-cut-blocks-" + job + ".dxf");
                staging = System.IO.Path.Combine(folder, ".Vanya-cut-" + job + ".partial");

                object exportDocument = (object)document;
                var partLayers = new List<string>();
                {
                    // Snapshot vector paths in millimetres. Only the temporary
                    // document is recoloured; source objects and colours stay intact.
                    double left = Convert.ToDouble(page.LeftX), bottom = Convert.ToDouble(page.BottomY);
                    var copies = new List<CurveCopy>();
                    foreach (object original in cuttingCurves)
                    {
                        if (!ExportVisible(original)) continue;
                        dynamic shape = original;
                        dynamic part = SportCutPiece.Root(original);
                        copies.Add(new CurveCopy { Curve = (object)shape.DisplayCurve.GetCopy(),
                            Color = (object)shape.Outline.Color.GetCopy(),
                            PartId = Convert.ToInt64(part.StaticID), PartName = Convert.ToString(part.Name) ?? "",
                            Name = Convert.ToString(shape.Name) ?? "",
                            OutlineWidth = Math.Max(.001, Convert.ToDouble(shape.Outline.Width)),
                            Left = Convert.ToDouble(shape.LeftX), Bottom = Convert.ToDouble(shape.BottomY),
                            Width = Convert.ToDouble(shape.SizeWidth), Height = Convert.ToDouble(shape.SizeHeight) });
                    }
                    if (copies.Count == 0) throw new InvalidOperationException("На листе нет видимых контуров для экспорта.");
                    selection = new List<object>();
                    foreach (dynamic shape in application.ActiveSelectionRange) selection.Add((object)shape);
                    oldOptimization = Convert.ToBoolean(application.Optimization);
                    application.Optimization = true;
                    colorDocument = application.CreateDocument();
                    colorDocument.Unit = CorelConstants.CdrMillimeter;
                    colorDocument.AssignColorContext(document.ColorContext);
                    dynamic colorPage = colorDocument.ActivePage;
                    colorPage.SetSize(result.WidthMm, result.HeightMm);
                    double dx = Convert.ToDouble(colorPage.LeftX) - left, dy = Convert.ToDouble(colorPage.BottomY) - bottom;
                    var layers = new Dictionary<long, object>();
                    var groups = new Dictionary<long, object>();
                    var names = new Dictionary<long, string>();
                    foreach (CurveCopy copy in copies)
                    {
                        if (!layers.TryGetValue(copy.PartId, out object partLayer))
                        {
                            string layerName = "VT_PART_" + (layers.Count + 1).ToString("00000", CultureInfo.InvariantCulture);
                            partLayer = (object)colorPage.CreateLayer(layerName);
                            partLayers.Add(layerName);
                            layers.Add(copy.PartId, partLayer);
                            groups.Add(copy.PartId, (object)application.CreateShapeRange());
                            names.Add(copy.PartId, copy.PartName);
                        }
                        dynamic colorLayer = partLayer;
                        dynamic shape = colorLayer.CreateCurve(copy.Curve);
                        if (shape == null || Math.Abs(Convert.ToDouble(shape.SizeWidth) - copy.Width) > .01 ||
                            Math.Abs(Convert.ToDouble(shape.SizeHeight) - copy.Height) > .01)
                            throw new InvalidOperationException("Не удалось сохранить размер контура 1:1 во временном документе DXF.");
                        shape.Move(copy.Left + dx - Convert.ToDouble(shape.LeftX),
                            copy.Bottom + dy - Convert.ToDouble(shape.BottomY));
                        shape.Fill.ApplyNoFill(); shape.Outline.Width = copy.OutlineWidth;
                        shape.Name = copy.Name;
                        // The label has its own laser power/color. A unified
                        // cutting color applies only to the body and notches.
                        if (singleColor.HasValue && !copy.Name.StartsWith(SportCutPiece.LabelPrefix, StringComparison.Ordinal))
                        {
                            Color color = singleColor.Value;
                            shape.Outline.Color.RGBAssign((int)color.R, (int)color.G, (int)color.B);
                        }
                        else shape.Outline.Color.CopyAssign(copy.Color);
                        ((dynamic)groups[copy.PartId]).Add((object)shape);
                    }
                    foreach (var entry in groups)
                    {
                        dynamic range = entry.Value;
                        object piece = Convert.ToInt32(range.Count) == 1 ? (object)range[1] : (object)range.Group();
                        piece = SportCutPiece.Combine((object)application, piece);
                        ((dynamic)piece).Name = names[entry.Key];
                    }
                    result.CurveCount = copies.Count;
                    exportDocument = (object)colorDocument;
                }

                // Native vector export: current page, independent of selection.
                // Values verified against Corel 2026 FilterDXF.tlb. No dialog,
                // rasterisation, curve simplification or fit-to-page scaling.
                // The dynamic COM binder can reject ExportEx's first argument
                // on Corel 27. Use explicit Automation string marshaling, as in
                // our ImportEx path, and supply real optional options objects.
                object exportOptions = application.CreateStructExportOptions();
                object paletteOptions = application.CreateStructPaletteOptions();
                object filter;
                try
                {
                    filter = exportDocument.GetType().InvokeMember("ExportEx", BindingFlags.InvokeMethod,
                        null, exportDocument, new object[] { new BStrWrapper(System.IO.Path.GetFullPath(temporary)),
                            CorelConstants.CdrDxf, CorelConstants.CdrCurrentPage, exportOptions, paletteOptions });
                }
                catch (Exception error)
                {
                    Log.Error("Could not start DXF ExportEx with explicit Automation arguments.", error);
                    throw new InvalidOperationException("Не удалось начать экспорт DXF: " +
                        error.GetBaseException().Message, error);
                }
                if (filter == null) throw new InvalidOperationException("CorelDRAW не открыл фильтр экспорта DXF.");
                Set(filter, "Version", 1); // dxfVersion2000
                Set(filter, "Units", 3); // dxfMillimeters
                Set(filter, "TextAsCurves", true);
                Set(filter, "FillUnmapped", false);
                Log.Info("Sport DXF export: page=" + page.Index + ", " + result.WidthMm + "x" +
                    result.HeightMm + " mm, curves=" + result.CurveCount + ", singleColor=" + singleColor.HasValue +
                    ", AutoCAD 2000, millimeters.");
                try { filter.GetType().InvokeMember("Finish", BindingFlags.InvokeMethod, null, filter, null); }
                catch (Exception error)
                {
                    throw new InvalidOperationException("CorelDRAW не завершил экспорт DXF: " +
                        error.GetBaseException().Message, error);
                }
                ValidateFile(temporary);
                result.PartCount = SportCutDxfBlocks.Write(temporary, groupedTemporary, partLayers);
                ValidateFile(groupedTemporary);
                long bytes = new FileInfo(groupedTemporary).Length;
                File.Copy(groupedTemporary, staging);
                File.Move(staging, target);
                result.Path = target;
                result.Bytes = bytes;
                Log.Info("Sport DXF export complete: " + System.IO.Path.GetFileName(target) +
                    ", parts/blocks=" + result.PartCount + ", bytes=" + result.Bytes);
                return result;
            }
            finally
            {
                if (colorDocument != null)
                    try { colorDocument.Dirty = false; colorDocument.Close(); }
                    catch (Exception error)
                    {
                        result.Warning = "DXF сохранён, но временный документ Corel не удалось закрыть.";
                        Log.Error("Could not close single-color DXF document.", error);
                    }
                if (selection != null)
                    try
                    {
                        document.Activate(); page.Activate();
                        if (selection.Count == 0) document.ClearSelection();
                        else
                        {
                            dynamic range = application.CreateShapeRange();
                            foreach (object shape in selection) range.Add(shape);
                            range.CreateSelection();
                        }
                    }
                    catch (Exception error)
                    {
                        result.Warning = "DXF сохранён, но прежнее выделение Corel не удалось восстановить.";
                        Log.Error("Could not restore selection after single-color DXF export.", error);
                    }
                try { document.Unit = oldUnit; document.Dirty = oldDirty; }
                catch (Exception error)
                {
                    result.Warning = "DXF сохранён, но прежние единицы документа Corel не удалось восстановить.";
                    Log.Error("Could not restore document state after DXF export.", error);
                }
                if (oldOptimization.HasValue)
                    try { application.Optimization = oldOptimization.Value; }
                    catch (Exception error) { Log.Error("Could not restore Corel optimization after DXF export.", error); }
                DeleteTemporary(temporary);
                DeleteTemporary(groupedTemporary);
                DeleteTemporary(staging);
            }
        }

        private static bool ExportVisible(object original)
        {
            dynamic shape = original, layer = shape.Layer;
            if (!Convert.ToBoolean(layer.Visible) || !Convert.ToBoolean(layer.Printable)) return false;
            for (int depth = 0; shape != null && depth <= 12; depth++, shape = shape.ParentGroup)
                if (!Convert.ToBoolean(shape.Visible)) return false;
            return true;
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
