using System;
using System.Collections.Generic;
using System.Globalization;

namespace VanyaTools.Native
{
    internal enum SleeveQuilting { Straight, Arcs, Chevrons, None }
    internal enum SleeveProduct { Laptop, Phone }

    internal sealed class PuffySleeveOptions
    {
        public SleeveProduct Product;
        public bool FourUp;
        public bool UseDiagonal;
        public double DeviceWidth = 325, DeviceHeight = 225, DeviceThickness = 18;
        public double DiagonalInches = 14, AspectWidth = 16, AspectHeight = 10, Bezel = 8;
        public double Ease = 5, Loft = 2, SeamAllowance = 5, CornerRadius = 12;
        public double FoamThickness = 4.5, FoamInset = 2, LiningThickness = 0.5, DepthEase = 2;
        public double ZipperTapeWidth = 40;
        public double ZipperDropPercent = 25, ZipperTail = 20;
        public double ShrinkX, ShrinkY, Bleed = 5;
        public double TrimMargin = 15, FixingOffset = 2;
        public double CommonBlankWidth, CommonBlankHeight;
        public SleeveQuilting Quilting = SleeveQuilting.Straight;
        public double QuiltStep = 45, QuiltInset = 10;
        public SleeveJigOptions Jigs;

        public static PuffySleeveOptions Phone() => new PuffySleeveOptions
        {
            Product = SleeveProduct.Phone, DeviceWidth = 75, DeviceHeight = 150,
            DeviceThickness = 18, QuiltStep = 20, FourUp = true
        };
    }

    // All geometry is in millimetres, with the origin at the top left of the
    // FINAL cut panel (after quilting). Only the blank/print output is enlarged
    // for quilting take-up. This keeps sewing patterns and zipper lengths true.
    internal sealed class PuffySleevePattern
    {
        public double DeviceWidth, DeviceHeight, SeamWidth, SeamHeight, CutWidth, CutHeight;
        public double Allowance, Radius, ScaleX, ScaleY, BlankWidth, BlankHeight, Bleed;
        public double ZipperDrop, ZipperLength, ZipperTapeLength, QuiltStep, QuiltInset;
        public double RingDepth, RequiredRingDepth, ZipperTapeWidth, PerimeterLength, BottomBandLength;
        public double FoamWidth, FoamHeight, FoamInset, FoamThickness, Loft, LiningThickness, DepthEase;
        public double TrimMargin, FixingOffset;
        public double BlankMarginX, BlankMarginY;
        public string FoamPath, FixingPath;
        public SleeveQuilting Quilting;
        public SleeveProduct Product;
        public bool FourUp;
        public int BatchSide => FourUp ? 2 : 1;
        public int PanelCount => BatchSide * BatchSide;
        public double BatchWidth => BlankWidth * BatchSide;
        public double BatchHeight => BlankHeight * BatchSide;
        public double BatchPrintWidth => BatchWidth + 2 * Bleed;
        public double BatchPrintHeight => BatchHeight + 2 * Bleed;
        public string CutPath, SeamPath, ZipperPath;
        public readonly List<string> QuiltPaths = new List<string>();
        public double PrintWidth { get { return BlankWidth + 2 * Bleed; } }
        public double PrintHeight { get { return BlankHeight + 2 * Bleed; } }

        public static PuffySleevePattern Build(PuffySleeveOptions o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            double w = o.DeviceWidth, h = o.DeviceHeight;
            if (o.UseDiagonal)
            {
                Range(o.DiagonalInches, 7, 22, "Диагональ, дюймы");
                Range(o.AspectWidth, 1, 30, "Соотношение сторон: ширина");
                Range(o.AspectHeight, 1, 30, "Соотношение сторон: высота");
                Range(o.Bezel, 0, 50, "Рамка экрана");
                double factor = o.DiagonalInches * 25.4 /
                    Math.Sqrt(o.AspectWidth * o.AspectWidth + o.AspectHeight * o.AspectHeight);
                w = o.AspectWidth * factor + 2 * o.Bezel;
                h = o.AspectHeight * factor + 2 * o.Bezel;
            }
            Range(w, 40, 650, "Ширина корпуса");
            Range(h, 60, 500, "Вторая сторона корпуса");
            Range(o.DeviceThickness, 1, 60, "Толщина устройства");
            if (!Enum.IsDefined(typeof(SleeveProduct), o.Product))
                throw new InvalidOperationException("Выберите вид изделия.");
            if (o.Product == SleeveProduct.Phone && o.UseDiagonal)
                throw new InvalidOperationException("Для телефона задайте размеры корпуса, включая защитный чехол.");
            Range(o.Ease, 0, 30, "Свобода с каждой стороны");
            Range(o.Loft, 0, 15, "Толщина сжатого стёганого слоя");
            Range(o.FoamThickness, 0, 15, "Толщина пены");
            Range(o.LiningThickness, 0, 5, "Толщина микрофибры");
            Range(o.DepthEase, 0, 20, "Свобода по толщине");
            Range(o.FoamInset, 0, 20, "Отступ пены внутрь от шва");
            Range(o.SeamAllowance, 3, 30, "Припуск на шов");
            Range(o.TrimMargin, 5, 60, "Запас прямоугольной заготовки за припуском");
            Range(o.FixingOffset, 0, o.SeamAllowance - 1, "Строчка фиксации наружу от сборочного шва");
            Range(o.ShrinkX, 0, 20, "Усадка по ширине, %");
            Range(o.ShrinkY, 0, 20, "Усадка по высоте, %");
            Range(o.Bleed, 0, 30, "Вылет печати");
            Range(o.ZipperDropPercent, 5, 50, "Спуск молнии, % высоты");
            Range(o.ZipperTail, 0, 100, "Запас ленты на каждом конце");
            if (!Enum.IsDefined(typeof(SleeveQuilting), o.Quilting))
                throw new InvalidOperationException("Выберите вид стёжки.");
            var p = new PuffySleevePattern
            {
                DeviceWidth = w, DeviceHeight = h,
                Product = o.Product, FourUp = o.FourUp,
                SeamWidth = w + 2 * o.Ease,
                SeamHeight = h + 2 * o.Ease,
                Allowance = o.SeamAllowance, Radius = o.CornerRadius,
                ScaleX = 1 / (1 - o.ShrinkX / 100), ScaleY = 1 / (1 - o.ShrinkY / 100),
                Bleed = o.Bleed, Quilting = o.Quilting, QuiltStep = o.QuiltStep, QuiltInset = o.QuiltInset,
                FoamInset = o.FoamInset, FoamThickness = o.FoamThickness,
                Loft = o.Loft, LiningThickness = o.LiningThickness, DepthEase = o.DepthEase,
                TrimMargin = o.TrimMargin, FixingOffset = o.FixingOffset,
                RingDepth = o.DeviceThickness + 2 * (o.FoamThickness + o.Loft + o.LiningThickness) + o.DepthEase
            };
            p.RequiredRingDepth = p.RingDepth;
            p.ZipperTapeWidth = p.RingDepth + 2 * p.Allowance;
            if (o.ZipperTapeWidth != 0)
            {
                Range(o.ZipperTapeWidth, 2 * p.Allowance + 2, 200,
                    "Полная ширина голой молнии с лентами при заданных припусках");
                p.ZipperTapeWidth = o.ZipperTapeWidth;
                p.RingDepth = o.ZipperTapeWidth - 2 * p.Allowance;
            }
            p.ZipperDrop = p.SeamHeight * o.ZipperDropPercent / 100;
            // Both ends must lie on the straight vertical edges, below the top arcs.
            Range(p.Radius, 0, Math.Min(Math.Min(p.SeamWidth, p.SeamHeight) / 2,
                p.ZipperDrop), "Радиус углов (не больше спуска молнии)");
            p.CutWidth = p.SeamWidth + 2 * p.Allowance;
            p.CutHeight = p.SeamHeight + 2 * p.Allowance;
            p.BlankWidth = (p.CutWidth + 2 * p.TrimMargin) * p.ScaleX;
            p.BlankHeight = (p.CutHeight + 2 * p.TrimMargin) * p.ScaleY;
            if (o.CommonBlankWidth != 0)
            {
                Range(o.CommonBlankWidth, p.BlankWidth, 1500, "Общая ширина заготовки до стёжки");
                p.BlankWidth = o.CommonBlankWidth;
            }
            if (o.CommonBlankHeight != 0)
            {
                Range(o.CommonBlankHeight, p.BlankHeight, 1500, "Общая высота заготовки до стёжки");
                p.BlankHeight = o.CommonBlankHeight;
            }
            p.BlankMarginX = (p.BlankWidth / p.ScaleX - p.CutWidth) / 2;
            p.BlankMarginY = (p.BlankHeight / p.ScaleY - p.CutHeight) / 2;
            // Two vertical runs, two quarter circles, and one straight top edge.
            p.ZipperLength = p.SeamWidth + 2 * p.ZipperDrop + (Math.PI - 4) * p.Radius;
            p.ZipperTapeLength = p.ZipperLength + 2 * p.Allowance + 2 * o.ZipperTail;
            p.PerimeterLength = 2 * (p.SeamWidth + p.SeamHeight) + (2 * Math.PI - 8) * p.Radius;
            p.BottomBandLength = p.PerimeterLength - p.ZipperLength;
            p.FoamWidth = p.SeamWidth - 2 * o.FoamInset;
            p.FoamHeight = p.SeamHeight - 2 * o.FoamInset;
            if (p.FoamWidth < 1 || p.FoamHeight < 1)
                throw new InvalidOperationException("Отступ пены слишком велик для этой боковинки.");
            p.FoamPath = RoundedRect(p.Allowance + o.FoamInset, p.Allowance + o.FoamInset,
                p.FoamWidth, p.FoamHeight, Math.Max(0, p.Radius - o.FoamInset));
            double a = p.Allowance, r = p.Radius, right = a + p.SeamWidth;
            p.CutPath = RoundedRect(0, 0, p.CutWidth, p.CutHeight, r + a);
            p.SeamPath = RoundedRect(a, a, p.SeamWidth, p.SeamHeight, r);
            p.FixingPath = RoundedRect(a - p.FixingOffset, a - p.FixingOffset,
                p.SeamWidth + 2 * p.FixingOffset, p.SeamHeight + 2 * p.FixingOffset, r + p.FixingOffset);
            p.ZipperPath = "M " + Pair(a, a + p.ZipperDrop) + " L " + Pair(a, a + r);
            if (r > 0) p.ZipperPath += " A " + Pair(r, r) + " 0 0 1 " + Pair(a + r, a);
            p.ZipperPath += " L " + Pair(right - r, a);
            if (r > 0) p.ZipperPath += " A " + Pair(r, r) + " 0 0 1 " + Pair(right, a + r);
            p.ZipperPath += " L " + Pair(right, a + p.ZipperDrop);
            if (o.Quilting != SleeveQuilting.None)
            {
                Range(o.QuiltStep, 10, 150, "Шаг стёжки");
                Range(o.QuiltInset, 0, Math.Min(p.BlankWidth / p.ScaleX,
                    p.BlankHeight / p.ScaleY) / 2 - 5, "Отступ стёжки от края заготовки");
                p.BuildQuilting();
            }
            return p;
        }

        private void BuildQuilting()
        {
            // Quilt an oversized RECTANGLE first. The shaping template establishes
            // the garment contour afterwards; quilting is independent of its size.
            double left = -BlankMarginX + QuiltInset, top = -BlankMarginY + QuiltInset;
            double width = BlankWidth / ScaleX - 2 * QuiltInset;
            double height = BlankHeight / ScaleY - 2 * QuiltInset;
            double amplitude = Quilting == SleeveQuilting.Straight ? 0 : QuiltStep * 0.35;
            amplitude = Math.Min(amplitude, height / 3);
            int count = Math.Max(1, (int)Math.Floor((height - amplitude) / QuiltStep));
            double start = top + (height - ((count - 1) * QuiltStep + amplitude)) / 2;
            for (int i = 0; i < count; i++)
            {
                double y = start + i * QuiltStep;
                double x1 = left, x2 = left + width, middle = left + width / 2;
                string path = "M " + Pair(x1, y);
                if (Quilting == SleeveQuilting.Arcs)
                    path += " Q " + Pair(middle, y + 2 * amplitude) + " " + Pair(x2, y);
                else if (Quilting == SleeveQuilting.Chevrons)
                    path += " L " + Pair(middle, y + amplitude) + " L " + Pair(x2, y);
                else path += " L " + Pair(x2, y);
                QuiltPaths.Add(path);
            }
        }

        public static string RoundedRect(double x, double y, double w, double h, double r)
        {
            if (r <= 0) return "M " + Pair(x, y) + " h " + N(w) + " v " + N(h) + " h " + N(-w) + " Z";
            return "M " + Pair(x + r, y) + " H " + N(x + w - r) +
                " A " + Pair(r, r) + " 0 0 1 " + Pair(x + w, y + r) + " V " + N(y + h - r) +
                " A " + Pair(r, r) + " 0 0 1 " + Pair(x + w - r, y + h) + " H " + N(x + r) +
                " A " + Pair(r, r) + " 0 0 1 " + Pair(x, y + h - r) + " V " + N(y + r) +
                " A " + Pair(r, r) + " 0 0 1 " + Pair(x + r, y) + " Z";
        }

        public static string N(double n) { return n.ToString("0.######", CultureInfo.InvariantCulture); }
        private static string Pair(double x, double y) { return N(x) + " " + N(y); }
        private static void Range(double n, double min, double max, string name)
        {
            if (Double.IsNaN(n) || Double.IsInfinity(n) || n < min || n > max)
                throw new InvalidOperationException(name + ": от " + N(min) + " до " + N(max) + ".");
        }
    }
}
