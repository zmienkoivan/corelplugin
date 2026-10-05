using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal sealed class NamesGridEntry
    {
        public string Text;
        public double? MaxWidthMm;

        public string PrintText(bool twoLines)
        {
            if (!twoLines) return Text;
            int separator = Text.IndexOf(' ');
            return separator < 0 ? Text : Text.Substring(0, separator) + "\r" + Text.Substring(separator + 1);
        }
    }

    internal sealed class NamesGridOptions
    {
        public string FontName;
        public double FontSizePt;
        public double MaxWidthMm;
        public int Columns;
        public double HorizontalGapMm;
        public double VerticalGapMm;
        public bool CenterInCell;
        public bool TwoLines;
    }

    internal sealed class NamesGridResult
    {
        public int Count;
        public int ReducedCount;
        public double WidthMm;
        public double HeightMm;
    }

    internal sealed class NamesGridService
    {
        public NamesGridResult Create(IList<NamesGridEntry> names, NamesGridOptions options)
        {
            if (names == null || names.Count == 0)
                throw new InvalidOperationException("Добавьте хотя бы одну строку с именем.");

            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("Откройте документ CorelDRAW.");

            int oldUnit = (int)doc.Unit;
            dynamic range = app.CreateShapeRange();
            var shapes = new List<dynamic>();
            int rows = (names.Count + options.Columns - 1) / options.Columns;
            var columnWidths = new double[options.Columns];
            var rowHeights = new double[rows];
            int reducedCount = 0;
            bool commandStarted = false;

            try
            {
                doc.BeginCommandGroup("Vanya Tools - names grid");
                commandStarted = true;
                doc.Unit = CorelConstants.CdrMillimeter;
                dynamic layer = doc.ActiveLayer;

                for (int i = 0; i < names.Count; i++)
                {
                    // Wide text keeps Cyrillic and other Unicode characters intact.
                    dynamic shape = layer.CreateArtisticTextWide(
                        0.0, 0.0, names[i].PrintText(options.TwoLines), 0, -1,
                        options.FontName, (float)options.FontSizePt);
                    shapes.Add(shape);
                    range.Add(shape);
                    if (options.TwoLines)
                        shape.Text.Story.Alignment = 3;
                    shape.Fill.UniformColor.RGBAssign(0, 0, 0);
                    shape.Outline.SetNoOutline();

                    double width = (double)shape.SizeWidth;
                    double height = (double)shape.SizeHeight;
                    if (width <= 0 || height <= 0)
                        throw new InvalidOperationException("CorelDRAW не смог измерить текст: " + names[i].Text);

                    double maxWidth = names[i].MaxWidthMm.HasValue
                        ? Math.Min(options.MaxWidthMm, names[i].MaxWidthMm.Value)
                        : options.MaxWidthMm;
                    if (width > maxWidth)
                    {
                        double scale = maxWidth / width;
                        shape.SetSize(maxWidth, height * scale);
                        width = (double)shape.SizeWidth;
                        height = (double)shape.SizeHeight;
                        if (width > maxWidth + 0.1)
                            throw new InvalidOperationException("CorelDRAW не уменьшил текст до заданной ширины: " + names[i].Text);
                        reducedCount++;
                    }

                    int column = i % options.Columns;
                    int row = i / options.Columns;
                    columnWidths[column] = Math.Max(columnWidths[column], width);
                    rowHeights[row] = Math.Max(rowHeights[row], height);
                }

                dynamic page = doc.ActivePage;
                double left = 10.0;
                double top = (double)page.SizeHeight - 10.0;
                double[] columnLefts = new double[options.Columns];
                double[] rowTops = new double[rows];
                double x = left;
                for (int column = 0; column < options.Columns; column++)
                {
                    columnLefts[column] = x;
                    x += columnWidths[column] + (column < options.Columns - 1 ? options.HorizontalGapMm : 0);
                }

                double y = top;
                for (int row = 0; row < rows; row++)
                {
                    rowTops[row] = y;
                    y -= rowHeights[row] + (row < rows - 1 ? options.VerticalGapMm : 0);
                }

                for (int i = 0; i < shapes.Count; i++)
                {
                    dynamic shape = shapes[i];
                    int column = i % options.Columns;
                    int row = i / options.Columns;
                    double targetLeft = columnLefts[column];
                    if (options.CenterInCell)
                        targetLeft += (columnWidths[column] - (double)shape.SizeWidth) / 2.0;
                    shape.Move(targetLeft - (double)shape.LeftX, rowTops[row] - (double)shape.TopY);
                }

                try { range.CreateSelection(); } catch (Exception ex) { Log.Info("Names grid selection: " + ex.Message); }
                try { app.ActiveWindow.Refresh(); } catch { }
                return new NamesGridResult
                {
                    Count = names.Count,
                    ReducedCount = reducedCount,
                    WidthMm = x - left,
                    HeightMm = top - y
                };
            }
            catch
            {
                try { range.Delete(); } catch (Exception ex) { Log.Error("Names grid rollback failed.", ex); }
                throw;
            }
            finally
            {
                try { doc.Unit = oldUnit; } catch { }
                if (commandStarted)
                    try { doc.EndCommandGroup(); } catch (Exception ex) { Log.Error("Names grid command group failed.", ex); }
            }
        }
    }
}
