using System;
using System.Collections.Generic;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// Compares what two sessions ran with: their mod lists (guid, name, version, file) and their
    /// config file lists (file, size, hash), both as tab-separated text with a header line.
    /// Lines it cannot read are skipped.
    /// </summary>
    public static class SessionDiff
    {
        public static List<string> Mods(string a, string b)
        {
            var before = Parse(a, 4);
            var after = Parse(b, 4);
            var result = new List<string>();
            foreach (var guid in before.Keys.Union(after.Keys).OrderBy(k => Name(before, after, k), StringComparer.OrdinalIgnoreCase))
            {
                bool had = before.TryGetValue(guid, out var x);
                bool has = after.TryGetValue(guid, out var y);
                if (had && !has) result.Add(x[1] + " " + x[2] + " removed");
                else if (!had && has) result.Add(y[1] + " " + y[2] + " added");
                else if (x[2] != y[2]) result.Add(y[1] + " " + x[2] + " -> " + y[2]);
            }
            return result;
        }

        public static List<string> Configs(string a, string b)
        {
            var before = Parse(a, 3);
            var after = Parse(b, 3);
            var result = new List<string>();
            foreach (var file in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                bool had = before.TryGetValue(file, out var x);
                bool has = after.TryGetValue(file, out var y);
                if (had && !has) result.Add(file + " removed");
                else if (!had && has) result.Add(file + " added");
                else if (x[2] != y[2]) result.Add(file + " changed (" + x[1] + " -> " + y[1] + " bytes)");
            }
            return result;
        }

        private static string Name(Dictionary<string, string[]> a, Dictionary<string, string[]> b, string key) =>
            a.TryGetValue(key, out var x) ? x[1] : b[key][1];

        // Keyed by the first column; the header line (first column "guid" or "file") is skipped.
        private static Dictionary<string, string[]> Parse(string text, int columns)
        {
            var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return d;
            foreach (var raw in text.Split('\n'))
            {
                var parts = raw.TrimEnd('\r').Split('\t');
                if (parts.Length < columns || parts[0].Length == 0) continue;
                if (parts[0] == "guid" || parts[0] == "file") continue;
                d[parts[0]] = parts;
            }
            return d;
        }
    }
}
