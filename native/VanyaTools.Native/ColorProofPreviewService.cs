using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class ColorProofPreviewResult
    {
        public IReadOnlyList<int[]> Rgb { get; internal set; }
        public string ContextDescription { get; internal set; }
    }

    /// <summary>Explicit UI-thread ICC conversion. No document, source color or numeric recipe is modified.</summary>
    internal sealed class ColorProofPreviewService
    {
        private const int CdrColorCmyk = 2, CdrColorRgb = 5, BatchSize = 16, MaxColors = 10000;
        private sealed class Context
        {
            internal object App, Document, Source, SourceRgb, SourceCmyk, Srgb, SrgbProfile;
            internal int ThreadId;
            internal string GlobalSettings, Description;
        }

        public int[] Convert(bool cmyk, int[] values)
        {
            int[] recipe = Validate(values, cmyk);
            Context context = Capture(true);
            RequireSourceProfile(context, cmyk);
            int[] rgb = ToScreen(context, cmyk, recipe);
            RequireCurrent(context);
            return rgb;
        }

        public int[] ConvertNative(object nativeColor)
        {
            if (nativeColor == null) throw new ArgumentNullException(nameof(nativeColor));
            Context context = Capture(true);
            dynamic color = ((dynamic)context.App).CreateColor();
            color.CopyAssign((dynamic)nativeColor);
            int sourceType = (int)color.Type;
            if (sourceType == CdrColorCmyk) RequireSourceProfile(context, true);
            else if (sourceType == CdrColorRgb) RequireSourceProfile(context, false);
            int[] rgb = ConvertOwnToScreen(context, (object)color, sourceType == CdrColorRgb);
            RequireCurrent(context);
            return rgb;
        }

        public int[] ConvertComponents(bool sourceCmyk, int[] values, bool targetCmyk)
        {
            int[] recipe = Validate(values, sourceCmyk);
            if (sourceCmyk == targetCmyk) return recipe;
            Context context = Capture(false);
            RequireSourceProfile(context, sourceCmyk); RequireSourceProfile(context, targetCmyk);
            dynamic color = CreateOwnColor(context, sourceCmyk, recipe);
            // ConvertTo(colorType, destination context, source context), with both models in this CDR.
            bool converted = System.Convert.ToBoolean(color.ConvertTo(targetCmyk ? CdrColorCmyk : CdrColorRgb,
                (dynamic)context.Source, (dynamic)context.Source));
            if (!converted) throw new InvalidOperationException("Corel не смог пересчитать каналы по профилям текущего документа.");
            int[] result = ReadComponents((object)color, targetCmyk);
            RequireCurrent(context);
            return result;
        }

        public async Task<ColorProofPreviewResult> ConvertBatchAsync(bool cmyk, IReadOnlyList<int[]> values,
            CancellationToken cancellation = default(CancellationToken), Action<int, int> progress = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Count > MaxColors) throw new InvalidOperationException("Для предпросмотра допускается не больше " + MaxColors + " цветов за один запрос.");
            // Snapshot and validate every numeric recipe before the first COM call.
            var recipes = values.Select(value => Validate(value, cmyk)).ToArray();
            if (recipes.Length == 0) return new ColorProofPreviewResult { Rgb = Array.AsReadOnly(new int[0][]), ContextDescription = "Нет цветов для предпросмотра." };
            cancellation.ThrowIfCancellationRequested();
            Context context = Capture(true); RequireSourceProfile(context, cmyk);
            var output = new List<int[]>(recipes.Length);
            // This cache is intentionally request-local. The immutable CDR-context snapshot belongs to this request.
            var cache = new Dictionary<string, int[]>(StringComparer.Ordinal);
            for (int start = 0; start < recipes.Length; start += BatchSize)
            {
                cancellation.ThrowIfCancellationRequested(); RequireCurrent(context);
                int stop = Math.Min(recipes.Length, start + BatchSize);
                for (int index = start; index < stop; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string key = (cmyk ? "CMYK:" : "RGB:") + String.Join(",", recipes[index]);
                    if (!cache.TryGetValue(key, out int[] rgb))
                    { rgb = ToScreen(context, cmyk, recipes[index]); cache.Add(key, rgb); }
                    output.Add((int[])rgb.Clone());
                }
                progress?.Invoke(stop, recipes.Length);
                cancellation.ThrowIfCancellationRequested();
                if (stop < recipes.Length) await Dispatcher.Yield(DispatcherPriority.Background);
            }
            cancellation.ThrowIfCancellationRequested(); RequireCurrent(context); cancellation.ThrowIfCancellationRequested();
            return new ColorProofPreviewResult { Rgb = output.AsReadOnly(), ContextDescription = context.Description };
        }

        public string DescribeContext()
        {
            RequireSta();
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic context = doc.ColorContext;
            return Description((object)context, false);
        }

        private static Context Capture(bool screen)
        {
            RequireSta();
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            // Corel SDK: a copied context retains profiles and rendering intent without changing CDR settings.
            // https://community.coreldraw.com/sdk/api/draw/24/c/colorcontext?lang=cpp
            dynamic source = doc.ColorContext.GetCopy();
            if (source == null) throw new InvalidOperationException("Не удалось получить цветовой контекст текущего CDR.");
            var context = new Context { App = (object)app, Document = (object)doc, Source = (object)source,
                SourceRgb = (object)source.RGBColorProfile, SourceCmyk = (object)source.CMYKColorProfile,
                ThreadId = Thread.CurrentThread.ManagedThreadId, GlobalSettings = GlobalSignature((object)app),
                Description = Description((object)source, screen) };
            if (screen)
            {
                object srgb = FindSrgb((object)app);
                // SDK: ColorProfile.CreateColorContext(RenderingIntent) creates a destination context.
                // https://community.coreldraw.com/sdk/api/draw/25/m/colorprofile.createcolorcontext?lang=cli
                context.SrgbProfile = srgb;
                context.Srgb = (object)((dynamic)srgb).CreateColorContext(source.RenderingIntent);
                if (context.Srgb == null) throw new InvalidOperationException("Corel не смог создать контекст sRGB для предпросмотра.");
            }
            RequireCurrent(context);
            return context;
        }

        private static int[] ToScreen(Context context, bool cmyk, int[] values)
            => ConvertOwnToScreen(context, CreateOwnColor(context, cmyk, values), !cmyk);

        private static int[] ConvertOwnToScreen(Context context, object colorObject, bool sourceWasRgb)
        {
            dynamic color = colorObject;
            // Destination must be sRGB: WPF Color.FromRgb does not interpret Adobe RGB channel values.
            // https://community.coreldraw.com/sdk/api/draw/18/m/color.convertto
            bool converted = System.Convert.ToBoolean(color.ConvertTo(CdrColorRgb, (dynamic)context.Srgb, (dynamic)context.Source));
            if (!converted)
            {
                bool sameRgb = sourceWasRgb && context.SourceRgb != null &&
                    System.Convert.ToBoolean(((dynamic)context.SourceRgb).IsSame((dynamic)context.SrgbProfile));
                if (!sameRgb) throw new InvalidOperationException("Corel не смог преобразовать цвет из профиля документа в sRGB. Предпросмотр не обновлён.");
            }
            return ReadComponents((object)color, false);
        }

        private static object CreateOwnColor(Context context, bool cmyk, int[] values)
        {
            dynamic app = context.App;
            dynamic original = cmyk ? app.CreateCMYKColor(values[0], values[1], values[2], values[3])
                : app.CreateRGBColor(values[0], values[1], values[2]);
            dynamic converted = app.CreateColor(); converted.CopyAssign(original);
            return (object)converted;
        }
        private static int[] ReadComponents(object colorObject, bool cmyk)
        {
            dynamic color = colorObject;
            var values = cmyk ? new int[] { (int)color.CMYKCyan, (int)color.CMYKMagenta, (int)color.CMYKYellow, (int)color.CMYKBlack }
                : new int[] { (int)color.RGBRed, (int)color.RGBGreen, (int)color.RGBBlue };
            return Validate(values, cmyk);
        }
        private static int[] Validate(int[] values, bool cmyk)
        {
            int channels = cmyk ? 4 : 3, maximum = cmyk ? 100 : 255;
            if (values == null || values.Length != channels || values.Any(value => value < 0 || value > maximum))
                throw new InvalidOperationException("Для " + (cmyk ? "CMYK нужны четыре канала 0–100." : "RGB нужны три канала 0–255."));
            return (int[])values.Clone();
        }
        private static void RequireSourceProfile(Context context, bool cmyk)
        {
            if ((cmyk ? context.SourceCmyk : context.SourceRgb) == null)
                throw new InvalidOperationException("В текущем документе нет профиля " + (cmyk ? "CMYK" : "RGB") + ". Задайте профиль в параметрах цвета CorelDRAW.");
        }

        private static object FindSrgb(object appObject)
        {
            dynamic profiles = ((dynamic)appObject).ColorManager.ColorProfiles;
            foreach (string name in new[] { "sRGB IEC61966-2.1", "sRGB Color Space Profile", "IEC 61966-2.1 Default RGB colour space - sRGB" })
                try { object profile = (object)profiles[name]; if (IsSrgb(profile)) return profile; } catch { }
            int count = System.Convert.ToInt32(profiles.Count);
            if (count < 0 || count > 4096) throw new InvalidOperationException("Corel вернул некорректный список ICC-профилей.");
            for (int index = 1; index <= count; index++)
                try { object profile = (object)profiles[index]; if (IsSrgb(profile)) return profile; } catch { }
            throw new InvalidOperationException("В CorelDRAW не найден стандартный профиль sRGB IEC61966-2.1 для экранного предпросмотра. Установите или включите его в управлении цветом Corel.");
        }
        private static bool IsSrgb(object profileObject)
        {
            if (profileObject == null) return false;
            dynamic profile = profileObject;
            string name = Normalize(System.Convert.ToString(profile.Name));
            if (name == "srgbiec6196621" || name == "srgbcolorspaceprofile" || name == "iec6196621defaultrgbcolourspacesrgb") return true;
            string file = Normalize(Path.GetFileName(System.Convert.ToString(profile.FileName)));
            return file == "srgbcolorspaceprofileicm" || file == "srgbiec6196621icc";
        }
        private static string Description(object contextObject, bool screen)
        {
            dynamic context = contextObject;
            return "RGB: " + ProfileName((object)context.RGBColorProfile) + "; CMYK: " + ProfileName((object)context.CMYKColorProfile) +
                (screen ? "; экран: sRGB IEC61966-2.1" : "");
        }
        private static string ProfileName(object profileObject)
        {
            if (profileObject == null) return "не задан";
            string name = System.Convert.ToString(((dynamic)profileObject).Name);
            return String.IsNullOrWhiteSpace(name) ? "без имени" : name;
        }
        private static string GlobalSignature(object appObject)
        {
            dynamic manager = ((dynamic)appObject).ColorManager;
            return System.Convert.ToString(manager.ColorEngine, CultureInfo.InvariantCulture) + "|" +
                System.Convert.ToString(manager.PreservePureBlack, CultureInfo.InvariantCulture) + "|" +
                System.Convert.ToString(manager.MapGrayToCMYKBlack, CultureInfo.InvariantCulture);
        }
        private static void RequireCurrent(Context context)
        {
            RequireSta();
            if (Thread.CurrentThread.ManagedThreadId != context.ThreadId)
                throw new InvalidOperationException("Преобразование цветов должно продолжаться в исходном потоке интерфейса CorelDRAW.");
            dynamic app = context.App;
            object active = (object)app.ActiveDocument;
            if (!SameObject(active, context.Document))
                throw new InvalidOperationException("Активный CDR изменился. Обновите предпросмотр в нужном документе.");
            dynamic document = context.Document;
            if (!System.Convert.ToBoolean(document.ColorContext.IsSame((dynamic)context.Source)) || GlobalSignature(context.App) != context.GlobalSettings)
                throw new InvalidOperationException("Цветовые профили или настройки Corel изменились во время расчёта. Обновите предпросмотр.");
        }
        private static void RequireSta()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Преобразование ICC выполняется только в интерфейсе CorelDRAW, без Task.Run.");
        }
        private static bool SameObject(object first, object second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first == null || second == null || !Marshal.IsComObject(first) || !Marshal.IsComObject(second)) return false;
            IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try { a = Marshal.GetIUnknownForObject(first); b = Marshal.GetIUnknownForObject(second); return a == b; }
            finally { if (a != IntPtr.Zero) Marshal.Release(a); if (b != IntPtr.Zero) Marshal.Release(b); }
        }
        private static string Normalize(string value)
        {
            var normalized = new StringBuilder();
            foreach (char character in value ?? "") if (Char.IsLetterOrDigit(character)) normalized.Append(Char.ToLowerInvariant(character));
            return normalized.ToString();
        }
    }
}
