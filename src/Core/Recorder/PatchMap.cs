using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>One Harmony patch on one game method, as seen at run time.</summary>
    public sealed class PatchRow
    {
        public string Method;   // Type.Method of the patched game method
        public string Kind;     // prefix, postfix, transpiler, finalizer, ilmanipulator
        public string Owner;    // the mod (plugin name) the patch method belongs to
        public int Priority;
        public string Patch;    // Type.Method of the patch method
        public bool CanSkip;    // a prefix returning bool can stop the original from running
    }

    /// <summary>
    /// The session's map of which mod patches which game method and how (Vigil-patches.tsv),
    /// and the differences between two such maps.
    /// </summary>
    public static class PatchMap
    {
        public const string Header = "method\tkind\towner\tpriority\tpatch\tcan_skip";

        public static string Format(IEnumerable<PatchRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append("\r\n");
            foreach (var r in rows.OrderBy(r => r.Method, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal)
                         .ThenBy(r => r.Owner, StringComparer.Ordinal).ThenBy(r => r.Patch, StringComparer.Ordinal))
            {
                sb.Append(Clean(r.Method)).Append('\t').Append(Clean(r.Kind)).Append('\t').Append(Clean(r.Owner)).Append('\t')
                  .Append(r.Priority.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(r.Patch)).Append('\t')
                  .Append(r.CanSkip ? "yes" : "").Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Patches added, removed or changed (priority or ability to skip) from <paramref name="older"/> to <paramref name="newer"/>.</summary>
        public static List<string> Diff(IEnumerable<string> older, IEnumerable<string> newer)
        {
            var a = Parse(older);
            var b = Parse(newer);
            var result = new List<string>();
            foreach (var kv in b.Where(k => !a.ContainsKey(k.Key)).OrderBy(k => k.Key, StringComparer.Ordinal))
                result.Add("added    " + Describe(kv.Value));
            foreach (var kv in a.Where(k => !b.ContainsKey(k.Key)).OrderBy(k => k.Key, StringComparer.Ordinal))
                result.Add("removed  " + Describe(kv.Value));
            foreach (var kv in b.Where(k => a.ContainsKey(k.Key)).OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var old = a[kv.Key];
                if (old[3] != kv.Value[3] || old[5] != kv.Value[5])
                    result.Add("changed  " + Describe(kv.Value) + "   (was priority " + old[3] + (old[5] == "yes" ? ", can skip" : "") + ")");
            }
            return result;
        }

        private static string Describe(string[] f) =>
            f[0] + "  " + f[1] + "  by " + f[2] + "  priority " + f[3] + (f[5] == "yes" ? ", can skip the original" : "") + "  (" + f[4] + ")";

        private static Dictionary<string, string[]> Parse(IEnumerable<string> lines)
        {
            var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var raw in lines)
            {
                string line = raw?.TrimEnd('\r');
                if (string.IsNullOrEmpty(line) || line == Header) continue;
                var f = line.Split('\t');
                if (f.Length < 6) continue;
                d[f[0] + "\t" + f[1] + "\t" + f[2] + "\t" + f[4]] = f;
            }
            return d;
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
