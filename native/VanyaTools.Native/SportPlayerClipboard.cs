using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VanyaTools.Native
{
    internal static class SportPlayerClipboard
    {
        internal sealed class Row
        {
            public int Line;
            // Data columns: size, full name, number, quantity. Missing columns
            // are left untouched when pasting into an existing range.
            public Dictionary<int, string> Cells = new Dictionary<int, string>();
        }

        internal static IList<Row> Parse(string text, int startColumn)
        {
            if (String.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("В буфере обмена нет таблицы.");
            char separator = text.IndexOf('\t') >= 0 ? '\t' :
                text.IndexOf(';') >= 0 ? ';' : '\t';
            IList<string[]> table = ReadTable(text, separator);
            var result = new List<Row>();
            Dictionary<int, int> header = null;
            for (int index = 0; index < table.Count; index++)
            {
                string[] values = table[index];
                if (values.All(String.IsNullOrWhiteSpace)) continue;
                if (result.Count == 0 && header == null)
                {
                    var candidate = new Dictionary<int, int>();
                    for (int column = 0; column < values.Length; column++)
                    {
                        int role = HeaderRole(values[column]);
                        if (role < 0) continue;
                        if (candidate.ContainsKey(role))
                            throw new InvalidOperationException("В заголовке два одинаковых столбца: " + values[column]);
                        candidate.Add(role, column);
                    }
                    if (candidate.Count >= 2 || candidate.Count == 1 && values.Length == 1)
                    { header = candidate; continue; }
                }
                var row = new Row { Line = index + 1 };
                if (header != null)
                {
                    foreach (var column in header.Where(p => p.Key < 4))
                        row.Cells[column.Key] = Cell(values, column.Value);
                    if (header.TryGetValue(4, out int surname))
                    {
                        string name = row.Cells.TryGetValue(1, out string given) ? given : "";
                        row.Cells[1] = String.Join(" ", new[] { Cell(values, surname), name }
                            .Where(value => !String.IsNullOrWhiteSpace(value)));
                    }
                }
                else
                {
                    if (values.Length > 4 - startColumn)
                        throw new InvalidOperationException("Строка вставки " + row.Line +
                            ": диапазон шире четырёх столбцов игроков. Используйте заголовки " +
                            "или копируйте размер, имя, номер и количество.");
                    for (int column = 0; column < values.Length; column++)
                        row.Cells[startColumn + column] = values[column].Trim();
                }
                result.Add(row);
            }
            if (result.Count == 0)
                throw new InvalidOperationException("В выделенном диапазоне нет строк с данными.");
            return result;
        }

        private static string Cell(string[] values, int column) =>
            column < values.Length ? values[column].Trim() : "";

        private static int HeaderRole(string value)
        {
            string header = new string((value ?? "").ToLowerInvariant()
                .Where(ch => Char.IsLetterOrDigit(ch) || ch == '№' || ch == '#').ToArray());
            switch (header)
            {
                case "размер": case "size": case "рост": return 0;
                case "имя": case "фио": case "игрок": case "name": case "fullname":
                case "фамилияимя": case "имяфамилия": return 1;
                case "номер": case "номеригрока": case "№игрока":
                case "№": case "#": case "number": case "no": return 2;
                case "количество": case "колво": case "шт": case "штук":
                case "qty": case "quantity": case "count": return 3;
                case "фамилия": case "surname": case "lastname": return 4;
                default: return -1;
            }
        }

        private static IList<string[]> ReadTable(string text, char separator)
        {
            var rows = new List<string[]>();
            var cells = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '"' && (quoted || cell.Length == 0))
                {
                    if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                    { cell.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (!quoted && ch == separator)
                { cells.Add(cell.ToString()); cell.Clear(); }
                else if (!quoted && (ch == '\r' || ch == '\n'))
                {
                    cells.Add(cell.ToString()); cell.Clear();
                    rows.Add(cells.ToArray()); cells.Clear();
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else cell.Append(ch);
            }
            if (quoted) throw new InvalidOperationException("В таблице не закрыта кавычка текстовой ячейки.");
            if (cell.Length > 0 || cells.Count > 0)
            { cells.Add(cell.ToString()); rows.Add(cells.ToArray()); }
            return rows;
        }
    }
}
