using System;
using System.Collections.Generic;
using System.Linq;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool ok, string name)
    { if (!ok) throw new InvalidOperationException(name); _checks++; }

    private static int Main()
    {
        try
        {
            ExactBounds(); NativeAdapter(); Failures();
            Console.WriteLine("Passed " + _checks + " monochrome trim checks. Managed predicates and fake native adapter; no Corel process.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void ExactBounds()
    {
        var random = new Random(2749);
        for (int index = 0; index < 250; index++)
        {
            int w = random.Next(1, 25), h = random.Next(1, 25);
            var points = new HashSet<int>();
            for (int pixel = 0; pixel < w * h; pixel++) if (random.Next(10) == 0) points.Add(pixel);
            MonochromeBitmapTrim.Bounds box;
            bool found = MonochromeBitmapTrim.FindBounds(w, h, (x, y) => points.Contains(y * w + x), out box);
            Check(found == (points.Count > 0), "Empty/nonempty classification");
            if (found) Check(box.Left == points.Min(p => p % w) && box.Right == points.Max(p => p % w) &&
                box.Bottom == points.Min(p => p / w) && box.Top == points.Max(p => p / w), "Exact bounds match exhaustive oracle");
        }
        MonochromeBitmapTrim.Bounds isolated;
        Check(MonochromeBitmapTrim.FindBounds(13, 11, (x, y) => x == 10 && y == 8, out isolated) &&
            isolated.Left == 10 && isolated.Right == 10 && isolated.Bottom == 8 && isolated.Top == 8,
            "Single pixel on non-byte-aligned image survives without coarse sampling");
        int reads = 0;
        Check(MonochromeBitmapTrim.FindBounds(10000, 10000, (x, y) => { reads++; return true; }, out isolated) && reads == 4,
            "Occupied boundary exits without scanning the interior");
        Check(!MonochromeBitmapTrim.Visible(true, false, true, 255) && MonochromeBitmapTrim.Visible(false, false, true, 255),
            "No-fill background hides only original white samples");
        Check(MonochromeBitmapTrim.Visible(true, true, false, 255) && !MonochromeBitmapTrim.Visible(false, true, false, 255),
            "No-outline foreground hides only original black samples");
        Check(!MonochromeBitmapTrim.Visible(false, true, true, 10) && MonochromeBitmapTrim.Visible(false, true, true, 11),
            "Native alpha coverage threshold matches ordinary trim");
    }

    private static FakeShape Shape(bool whiteVisible = false, bool blackVisible = true)
    {
        var image = new FakeImage(13, 11, 255);
        for (int y = 2; y <= 6; y++) for (int x = 3; x <= 7; x++) image.Values[y * 13 + x] = 0;
        image.Values[8 * 13 + 10] = 0;
        return new FakeShape(image, whiteVisible, blackVisible);
    }

    private static TrimResult Run(FakeShape shape, TrimMode mode = TrimMode.TransparentPixels,
        TrimSides sides = TrimSides.All, int padding = 0)
    {
        CorelApp.Application = new FakeApp(shape);
        return new BitmapTrimService().TrimSelected(mode, sides, padding);
    }

    private static void NativeAdapter()
    {
        var shape = Shape();
        var original = (byte[])shape.Bitmap.Image.Values.Clone();
        Check(!shape.Bitmap.Transparent && shape.Bitmap.ImageAlpha == null, "Synthetic palette transparency has no actual alpha");
        var result = Run(shape);
        Check(result.Trimmed == 1 && shape.Bitmap.CropCalls == 1, "One-bit no-fill background is cropped despite Transparent=false");
        Check(shape.Bitmap.Nodes[0].PositionX == 103 && shape.Bitmap.Nodes[0].PositionY == 202 &&
            shape.Bitmap.Nodes[1].PositionX == 111 && shape.Bitmap.Nodes[1].PositionY == 209,
            "Native CropEnvelope placement uses exact bottom-origin inclusive pixel bounds");
        Check(shape.Bitmap.Mode == 0 && shape.Bitmap.ResolutionX == 600 && shape.Bitmap.ResolutionY == 300 &&
            shape.Bitmap.Image.Values.SequenceEqual(original), "Crop keeps original 1-bit samples and DPI");
        Check(shape.Fill.Type == 0 && shape.Outline.Type == 1 && CorelApp.Application.ActiveDocument.ReferencePoint == 9,
            "Monochrome paints and document reference are restored/preserved");

        shape = Shape(); Run(shape, padding: 2, sides: TrimSides.Left | TrimSides.Top);
        Check(shape.Bitmap.Nodes[0].PositionX == 101 && shape.Bitmap.Nodes[1].PositionX == 113 &&
            shape.Bitmap.Nodes[0].PositionY == 200 && shape.Bitmap.Nodes[1].PositionY == 211,
            "Padding clamps to image and unchecked sides retain original extent");

        shape = Shape(); shape.RotationAngle = 90; Run(shape);
        Check(shape.RotationAngle == 90, "Temporary rotation is restored after native crop");

        shape = Shape(true, true); result = Run(shape);
        Check(result.Skipped == 1 && shape.Bitmap.CropCalls == 0 && shape.Bitmap.Image.Reads == 0,
            "Fully painted one-bit bitmap is not cropped as transparent");
        result = Run(shape, TrimMode.TopLeftColor);
        Check(result.Trimmed == 1 && shape.Bitmap.Nodes[0].PositionX == 103,
            "Explicit corner color can crop an opaque one-bit background");

        shape = Shape(false, false); result = Run(shape);
        Check(result.Skipped == 1 && shape.Bitmap.Image.Reads == 0, "Two hidden paints produce no crop and no scan");

        shape = Shape(); shape.Bitmap.Transparent = true; shape.Bitmap.ImageAlpha = new FakeImage(13, 11, 0);
        shape.Bitmap.ImageAlpha.Values[4 * 13 + 5] = 255;
        result = Run(shape);
        Check(result.Trimmed == 1 && shape.Bitmap.Nodes[0].PositionX == 105 && shape.Bitmap.Nodes[1].PositionX == 106 &&
            shape.Bitmap.Nodes[0].PositionY == 204 && shape.Bitmap.Nodes[1].PositionY == 205,
            "Actual alpha intersects the visible monochrome paint mask");

        shape = Shape(); shape.Bitmap.Transparent = true; shape.Bitmap.ImageAlpha = new FakeImage(13, 11, 0);
        result = Run(shape);
        Check(result.Skipped == 1 && shape.Bitmap.CropCalls == 0, "Fully transparent mono image remains intact");

        shape = Shape(true, false);
        for (int pixel = 0; pixel < shape.Bitmap.Image.Values.Length; pixel++)
            shape.Bitmap.Image.Values[pixel] = (byte)(255 - shape.Bitmap.Image.Values[pixel]);
        result = Run(shape);
        Check(result.Trimmed == 1 && shape.Bitmap.Nodes[0].PositionX == 103,
            "No-outline black background can be cropped around visible white artwork");
    }

    private static void Failures()
    {
        var shape = Shape(); shape.Bitmap.Image.ThrowRead = true; shape.RotationAngle = 45;
        bool failed = false;
        try { Run(shape); } catch (InvalidOperationException) { failed = true; }
        Check(failed && shape.Bitmap.CropCalls == 0 && shape.RotationAngle == 45,
            "Unreadable source fails before crop and restores rotation");
        Check(!CorelApp.Application.Optimization && CorelApp.Application.ActiveDocument.Ended,
            "Failure closes command group and reenables refresh");
        shape = Shape(); shape.Bitmap.Transparent = true; failed = false;
        try { Run(shape); } catch (InvalidOperationException) { failed = true; }
        Check(failed && shape.Bitmap.CropCalls == 0, "Missing real alpha cannot silently become opaque");
        shape = Shape(); shape.Bitmap.Image.ColoredPixel = true; failed = false;
        try { Run(shape); } catch (InvalidOperationException) { failed = true; }
        Check(failed && shape.Bitmap.CropCalls == 0, "Painted RGB cannot masquerade as the native one-bit mask");
    }
}

// Only the narrow native contract used by BitmapTrimService is simulated here.
public sealed class FakePaint { public int Type { get; set; } }
public sealed class FakePixel
{
    public int RGBRed { get; set; }
    public int RGBGreen { get; set; }
    public int RGBBlue { get; set; }
    public int Gray { get; set; }
}
public sealed class FakeImage
{
    public FakeImage(int width, int height, byte value)
    { Width = width; Height = height; Values = Enumerable.Repeat(value, width * height).ToArray(); }
    public int Width { get; }
    public int Height { get; }
    public byte[] Values { get; }
    public int Reads { get; private set; }
    public bool ThrowRead { get; set; }
    public bool ColoredPixel { get; set; }
    public FakeImage Pixel => this;
    public FakePixel this[int x, int y]
    {
        get
        {
            Reads++; if (ThrowRead) throw new InvalidOperationException("Synthetic source failure");
            int value = Values[y * Width + x];
            return new FakePixel { Gray = value, RGBRed = value, RGBGreen = ColoredPixel ? 50 : value, RGBBlue = value };
        }
    }
}
public sealed class FakeNode { public double PositionX { get; set; } public double PositionY { get; set; } }
public sealed class FakeBitmap
{
    public FakeBitmap(FakeImage image) { Image = image; }
    public int Mode => 0;
    public double ResolutionX => 600;
    public double ResolutionY => 300;
    public bool Transparent { get; set; }
    public FakeImage Image { get; }
    public FakeImage ImageAlpha { get; set; }
    public List<FakeNode> Nodes { get; } = new List<FakeNode>();
    public FakeBitmap CropEnvelope => this;
    public bool Cropped => Nodes[0].PositionX != 100 || Nodes[0].PositionY != 200 ||
        Nodes[1].PositionX != 100 + Image.Width || Nodes[1].PositionY != 200 + Image.Height;
    public int CropCalls { get; private set; }
    public void ResetCropEnvelope()
    {
        Nodes.Clear(); Nodes.Add(new FakeNode { PositionX = 100, PositionY = 200 });
        Nodes.Add(new FakeNode { PositionX = 100 + Image.Width, PositionY = 200 + Image.Height });
    }
    public void Crop() { CropCalls++; }
}
public sealed class FakeShape
{
    public FakeShape(FakeImage image, bool white, bool black)
    { Bitmap = new FakeBitmap(image); Fill = new FakePaint { Type = white ? 1 : 0 }; Outline = new FakePaint { Type = black ? 1 : 0 }; }
    public int Type => CorelConstants.CdrBitmapShape;
    public double RotationAngle { get; set; }
    public FakeBitmap Bitmap { get; }
    public FakePaint Fill { get; }
    public FakePaint Outline { get; }
    public void GetBoundingBox(ref double x, ref double y, ref double width, ref double height, bool outline)
    { x = 100; y = 200; width = Bitmap.Image.Width; height = Bitmap.Image.Height; }
}
public sealed class FakeSelection
{
    public FakeSelection(FakeShape shape) { Shapes = new[] { shape }; }
    public int Count => Shapes.Length;
    public FakeShape[] Shapes { get; }
}
public sealed class FakeDocument
{
    public int ReferencePoint { get; set; } = 9;
    public bool Ended { get; private set; }
    public void BeginCommandGroup(string name) { }
    public void EndCommandGroup() { Ended = true; }
}
public sealed class FakeWindow { public void Refresh() { } }
public sealed class FakeApp
{
    public FakeApp(FakeShape shape) { ActiveSelectionRange = new FakeSelection(shape); }
    public FakeDocument ActiveDocument { get; } = new FakeDocument();
    public FakeSelection ActiveSelectionRange { get; }
    public FakeWindow ActiveWindow { get; } = new FakeWindow();
    public bool Optimization { get; set; }
}
namespace VanyaTools.Native
{
    internal static class CorelApp { internal static FakeApp Application; internal static dynamic Get() => Application; }
    internal static class CorelConstants { internal const int CdrBitmapShape = 5, CdrBottomLeft = 1; }
    internal static class Log { internal static void Info(string text) { } internal static void Error(string text, Exception error) { } }
}
