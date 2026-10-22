using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VanyaTools.Native
{
    internal static class OrderCdrFolders
    {
        internal static string YearFolder(string configured, string period)
        {
            if (String.IsNullOrWhiteSpace(configured) || !Path.IsPathRooted(configured)) return null;
            string root = Path.GetFullPath(configured);
            string volume = Path.GetPathRoot(root);
            root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Length < volume.Length) root = volume;
            string year = (period ?? "").Split('-')[0];
            if (year.Length == 4 && Int32.TryParse(year, out int number) && number >= 2000 && number <= 2100)
            {
                string name = Path.GetFileName(root);
                if (!String.Equals(name, year, StringComparison.OrdinalIgnoreCase))
                {
                    // The setting can point to a year or to its parent directory.
                    if (name.Length == 4 && Int32.TryParse(name, out int existingYear) &&
                        existingYear >= 2000 && existingYear <= 2100)
                    {
                        string parent = Path.GetDirectoryName(root);
                        if (!String.IsNullOrEmpty(parent)) root = Path.Combine(parent, year);
                    }
                    else
                    {
                        string child = Path.Combine(root, year);
                        if (Directory.Exists(child)) root = child;
                    }
                }
            }
            return Directory.Exists(root) ? root : null;
        }

        private static string CustomerComponent(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string safe = new string((value ?? "").Trim().Select(symbol =>
                Char.IsControl(symbol) || invalid.Contains(symbol) ? '_' : symbol).ToArray()).TrimEnd(' ', '.');
            if (safe.Length > 80) safe = safe.Substring(0, 80).TrimEnd(' ', '.');
            if (Regex.IsMatch(safe, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                safe = "_" + safe;
            return safe;
        }

        private static string CustomerKey(string value) => Regex.Replace(
            CustomerComponent(value).Normalize(NormalizationForm.FormKC), @"\s+", " ").ToUpperInvariant();

        private static int MonthNumber(string name)
        {
            var match = Regex.Match(name, @"^(0?[1-9]|1[0-2])(?=\D|20\d{2}$|$)");
            return match.Success ? Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static string CustomerIn(string month, string customer)
        {
            if (String.IsNullOrEmpty(customer)) return null;
            string key = CustomerKey(customer);
            if (key.Length == 0) return null;
            string direct = Path.Combine(month, CustomerComponent(customer));
            if (Directory.Exists(direct)) return direct;
            var matches = Directory.EnumerateDirectories(month)
                .Where(path => CustomerKey(Path.GetFileName(path)) == key).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        internal static string Suggested(string yearRoot, string period, string customer)
        {
            if (String.IsNullOrEmpty(yearRoot)) return null;
            bool hasPeriod = DateTime.TryParseExact((period ?? "") + "-01", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date);
            var months = Directory.EnumerateDirectories(yearRoot).Where(path => {
                string name = Path.GetFileName(path);
                return MonthNumber(name) > 0 &&
                    !String.Equals(name, ".VanyaTools", StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            var preferred = new List<string>();
            if (hasPeriod)
            {
                string exactMonth = Path.Combine(yearRoot, date.Month.ToString("00", CultureInfo.InvariantCulture) +
                    new CultureInfo("ru-RU").DateTimeFormat.GetMonthName(date.Month) +
                    date.Year.ToString(CultureInfo.InvariantCulture));
                if (Directory.Exists(exactMonth)) preferred.Add(exactMonth);
                preferred.AddRange(months.Where(path => MonthNumber(Path.GetFileName(path)) == date.Month &&
                    !preferred.Contains(path, StringComparer.OrdinalIgnoreCase)));
            }
            foreach (string month in preferred)
            {
                string found = CustomerIn(month, customer);
                if (found != null) return found;
            }
            // A design can have been saved in an earlier month. Only use a unique
            // customer folder; ambiguous matches fall back to the order's month.
            var elsewhere = new List<string>();
            foreach (string month in months.Where(path => !preferred.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                string found = CustomerIn(month, customer);
                if (found != null) elsewhere.Add(found);
                if (elsewhere.Count > 1) break;
            }
            if (elsewhere.Count == 1) return elsewhere[0];
            return preferred.FirstOrDefault() ?? yearRoot;
        }
    }
}
