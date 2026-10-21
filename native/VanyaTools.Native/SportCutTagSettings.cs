using System;
using System.Globalization;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class SportCutTagSettings
    {
        public double WidthMm = 24;
        public double HeightMm = 12;
        public double BridgeMm = 2;
        public double LetterHeightMm = 8;
        public double FirstOffsetMm = 10;
        public double SizeStepMm = 20;
        public bool CutLabel = true;
        public string LabelHex = "#000000";

        public void Validate()
        {
            Check(WidthMm, 12, 150, "Ширина язычка");
            Check(HeightMm, 12, 50, "Высота язычка");
            Check(BridgeMm, .3, 5, "Длина каждого мостика");
            Check(LetterHeightMm, 3, 40, "Высота буквы");
            Check(FirstOffsetMm, 0, 500, "Отступ первого размера");
            Check(SizeStepMm, 0, 100, "Шаг следующих размеров");
            if (CutLabel && LetterHeightMm > HeightMm - 2)
                throw new InvalidOperationException("Высота язычка должна быть хотя бы на 2 мм больше высоты буквы.");
            if (CutLabel) LabelColor();
        }

        internal Color LabelColor()
        {
            if (!TryLabelColor(LabelHex, out Color color))
                throw new InvalidOperationException("Цвет подписи язычка должен быть в формате #RRGGBB.");
            return color;
        }

        internal static bool TryLabelColor(string value, out Color color)
        {
            color = Colors.Black;
            string hex = (value ?? "").Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal)) hex = hex.Substring(1);
            if (hex.Length != 6 || !Int32.TryParse(hex, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out int rgb)) return false;
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        private static void Check(double value, double min, double max, string label)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < min || value > max)
                throw new InvalidOperationException(label + ", мм: от " + min + " до " + max + ".");
        }
    }
}
