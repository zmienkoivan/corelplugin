using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VanyaTools.Native
{
    // Corel groups are not a DXF grouping contract. Keep the native filter's
    // geometry, but put each temporary VT_PART layer into one BLOCK/INSERT.
    // No point, spline, bulge, color, or line-type value is recalculated here.
    internal static class SportCutDxfBlocks
    {
        private sealed class Tag
        {
            internal int Code;
            internal string Value;
            internal Tag(int code, string value) { Code = code; Value = value; }
        }

        private sealed class Record
        {
            internal readonly List<Tag> Tags = new List<Tag>();
            internal string Type => Tags[0].Value.Trim();
            internal string Get(int code)
            {
                int index = Index(code);
                return index < 0 ? null : Tags[index].Value.Trim();
            }
            private int Index(int code)
            {
                int braces = 0;
                for (int i = 1; i < Tags.Count; i++)
                {
                    Tag tag = Tags[i];
                    if (tag.Code == 102)
                    {
                        if (tag.Value.TrimStart().StartsWith("{", StringComparison.Ordinal)) braces++;
                        else if (tag.Value.Trim() == "}") braces--;
                        continue;
                    }
                    if (tag.Code == 1001) break;
                    if (braces == 0 && tag.Code == code) return i;
                }
                return -1;
            }
            internal void Set(int code, string value)
            {
                int index = Index(code);
                if (index >= 0) { Tags[index].Value = value; return; }
                // Common handles precede subclass data, and are not reactors.
                int insert = Tags.FindIndex(1, tag => tag.Code == 100 || tag.Code == 1001);
                Tags.Insert(insert < 0 ? Tags.Count : insert, new Tag(code, value));
            }
            internal void ClearSpace()
            {
                // Block contents belong to the block, not a paper-space layout.
                Tags.RemoveAll(tag => tag.Code == 67 || tag.Code == 410);
            }
        }

        private sealed class Section
        {
            internal Record Start, End;
            internal readonly List<Record> Body = new List<Record>();
        }

        private sealed class Drawing
        {
            internal readonly List<Tag> Prefix = new List<Tag>();
            internal readonly List<Section> Sections = new List<Section>();
            internal Record Eof;
            internal bool Bom;
            internal Section Find(string name)
            {
                Section found = null;
                foreach (Section section in Sections)
                    if (Equal(section.Start.Get(2), name))
                    {
                        if (found != null) throw Invalid("повторяется раздел " + name);
                        found = section;
                    }
                return found;
            }
            internal Section Ensure(string name)
            {
                Section section = Find(name);
                if (section != null) return section;
                section = new Section { Start = Make("SECTION", 2, name), End = Make("ENDSEC") };
                int index = Sections.FindIndex(item => Equal(item.Start.Get(2), "ENTITIES") ||
                    (name == "TABLES" && Equal(item.Start.Get(2), "BLOCKS")));
                if (index < 0) throw Invalid("нет раздела ENTITIES");
                Sections.Insert(index, section);
                return section;
            }
            internal IEnumerable<Record> Records()
            {
                foreach (Section section in Sections)
                {
                    yield return section.Start;
                    foreach (Record record in section.Body) yield return record;
                    yield return section.End;
                }
                yield return Eof;
            }
        }

        private sealed class Block
        {
            internal Record Begin, End;
            internal readonly List<Record> Body = new List<Record>();
        }

        private sealed class Chunk
        {
            internal readonly List<Record> Records = new List<Record>();
            internal Record Root => Records[0];
        }

        private sealed class Handles
        {
            private ulong _next = 0x100;
            internal Handles(Drawing drawing)
            {
                foreach (Record record in drawing.Records())
                    foreach (Tag tag in record.Tags)
                        if ((tag.Code == 5 || tag.Code == 105 || tag.Code == 1005 ||
                            (tag.Code >= 320 && tag.Code <= 369) || (tag.Code >= 390 && tag.Code <= 399) ||
                            tag.Code == 480 || tag.Code == 481) &&
                            UInt64.TryParse(tag.Value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value) &&
                            value >= _next)
                            _next = checked(value + 1);
            }
            internal string Take() { string value = Next; _next = checked(_next + 1); return value; }
            internal string Next => _next.ToString("X", CultureInfo.InvariantCulture);
        }

        internal static int Write(string source, string target, IList<string> partLayers)
        {
            Drawing drawing = Load(source);
            Section header = drawing.Find("HEADER"), entities = drawing.Find("ENTITIES");
            if (header == null || entities == null) throw Invalid("нет HEADER или ENTITIES");
            string version = HeaderValue(header, "$ACADVER");
            if (version == null || !version.StartsWith("AC", StringComparison.Ordinal) ||
                !Int32.TryParse(version.Substring(2), out int versionNumber) || versionNumber < 1015)
                throw Invalid("для блоков требуется DXF AutoCAD 2000 или новее");
            var handles = new Handles(drawing);
            Section blocks = drawing.Ensure("BLOCKS"), tables = drawing.Ensure("TABLES");
            Dictionary<string, Block> definitions = ReadBlocks(blocks);
            var expected = new HashSet<string>(partLayers, StringComparer.OrdinalIgnoreCase);
            if (expected.Count == 0 || expected.Count != partLayers.Count) throw Invalid("неверный список деталей");
            var groups = new Dictionary<string, List<Chunk>>(StringComparer.OrdinalIgnoreCase);
            foreach (string layer in partLayers) groups.Add(layer, new List<Chunk>());
            foreach (Chunk chunk in Chunks(entities.Body))
            {
                string layer = PartLayer(chunk.Root, expected, definitions, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                groups[layer].Add(chunk);
            }
            foreach (var group in groups)
                if (group.Value.Count == 0) throw Invalid("Corel не сохранил слой детали " + group.Key);

            // Every R2000 block also needs a symbol-table record and owner.
            int tableStart = -1, tableEnd = -1;
            for (int i = 0; i < tables.Body.Count; i++)
                if (tables.Body[i].Type == "TABLE" && Equal(tables.Body[i].Get(2), "BLOCK_RECORD"))
                {
                    if (tableStart >= 0) throw Invalid("повторяется таблица BLOCK_RECORD");
                    tableStart = i;
                    for (int j = i + 1; j < tables.Body.Count; j++)
                        if (tables.Body[j].Type == "ENDTAB") { tableEnd = j; break; }
                }
            if (tableStart < 0)
            {
                tableStart = tables.Body.Count;
                tables.Body.Add(Make("TABLE", 2, "BLOCK_RECORD", 5, handles.Take(), 330, "0", 100, "AcDbSymbolTable", 70, "0"));
                tableEnd = tables.Body.Count; tables.Body.Add(Make("ENDTAB"));
            }
            if (tableEnd <= tableStart) throw Invalid("незавершённая таблица BLOCK_RECORD");
            Record table = tables.Body[tableStart];
            string tableHandle = table.Get(5) ?? handles.Take(); table.Set(5, tableHandle);
            var blockRecords = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
            for (int i = tableStart + 1; i < tableEnd; i++)
            {
                Record record = tables.Body[i];
                if (record.Type != "BLOCK_RECORD" || String.IsNullOrEmpty(record.Get(2))) throw Invalid("неверная запись BLOCK_RECORD");
                blockRecords.Add(record.Get(2), record);
            }
            Func<string, string> ensureRecord = name =>
            {
                if (!blockRecords.TryGetValue(name, out Record record))
                {
                    record = Make("BLOCK_RECORD", 5, handles.Take(), 330, tableHandle,
                        100, "AcDbSymbolTableRecord", 100, "AcDbBlockTableRecord", 2, name);
                    tables.Body.Insert(tableEnd++, record); blockRecords.Add(name, record);
                }
                string handle = record.Get(5) ?? handles.Take(); record.Set(5, handle);
                return handle;
            };
            string modelOwner = ensureRecord("*Model_Space");
            EnsureSpace(blocks, definitions, "*Model_Space", modelOwner, handles);
            string paperOwner = ensureRecord("*Paper_Space");
            EnsureSpace(blocks, definitions, "*Paper_Space", paperOwner, handles);

            var inserts = new List<Record>();
            var written = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
            foreach (string layer in partLayers)
            {
                string name = "VT_PIECE_" + layer.Substring("VT_PART_".Length);
                while (definitions.ContainsKey(name) || blockRecords.ContainsKey(name)) name += "_CUT";
                string owner = ensureRecord(name);
                Block block = NewBlock(name, owner, layer, handles);
                foreach (Chunk chunk in groups[layer])
                {
                    chunk.Root.Set(330, owner);
                    string parentHandle = chunk.Root.Get(5) ?? handles.Take(); chunk.Root.Set(5, parentHandle);
                    for (int i = 0; i < chunk.Records.Count; i++)
                    {
                        Record record = chunk.Records[i]; record.ClearSpace();
                        if (i > 0) { record.Set(330, parentHandle); if (record.Get(5) == null) record.Set(5, handles.Take()); }
                        block.Body.Add(record);
                    }
                }
                blocks.Body.Add(block.Begin); blocks.Body.AddRange(block.Body); blocks.Body.Add(block.End);
                definitions.Add(name, block); written.Add(name, block);
                // Identity placement preserves all native coordinates and sizes.
                // Explicit color 7 keeps model-space ByBlock's default; explicit
                // colors on the body/letter and ByLayer colors are untouched.
                inserts.Add(Make("INSERT", 5, handles.Take(), 330, modelOwner, 100, "AcDbEntity", 8, layer,
                    62, "7", 100, "AcDbBlockReference", 2, name, 10, "0", 20, "0", 30, "0",
                    41, "1", 42, "1", 43, "1", 50, "0"));
            }
            entities.Body.Clear(); entities.Body.AddRange(inserts);
            table.Set(70, blockRecords.Count.ToString(CultureInfo.InvariantCulture));
            SetHeaderValue(header, "$HANDSEED", 5, handles.Next);
            Save(drawing, target);
            Verify(target, written, inserts);
            return written.Count;
        }

        private static string PartLayer(Record root, HashSet<string> expected, Dictionary<string, Block> blocks, HashSet<string> visiting)
        {
            string layer = root.Get(8);
            if (layer != null && expected.Contains(layer)) return layer;
            string name = root.Get(2);
            if (root.Type != "INSERT" || name == null || !blocks.TryGetValue(name, out Block block) ||
                !visiting.Add(name) || visiting.Count > 32)
                throw Invalid("не удалось определить деталь для объекта " + root.Type + " на слое «" + layer + "»");
            string result = null;
            foreach (Chunk child in Chunks(block.Body))
            {
                string childLayer = PartLayer(child.Root, expected, blocks, visiting);
                if (result != null && !Equal(result, childLayer)) throw Invalid("в одном блоке смешаны разные детали");
                result = childLayer;
            }
            visiting.Remove(name);
            if (result == null) throw Invalid("пустой блок " + name);
            return result;
        }

        private static List<Chunk> Chunks(List<Record> records)
        {
            var result = new List<Chunk>();
            for (int i = 0; i < records.Count; i++)
            {
                Record root = records[i];
                if (root.Type == "VERTEX" || root.Type == "ATTRIB" || root.Type == "SEQEND")
                    throw Invalid("отдельный вложенный объект " + root.Type);
                var chunk = new Chunk(); chunk.Records.Add(root);
                bool sequence = root.Type == "POLYLINE" || (root.Type == "INSERT" && root.Get(66) == "1");
                if (sequence)
                {
                    bool closed = false;
                    while (++i < records.Count)
                    {
                        Record child = records[i]; chunk.Records.Add(child);
                        if (child.Type == "SEQEND") { closed = true; break; }
                        if (child.Type != (root.Type == "POLYLINE" ? "VERTEX" : "ATTRIB"))
                            throw Invalid("нарушена последовательность " + root.Type);
                    }
                    if (!closed) throw Invalid("не завершена последовательность " + root.Type);
                }
                result.Add(chunk);
            }
            return result;
        }

        private static Dictionary<string, Block> ReadBlocks(Section section)
        {
            var blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < section.Body.Count; i++)
            {
                Record begin = section.Body[i];
                if (begin.Type != "BLOCK" || String.IsNullOrEmpty(begin.Get(2))) throw Invalid("неверное начало блока");
                var block = new Block { Begin = begin };
                while (++i < section.Body.Count)
                {
                    Record item = section.Body[i];
                    if (item.Type == "ENDBLK") { block.End = item; break; }
                    if (item.Type == "BLOCK") throw Invalid("вложенное определение блока");
                    block.Body.Add(item);
                }
                if (block.End == null) throw Invalid("незавершённый блок " + begin.Get(2));
                blocks.Add(begin.Get(2), block);
            }
            return blocks;
        }

        private static Block NewBlock(string name, string owner, string layer, Handles handles)
        {
            return new Block
            {
                Begin = Make("BLOCK", 5, handles.Take(), 330, owner, 100, "AcDbEntity", 8, layer,
                    100, "AcDbBlockBegin", 2, name, 70, "0", 10, "0", 20, "0", 30, "0", 3, name, 1, ""),
                End = Make("ENDBLK", 5, handles.Take(), 330, owner, 100, "AcDbEntity", 8, layer, 100, "AcDbBlockEnd")
            };
        }

        private static void EnsureSpace(Section section, Dictionary<string, Block> blocks, string name, string owner, Handles handles)
        {
            if (blocks.TryGetValue(name, out Block existing))
            {
                existing.Begin.Set(330, owner); existing.End.Set(330, owner);
                foreach (Chunk chunk in Chunks(existing.Body)) chunk.Root.Set(330, owner);
                return;
            }
            Block block = NewBlock(name, owner, "0", handles);
            section.Body.Add(block.Begin); section.Body.Add(block.End); blocks.Add(name, block);
        }

        private static void Verify(string path, Dictionary<string, Block> expected, List<Record> inserts)
        {
            Drawing drawing = Load(path);
            Dictionary<string, Block> blocks = ReadBlocks(drawing.Find("BLOCKS"));
            Section entities = drawing.Find("ENTITIES");
            if (entities.Body.Count != inserts.Count) throw Invalid("изменилось количество деталей после записи");
            for (int i = 0; i < inserts.Count; i++)
                if (!Same(entities.Body[i], inserts[i])) throw Invalid("изменилось положение блока после записи");
            foreach (var entry in expected)
            {
                if (!blocks.TryGetValue(entry.Key, out Block actual) || actual.Body.Count != entry.Value.Body.Count ||
                    !Same(actual.Begin, entry.Value.Begin) || !Same(actual.End, entry.Value.End))
                    throw Invalid("потерян блок " + entry.Key);
                for (int i = 0; i < actual.Body.Count; i++)
                    if (!Same(actual.Body[i], entry.Value.Body[i])) throw Invalid("изменились линии или цвета детали " + entry.Key);
            }
            var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Record record in drawing.Records())
            {
                if (record.Type == "SECTION") continue; // HEADER's HANDSEED is not an object handle.
                string handle = record.Get(record.Type == "DIMSTYLE" ? 105 : 5);
                if (handle != null && !handles.Add(handle)) throw Invalid("повторяется идентификатор объекта " + handle);
                if (record.Type == "INSERT" && !blocks.ContainsKey(record.Get(2) ?? "")) throw Invalid("ссылка на отсутствующий блок");
            }
        }

        private static bool Same(Record first, Record second)
        {
            if (first.Tags.Count != second.Tags.Count) return false;
            for (int i = 0; i < first.Tags.Count; i++)
                if (first.Tags[i].Code != second.Tags[i].Code || first.Tags[i].Value != second.Tags[i].Value) return false;
            return true;
        }

        private static Drawing Load(string path)
        {
            // R2000 uses an ANSI code page. Latin-1 is a byte-preserving bridge:
            // all native names/text survive regardless of the declared codepage.
            byte[] bytes = File.ReadAllBytes(path);
            if (Encoding.ASCII.GetString(bytes, 0, Math.Min(18, bytes.Length)).StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
                throw Invalid("Corel создал бинарный DXF; требуется текстовый DXF для сохранения блоков деталей");
            var drawing = new Drawing { Bom = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 };
            int offset = drawing.Bom ? 3 : 0;
            var records = new List<Record>(); Record current = null;
            using (var reader = new StringReader(Encoding.GetEncoding(28591).GetString(bytes, offset, bytes.Length - offset)))
            {
                string code;
                while ((code = reader.ReadLine()) != null)
                {
                    if (String.IsNullOrWhiteSpace(code))
                    {
                        if (String.IsNullOrWhiteSpace(reader.ReadToEnd())) break;
                        throw Invalid("пустая строка вместо группового кода");
                    }
                    string value = reader.ReadLine();
                    if (value == null || !Int32.TryParse(code.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                        throw Invalid("неверная пара кода и значения");
                    var tag = new Tag(number, value);
                    if (number == 0) { current = new Record(); records.Add(current); }
                    if (current == null) drawing.Prefix.Add(tag); else current.Tags.Add(tag);
                }
            }
            for (int i = 0; i < records.Count; i++)
            {
                Record record = records[i];
                if (record.Type == "EOF")
                {
                    if (i != records.Count - 1) throw Invalid("данные после EOF");
                    drawing.Eof = record; break;
                }
                if (record.Type != "SECTION") throw Invalid("объект вне раздела");
                var section = new Section { Start = record };
                while (++i < records.Count)
                {
                    Record item = records[i];
                    if (item.Type == "ENDSEC") { section.End = item; break; }
                    if (item.Type == "SECTION" || item.Type == "EOF") throw Invalid("незавершённый раздел");
                    section.Body.Add(item);
                }
                if (section.End == null) throw Invalid("нет конца раздела");
                drawing.Sections.Add(section);
            }
            if (drawing.Eof == null) throw Invalid("нет EOF");
            return drawing;
        }

        private static void Save(Drawing drawing, string path)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                if (drawing.Bom) stream.Write(new byte[] { 239, 187, 191 }, 0, 3);
                using (var writer = new StreamWriter(stream, Encoding.GetEncoding(28591)))
                {
                    writer.NewLine = "\r\n";
                    Action<Tag> write = tag => { writer.WriteLine(tag.Code.ToString(CultureInfo.InvariantCulture)); writer.WriteLine(tag.Value); };
                    foreach (Tag tag in drawing.Prefix) write(tag);
                    foreach (Record record in drawing.Records()) foreach (Tag tag in record.Tags) write(tag);
                }
            }
        }

        private static string HeaderValue(Section header, string name)
        {
            List<Tag> tags = header.Start.Tags;
            for (int i = 0; i + 1 < tags.Count; i++) if (tags[i].Code == 9 && tags[i].Value.Trim() == name) return tags[i + 1].Value.Trim();
            return null;
        }

        private static void SetHeaderValue(Section header, string name, int code, string value)
        {
            List<Tag> tags = header.Start.Tags;
            for (int i = 0; i + 1 < tags.Count; i++)
                if (tags[i].Code == 9 && tags[i].Value.Trim() == name)
                {
                    if (tags[i + 1].Code != code) throw Invalid("неверный формат " + name);
                    tags[i + 1].Value = value; return;
                }
            tags.Add(new Tag(9, name)); tags.Add(new Tag(code, value));
        }

        private static Record Make(string type, params object[] values)
        {
            var record = new Record(); record.Tags.Add(new Tag(0, type));
            for (int i = 0; i < values.Length; i += 2) record.Tags.Add(new Tag((int)values[i], (string)values[i + 1]));
            return record;
        }

        private static bool Equal(string first, string second) => String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        private static InvalidDataException Invalid(string detail) => new InvalidDataException("Не удалось сохранить детали блоками DXF: " + detail + ".");
    }
}
