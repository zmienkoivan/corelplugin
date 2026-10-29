using System;
using System.Threading;

namespace VanyaTools.Native
{
    // Owned managed planes, top-to-bottom rows, with no COM or WPF objects.
    // CMYK bytes deliberately retain Corel's native channel order. Channel-wise
    // filters need not guess that order, and model-preserving writes round-trip it.
    internal sealed class NativeRasterBuffer
    {
        internal readonly int Width, Height, Mode, Channels;
        internal readonly double DpiX, DpiY;
        internal readonly byte[] Colors, Alpha;
        internal readonly int[] CmykOrder;
        internal byte[] ColorBytes => Colors;
        internal byte[] AlphaBytes => Alpha;

        internal NativeRasterBuffer(int width, int height, int mode, double dpiX, double dpiY,
            byte[] colors, byte[] alpha, int[] cmykOrder = null)
        {
            int channels = ChannelCount(mode);
            if (width <= 0 || height <= 0 || (long)width * height > Int32.MaxValue / channels ||
                Double.IsNaN(dpiX) || Double.IsInfinity(dpiX) || dpiX <= 0 ||
                Double.IsNaN(dpiY) || Double.IsInfinity(dpiY) || dpiY <= 0)
                throw new InvalidOperationException("Некорректные размеры или разрешение исходного растра.");
            int pixels = checked(width * height);
            if (colors == null || colors.Length != checked(pixels * channels) || alpha == null || alpha.Length != pixels)
                throw new InvalidOperationException("Неполные цветовые данные или альфа-канал исходного растра.");
            if (cmykOrder != null)
            {
                if (mode != 5 || cmykOrder.Length != 4) throw new InvalidOperationException("Некорректный порядок CMYK-каналов.");
                int seen = 0;
                foreach (int index in cmykOrder)
                {
                    if (index < 0 || index > 3 || (seen & (1 << index)) != 0) throw new InvalidOperationException("Некорректный порядок CMYK-каналов.");
                    seen |= 1 << index;
                }
            }
            Width = width; Height = height; Mode = mode; Channels = channels;
            DpiX = dpiX; DpiY = dpiY; Colors = colors; Alpha = alpha;
            CmykOrder = cmykOrder == null ? null : (int[])cmykOrder.Clone();
        }

        internal static int ChannelCount(int mode)
        {
            switch (mode)
            {
                case 2: return 1; // cdrGrayscaleImage
                case 4: return 3; // cdrRGBColorImage, Corel tile bytes are BGR
                case 5: return 4; // cdrCMYKColorImage, native byte order retained
                default: throw new InvalidOperationException("Для обработки без преобразования поддерживаются растры RGB, CMYK и Grayscale. Текущая модель: " + mode + ".");
            }
        }

        internal NativeRasterBuffer Copy() => new NativeRasterBuffer(Width, Height, Mode, DpiX, DpiY,
            (byte[])Colors.Clone(), (byte[])Alpha.Clone(), CmykOrder);

        internal NativeRasterBuffer WithCmykOrder(int[] order) =>
            new NativeRasterBuffer(Width, Height, Mode, DpiX, DpiY, Colors, Alpha, order);

        // The CMYK conversion here is an unprofiled screen preview. Printable
        // results keep their native CMYK channels, never these RGB preview bytes.
        internal byte[] ToPreviewBgra(CancellationToken cancellation = default(CancellationToken))
        {
            if (Mode == 5 && CmykOrder == null)
                throw new InvalidOperationException("Не определён порядок исходных CMYK-каналов для просмотра.");
            var result = new byte[checked(Width * Height * 4)];
            for (int pixel = 0; pixel < Alpha.Length; pixel++)
            {
                if ((pixel & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                int source = pixel * Channels, target = pixel * 4;
                if (Mode == 4)
                {
                    result[target] = Colors[source]; result[target + 1] = Colors[source + 1]; result[target + 2] = Colors[source + 2];
                }
                else if (Mode == 2) result[target] = result[target + 1] = result[target + 2] = Colors[source];
                else
                {
                    int black = Colors[source + CmykOrder[3]], light = 255 - black;
                    result[target + 2] = (byte)(((255 - Colors[source + CmykOrder[0]]) * light + 127) / 255);
                    result[target + 1] = (byte)(((255 - Colors[source + CmykOrder[1]]) * light + 127) / 255);
                    result[target] = (byte)(((255 - Colors[source + CmykOrder[2]]) * light + 127) / 255);
                }
                result[target + 3] = Alpha[pixel];
            }
            return result;
        }

        internal bool IsPureWhite(int pixel)
        {
            if (pixel < 0 || pixel >= Alpha.Length) throw new ArgumentOutOfRangeException(nameof(pixel));
            byte white = Mode == 5 ? (byte)0 : (byte)255;
            int offset = pixel * Channels;
            for (int channel = 0; channel < Channels; channel++)
                if (Colors[offset + channel] != white) return false;
            return true;
        }

        internal void SetPureWhite(int pixel)
        {
            if (pixel < 0 || pixel >= Alpha.Length) throw new ArgumentOutOfRangeException(nameof(pixel));
            byte white = Mode == 5 ? (byte)0 : (byte)255;
            int offset = pixel * Channels;
            for (int channel = 0; channel < Channels; channel++) Colors[offset + channel] = white;
        }
    }
}
