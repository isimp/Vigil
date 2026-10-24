using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vigil.Core
{
    /// <summary>One bundle the cache knows: its key, when a copy of it was last used, sizes, and whether its copy is ready or failed.</summary>
    internal sealed class BundleEntry
    {
        public const string Ready = "ready", Blocked = "blocked";

        public BundleEntry(string key, DateTime lastUsed, long packedBytes, long copyBytes, string state, string assets, string plugin)
        {
            Key = key ?? "";
            LastUsed = lastUsed;
            PackedBytes = packedBytes;
            CopyBytes = copyBytes;
            State = state ?? Ready;
            Assets = assets ?? "";
            Plugin = plugin ?? "";
        }

        public string Key { get; }

        public DateTime LastUsed { get; set; }

        public long PackedBytes { get; }

        public long CopyBytes { get; }

        public string State { get; }

        /// <summary>The original's <see cref="BundleCache.AssetsSignature"/>, to check a copy against.</summary>
        public string Assets { get; }

        /// <summary>The plugin that was loading when the bundle was first seen.</summary>
        public string Plugin { get; }

        public bool IsReady => State == Ready;

        public bool IsBlocked => State == Blocked;
    }

    /// <summary>The cache's index file, one tab-separated line per bundle; Vigil writes it, the patcher reads it at the next start.</summary>
    internal static class BundleCacheIndex
    {
        public const string FileName = "index.tsv";
        private const string Header = "key\tlast_used_utc\tpacked_bytes\tcopy_bytes\tstate\tassets\tplugin";
        private const string When = "yyyy-MM-dd HH:mm:ss";
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static string Format(IEnumerable<BundleEntry> entries)
        {
            var sb = new StringBuilder(Header).Append('\n');
            if (entries == null) return sb.ToString();
            foreach (var e in entries)
            {
                sb.Append(e.Key).Append('\t').Append(e.LastUsed.ToUniversalTime().ToString(When, Ci)).Append('\t')
                    .Append(e.PackedBytes.ToString(Ci)).Append('\t').Append(e.CopyBytes.ToString(Ci)).Append('\t')
                    .Append(e.State).Append('\t').Append(Clean(e.Assets)).Append('\t').Append(Clean(e.Plugin)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The entries of an index file; lines that are not one are skipped.</summary>
        public static List<BundleEntry> Parse(string text)
        {
            var list = new List<BundleEntry>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var raw in text.Split('\n'))
            {
                var p = raw.TrimEnd('\r').Split('\t');
                if (p.Length < 7 || p[0] == "key" || p[0].Length == 0) continue;
                if (!DateTime.TryParseExact(p[1], When, Ci, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime used)) continue;
                if (!long.TryParse(p[2], NumberStyles.Integer, Ci, out long packed) || !long.TryParse(p[3], NumberStyles.Integer, Ci, out long copy)) continue;
                if (p[4] != BundleEntry.Ready && p[4] != BundleEntry.Blocked) continue;
                list.Add(new BundleEntry(p[0], used, packed, copy, p[4], p[5], p[6]));
            }
            return list;
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
