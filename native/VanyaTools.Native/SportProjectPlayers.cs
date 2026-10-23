using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class SportProjectPlayers
    {
        private const string Owner = "64A657EE-80DD-4D61-AB19-626F05600647";
        public int SchemaVersion { get; set; } = 1;
        public List<SportLayoutPlayer> Players { get; set; } = new List<SportLayoutPlayer>();

        internal static string DocumentKey(object document)
        {
            if (document == null) return null;
            IntPtr identity = Marshal.GetIUnknownForObject(document);
            try { return identity.ToInt64().ToString("X", CultureInfo.InvariantCulture); }
            finally { Marshal.Release(identity); }
        }

        internal static bool SameDocument(object first, object second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first == null || second == null) return false;
            IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try
            {
                a = Marshal.GetIUnknownForObject(first); b = Marshal.GetIUnknownForObject(second);
                return a == b;
            }
            catch { return false; }
            finally { if (a != IntPtr.Zero) Marshal.Release(a); if (b != IntPtr.Zero) Marshal.Release(b); }
        }

        internal static SportProjectPlayers Read(object document)
        {
            dynamic doc = document, properties = doc.Properties;
            if (!Convert.ToBoolean(properties.Exists(Owner, 1))) return null;
            string json = Convert.ToString(((object)properties).GetType().InvokeMember("Item", BindingFlags.GetProperty,
                null, (object)properties, new object[] { Owner, 1 }));
            var state = new JavaScriptSerializer { MaxJsonLength = 16777216 }.Deserialize<SportProjectPlayers>(json);
            if (state == null || state.SchemaVersion != 1 || state.Players == null ||
                state.Players.Count > 10000 || state.Players.Any(row => row == null))
                throw new InvalidOperationException("Не удалось прочитать список игроков из CDR.");
            return state;
        }

        internal static string Json(IEnumerable<SportLayoutPlayer> players) =>
            new JavaScriptSerializer { MaxJsonLength = 16777216 }.Serialize(new SportProjectPlayers { Players = players.ToList() });

        internal static void Write(object document, string json)
        {
            object properties = ((dynamic)document).Properties;
            properties.GetType().InvokeMember("Item", BindingFlags.SetProperty, null, properties, new object[] { Owner, 1, json });
        }

        internal static string Csv(IEnumerable<SportLayoutPlayer> players)
        {
            var text = new StringBuilder("Размер;Имя;Номер;Количество;Печатать;Перед;Спина;Левый рукав;Правый рукав;Горловина\r\n");
            foreach (SportLayoutPlayer row in players)
            {
                string[] cells = { row.Size, row.Name, row.Number, row.Quantity.ToString(CultureInfo.InvariantCulture),
                    Flag(row.PrintEnabled), Flag(row.PrintFront), Flag(row.PrintBack), Flag(row.PrintLeftSleeve),
                    Flag(row.PrintRightSleeve), Flag(row.PrintNeck) };
                text.AppendLine(String.Join(";", cells.Select(value => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"")));
            }
            return text.ToString();
        }

        internal static List<SportLayoutPlayer> ParseCsv(string text)
        {
            var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder();
            bool quoted = false;
            text = (text ?? "").TrimStart('\uFEFF');
            for (int i = 0; i < text.Length; i++)
            {
                char character = text[i];
                if (character == '"')
                {
                    if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (!quoted && (character == ';' || character == '\r' || character == '\n'))
                {
                    row.Add(cell.ToString()); cell.Clear();
                    if (character != ';')
                    {
                        if (row.Any(value => !String.IsNullOrWhiteSpace(value))) rows.Add(row);
                        row = new List<string>();
                        if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    }
                }
                else cell.Append(character);
            }
            if (quoted) throw new InvalidOperationException("В CSV не закрыта кавычка.");
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            if (rows.Count == 0 || rows[0].Count != 10 || rows[0][0] != "Размер" || rows[0][1] != "Имя")
                throw new InvalidOperationException("Выберите CSV, сохранённый кнопкой «Сохранить список».");
            var result = new List<SportLayoutPlayer>();
            for (int i = 1; i < rows.Count; i++)
            {
                List<string> cells = rows[i];
                if (cells.Count != 10 || !Int32.TryParse(cells[3], out int quantity) || quantity < 1 || quantity > 100 ||
                    cells[1].Length > 100 || cells[2].Length > 20)
                    throw new InvalidOperationException("Некорректная строка CSV: " + (i + 1) + ".");
                result.Add(new SportLayoutPlayer { Size = cells[0], Name = cells[1], Number = cells[2], Quantity = quantity,
                    PrintEnabled = ReadFlag(cells[4]), PrintFront = ReadFlag(cells[5]), PrintBack = ReadFlag(cells[6]),
                    PrintLeftSleeve = ReadFlag(cells[7]), PrintRightSleeve = ReadFlag(cells[8]), PrintNeck = ReadFlag(cells[9]) });
            }
            if (result.Count > 10000) throw new InvalidOperationException("В CSV слишком много строк.");
            return result;
        }
        private static string Flag(bool value) => value ? "1" : "0";
        private static bool ReadFlag(string value) => value == "1" ? true : value == "0" ? false :
            throw new InvalidOperationException("Галочки CSV должны иметь значение 1 или 0.");
    }
}
