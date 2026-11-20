using System;
using System.IO;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class StickerPrintPlateSettings
    {
        private readonly string _path;
        public int WhiteTint { get; private set; } = 100;
        public int ClearTint { get; private set; } = 100;

        internal StickerPrintPlateSettings(string path = null)
        {
            _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "sticker-print-plates.json");
            try
            {
                if (!File.Exists(_path)) return;
                var saved = new JavaScriptSerializer().Deserialize<Saved>(File.ReadAllText(_path));
                if (saved == null) return;
                if (saved.WhiteTint >= 10 && saved.WhiteTint <= 100) WhiteTint = saved.WhiteTint;
                if (saved.ClearTint >= 10 && saved.ClearTint <= 100) ClearTint = saved.ClearTint;
            }
            catch (Exception error) { Log.Error("Could not load WHITE/CLEAR fill densities.", error); }
        }

        internal int Tint(StickerPrintPlateKind kind) => kind == StickerPrintPlateKind.White ? WhiteTint : ClearTint;

        internal void SetTint(StickerPrintPlateKind kind, int value)
        {
            StickerPrintPlateModel.ValidateTint(value);
            if (Tint(kind) == value) return;
            var next = new Saved { WhiteTint = kind == StickerPrintPlateKind.White ? value : WhiteTint,
                ClearTint = kind == StickerPrintPlateKind.Clear ? value : ClearTint };
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(next));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                WhiteTint = next.WhiteTint; ClearTint = next.ClearTint;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public sealed class Saved
        {
            public int WhiteTint { get; set; } = 100;
            public int ClearTint { get; set; } = 100;
        }
    }
}
