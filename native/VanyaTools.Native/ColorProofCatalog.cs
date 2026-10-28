using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    // Numeric recipes captured from the completed plan, never reconstructed from current controls.
    internal sealed class ColorProofCatalogPatch
    {
        public int Number { get; set; }
        public int PageNumber { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int Quadrant { get; set; }
        public int[] Values { get; set; }
    }

    internal sealed class ColorProofCatalogPageReference
    {
        public string CatalogId { get; set; }
        public int PageNumber { get; set; }
    }

    internal sealed class ColorProofCatalog
    {
        internal const int MaxPatches = 10000;
        internal const int MaxEncodedLength = 16777216;
        private const string Format = "VT_COLOR_PROOF_CATALOG";

        public string Id { get; set; }
        public string CreatedUtc { get; set; }
        public bool Cmyk { get; set; }
        public int[] ReferenceChannels { get; set; }
        public string ContextDescription { get; set; }
        public List<ColorProofCatalogPatch> Patches { get; set; }

        // A series requires the explicit original reference: its first page may already be shifted.
        internal static ColorProofCatalog Build(IReadOnlyList<ColorProofGridPlan> plans, string context = null)
        {
            if (plans == null || plans.Count != 1 || plans[0] == null)
                throw new InvalidOperationException("Для каталога серии передайте исходный эталон явно.");
            return Build(plans, plans[0].BaseChannels, context);
        }

        internal static ColorProofCatalog Build(IReadOnlyList<ColorProofGridPlan> plans,
            int[] referenceChannels, string context = null)
        {
            if (plans == null || plans.Count == 0 || plans.Count > MaxPatches || plans[0] == null || plans[0].Options == null)
                throw new InvalidOperationException("Не заданы листы каталога цветопробы.");
            bool cmyk = plans[0].Options.Cmyk;
            var patches = new List<ColorProofCatalogPatch>();
            for (int pageIndex = 0; pageIndex < plans.Count; pageIndex++)
            {
                ColorProofGridPlan page = plans[pageIndex];
                if (page == null || page.Options == null || page.Options.Cmyk != cmyk || page.Patches == null)
                    throw new InvalidOperationException("Все листы каталога должны использовать одну цветовую модель.");
                if (page.Patches.Count == 0 || page.Patches.Count > MaxPatches - patches.Count)
                    throw new InvalidOperationException("Каталог должен содержать от 1 до " + MaxPatches + " плашек.");
                var ordered = new List<ColorProofGridPatch>(page.Patches.Count);
                foreach (ColorProofGridPatch patch in page.Patches)
                {
                    if (patch == null) throw new InvalidOperationException("В плане цветопробы отсутствует плашка.");
                    ordered.Add(patch);
                }
                ordered.Sort((first, second) => {
                    int vertical = first.Ymm.CompareTo(second.Ymm);
                    return vertical != 0 ? vertical : first.Xmm.CompareTo(second.Xmm);
                });
                foreach (ColorProofGridPatch patch in ordered)
                {
                    patches.Add(new ColorProofCatalogPatch {
                        Number = patches.Count + 1, PageNumber = pageIndex + 1,
                        Row = patch.Row, Column = patch.Column, Quadrant = patch.Quadrant,
                        Values = patch.Values == null ? null : (int[])patch.Values.Clone()
                    });
                }
            }
            return CopyValidated(new ColorProofCatalog {
                Id = "CP-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant(),
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Cmyk = cmyk, ReferenceChannels = referenceChannels,
                ContextDescription = context ?? "", Patches = patches
            });
        }

        internal ColorProofCatalogPatch FindPatch(int number)
        {
            if (Patches == null || number < 1 || number > Patches.Count)
                throw new InvalidOperationException("В таблице " + Id + " нет плашки №" + number + ".");
            ColorProofCatalogPatch patch = Patches[number - 1];
            if (patch == null || patch.Number != number)
                throw new InvalidOperationException("Нарушена нумерация сохранённой цветопробы.");
            return CopyPatch(patch, Cmyk);
        }

        internal static ColorProofCatalog CopyValidated(ColorProofCatalog source)
        {
            if (source == null || !ValidId(source.Id))
                throw new InvalidOperationException("Некорректный номер таблицы цветопробы.");
            if (!DateTime.TryParseExact(source.CreatedUtc, "o", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime created) || created.Kind != DateTimeKind.Utc)
                throw new InvalidOperationException("Некорректная дата создания таблицы цветопробы.");
            int[] reference = CopyValues(source.ReferenceChannels, source.Cmyk);
            string context = source.ContextDescription ?? "";
            if (context.Length > 8192)
                throw new InvalidOperationException("Слишком длинное описание условий цветопробы.");
            if (source.Patches == null || source.Patches.Count < 1 || source.Patches.Count > MaxPatches)
                throw new InvalidOperationException("Каталог должен содержать от 1 до " + MaxPatches + " плашек.");
            var patches = new List<ColorProofCatalogPatch>(source.Patches.Count);
            var positions = new HashSet<string>(StringComparer.Ordinal);
            int previousPage = 0;
            bool containsReference = false;
            foreach (ColorProofCatalogPatch item in source.Patches)
            {
                ColorProofCatalogPatch patch = CopyPatch(item, source.Cmyk);
                if (patch.Number != patches.Count + 1 || patch.PageNumber < previousPage ||
                    patch.PageNumber > previousPage + 1)
                    throw new InvalidOperationException("Нарушена сквозная нумерация плашек или листов цветопробы.");
                string position = patch.PageNumber + ":" + patch.Row + ":" + patch.Column + ":" + patch.Quadrant;
                if (!positions.Add(position))
                    throw new InvalidOperationException("В каталоге повторяется позиция плашки.");
                previousPage = patch.PageNumber;
                containsReference |= EqualValues(reference, patch.Values);
                patches.Add(patch);
            }
            if (!containsReference)
                throw new InvalidOperationException("В каталоге нет плашки исходного эталона.");
            return new ColorProofCatalog {
                Id = source.Id, CreatedUtc = source.CreatedUtc, Cmyk = source.Cmyk,
                ReferenceChannels = reference, ContextDescription = context, Patches = patches
            };
        }

        internal static string Encode(IReadOnlyList<ColorProofCatalog> catalogs)
        {
            var envelope = new CatalogEnvelope { Format = Format, SchemaVersion = 1,
                Catalogs = CopyCatalogs(catalogs) };
            string json = Serializer().Serialize(envelope);
            if (json.Length > MaxEncodedLength)
                throw new InvalidOperationException("Каталоги цветопроб превышают допустимый размер данных CDR.");
            return json;
        }

        internal static IReadOnlyList<ColorProofCatalog> Decode(string encoded)
        {
            if (String.IsNullOrWhiteSpace(encoded)) return new List<ColorProofCatalog>().AsReadOnly();
            if (encoded.Length > MaxEncodedLength)
                throw new InvalidOperationException("Сохранённый каталог цветопроб слишком большой.");
            CatalogEnvelope envelope;
            try { envelope = Serializer().Deserialize<CatalogEnvelope>(encoded); }
            catch (ArgumentException error) { throw InvalidData(error); }
            catch (InvalidOperationException error) { throw InvalidData(error); }
            if (envelope == null || envelope.Format != Format || envelope.SchemaVersion != 1 || envelope.Catalogs == null)
                throw InvalidData(null);
            return CopyCatalogs(envelope.Catalogs).AsReadOnly();
        }

        internal static bool ValidId(string id)
        {
            if (id == null || id.Length != 11 || !id.StartsWith("CP-", StringComparison.Ordinal)) return false;
            for (int index = 3; index < id.Length; index++)
                if (!((id[index] >= '0' && id[index] <= '9') || (id[index] >= 'A' && id[index] <= 'F'))) return false;
            return true;
        }

        private static List<ColorProofCatalog> CopyCatalogs(IReadOnlyList<ColorProofCatalog> catalogs)
        {
            if (catalogs == null) throw InvalidData(null);
            var copy = new List<ColorProofCatalog>(catalogs.Count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ColorProofCatalog catalog in catalogs)
            {
                ColorProofCatalog item = CopyValidated(catalog);
                if (!ids.Add(item.Id)) throw new InvalidOperationException("Повторяется номер таблицы цветопробы " + item.Id + ".");
                copy.Add(item);
            }
            return copy;
        }

        private static ColorProofCatalogPatch CopyPatch(ColorProofCatalogPatch source, bool cmyk)
        {
            if (source == null || source.Number < 1 || source.Number > MaxPatches ||
                source.PageNumber < 1 || source.PageNumber > MaxPatches ||
                source.Row < 0 || source.Row >= ColorProofGridPlan.MaxAxisCount ||
                source.Column < 0 || source.Column >= ColorProofGridPlan.MaxAxisCount ||
                source.Quadrant < 0 || source.Quadrant > 3)
                throw new InvalidOperationException("Некорректный адрес плашки в каталоге цветопробы.");
            return new ColorProofCatalogPatch {
                Number = source.Number, PageNumber = source.PageNumber, Row = source.Row,
                Column = source.Column, Quadrant = source.Quadrant, Values = CopyValues(source.Values, cmyk)
            };
        }

        private static int[] CopyValues(int[] values, bool cmyk)
        {
            int count = cmyk ? 4 : 3, maximum = cmyk ? 100 : 255;
            if (values == null || values.Length != count)
                throw new InvalidOperationException("Неполный состав " + (cmyk ? "CMYK" : "RGB") + " сохранённой плашки.");
            var copy = (int[])values.Clone();
            foreach (int value in copy)
                if (value < 0 || value > maximum)
                    throw new InvalidOperationException("Каналы сохранённой плашки должны быть от 0 до " + maximum + ".");
            return copy;
        }

        private static bool EqualValues(int[] first, int[] second)
        {
            if (first.Length != second.Length) return false;
            for (int index = 0; index < first.Length; index++) if (first[index] != second[index]) return false;
            return true;
        }

        private static JavaScriptSerializer Serializer() => new JavaScriptSerializer {
            MaxJsonLength = MaxEncodedLength, RecursionLimit = 64 };

        private static InvalidOperationException InvalidData(Exception error) => new InvalidOperationException(
            "Не удалось прочитать сохранённые каталоги цветопроб. Данные CDR не изменены.", error);

        private sealed class CatalogEnvelope
        {
            public string Format { get; set; }
            public int SchemaVersion { get; set; }
            public List<ColorProofCatalog> Catalogs { get; set; }
        }
    }
}
