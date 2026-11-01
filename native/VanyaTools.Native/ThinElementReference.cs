using System;
using System.Collections.Generic;
using System.Globalization;

namespace VanyaTools.Native
{
    internal static class ThinElementReference
    {
        internal static double[] Values()
        { var values = new double[10]; for (int i = 0; i < values.Length; i++) values[i] = (i + 1) / 10d; return values; }
        internal static void Insert(bool uv, ThinAuditOptions options)
        { Insert((object)CorelApp.Get(), uv, options); }
        internal static void Insert(object application, bool uv, ThinAuditOptions options)
        {
            options.Validate();
            dynamic app = application, doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ для вставки линейки.");
            int unit = Convert.ToInt32(doc.Unit); bool started = false;
            var made = new List<object>();
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double left = Convert.ToDouble(doc.ActivePage.LeftX) + 5, top = Convert.ToDouble(doc.ActivePage.TopY) - 5;
                dynamic selected = app.ActiveSelectionRange;
                if (selected != null && Convert.ToInt32(selected.Count) > 0)
                { left = Convert.ToDouble(selected.LeftX); top = Convert.ToDouble(selected.BottomY) - 5; }
                doc.BeginCommandGroup("Vanya Tools · линейка тонких элементов"); started = true;
                dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
                Action<object> add = shape => { made.Add(shape); range.Add((dynamic)shape); };
                Action<double, double, string, float> text = (x, y, value, points) => {
                    dynamic shape = layer.CreateArtisticTextWide(0, 0, value, 0, -1, "Arial", points); add((object)shape);
                    shape.Fill.UniformColor.CMYKAssign(0, 0, 0, 100); shape.Outline.SetNoOutline();
                    shape.Move(x - Convert.ToDouble(shape.LeftX), y - Convert.ToDouble(shape.TopY));
                };
                Action<double, double, double, double, double> bar = (x, y, width, height, angle) => {
                    dynamic shape = layer.CreateRectangle2(x, y, width, height); add((object)shape);
                    shape.Fill.UniformColor.CMYKAssign(0, 0, 0, 100); shape.Outline.SetNoOutline();
                    if (angle != 0) shape.Rotate(angle);
                };
                string thresholds = String.Format(CultureInfo.CurrentCulture, "{0}: линии {1:0.##}–{2:0.##} мм · точки ≥{3:0.##} мм",
                    uv ? "УФ-ДТФ" : "ДТФ", options.MinimumMm, options.SafeMm, options.DotMm);
                text(left, top, thresholds, 7f);
                string[] rows = { "Круг Ø", "Гориз.", "Вертик.", "45°", "Просвет" };
                for (int row = 0; row < rows.Length; row++) text(left, top - 9 - row * 7, rows[row], 6f);
                foreach (double mm in Values())
                {
                    double x = left + 16 + (mm * 10 - 1) * 6;
                    text(x, top - 4, mm.ToString("0.0", CultureInfo.CurrentCulture), 6f);
                    dynamic circle = layer.CreateEllipse2(x + 2, top - 10, mm / 2, mm / 2); add((object)circle);
                    circle.Fill.UniformColor.CMYKAssign(0, 0, 0, 100); circle.Outline.SetNoOutline();
                    bar(x, top - 17, 4, mm, 0);
                    bar(x + 2 - mm / 2, top - 25, mm, 4, 0);
                    bar(x, top - 31, 4, mm, 45);
                    bar(x, top - 39, 4, 1.5, 0); bar(x, top - 37.5 + mm, 4, 1.5, 0);
                }
                bar(left, top - 45, 10, .2, 0); // Physical 10 mm calibration bar.
                text(left + 12, top - 43, "10 мм · масштаб 1:1 · векторные заливки, без обводок", 6f);
                dynamic group = range.Group(); group.Name = "Vanya · " + (uv ? "УФ-ДТФ" : "ДТФ") + " · линейка 0,1–1 мм";
                group.CreateSelection();
            }
            catch
            {
                for (int i = made.Count - 1; i >= 0; i--) try { ((dynamic)made[i]).Delete(); } catch { }
                throw;
            }
            finally { try { if (started) doc.EndCommandGroup(); } finally { doc.Unit = unit; } }
        }
    }
}
