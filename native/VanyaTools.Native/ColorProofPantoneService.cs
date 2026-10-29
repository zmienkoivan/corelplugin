using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class ColorProofPaletteChoice
    {
        public string Name { get; set; }
        public string Identifier { get; set; }
        public string FileName { get; set; }
        public int Version { get; set; }
    }

    internal sealed class ColorProofPantoneMatch
    {
        public string Name { get; internal set; }
        public string PaletteName { get; internal set; }
        public string PaletteIdentifier { get; internal set; }
        public string PaletteFileName { get; internal set; }
        public double DeltaE { get; internal set; }
        public int[] ScreenRgb { get; internal set; }
        public ColorProofLab Lab { get; internal set; }
        // An untouched native spot clone; never convert this object for previews or matching.
        public object Color { get; internal set; }
    }

    internal sealed class ColorProofPantoneResult
    {
        public string PaletteName { get; internal set; }
        public string PaletteIdentifier { get; internal set; }
        public IReadOnlyList<ColorProofPaletteChoice> Palettes { get; internal set; }
        public IReadOnlyList<ColorProofPantoneMatch> Matches { get; internal set; }
        public bool Cmyk { get; internal set; }
        public int[] BaseChannels { get; internal set; }
        public ColorProofLab ReferenceLab { get; internal set; }
        public int[] ReferenceScreenRgb { get; internal set; }
        public int PaletteColorCount { get; internal set; }
        public int SkippedCount { get; internal set; }
        public string Warning { get; internal set; }
        internal object SourceDocument, SourceColorContext, NativeReference;
        internal int SourceThread;
    }

    /// <summary>Uses only the user's installed Corel palette. No Pantone table is bundled or downloaded.</summary>
    internal sealed class ColorProofPantoneService
    {
        private const int MaxMatches = 32;
        private sealed class PaletteInfo
        {
            internal object Native;
            internal string Name, Identifier, FileName;
        }
        private sealed class PaletteSearch
        {
            internal PaletteInfo Info;
            internal object Colors;
            internal int Count, Index = 1;
        }
        private sealed class Search
        {
            internal object App;
            internal int Count, PaletteIndex, Processed;
            internal readonly List<PaletteSearch> Palettes = new List<PaletteSearch>();
            internal ColorProofPantoneResult Result;
            internal readonly List<ColorProofPantoneMatch> Best = new List<ColorProofPantoneMatch>();
            internal readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ColorProofPaletteChoice> ListPalettes()
        {
            RequireSta();
            return DiscoverPalettes((object)CorelApp.Get(), true).Select(Choice).ToList().AsReadOnly();
        }

        public ColorProofPantoneResult Find(bool cmyk, int[] baseChannels, int count = 8, string palettePath = null)
        {
            Search search = Prepare(cmyk, baseChannels, count, app => new[] { ResolvePalette(app, palettePath) });
            while (!ReadBatch(search, 64, CancellationToken.None)) { }
            return Finish(search, CancellationToken.None);
        }

        // This yields between UI-thread COM batches. It must not be wrapped in Task.Run.
        public Task<ColorProofPantoneResult> FindAsync(bool cmyk, int[] baseChannels, int count = 8,
            string palettePath = null, CancellationToken cancellation = default(CancellationToken), Action<int, int> progress = null)
        {
            cancellation.ThrowIfCancellationRequested();
            Search search = Prepare(cmyk, baseChannels, count, app => new[] { ResolvePalette(app, palettePath) });
            return FindPreparedAsync(search, cancellation, progress);
        }

        public Task<ColorProofPantoneResult> FindSelectedAsync(bool cmyk, int[] baseChannels, int count,
            IReadOnlyList<string> selectedIds, CancellationToken cancellation = default(CancellationToken), Action<int, int> progress = null)
        {
            cancellation.ThrowIfCancellationRequested();
            string[] identifiers = selectedIds == null ? new string[0] : selectedIds.ToArray();
            if (identifiers.Length == 0 || identifiers.Any(String.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Отметьте хотя бы одну палитру Pantone Solid Coated.");
            identifiers = identifiers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Search search = Prepare(cmyk, baseChannels, count, app => {
                var available = DiscoverPalettes(app, true).ToDictionary(PaletteKey, StringComparer.OrdinalIgnoreCase);
                var selected = new List<PaletteInfo>();
                foreach (string identifier in identifiers)
                {
                    cancellation.ThrowIfCancellationRequested();
                    PaletteInfo palette;
                    if (!available.TryGetValue(identifier, out palette))
                        throw new InvalidOperationException("Выбранная палитра больше недоступна. Обновите список палитр и отметьте её заново.");
                    selected.Add(palette);
                }
                return selected;
            });
            return FindPreparedAsync(search, cancellation, progress);
        }

        private static async Task<ColorProofPantoneResult> FindPreparedAsync(Search search, CancellationToken cancellation, Action<int, int> progress)
        {
            while (true)
            {
                bool finished = ReadBatch(search, 24, cancellation);
                progress?.Invoke(search.Processed, search.Result.PaletteColorCount);
                if (finished) break;
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            cancellation.ThrowIfCancellationRequested();
            ColorProofPantoneResult result = Finish(search, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return result;
        }

        private static Search Prepare(bool cmyk, int[] baseChannels, int count, Func<object, IEnumerable<PaletteInfo>> resolvePalettes)
        {
            RequireSta();
            int channelCount = cmyk ? 4 : 3, limit = cmyk ? 100 : 255;
            if (baseChannels == null || baseChannels.Length != channelCount || baseChannels.Any(value => value < 0 || value > limit))
                throw new InvalidOperationException("Введите " + channelCount + " допустимых канала эталона " + (cmyk ? "CMYK (0–100)." : "RGB (0–255)."));
            if (count < 1 || count > MaxMatches) throw new InvalidOperationException("Число ближайших Pantone: от 1 до " + MaxMatches + ".");
            int[] baseline = (int[])baseChannels.Clone();
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            object sourceColorContext = (object)doc.ColorContext.GetCopy();
            List<PaletteInfo> palettes = resolvePalettes((object)app).ToList();
            dynamic reference = cmyk ? app.CreateCMYKColor(baseline[0], baseline[1], baseline[2], baseline[3])
                : app.CreateRGBColor(baseline[0], baseline[1], baseline[2]);
            var result = new ColorProofPantoneResult {
                PaletteName = String.Join("; ", palettes.Select(palette => palette.Name)),
                PaletteIdentifier = palettes.Count == 1 ? PaletteKey(palettes[0]) : "",
                Palettes = palettes.Select(Choice).ToList().AsReadOnly(), Cmyk = cmyk,
                BaseChannels = baseline, ReferenceLab = ReadLab((object)app, (object)reference),
                ReferenceScreenRgb = ReadRgb((object)app, (object)reference), NativeReference = (object)reference,
                SourceDocument = (object)doc, SourceColorContext = sourceColorContext, SourceThread = Thread.CurrentThread.ManagedThreadId,
                Warning = ""
            };
            var search = new Search { App = (object)app, Count = count, Result = result };
            foreach (PaletteInfo palette in palettes)
            {
                dynamic nativePalette = palette.Native;
                int paletteCount = Convert.ToInt32(nativePalette.ColorCount);
                if (paletteCount < 1 || paletteCount > 20000)
                    throw new InvalidOperationException("Палитра «" + palette.Name + "» пуста или содержит недопустимое число цветов.");
                // Defer Colors() until the palette's first batch, after cancellation/document checks.
                search.Palettes.Add(new PaletteSearch { Info = palette, Count = paletteCount });
                result.PaletteColorCount = checked(result.PaletteColorCount + paletteCount);
            }
            RequireSearchDocument(search);
            return search;
        }

        private static bool ReadBatch(Search search, int batchSize, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            RequireSearchDocument(search);
            int read = 0;
            while (read < batchSize && search.PaletteIndex < search.Palettes.Count)
            {
                PaletteSearch palette = search.Palettes[search.PaletteIndex];
                cancellation.ThrowIfCancellationRequested();
                if (palette.Index == 1)
                    try { palette.Colors = (object)((dynamic)palette.Info.Native).Colors(); } catch { }
                if (palette.Index > palette.Count) { search.PaletteIndex++; continue; }
                int index = palette.Index++;
                search.Processed++; read++;
                try
                {
                    dynamic color = ReadPaletteColor(palette.Info.Native, palette.Colors, index);
                    if (!Convert.ToBoolean(color.IsSpot)) continue;
                    string name = Convert.ToString(color.SpotColorName) ?? "";
                    string identifier = PaletteKey(palette.Info);
                    if (String.IsNullOrWhiteSpace(name) || !search.Names.Add(identifier + "\0" + name)) continue;
                    ColorProofLab lab = ReadLab(search.App, (object)color);
                    double delta = ColorProofLabMatcher.DeltaE2000(search.Result.ReferenceLab, lab);
                    var candidate = new ColorProofPantoneMatch { Name = name, DeltaE = delta, Lab = lab,
                        PaletteName = palette.Info.Name, PaletteIdentifier = identifier, PaletteFileName = palette.Info.FileName };
                    if (search.Best.Count >= search.Count && CompareMatches(candidate, search.Best[search.Best.Count - 1]) >= 0) continue;
                    object spot = CloneColor(search.App, (object)color);
                    CheckSpot(spot, name); candidate.Color = spot;
                    search.Best.Add(candidate);
                    search.Best.Sort(CompareMatches);
                    if (search.Best.Count > search.Count) search.Best.RemoveAt(search.Best.Count - 1);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    search.Result.SkippedCount++;
                    if (search.Result.SkippedCount == 1) Log.Error("Could not evaluate an installed Pantone palette entry.", error);
                }
            }
            // Advancing an exhausted palette must not consume a second native batch or reset progress.
            while (search.PaletteIndex < search.Palettes.Count && search.Palettes[search.PaletteIndex].Index > search.Palettes[search.PaletteIndex].Count)
                search.PaletteIndex++;
            return search.PaletteIndex >= search.Palettes.Count;
        }

        private static ColorProofPantoneResult Finish(Search search, CancellationToken cancellation)
        {
            RequireSearchDocument(search);
            if (search.Best.Count == 0)
                throw new InvalidOperationException("В палитре «" + search.Result.PaletteName + "» не удалось прочитать настоящие плашечные цвета и их Lab. Выберите установленный файл Pantone Solid Coated в Corel.");
            foreach (ColorProofPantoneMatch match in search.Best)
            {
                cancellation.ThrowIfCancellationRequested();
                RequireSearchDocument(search);
                match.ScreenRgb = ReadRgb(search.App, match.Color);
            }
            RequireSearchDocument(search);
            search.Result.Matches = search.Best.AsReadOnly();
            if (search.Result.SkippedCount > 0) search.Result.Warning = "Не удалось оценить цветов: " + search.Result.SkippedCount +
                ". Результат рассчитан по остальным доступным цветам палитры.";
            Log.Info("Pantone match: palette=" + search.Result.PaletteName + ", entries=" + search.Result.PaletteColorCount +
                ", matches=" + search.Best.Count + ", skipped=" + search.Result.SkippedCount + ".");
            return search.Result;
        }

        private static int CompareMatches(ColorProofPantoneMatch first, ColorProofPantoneMatch second)
        {
            int distance = first.DeltaE.CompareTo(second.DeltaE);
            if (distance != 0) return distance;
            int name = StringComparer.OrdinalIgnoreCase.Compare(first.Name, second.Name);
            if (name != 0) return name;
            int palette = StringComparer.OrdinalIgnoreCase.Compare(first.PaletteName, second.PaletteName);
            return palette != 0 ? palette : StringComparer.OrdinalIgnoreCase.Compare(first.PaletteIdentifier, second.PaletteIdentifier);
        }

        public int Create(ColorProofPantoneResult result, IEnumerable<ColorProofPantoneMatch> selected, double patchMm = 20)
        {
            if (result == null || result.Matches == null) throw new InvalidOperationException("Сначала найдите ближайшие Pantone.");
            RequireThread(result);
            if (Double.IsNaN(patchMm) || Double.IsInfinity(patchMm) || patchMm < 5 || patchMm > 100)
                throw new InvalidOperationException("Размер плашки: от 5 до 100 мм.");
            var matches = selected == null ? new List<ColorProofPantoneMatch>() : selected.Distinct().ToList();
            if (matches.Count < 1 || matches.Count > MaxMatches || matches.Any(match => !result.Matches.Contains(match)))
                throw new InvalidOperationException("Выберите хотя бы один цвет из текущего списка Pantone.");
            foreach (ColorProofPantoneMatch match in matches) CheckSpot(match.Color, match.Name);
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null || !SameObject((object)doc, result.SourceDocument))
                throw new InvalidOperationException("Вернитесь в документ, для которого выполнен подбор Pantone, или запустите подбор заново.");
            RequireSearchDocument(new Search { App = (object)app, Result = result });
            int oldUnit = (int)doc.Unit;
            object originalPage = doc.ActivePage;
            bool started = false, optimized = Convert.ToBoolean(app.Optimization);
            dynamic page = null;
            try
            {
                const double margin = 8, gap = 6, labelHeight = 16, headerHeight = 26;
                int samples = matches.Count + 1;
                int columns = Math.Min(6, Math.Min(samples, Math.Max(1, (int)Math.Floor((570 - 2 * margin + gap) / (patchMm + gap)))));
                int rows = (samples + columns - 1) / columns;
                double width = 2 * margin + columns * patchMm + (columns - 1) * gap;
                double height = 2 * margin + headerHeight + rows * (patchMm + labelHeight);
                doc.Unit = CorelConstants.CdrMillimeter; app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools — ближайшие Pantone"); started = true;
                page = doc.AddPages(1); page.Activate(); page.SetSize(width, height);
                page.Name = "Близкие Pantone · " + matches.Count;
                dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
                double left = Convert.ToDouble(page.LeftX) + margin, top = Convert.ToDouble(page.TopY) - margin;
                Text(layer, range, left, top, "Близкие Pantone · CIEDE2000 (Lab Corel)", 9f, width - 2 * margin);
                Text(layer, range, left, top - 5, "Палитра: " + result.PaletteName, 7f, width - 2 * margin);
                Text(layer, range, left, top - 9, "Ручной эталон: " + FormatChannels(result.BaseChannels, result.Cmyk), 7f, width - 2 * margin);
                Text(layer, range, left, top - 13, "Lab получен из Corel; ΔE00 — расстояние до эталона. Проверяйте результат на своём материале.", 6.5f, width - 2 * margin);
                Text(layer, range, left, top - 17, "Выбранные Pantone сохранены плашечными цветами; эталон — " + (result.Cmyk ? "CMYK." : "RGB."), 6.5f, width - 2 * margin);
                for (int index = 0; index < samples; index++)
                {
                    double x = left + (index % columns) * (patchMm + gap), y = top - headerHeight - (index / columns) * (patchMm + labelHeight);
                    bool reference = index == 0;
                    ColorProofPantoneMatch match = reference ? null : matches[index - 1];
                    object native = CloneColor((object)app, reference ? result.NativeReference : match.Color);
                    if (!reference) CheckSpot(native, match.Name);
                    dynamic shape = layer.CreateRectangle2(x, y - patchMm, patchMm, patchMm); range.Add(shape);
                    shape.Fill.ApplyUniformFill((dynamic)native); shape.Outline.SetNoOutline();
                    if (!reference) CheckSpot((object)shape.Fill.UniformColor, match.Name);
                    shape.Name = reference ? "Исходный эталон " + FormatChannels(result.BaseChannels, result.Cmyk) : "PANTONE spot " + match.Name + " · " + match.PaletteName + " [" + match.PaletteIdentifier + "] ΔE00 " + match.DeltaE.ToString("0.00", CultureInfo.InvariantCulture);
                    Text(layer, range, x, y - patchMm - 1, reference ? "Исходный эталон" : match.Name, 7f, patchMm);
                    if (!reference)
                    {
                        Text(layer, range, x, y - patchMm - 5, "ΔE00 " + match.DeltaE.ToString("0.00", CultureInfo.CurrentCulture), 7f, patchMm);
                        Text(layer, range, x, y - patchMm - 9, match.PaletteName, 5.5f, patchMm);
                    }
                }
                range.CreateSelection();
                return matches.Count;
            }
            catch
            {
                if (page != null) try { page.Delete(); } catch (Exception error) { Log.Error("Pantone swatches rollback failed.", error); }
                try { ((dynamic)originalPage).Activate(); } catch { }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); } catch (Exception error) { Log.Error("Pantone swatches command group failed.", error); }
                try { doc.Unit = oldUnit; } catch { }
                try { app.Optimization = optimized; } catch { }
            }
        }

        private static PaletteInfo ResolvePalette(object appObject, string palettePath)
        {
            dynamic app = appObject;
            object manager = null;
            try { manager = (object)app.PaletteManager; } catch { }
            if (!String.IsNullOrWhiteSpace(palettePath))
            {
                string path = Path.GetFullPath(palettePath);
                string extension = Path.GetExtension(path);
                if ((!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".cpl", StringComparison.OrdinalIgnoreCase)) || !File.Exists(path))
                    throw new InvalidOperationException("Выберите существующий файл установленной палитры Corel (.xml или .cpl).");
                object loaded = null;
                try { if (manager != null) loaded = (object)((dynamic)manager).LoadPalette(path); } catch { }
                if (loaded == null) try { loaded = (object)app.Palettes.Open(path); } catch (Exception error)
                { throw new InvalidOperationException("CorelDRAW не смог открыть выбранную палитру: " + error.GetBaseException().Message, error); }
                PaletteInfo info = DescribePalette(loaded);
                if (!IsSolidCoated(info)) throw new InvalidOperationException("Выбранная палитра — не PANTONE Solid Coated. Process, Bridge и Uncoated не подходят.");
                return info;
            }
            List<PaletteInfo> palettes = DiscoverPalettes(appObject, false);
            if (palettes.Count > 1)
                throw new InvalidOperationException("Найдено несколько палитр Solid Coated: " + String.Join("; ", palettes.Select(p => p.Name)) +
                    ". Отметьте нужную палитру в списке или выберите её локальный файл.");
            return palettes.Single();
        }

        private static List<PaletteInfo> DiscoverPalettes(object appObject, bool includeRegistered)
        {
            dynamic app = appObject;
            object manager = null;
            try { manager = (object)app.PaletteManager; } catch { }
            var palettes = new Dictionary<string, PaletteInfo>(StringComparer.OrdinalIgnoreCase);
            try { CollectOpenPalettes(palettes, (object)app.Palettes); }
            catch (Exception error) { Log.Error("Could not enumerate open Corel palettes.", error); }
            if (manager != null)
                try { CollectOpenPalettes(palettes, (object)((dynamic)manager).OpenPalettes); } catch { }
            if (manager != null && (includeRegistered || palettes.Count == 0))
            {
                CollectRegisteredPalettes(palettes, manager);
                if (palettes.Count == 0)
                {
                    // Load only when Corel's already open/registered palettes did not suffice.
                    try { ((dynamic)manager).LoadAllPalettes(); }
                    catch (Exception error) { Log.Error("Could not load registered Corel palettes for Pantone lookup.", error); }
                    CollectRegisteredPalettes(palettes, manager);
                }
            }
            if (palettes.Count == 0)
                throw new InvalidOperationException("Не найдена установленная PANTONE Solid Coated. Откройте её в Corel или выберите локальный файл палитры. Process, Bridge и Uncoated не используются.");
            return palettes.Values.OrderByDescending(palette => Choice(palette).Version)
                .ThenBy(palette => palette.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(PaletteKey, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static ColorProofPaletteChoice Choice(PaletteInfo palette)
        {
            Match version = Regex.Match(palette.Name + " " + Path.GetFileNameWithoutExtension(palette.FileName), @"v\s*(\d+)", RegexOptions.IgnoreCase);
            int number = 0;
            if (version.Success) Int32.TryParse(version.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
            return new ColorProofPaletteChoice { Name = palette.Name, Identifier = PaletteKey(palette), FileName = palette.FileName, Version = number };
        }

        private static string PaletteKey(PaletteInfo palette)
        {
            if (!String.IsNullOrWhiteSpace(palette.Identifier)) return palette.Identifier.Trim();
            if (!String.IsNullOrWhiteSpace(palette.FileName))
            {
                string file = palette.FileName;
                try { file = Path.GetFullPath(file); } catch { }
                return "file:" + file;
            }
            return "name:" + palette.Name;
        }
        private static void CollectOpenPalettes(Dictionary<string, PaletteInfo> palettes, object collectionObject)
        {
            dynamic collection = collectionObject;
            int count = Convert.ToInt32(collection.Count);
            if (count < 0 || count > 5000) throw new InvalidOperationException("Corel вернул некорректное число открытых палитр.");
            for (int index = 1; index <= count; index++)
                try { AddPalette(palettes, (object)collection[index]); } catch { }
        }
        private static void CollectRegisteredPalettes(Dictionary<string, PaletteInfo> palettes, object managerObject)
        {
            try
            {
                dynamic manager = managerObject;
                int count = Convert.ToInt32(manager.PaletteCount);
                if (count < 0 || count > 5000) throw new InvalidOperationException("Corel вернул некорректное число зарегистрированных палитр.");
                for (int index = 1; index <= count; index++)
                    try { AddPalette(palettes, (object)manager.GetPalette(index)); } catch { }
            }
            catch (Exception error) { Log.Error("Could not enumerate registered Corel palettes.", error); }
        }
        private static void AddPalette(Dictionary<string, PaletteInfo> palettes, object palette)
        {
            PaletteInfo info = DescribePalette(palette);
            if (!IsSolidCoated(info)) return;
            string key = PaletteKey(info);
            if (!palettes.ContainsKey(key)) palettes.Add(key, info);
        }
        private static PaletteInfo DescribePalette(object paletteObject)
        {
            if (paletteObject == null) throw new InvalidOperationException("Corel не вернул палитру.");
            dynamic palette = paletteObject;
            var info = new PaletteInfo { Native = paletteObject, Name = "", Identifier = "", FileName = "" };
            try { info.Name = Convert.ToString(palette.Name) ?? ""; } catch { }
            try { info.Identifier = Convert.ToString(palette.Identifier) ?? ""; } catch { }
            try { info.FileName = Convert.ToString(palette.FileName) ?? ""; } catch { }
            return info;
        }
        private static bool IsSolidCoated(PaletteInfo palette)
        {
            string text = Normalize(palette.Name + " " + Path.GetFileNameWithoutExtension(palette.FileName));
            return text.Contains("pantone") && text.Contains("solid") && text.Contains("coated") &&
                !text.Contains("uncoated") && !text.Contains("bridge") && !text.Contains("process");
        }
        private static string Normalize(string value)
        {
            var text = new StringBuilder();
            foreach (char character in value ?? "") if (Char.IsLetterOrDigit(character)) text.Append(Char.ToLowerInvariant(character));
            return text.ToString();
        }
        private static object ReadPaletteColor(object palette, object colors, int index)
        {
            if (colors != null) try { return (object)((dynamic)colors)[index]; } catch { }
            try { return (object)((dynamic)palette).Color[index]; }
            catch { return (object)((dynamic)palette).Color(index); }
        }
        private static object CloneColor(object appObject, object colorObject)
        { dynamic clone = ((dynamic)appObject).CreateColor(); clone.CopyAssign((dynamic)colorObject); return (object)clone; }
        private static ColorProofLab ReadLab(object app, object color)
        {
            dynamic copy = CloneColor(app, color); copy.ConvertToLab();
            double rawL = Convert.ToDouble(copy.LabLuminance), a = Convert.ToDouble(copy.LabComponentA), b = Convert.ToDouble(copy.LabComponentB);
            if (Double.IsNaN(rawL) || Double.IsInfinity(rawL) || rawL < 0 || rawL > 255)
                throw new InvalidOperationException("Corel вернул недопустимую светлоту Lab.");
            // Corel SDK LabLuminance is byte-scaled 0..255; CIEDE2000 expects L*=0..100.
            // https://community.coreldraw.com/sdk/api/draw/24/p/color.labluminance?lang=vba
            return new ColorProofLab(rawL * 100.0 / 255.0, a, b);
        }
        private static int[] ReadRgb(object app, object color)
            => new ColorProofPreviewService().ConvertNative(color);
        private static void CheckSpot(object colorObject, string expected)
        {
            if (colorObject == null) throw new InvalidOperationException("Не сохранён исходный плашечный цвет.");
            dynamic color = colorObject;
            if (!Convert.ToBoolean(color.IsSpot) || !String.Equals(Convert.ToString(color.SpotColorName), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Corel не сохранил настоящий spot-цвет «" + expected + "». Лист не создан.");
        }
        private static void RequireSearchDocument(Search search)
        {
            RequireThread(search.Result);
            object active = (object)((dynamic)search.App).ActiveDocument;
            if (!SameObject(active, search.Result.SourceDocument))
                throw new InvalidOperationException("Во время подбора изменился активный документ CorelDRAW. Вернитесь в нужный CDR и запустите подбор заново.");
            if (search.Result.SourceColorContext == null || !Convert.ToBoolean(((dynamic)active).ColorContext.IsSame((dynamic)search.Result.SourceColorContext)))
                throw new InvalidOperationException("Во время подбора изменились ICC-профили документа. Запустите подбор Pantone заново.");
        }
        private static void RequireSta()
        { if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Подбор Pantone должен выполняться в интерфейсе CorelDRAW, а не в фоновом потоке."); }
        private static void RequireThread(ColorProofPantoneResult result)
        { RequireSta(); if (Thread.CurrentThread.ManagedThreadId != result.SourceThread) throw new InvalidOperationException("Вернитесь в исходный поток интерфейса CorelDRAW и повторите действие."); }
        private static bool SameObject(object first, object second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first == null || second == null || !Marshal.IsComObject(first) || !Marshal.IsComObject(second)) return false;
            IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try { a = Marshal.GetIUnknownForObject(first); b = Marshal.GetIUnknownForObject(second); return a == b; }
            finally { if (a != IntPtr.Zero) Marshal.Release(a); if (b != IntPtr.Zero) Marshal.Release(b); }
        }
        private static void Text(dynamic layer, dynamic range, double left, double top, string value, float points, double width)
        {
            dynamic text = layer.CreateArtisticTextWide(0, 0, value, 0, -1, "Arial", points); range.Add(text);
            text.Fill.UniformColor.RGBAssign(0, 0, 0); text.Outline.SetNoOutline();
            if ((double)text.SizeWidth > width && (double)text.SizeWidth > 0)
                text.SetSize(width, (double)text.SizeHeight * width / (double)text.SizeWidth);
            text.Move(left - (double)text.LeftX, top - (double)text.TopY);
        }
        private static string FormatChannels(int[] values, bool cmyk) => cmyk
            ? String.Format(CultureInfo.InvariantCulture, "C:{0} M:{1} Y:{2} K:{3}", values[0], values[1], values[2], values[3])
            : String.Format(CultureInfo.InvariantCulture, "R:{0} G:{1} B:{2}", values[0], values[1], values[2]);
    }
}
