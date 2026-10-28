using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    // Call only during explicit document actions on Corel's UI thread; no polling or active-document lookup.
    internal static class ColorProofCatalogStore
    {
        private const string Owner = "44A4E6CB-80DD-4B3F-96EC-33C5C433E7CB";
        private const int CatalogProperty = 1;
        private const int PageProperty = 2;

        internal static IReadOnlyList<ColorProofCatalog> Load(object document)
            => ColorProofCatalog.Decode(ReadEncoded(document));

        internal static void Append(object document, ColorProofCatalog catalog)
        {
            ColorProofCatalog snapshot = ColorProofCatalog.CopyValidated(catalog);
            var catalogs = new List<ColorProofCatalog>(Load(document));
            foreach (ColorProofCatalog existing in catalogs)
                if (String.Equals(existing.Id, snapshot.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException("В CDR уже есть таблица цветопробы " + snapshot.Id + ". Создайте таблицу заново.");
            catalogs.Add(snapshot);
            string encoded = ColorProofCatalog.Encode(catalogs);
            WriteProperty(document, CatalogProperty, encoded);
            ((dynamic)document).Dirty = true;
        }

        // Keep the exact old string before rendering. Null means our property did not exist.
        internal static string ReadEncoded(object document) => ReadProperty(document, CatalogProperty);

        // Deliberately accepts the original raw string, including data from an unknown future schema.
        internal static void RestoreEncoded(object document, string encoded)
        {
            WriteProperty(document, CatalogProperty, encoded);
            ((dynamic)document).Dirty = true;
        }

        internal static void BindPage(object page, string catalogId, int pageNumber)
        {
            if (!ColorProofCatalog.ValidId(catalogId) || pageNumber < 1 || pageNumber > ColorProofCatalog.MaxPatches)
                throw new InvalidOperationException("Некорректная связь листа с каталогом цветопробы.");
            string json = new JavaScriptSerializer().Serialize(new ColorProofCatalogPageReference {
                CatalogId = catalogId, PageNumber = pageNumber });
            WriteProperty(page, PageProperty, json);
        }

        internal static ColorProofCatalogPageReference ReadPage(object page)
        {
            string encoded = ReadProperty(page, PageProperty);
            if (String.IsNullOrWhiteSpace(encoded)) return null;
            if (encoded.Length > 1024) throw InvalidPage(null);
            ColorProofCatalogPageReference reference;
            try { reference = new JavaScriptSerializer { MaxJsonLength = 1024, RecursionLimit = 8 }
                .Deserialize<ColorProofCatalogPageReference>(encoded); }
            catch (ArgumentException error) { throw InvalidPage(error); }
            catch (InvalidOperationException error) { throw InvalidPage(error); }
            if (reference == null || !ColorProofCatalog.ValidId(reference.CatalogId) ||
                reference.PageNumber < 1 || reference.PageNumber > ColorProofCatalog.MaxPatches)
                throw InvalidPage(null);
            return reference;
        }

        private static string ReadProperty(object owner, int index)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            object properties = ((dynamic)owner).Properties;
            if (!Convert.ToBoolean(properties.GetType().InvokeMember("Exists", BindingFlags.InvokeMethod,
                null, properties, new object[] { Owner, index }))) return null;
            return Convert.ToString(properties.GetType().InvokeMember("Item", BindingFlags.GetProperty,
                null, properties, new object[] { Owner, index }));
        }

        private static void WriteProperty(object owner, int index, string encoded)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            object properties = ((dynamic)owner).Properties;
            if (encoded == null)
            {
                if (Convert.ToBoolean(properties.GetType().InvokeMember("Exists", BindingFlags.InvokeMethod,
                    null, properties, new object[] { Owner, index })))
                    properties.GetType().InvokeMember("Delete", BindingFlags.InvokeMethod,
                        null, properties, new object[] { Owner, index });
            }
            else properties.GetType().InvokeMember("Item", BindingFlags.SetProperty,
                null, properties, new object[] { Owner, index, encoded });
        }

        private static InvalidOperationException InvalidPage(Exception error) => new InvalidOperationException(
            "Не удалось прочитать связь листа с каталогом цветопробы.", error);
    }
}
