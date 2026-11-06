using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class DtfCorrectionPreset
    {
        private const string FormatName = "VanyaTools.Correction";
        private const int MaximumBytes = 65536;
        public string Format { get; set; }
        public int Version { get; set; }
        public DtfCorrectionPresetValues Settings { get; set; }

        internal static DtfCorrectionPreset FromOptions(DtfCorrectionOptions value) => new DtfCorrectionPreset {
            Format = FormatName, Version = 3, Settings = new DtfCorrectionPresetValues {
                RemoveSemiTransparent = value.RemoveSemiTransparent, Threshold = value.Threshold, SmoothStairs = value.SmoothStairs,
                WhitenNearWhite = value.WhitenNearWhite, NearWhiteTolerance = value.NearWhiteTolerance,
                Outline = value.Outline, RadiusMm = value.RadiusMm, ExpansionMm = value.ExpansionMm, OutlineColor = Hex(value.Color),
                Knockout = value.Knockout, KnockoutColor = Hex(value.KnockoutColor), KnockoutTolerance = value.KnockoutTolerance,
                KnockoutSoft = value.KnockoutSoft, HalftoneShape = (int)value.HalftoneShape, HalftoneColorBoost = value.HalftoneColorBoost,
                HalftoneRemoveSmall=value.HalftoneRemoveSmall,HalftoneRemoveMm=value.HalftoneRemoveMm,HalftoneGrowSmall=value.HalftoneGrowSmall,HalftoneGrowMm=value.HalftoneGrowMm,
                Halftone = value.Halftone, HalftoneAlphaOnly = value.HalftoneAlphaOnly, HalftoneAutoDot = value.HalftoneAutoDot,
                HalftoneLpi = value.HalftoneLpi, HalftoneAngle = value.HalftoneAngle, HalftoneDotMm = value.HalftoneDotMm } };

        internal DtfCorrectionOptions ToOptions()
        {
            if (!String.Equals(Format, FormatName, StringComparison.Ordinal) || Version<1 || Version>3 || Settings == null)
                throw new InvalidDataException("Это не поддерживаемый пресет коррекции Vanya Tools.");
            var s = Settings;
            if (s.Threshold < 0 || s.Threshold > 255 || s.NearWhiteTolerance < 0 || s.NearWhiteTolerance > 64 ||
                s.KnockoutTolerance < (Version>=3 && s.KnockoutSoft ? -255 : 0) || s.KnockoutTolerance > 255 || !Range(s.RadiusMm,0,50) || !Range(s.ExpansionMm,0,50) ||
                !Range(s.HalftoneRemoveMm,.05,5) || !Range(s.HalftoneGrowMm,.05,5) ||
                !Range(s.HalftoneLpi,.5,200) || !Range(s.HalftoneAngle,-180,180) ||
                !Range(s.HalftoneDotMm,.01,s.HalftoneAutoDot ? 1000 : 250) ||
                !Enum.IsDefined(typeof(DtfDotShape),s.HalftoneShape) || s.HalftoneColorBoost<0 || s.HalftoneColorBoost>100)
                throw new InvalidDataException("В пресете есть недопустимые значения коррекции.");
            var value = new DtfCorrectionOptions { RemoveSemiTransparent=s.RemoveSemiTransparent, Threshold=s.Threshold,
                SmoothStairs=s.SmoothStairs, WhitenNearWhite=s.WhitenNearWhite, NearWhiteTolerance=s.NearWhiteTolerance,
                Outline=s.Outline, RadiusMm=s.RadiusMm, ExpansionMm=s.ExpansionMm, Color=Color(s.OutlineColor),
                Knockout=s.Knockout, KnockoutColor=Color(s.KnockoutColor), KnockoutTolerance=s.KnockoutTolerance,
                KnockoutSoft=Version>=2 && s.KnockoutSoft, HalftoneShape=Version>=2 ? (DtfDotShape)s.HalftoneShape : DtfDotShape.Circle,
                HalftoneColorBoost=Version>=2 ? s.HalftoneColorBoost : 0,
                HalftoneRemoveSmall=Version>=3 && s.HalftoneRemoveSmall,HalftoneRemoveMm=s.HalftoneRemoveMm,
                HalftoneGrowSmall=Version>=3 && s.HalftoneGrowSmall,HalftoneGrowMm=s.HalftoneGrowMm,
                Halftone=s.Halftone, HalftoneAlphaOnly=s.HalftoneAlphaOnly, HalftoneAutoDot=s.HalftoneAutoDot,
                HalftoneLpi=s.HalftoneLpi, HalftoneAngle=s.HalftoneAngle, HalftoneDotMm=s.HalftoneDotMm };
            value.Preparation().Validate(); return value;
        }
        internal static void Save(string path, DtfCorrectionPreset value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            value.ToOptions();
            string json = Serializer().Serialize(value);
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Пресет слишком большой.");
            path = Path.GetFullPath(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { file.Write(bytes,0,bytes.Length); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static DtfCorrectionPreset Load(string path)
        {
            string json;
            using (var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if (file.Length < 2 || file.Length > MaximumBytes) throw new InvalidDataException("Некорректный размер файла пресета.");
                var bytes = new byte[(int)file.Length]; int read = 0;
                while (read < bytes.Length) { int next = file.Read(bytes,read,bytes.Length-read); if(next==0) throw new EndOfStreamException(); read+=next; }
                json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            }
            DtfCorrectionPreset result;
            try { result = Serializer().Deserialize<DtfCorrectionPreset>(json); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is FormatException || error is OverflowException)
            { throw new InvalidDataException("Не удалось прочитать JSON пресета коррекции.", error); }
            if (result == null) throw new InvalidDataException("Файл пресета пуст.");
            result.ToOptions(); return result;
        }
        private static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 8 };
        private static bool Range(double value,double min,double max) => !Double.IsNaN(value) && !Double.IsInfinity(value) && value>=min && value<=max;
        private static string Hex(Color value) => String.Format(CultureInfo.InvariantCulture,"#{0:X2}{1:X2}{2:X2}",value.R,value.G,value.B);
        private static Color Color(string value)
        {
            if (value == null || value.Length != 7 || value[0]!='#' || !UInt32.TryParse(value.Substring(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint rgb))
                throw new InvalidDataException("В пресете указан некорректный цвет.");
            return System.Windows.Media.Color.FromRgb((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb);
        }
    }
    internal sealed class DtfCorrectionPresetValues
    {
        public bool RemoveSemiTransparent { get; set; }
        public int Threshold { get; set; } = 130;
        public bool SmoothStairs { get; set; }
        public bool WhitenNearWhite { get; set; }
        public int NearWhiteTolerance { get; set; } = 6;
        public bool Outline { get; set; }
        public double RadiusMm { get; set; } = .127;
        public double ExpansionMm { get; set; } = .175;
        public string OutlineColor { get; set; } = "#FFFFFF";
        public bool Knockout { get; set; }
        public string KnockoutColor { get; set; } = "#000000";
        public int KnockoutTolerance { get; set; } = 10;
        public bool KnockoutSoft { get; set; }
        public int HalftoneShape { get; set; }
        public int HalftoneColorBoost { get; set; }
        public bool HalftoneRemoveSmall {get;set;}
        public bool HalftoneGrowSmall {get;set;}
        public double HalftoneRemoveMm {get;set;}=.3;
        public double HalftoneGrowMm {get;set;}=.5;
        public bool Halftone { get; set; }
        public bool HalftoneAlphaOnly { get; set; } = true;
        public bool HalftoneAutoDot { get; set; } = true;
        public double HalftoneLpi { get; set; } = 20;
        public double HalftoneAngle { get; set; } = 22.5;
        public double HalftoneDotMm { get; set; } = 1;
    }
}
