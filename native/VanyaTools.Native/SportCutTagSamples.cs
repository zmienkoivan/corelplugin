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
            public string Label;
            public SportCutTagSettings Settings;
        }

        public static int Create(SportCutTagSettings settings)
        {
            settings.Validate();
            var samples = new List<Sample>();
            foreach (double width in new[] { Math.Max(12, settings.WidthMm - 4), settings.WidthMm,
                Math.Min(150, settings.WidthMm + 4) }.Distinct())
            foreach (double bridge in new[] { 1.0, 1.5, 2.0, 2.5, 3.0, settings.BridgeMm }.Distinct().OrderBy(b => b))
                samples.Add(new Sample { Label = "M", Settings = Copy(settings, width, bridge) });
            foreach (string size in new[] { "S", "XS", "XL", "XXL", "116", "128", "152" })
                samples.Add(new Sample { Label = size, Settings = Copy(settings, settings.WidthMm, settings.BridgeMm) });

            const int columns = 5;
            const double margin = 10, gap = 8, bodyHeight = 14;
            double cellWidth = Math.Max(45, samples.Max(s => SportCutSizeTag.WidthFor(s.Label, s.Settings)) + 20);
            double cellHeight = bodyHeight + settings.HeightMm;
            int rows = (samples.Count + columns - 1) / columns;
            double pageWidth = margin * 2 + columns * cellWidth + (columns - 1) * gap;
            double pageHeight = margin * 2 + rows * cellHeight + (rows - 1) * gap;

            dynamic app = CorelApp.Get(), document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте CDR для листа пробной резки.");
            int oldUnit = Convert.ToInt32(document.Unit);
            object oldPage = document.ActivePage;
            bool oldOptimization = Convert.ToBoolean(app.Optimization), started = false;
            dynamic page = null;
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
                    SportCutSizeTag.Add((object)app, (object)shape, new List<object> { (object)shape },
                        sample.Label, "образец " + (i + 1), sample.Settings, 10);

                    double actualWidth = SportCutSizeTag.WidthFor(sample.Label, sample.Settings);
                    string code = actualWidth.ToString("0.##", CultureInfo.InvariantCulture) + "X" +
                        settings.HeightMm.ToString("0.##", CultureInfo.InvariantCulture) + "/B" +
                        sample.Settings.BridgeMm.ToString("0.##", CultureInfo.InvariantCulture);
                    // The specimen code is also single-line cutting geometry,
                    // so no ordinary text or engraving layer reaches the laser.
                    dynamic withCode = document.CreateCurve();
                    withCode.CopyAssign(shape.Curve);
                    SportCutSizeTag.AppendLabel((object)withCode, code, 2.5,
                        (x, y) => new Point(left + 3 + x, top - 7 + y));
                    shape.Curve.CopyAssign(withCode);
                    shape.Name = "VT_TAG_SAMPLE_" + (i + 1).ToString("00") + "_" + sample.Label + "_" + code;
                }
                Log.Info("Sport cutting: created " + samples.Count + " tab samples, " + pageWidth + "x" + pageHeight + " mm.");
                return samples.Count;
            }
            catch
            {
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

        private static SportCutTagSettings Copy(SportCutTagSettings source, double width, double bridge) =>
            new SportCutTagSettings { WidthMm = width, HeightMm = source.HeightMm, BridgeMm = bridge,
                LetterHeightMm = source.LetterHeightMm, FirstOffsetMm = source.FirstOffsetMm,
                SizeStepMm = source.SizeStepMm };
    }
}
