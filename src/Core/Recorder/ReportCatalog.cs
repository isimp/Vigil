using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The reports a Vigil hands out over the admin channel: its own report files, in its
    /// recordings folder or one session folder deep in Vigil-runs, each named by its kind and its
    /// first words that say what it is about. A report travels in pieces of <see cref="PartBytes"/>,
    /// each as URL-safe base64 without padding, since the channel's values cannot hold "=" or ";".
    /// </summary>
    internal static class ReportCatalog
    {
        public const int PartBytes = 32 * 1024, MaxListed = 30;

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        public sealed class Entry
        {
            /// <summary>Relative to the recordings folder, with "/" between folders.</summary>
            public string Path;
            public long Size;
            public DateTime Modified;
            public string Kind, Headline;
        }

        /// <summary>The kind of report a file holds, from its name; null for a file that is no report.</summary>
        public static string KindOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".txt", StringComparison.Ordinal) || !fileName.StartsWith("Vigil-", StringComparison.Ordinal)) return null;
            string stem = fileName.Substring(6, fileName.Length - 10);
            if (stem.StartsWith("incident-", StringComparison.Ordinal))
            {
                int dash = stem.LastIndexOf('-');
                string kind = dash > 9 ? stem.Substring(dash + 1) : null;
                return kind == "manual" ? "lag report" : kind;
            }
            if (stem.StartsWith("load-", StringComparison.Ordinal)) return "load test";
            if (stem.StartsWith("deep-", StringComparison.Ordinal)) return "deep capture";
            if (stem.StartsWith("environment-", StringComparison.Ordinal)) return "environment";
            if (stem.StartsWith("selftest", StringComparison.Ordinal)) return "self-test";
            if (stem == "summary") return "summary";
            return null;
        }

        /// <summary>Whether a path a player's game asked for is one of this Vigil's reports: in the recordings folder or one session folder deep in Vigil-runs, nothing above it.</summary>
        public static bool Servable(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 || path.StartsWith("/", StringComparison.Ordinal)) return false;
            var parts = path.Split('/');
            if (parts.Any(p => p.Length == 0 || p == "." || p == "..")) return false;
            if (parts.Length != 1 && parts.Length != 3) return false;
            if (parts.Length == 3 && parts[0] == SharedFolder) return ShareQueue.Shareable(parts[2]);
            if (parts.Length == 3 && parts[0] != "Vigil-runs") return false;
            return KindOf(parts[parts.Length - 1]) != null;
        }

        /// <summary>Where a server keeps the reports players shared with it, one folder per player.</summary>
        public const string SharedFolder = "shared";

        /// <summary>The player a shared report came from (their folder's name); null for the server's own.</summary>
        public static string From(string path)
        {
            var parts = (path ?? "").Split('/');
            return parts.Length == 3 && parts[0] == SharedFolder ? parts[1] : null;
        }

        /// <summary>What a report says first about itself: an incident's trigger, a load test's verdict, else its first line.</summary>
        public static string Headline(string kind, IEnumerable<string> firstLines)
        {
            var lines = firstLines.ToList();
            const string trigger = "What triggered it: ";
            var t = lines.FirstOrDefault(l => l.StartsWith(trigger, StringComparison.Ordinal));
            if (t != null) return t.Substring(trigger.Length).Trim();
            int verdict = lines.FindIndex(l => l.Trim() == "== Verdict ==");
            if (verdict >= 0)
            {
                var v = lines.Skip(verdict + 1).FirstOrDefault(l => l.Trim().Length > 0);
                if (v != null) return v.Trim();
            }
            return lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        }

        public static Dictionary<string, string> FormatList(IReadOnlyList<Entry> entries)
        {
            var d = new Dictionary<string, string> { { "n", entries.Count.ToString(Ci) } };
            for (int i = 0; i < entries.Count; i++)
            {
                string k = i.ToString(Ci);
                d["p" + k] = entries[i].Path;
                d["s" + k] = entries[i].Size.ToString(Ci);
                d["t" + k] = entries[i].Modified.ToString(TimeFormat, Ci);
                d["k" + k] = entries[i].Kind;
                d["h" + k] = entries[i].Headline ?? "";
            }
            return d;
        }

        public static List<Entry> ParseList(IDictionary<string, string> d)
        {
            var list = new List<Entry>();
            if (d == null || !d.TryGetValue("n", out string n) || !int.TryParse(n, NumberStyles.Integer, Ci, out int count)) return list;
            for (int i = 0; i < count; i++)
            {
                string k = i.ToString(Ci);
                if (!d.TryGetValue("p" + k, out string path) || !Servable(path)) continue;
                d.TryGetValue("s" + k, out string size);
                d.TryGetValue("t" + k, out string time);
                d.TryGetValue("k" + k, out string kind);
                d.TryGetValue("h" + k, out string headline);
                list.Add(new Entry
                {
                    Path = path,
                    Size = long.TryParse(size, NumberStyles.Integer, Ci, out long s) ? s : 0,
                    Modified = DateTime.TryParseExact(time, TimeFormat, Ci, DateTimeStyles.None, out var m) ? m : DateTime.MinValue,
                    Kind = kind ?? KindOf(path.Substring(path.LastIndexOf('/') + 1)),
                    Headline = headline ?? "",
                });
            }
            return list;
        }

        public static int Parts(long size) => size <= 0 ? 1 : (int)((size + PartBytes - 1) / PartBytes);

        public static string EncodePart(byte[] data, int part)
        {
            int offset = part * PartBytes;
            if (offset >= data.Length) return "";
            int count = Math.Min(PartBytes, data.Length - offset);
            return Convert.ToBase64String(data, offset, count).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] DecodePart(string text)
        {
            if (string.IsNullOrEmpty(text)) return new byte[0];
            string b = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b + new string('=', (4 - b.Length % 4) % 4));
        }
    }
}
