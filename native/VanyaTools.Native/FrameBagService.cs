using System;
using System.Collections.Generic;
using System.Globalization;

namespace VanyaTools.Native
{
    internal static class FrameBagService
    {
        internal static int Insert(FrameBagPlan plan) => Insert((object)CorelApp.Get(), plan);

        internal static int Insert(object application, FrameBagPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (application == null) throw new InvalidOperationException("CorelDRAW недоступен.");
            dynamic app = application, document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic previousPage = document.ActivePage, page = null;
            int oldUnit = Convert.ToInt32(document.Unit);
            bool optimized = Convert.ToBoolean(app.Optimization), oldDirty = Convert.ToBoolean(document.Dirty);
            bool begun = false, succeeded = false, cleaned = true;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                app.Optimization = true;
                document.BeginCommandGroup("Vanya Tools · выкройка велосумки"); begun = true;
                page = document.AddPages(1); page.Activate();
                page.Name = String.IsNullOrWhiteSpace(plan.Setup.Name) ? "Велосумка" : plan.Setup.Name.Trim();
                page.SetSize(plan.KitWidth, plan.KitHeight + 35);
                double left = 0, bottom = 0, width = 0, height = 0;
                page.GetBoundingBox(ref left, ref bottom, ref width, ref height);
                double top = bottom + height;
                dynamic cuts = page.CreateLayer("Велосумка · резка"), guides = page.CreateLayer("Велосумка · подписи и швы");
                guides.Printable = false;
                dynamic selected = app.CreateShapeRange();
                foreach (var section in plan.Sections)
                    Label(guides, section.Setup.Name + " · " + N(section.VolumeLitres) + " л", left + 15, top - section.LayoutTop - 5, false);
                foreach (var part in plan.Parts)
                {
                    dynamic cut = Curve(part.Cut, cuts, left + part.Left, top - part.Top, part.Name + " · резка", true, part.Cutouts);
                    selected.Add(cut);
                    Curve(part.Seam, guides, left + part.Left, top - part.Top, part.Name + " · линия шва", false);
                    foreach (var mark in part.Marks) Mark(mark, guides, left + part.Left, top - part.Top);
                    double centerX = left + part.Left + (MinX(part.Cut) + MaxX(part.Cut)) / 2;
                    Label(guides, part.Name, centerX, top - part.Top - MinY(part.Cut) - 5);
                    if (part.Edge >= 0)
                    {
                        Label(guides, ((char)('A' + part.Edge)).ToString(), left + part.Left + 3, top - part.Top, false);
                        Label(guides, ((char)('A' + (part.Edge + 1) % part.CornerCount)).ToString(),
                            left + part.Left + MaxX(part.Seam) - 3, top - part.Top, false);
                    }
                }
                // Corner letters match the start/end of each wall on both side panels.
                foreach (var part in plan.Parts)
                {
                    for (int n = 0; n < part.CornerPoints.Length; n++)
                    {
                        var p = part.CornerPoints[n];
                        if (!FrameBagClosures.Inside(part.Seam, p)) continue;
                        Label(guides, ((char)('A' + n)).ToString(), left + part.Left + p.X, top - part.Top - p.Y, false);
                    }
                }
                var s = plan.Setup;
                string frameText = s.TriangleFrame
                    ? "Треугольник A–B–C, мм: " + N(s.Top) + " / " + N(s.Down) + " / " + N(s.Seat)
                    : "Внутренний проём A–B–C–D, мм: " + N(s.Top) + " / " + N(s.Head) + " / " + N(s.Down) + " / " + N(s.Seat) + "; A–C: " + N(s.Diagonal);
                string summary = frameText + (s.BagLength > 0 ? "; длина по верху " + N(s.BagLength) + "; привязка " + (s.AttachToFront ? "спереди" : "сзади") : "; вся длина рамы") +
                    (s.DivideBags ? "; две самостоятельные сумки" : "") + "\nСумка: высота " + N(s.Height) + "; глубина " + N(s.TopDepth) + " → " + N(s.BottomDepth) +
                    "; высота спереди " + N(s.SplitHeights ? s.FrontHeight : s.Height) + "; зазор " + N(s.Clearance) + "; припуск " + N(s.Allowance) + "; R " +
                    (s.CornerRadii == null ? N(s.CornerRadius) : String.Join(" / ", Array.ConvertAll(s.CornerRadii, N))) +
                    "; молнии " + FrameBagClosures.Name(s.LeftZipper) + " / " + FrameBagClosures.Name(s.RightZipper) +
                    ". Красный — резка. Синий — непечатаемые швы и подписи." +
                    (s.Notches && s.Allowance >= .4 ? " Две надсечки — начало стенки, одна — конец; глубина до " + N(s.NotchDepth) + " мм, не больше половины припуска." : "");
                Label(guides, summary, left + 15, top - plan.KitHeight - 5, false);
                cuts.Activate(); selected.CreateSelection();
                succeeded = true;
                return plan.Parts.Length;

                object Curve(BagPoint[] points, dynamic layer, double x, double y, string name, bool cut, BagPoint[][] holes = null)
                {
                    dynamic curve = document.CreateCurve();
                    AddPath(points);
                    if (holes != null) foreach (var hole in holes) AddPath(hole);
                    void AddPath(BagPoint[] nodes)
                    {
                        dynamic path = curve.CreateSubPath(x + nodes[0].X, y - nodes[0].Y);
                        for (int n = 1; n < nodes.Length; n++) path.AppendLineSegment(x + nodes[n].X, y - nodes[n].Y);
                        path.Closed = true;
                    }
                    dynamic result = layer.CreateCurve(curve);
                    result.Fill.ApplyNoFill(); result.Outline.Width = cut ? .01 : .1;
                    result.Outline.Color.RGBAssign(cut ? 255 : 50, cut ? 0 : 100, cut ? 0 : 180);
                    result.Name = name; return (object)result;
                }

                void Mark(BagPoint[] points, dynamic layer, double x, double y)
                {
                    dynamic curve = document.CreateCurve();
                    dynamic path = curve.CreateSubPath(x + points[0].X, y - points[0].Y);
                    for (int n = 1; n < points.Length; n++) path.AppendLineSegment(x + points[n].X, y - points[n].Y);
                    path.Closed = false;
                    dynamic mark = layer.CreateCurve(curve);
                    mark.Fill.ApplyNoFill(); mark.Outline.Width = .1; mark.Outline.Color.RGBAssign(50, 100, 180);
                    mark.Name = "Велосумка · молния / притачивание клапана";
                }

                void Label(dynamic layer, string text, double x, double y, bool centered = true)
                {
                    dynamic label = layer.CreateArtisticTextWide(0, 0, text, 0, -1, "Arial", 9f);
                    label.Fill.UniformColor.RGBAssign(50, 100, 180); label.Outline.SetNoOutline();
                    label.Move(x - Convert.ToDouble(label.LeftX) - (centered ? Convert.ToDouble(label.SizeWidth) / 2 : 0),
                        y - Convert.ToDouble(label.TopY));
                    label.Name = "Велосумка · подпись";
                }
            }
            finally
            {
                if (!succeeded && page != null)
                {
                    try { previousPage.Activate(); page.Delete(); }
                    catch (Exception error) { cleaned = false; Log.Error("Could not roll back frame bag page.", error); }
                }
                try { if (begun) document.EndCommandGroup(); }
                finally
                {
                    try { document.Unit = oldUnit; }
                    finally
                    {
                        app.Optimization = optimized;
                        if (!succeeded && cleaned) document.Dirty = oldDirty;
                        if (!optimized) app.ActiveWindow.Refresh();
                    }
                }
            }
        }
        private static string N(double n) => n.ToString("0.#", CultureInfo.CurrentCulture);
        private static double MinX(BagPoint[] p) { double n = Double.MaxValue; foreach (var a in p) n = Math.Min(n, a.X); return n; }
        private static double MaxX(BagPoint[] p) { double n = Double.MinValue; foreach (var a in p) n = Math.Max(n, a.X); return n; }
        private static double MinY(BagPoint[] p) { double n = Double.MaxValue; foreach (var a in p) n = Math.Min(n, a.Y); return n; }
    }
}
