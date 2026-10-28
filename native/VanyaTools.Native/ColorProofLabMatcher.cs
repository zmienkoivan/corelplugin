using System;

namespace VanyaTools.Native
{
    internal struct ColorProofLab
    {
        public double L;
        public double A;
        public double B;

        public ColorProofLab(double l, double a, double b)
        { L = l; A = a; B = b; }
    }

    internal static class ColorProofLabMatcher
    {
        // CIEDE2000, kL = kC = kH = 1. Independent implementation of equations 2-22:
        // Sharma, Wu, Dalal, Color Research & Application 30 (2005), 21-30.
        // https://hajim.rochester.edu/ece/sites/gsharma/ciede2000/ciede2000noteCRNA.pdf
        public static double DeltaE2000(ColorProofLab a, ColorProofLab b)
        {
            Validate(a, nameof(a)); Validate(b, nameof(b));
            double c1 = Hypot(a.A, a.B), c2 = Hypot(b.A, b.B);
            double cMean = c1 / 2 + c2 / 2;
            double g = 0.5 * (1 - ChromaRatio(cMean));
            double a1Prime = (1 + g) * a.A, a2Prime = (1 + g) * b.A;
            double c1Prime = Hypot(a1Prime, a.B), c2Prime = Hypot(a2Prime, b.B);
            double h1Prime = Hue(a1Prime, a.B), h2Prime = Hue(a2Prime, b.B);

            double deltaL = b.L - a.L;
            double deltaC = c2Prime - c1Prime;
            // Check each chroma rather than their product: the latter can underflow
            // for valid nearly neutral inputs and incorrectly select the zero branch.
            bool achromatic = c1Prime == 0 || c2Prime == 0;
            double hueDifference = h2Prime - h1Prime;
            if (achromatic) hueDifference = 0;
            else if (hueDifference > 180) hueDifference -= 360;
            else if (hueDifference < -180) hueDifference += 360;
            double deltaH = 2 * Math.Sqrt(c1Prime) * Math.Sqrt(c2Prime) * SinDegrees(hueDifference / 2);

            double lMean = a.L / 2 + b.L / 2;
            double cPrimeMean = c1Prime / 2 + c2Prime / 2;
            double hueSum = h1Prime + h2Prime, hMean;
            if (achromatic) hMean = hueSum;
            else if (Math.Abs(h1Prime - h2Prime) <= 180) hMean = hueSum / 2;
            else if (hueSum < 360) hMean = (hueSum + 360) / 2;
            else hMean = (hueSum - 360) / 2;

            double t = 1 - 0.17 * CosDegrees(hMean - 30) + 0.24 * CosDegrees(2 * hMean) +
                0.32 * CosDegrees(3 * hMean + 6) - 0.20 * CosDegrees(4 * hMean - 63);
            double thetaDistance = (hMean - 275) / 25;
            double deltaTheta = 30 * Math.Exp(-thetaDistance * thetaDistance);
            double rc = 2 * ChromaRatio(cPrimeMean);
            double lightnessDistance = lMean - 50;
            double lightnessSquare = lightnessDistance * lightnessDistance;
            double sl = 1 + 0.015 * lightnessSquare / Math.Sqrt(20 + lightnessSquare);
            double sc = 1 + 0.045 * cPrimeMean;
            double sh = 1 + 0.015 * cPrimeMean * t;
            double rt = -SinDegrees(2 * deltaTheta) * rc;
            double dl = deltaL / sl, dc = deltaC / sc, dh = deltaH / sh;
            double squared = dl * dl + dc * dc + dh * dh + rt * dc * dh;
            if (!Finite(squared))
                throw new InvalidOperationException("Значения Lab слишком велики для вычисления цветового расстояния.");
            // The metric is nonnegative; suppress only a possible rounding residue.
            return Math.Sqrt(Math.Max(0, squared));
        }

        private static double Hue(double a, double b)
        {
            if (a == 0 && b == 0) return 0;
            double hue = Math.Atan2(b, a) * (180 / Math.PI);
            if (hue < 0) hue += 360;
            return hue >= 360 ? 0 : hue;
        }

        private static double Hypot(double a, double b)
        {
            a = Math.Abs(a); b = Math.Abs(b);
            double high = Math.Max(a, b), low = Math.Min(a, b);
            if (high == 0) return 0;
            double ratio = low / high;
            return high * Math.Sqrt(1 + ratio * ratio);
        }

        // sqrt(C^7 / (C^7 + 25^7)), without forming large seventh powers.
        private static double ChromaRatio(double chroma)
        {
            if (chroma == 0) return 0;
            if (chroma >= 25)
            {
                double inverse = Math.Pow(25 / chroma, 7);
                return Math.Sqrt(1 / (1 + inverse));
            }
            double power = Math.Pow(chroma / 25, 7);
            return Math.Sqrt(power / (1 + power));
        }

        private static double SinDegrees(double degrees) => Math.Sin(degrees * (Math.PI / 180));
        private static double CosDegrees(double degrees) => Math.Cos(degrees * (Math.PI / 180));
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        private static void Validate(ColorProofLab value, string parameter)
        {
            if (!Finite(value.L) || !Finite(value.A) || !Finite(value.B))
                throw new ArgumentException("Все координаты Lab должны быть конечными числами.", parameter);
            if (value.L < 0 || value.L > 100)
                throw new ArgumentOutOfRangeException(parameter, "Светлота Lab L должна быть от 0 до 100.");
        }
    }
}
