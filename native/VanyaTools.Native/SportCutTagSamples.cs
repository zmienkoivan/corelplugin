using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace VanyaTools.Native
{
    internal static class SportCutTagSamples
    {
        private sealed class Sample
        {
            public string Label, Code;
            public SportCutTagSettings Settings;
            public object Shape;
        }

        public static int Create(SportCutTagSettings settings)
        {
            settings.Validate();
            var samples = new List<Sample>();
            var seen = new HashSet<string>();
            void Add(string label, SportCutTagSettings value)
            {
                value.LabelHex = settings.LabelHex;
                value.Validate();
                string key = label + "|" + value.WidthMm.ToString("R", CultureInfo.InvariantCulture) + "|" +
                    value.HeightMm.ToString("R", CultureInfo.InvariantCulture) + "|" + value.BridgeMm.ToString("R", CultureInfo.InvariantCulture) +
                    "|" + (value.CutLabel ? value.LetterHeightMm : 0).ToString("R", CultureInfo.InvariantCulture) + "|" + value.CutLabel;
                if (seen.Add(key)) samples.Add(new Sample { Label = label, Settings = value });
            }
            void Pair(double width, double height, double bridge, double letter)
            {
                foreach (bool cut in new[] { true, false })
                    Add("M", new SportCutTagSettings { WidthMm = width, HeightMm = height, BridgeMm = bridge,
                        LetterHeightMm = letter, CutLabel = cut });
            }
            // Keep the failed geometry as a control and vary pad size, bridges,
            // and interior cuts separately. No small ID cuts are added to cloth.
            Pair(24, 12, 2, 8);
            foreach (var size in new[] { new[] { 30.0, 20.0 }, new[] { 40.0, 25.0 } })
                foreach (double bridge in new[] { 3.0, 4.0 }) Pair(size[0], size[1], bridge, 8);
            Pair(settings.WidthMm, settings.HeightMm, settings.BridgeMm, Math.Min(settings.LetterHeightMm, settings.HeightMm - 2));
            foreach (string size in new[] { "S", "XS", "XL", "XXL", "116", "128", "152" })
                Add(size, new SportCutTagSettings { WidthMm = 30, HeightMm = 20, BridgeMm = 4, LetterHeightMm = 8 });

            const double margin = 10, gap = 10, bodyHeight = 20, captionHeight = 12, headerHeight = 20;
            double cellWidth = Math.Max(52, samples.Max(s => SportCutSizeTag.WidthFor(s.Label, s.Settings)) + 20);
            double cellHeight = bodyHeight + samples.Max(s => s.Settings.HeightMm);
            int columns = Math.Max(1, Math.Min(4, (int)((570 - 2 * margin + gap) / (cellWidth + gap))));
            int rows = (samples.Count + columns - 1) / columns;
            double pageWidth = margin * 2 + columns * cellWidth + (columns - 1) * gap;
            double pageHeight = margin * 2 + rows * cellHeight + (rows - 1) * gap;

            dynamic app = CorelApp.Get(), document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте CDR для листа пробной резки.");
            int oldUnit = Convert.ToInt32(document.Unit);
            object oldPage = document.ActivePage;
            bool oldOptimization = Convert.ToBoolean(app.Optimization), started = false;
            dynamic page = null, legend = null;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                app.Optimization = true;
                document.BeginCommandGroup("Vanya Tools — лист пробной резки язычков"); started = true;
                page = document.AddPages(1);
                page.Activate();
                page.Name = "Резка спортформы · проба язычков";
                page.SetSize(pageWidth, pageHeight);
                dynamic layer = document.ActiveLayer;
                layer.Name = "Язычки · сквозная резка";
                double pageLeft = Convert.ToDouble(page.LeftX), pageTop = Convert.ToDouble(page.TopY);
                for (int i = 0; i < samples.Count; i++)
                {
                    Sample sample = samples[i];
                    sample.Code = ((char)('A' + i % columns)).ToString() + (i / columns + 1);
                    double left = pageLeft + margin + (i % columns) * (cellWidth + gap);
                    double top = pageTop - margin - (i / columns) * (cellHeight + gap);
                    double bottom = top - bodyHeight;
                    dynamic curve = document.CreateCurve();
                    dynamic path = curve.CreateSubPath(left, bottom);
                    path.AppendLineSegment(left + cellWidth, bottom);
                    path.AppendLineSegment(left + cellWidth, top);
                    path.AppendLineSegment(left, top);
                    path.Closed = true;
                    dynamic shape = layer.CreateCurve(curve);
                    shape.Name = "silhouette";
                    shape.Fill.ApplyNoFill();
                    shape.Outline.Width = .1;
                    shape.Outline.Color.RGBAssign(0, 160, 80);
                    shape = SportCutSizeTag.Add((object)app, (object)shape, (object)shape, new List<object> { (object)shape },
                        sample.Label, "образец " + (i + 1), sample.Settings, 10);
                    shape.Name = "VT_TAG_SAMPLE_" + sample.Code + "_" + sample.Label + "_" + Description(sample);
                    sample.Shape = (object)shape;
                }

                // Reference labels are deliberately on a separate, non-cutting
                // page. The existing DXF/page validator accepts only cut pages.
                legend = document.AddPages(1); legend.Activate();
                legend.Name = "Памятка · проба язычков";
                legend.SetSize(pageWidth, pageHeight + rows * captionHeight + headerHeight);
                dynamic legendLayer = document.ActiveLayer; legendLayer.Name = "Памятка · не для резки";
                double legendLeft = Convert.ToDouble(legend.LeftX), legendTop = Convert.ToDouble(legend.TopY);
                Text((object)legendLayer, legendLeft + margin, legendTop - margin,
                    "Проба язычков · памятка\nЦвет подписи " + settings.LabelHex + ". Сетка соответствует листу резки.", 10);
                for (int i = 0; i < samples.Count; i++)
                {
                    Sample sample = samples[i];
                    double left = legendLeft + margin + (i % columns) * (cellWidth + gap);
                    double top = legendTop - margin - headerHeight - (i / columns) * (cellHeight + captionHeight + gap);
                    Text((object)legendLayer, left, top, sample.Code + " · " + Description(sample) + "\n" +
                        (sample.Settings.CutLabel ? sample.Label + " · буква " + Number(sample.Settings.LetterHeightMm) + " мм" : "Без прорезной буквы"), 8);
                    dynamic diagram = ((dynamic)sample.Shape).CopyToLayer(legendLayer);
                    diagram.Move(left - Convert.ToDouble(diagram.LeftX), top - captionHeight - Convert.ToDouble(diagram.TopY));
                }
                page.Activate();
                Log.Info("Sport cutting: created " + samples.Count + " tab samples, " + pageWidth + "x" + pageHeight + " mm.");
                return samples.Count;
            }
            catch
            {
                if (legend != null)
                    try { legend.Delete(); } catch (Exception error) { Log.Error("Could not remove failed tab reference page.", error); }
                if (page != null)
                    try { page.Delete(); } catch (Exception error) { Log.Error("Could not remove failed tab sample page.", error); }
                try { ((dynamic)oldPage).Activate(); } catch { }
                throw;
            }
            finally
            {
                try { if (started) document.EndCommandGroup(); } catch { }
                try { document.Unit = oldUnit; } catch { }
                try { app.Optimization = oldOptimization; } catch { }
            }
        }

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Description(Sample sample) => Number(SportCutSizeTag.WidthFor(sample.Label, sample.Settings)) + "×" +
            Number(sample.Settings.HeightMm) + " мм · мостики " + Number(sample.Settings.BridgeMm) + "+" + Number(sample.Settings.BridgeMm);
        private static void Text(object target, double left, double top, string value, float points)
        {
            dynamic label = ((dynamic)target).CreateArtisticTextWide(left, top, value, 0, -1, "Arial", points);
            label.Fill.UniformColor.CMYKAssign(0, 0, 0, 100); label.Outline.SetNoOutline();
            label.Move(left - Convert.ToDouble(label.LeftX), top - Convert.ToDouble(label.TopY));
        }
    }
}
