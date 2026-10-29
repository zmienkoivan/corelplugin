using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static readonly byte[] Alpha = { 255, 0, 128, 255, 64, 255, 1, 200 };

    [STAThread]
    private static int Main()
    {
        try
        {
            ValidPixels(); CoverageErrors(); FormatErrors();
            Console.WriteLine("Native bitmap pixel checks passed: " + _checks + ". No Corel, files or network.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }

    private static void Reject(FakeBitmap bitmap, string part)
    {
        try { NativeBitmapPixelReader.Read(bitmap, 300, 100, true); }
        catch (InvalidOperationException error)
        { Check(error.Message.Contains(part), "Wrong error: " + error.Message); return; }
        throw new InvalidOperationException("Expected failure containing " + part);
    }

    private static FakeTile Tile(int left, int bottom, int width, int height, bool alpha = false, int padding = 0)
    {
        int bpp = alpha ? 1 : 3, stride = width * bpp + padding;
        var data = new byte[stride * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int global = (bottom + y) * 4 + left + x, index = y * stride + x * bpp;
            if (alpha) data[index] = Alpha[global];
            else { data[index] = (byte)(10 + global); data[index + 1] = (byte)(40 + global); data[index + 2] = (byte)(70 + global); }
        }
        return new FakeTile { Left = left, Top = bottom + height - 1, Width = width, Height = height,
            BytesPerLine = stride, BytesPerPixel = bpp, PixelData = data };
    }

    private static FakeImage Color(params FakeTile[] tiles) => new FakeImage(4, 2, tiles.Length == 0 ? new[] { Tile(0, 0, 4, 2) } : tiles);
    private static FakeImage Mask(params FakeTile[] tiles) => new FakeImage(4, 2, tiles.Length == 0 ? new[] { Tile(0, 0, 4, 2, true) } : tiles);
    private static byte[] Read(FakeBitmap bitmap, bool white = false)
    {
        BitmapSource source = NativeBitmapPixelReader.Read(bitmap, 300, 100, white);
        Check(source.PixelWidth == 4 && source.PixelHeight == 2 && source.DpiX == 300 && source.DpiY == 300,
            "Raster dimensions/DPI changed");
        Check(source.IsFrozen && source.Format == PixelFormats.Bgra32, "Raster must be frozen BGRA32");
        byte[] data = new byte[32]; source.CopyPixels(data, 16, 0); return data;
    }

    private static byte[] Expected(bool transparent, bool white)
    {
        // A hand-defined top-down oracle; Corel's source buffers are bottom-up.
        var bytes = new byte[32];
        for (int y = 0; y < 2; y++) for (int x = 0; x < 4; x++)
        {
            int global = (1 - y) * 4 + x, index = (y * 4 + x) * 4;
            int alpha = transparent ? Alpha[global] : 255;
            for (int component = 0; component < 3; component++)
            {
                int color = 10 + component * 30 + global;
                bytes[index + component] = white ? (byte)((color * alpha + 255 * (255 - alpha) + 127) / 255) : (byte)color;
            }
            bytes[index + 3] = white ? (byte)255 : (byte)alpha;
        }
        return bytes;
    }

    private static void ValidPixels()
    {
        Check(Read(new FakeBitmap(Color())).SequenceEqual(Expected(false, false)), "Opaque bottom-up pixel ordering changed");
        Check(Read(new FakeBitmap(Color(), Mask())).SequenceEqual(Expected(true, false)), "Transparent pixel colors or alpha changed");
        Check(Read(new FakeBitmap(Color(), Mask()), true).SequenceEqual(Expected(true, true)), "White compositing changed");
        var colored = Color(Tile(2, 0, 2, 2, false, 2), Tile(0, 1, 2, 1, false, 1), Tile(0, 0, 2, 1, false, 3));
        var mask = Mask(Tile(0, 1, 4, 1, true, 2), Tile(0, 0, 1, 1, true), Tile(1, 0, 3, 1, true, 1));
        Check(Read(new FakeBitmap(colored, mask)).SequenceEqual(Expected(true, false)), "Unaligned independent tile partitions or row padding changed pixels");
        Check(Read(new FakeBitmap(colored, mask), true).SequenceEqual(Expected(true, true)), "Multi-tile white image differs from single-tile result");
        Check(Read(new FakeBitmap(Color(Tile(0, 0, 4, 2), Tile(0, 0, 2, 2)))).SequenceEqual(Expected(false, false)), "Consistent overlapping tiles must remain readable");
        var noFinalPadding = Tile(0, 0, 4, 2, false, 3);
        noFinalPadding.PixelData = noFinalPadding.PixelData.Take(noFinalPadding.BytesPerLine + 12).ToArray();
        Check(Read(new FakeBitmap(Color(noFinalPadding))).SequenceEqual(Expected(false, false)), "Unused final-row padding should not be required");
        var rgba = Tile(0, 0, 4, 2);
        var paddedPixels = new byte[32];
        for (int pixel = 0; pixel < 8; pixel++) Array.Copy(rgba.PixelData, pixel * 3, paddedPixels, pixel * 4, 3);
        rgba.BytesPerPixel = 4; rgba.BytesPerLine = 16; rgba.PixelData = paddedPixels;
        Check(Read(new FakeBitmap(Color(rgba))).SequenceEqual(Expected(false, false)), "RGB tile padding byte affected colors");
    }

    private static void CoverageErrors()
    {
        Reject(new FakeBitmap(Color(Tile(0, 0, 2, 2))), "цветового изображения");
        Reject(new FakeBitmap(Color(), Mask(Tile(0, 0, 2, 2, true))), "альфа-канала");
        Reject(new FakeBitmap(Color(Tile(0, 0, 4, 1))), "Y=1…1");
        Reject(new FakeBitmap(Color(Tile(0, 1, 4, 1))), "Y=0…0");
        Reject(new FakeBitmap(Color(Tile(0, 0, 1, 2), Tile(2, 0, 2, 2))), "X=1…1");
        Reject(new FakeBitmap(Color(Tile(0, 0, 2, 2), Tile(0, 0, 2, 2))), "X=2…3");
        var tooWide = Tile(0, 0, 4, 2); tooWide.Left = 1;
        Reject(new FakeBitmap(Color(tooWide)), "границы");
        var tooHigh = Tile(0, 0, 4, 2); tooHigh.Top = 2;
        Reject(new FakeBitmap(Color(tooHigh)), "границы");
        var negative = Tile(0, 0, 4, 2); negative.Left = -1;
        Reject(new FakeBitmap(Color(negative)), "границы");
        var underflow = Tile(0, 0, 4, 2); underflow.Top = 0;
        Reject(new FakeBitmap(Color(underflow)), "границы");
    }

    private static void FormatErrors()
    {
        Reject(new FakeBitmap(Color(), new FakeImage(2, 2, Tile(0, 0, 2, 2, true))), "не совпадает");
        var noAlpha = new FakeBitmap(Color()) { Transparent = true };
        Reject(noAlpha, "не вернул альфа-канал");
        var empty = new FakeImage(4, 2, new FakeTile[0]);
        Reject(new FakeBitmap(empty), "количество тайлов: 0");
        var largeCount = new FakeImage(4, 2, Tile(0, 0, 4, 2)); largeCount.Tiles.CountOverride = 100001;
        Reject(new FakeBitmap(largeCount), "количество тайлов: 100001");
        var negativeStride = Tile(0, 0, 4, 2); negativeStride.BytesPerLine = -12;
        Reject(new FakeBitmap(Color(negativeStride)), "отрицательный шаг");
        var shortStride = Tile(0, 0, 4, 2); shortStride.BytesPerLine = 11;
        Reject(new FakeBitmap(Color(shortStride)), "формат пикселей");
        var shortBuffer = Tile(0, 0, 4, 2); shortBuffer.PixelData = new byte[23];
        Reject(new FakeBitmap(Color(shortBuffer)), "буфер пикселей неполный");
        var shortAlpha = Tile(0, 0, 4, 2, true); shortAlpha.PixelData = new byte[7];
        Reject(new FakeBitmap(Color(), Mask(shortAlpha)), "буфер пикселей неполный");
        var nullBuffer = Tile(0, 0, 4, 2); nullBuffer.PixelData = null;
        Reject(new FakeBitmap(Color(nullBuffer)), "буфер пикселей неполный");
        var tinyBpp = Tile(0, 0, 4, 2); tinyBpp.BytesPerPixel = 1;
        Reject(new FakeBitmap(Color(tinyBpp)), "формат пикселей");
        var zeroWidth = Tile(0, 0, 4, 2); zeroWidth.Width = 0;
        Reject(new FakeBitmap(Color(zeroWidth)), "границы");
        Reject(new FakeBitmap(new FakeImage(Int32.MaxValue, Int32.MaxValue, new FakeTile[0])), "Размер выделения");
    }
}

public sealed class FakeBitmap
{
    public FakeBitmap(FakeImage image, FakeImage alpha = null) { Image = image; ImageAlpha = alpha; Transparent = alpha != null; }
    public FakeImage Image { get; }
    public FakeImage ImageAlpha { get; }
    public bool Transparent { get; set; }
}
public sealed class FakeImage
{
    public FakeImage(int width, int height, params FakeTile[] tiles) { Width = width; Height = height; Tiles = new FakeTiles(tiles); }
    public int Width { get; }
    public int Height { get; }
    public FakeTiles Tiles { get; }
}
public sealed class FakeTiles
{
    public FakeTiles(FakeTile[] tiles) { Item = new FakeTileIndex(tiles); _count = tiles.Length; }
    private readonly int _count;
    public int? CountOverride;
    public int Count => CountOverride ?? _count;
    public FakeTileIndex Item { get; }
}
public sealed class FakeTileIndex
{
    private readonly FakeTile[] _tiles;
    public FakeTileIndex(FakeTile[] tiles) { _tiles = tiles; }
    public FakeTile this[int index] => _tiles[index - 1];
}
public sealed class FakeTile
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BytesPerLine { get; set; }
    public int BytesPerPixel { get; set; }
    public byte[] PixelData { get; set; }
}
