using System;
using System.IO;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal enum RasterInsertionMode
    {
        ReplaceOriginal = 0,
        MoveOriginalAside = 1,
        NewAside = 2
    }

    internal static class RasterInsertionSettings
    {
        private static readonly string PathName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "raster-insertion.json");
        private sealed class Saved { public RasterInsertionMode Mode { get; set; } = RasterInsertionMode.NewAside; }

        internal static RasterInsertionMode LoadMode()
        {
            try
            {
                if (File.Exists(PathName))
                {
                    var saved = new JavaScriptSerializer().Deserialize<Saved>(File.ReadAllText(PathName));
                    if (saved != null && Valid(saved.Mode)) return saved.Mode;
                }
            }
            catch (Exception error) { Log.Error("Could not read raster insertion preference.", error); }
            return RasterInsertionMode.NewAside;
        }

        internal static void SaveMode(RasterInsertionMode mode)
        {
            if (!Valid(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            Directory.CreateDirectory(Path.GetDirectoryName(PathName));
            File.WriteAllText(PathName, new JavaScriptSerializer().Serialize(new Saved { Mode = mode }));
        }

        internal static bool Valid(RasterInsertionMode mode) => mode == RasterInsertionMode.ReplaceOriginal ||
            mode == RasterInsertionMode.MoveOriginalAside || mode == RasterInsertionMode.NewAside;
    }
}
