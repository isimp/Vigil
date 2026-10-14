using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// A player's mods and configs against the server's. Mods are matched by their id and compared
    /// by version. Configs are matched by their path under the config folder (any case) and compared
    /// by a fingerprint of their settings: for a .cfg its section headers and settings, comments and
    /// blank lines left out; for other files every line; whitespace at line ends and line endings
    /// never count, so a Linux server and a Windows game agree on the same settings.
    /// </summary>
    internal static class Parity
    {
        public sealed class Mod
        {
            public Mod(string guid, string name, string version)
            {
                Guid = guid;
                Name = name;
                Version = version;
            }

            public string Guid { get; }
            public string Name { get; }
            public string Version { get; }
        }

        public sealed class VersionDiff
        {
            public string Name, Mine, Theirs;
        }

        public sealed class Report
        {
            public int ModsSame, ConfigsSame, ConfigsOnlyMine;
            public List<VersionDiff> VersionsDiffer = new List<VersionDiff>();
            public List<Mod> OnlyTheirs = new List<Mod>(), OnlyMine = new List<Mod>();
            public List<string> ConfigsDiffer = new List<string>(), ConfigsOnlyTheirs = new List<string>();
        }

        public static string Fingerprint(string fileName, string text)
        {
            bool cfg = fileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r', ' ', '\t');
                if (cfg)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) line = line.Substring(0, eq).Trim() + "=" + line.Substring(eq + 1).Trim();
                }
                sb.Append(line).Append('\n');
            }
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return string.Concat(hash.Take(8).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        public static Report Compare(IEnumerable<Mod> mine, IEnumerable<Mod> theirs, IDictionary<string, string> myConfigs, IDictionary<string, string> theirConfigs)
        {
            var r = new Report();
            var my = mine.GroupBy(m => m.Guid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var their = theirs.GroupBy(m => m.Guid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var t in their.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!my.TryGetValue(t.Guid, out var m)) r.OnlyTheirs.Add(t);
                else if (m.Version != t.Version) r.VersionsDiffer.Add(new VersionDiff { Name = t.Name, Mine = m.Version, Theirs = t.Version });
                else r.ModsSame++;
            }
            r.OnlyMine.AddRange(my.Values.Where(m => !their.ContainsKey(m.Guid)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase));

            var mc = new Dictionary<string, string>(myConfigs, StringComparer.OrdinalIgnoreCase);
            var tc = new Dictionary<string, string>(theirConfigs, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tc.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!mc.TryGetValue(kv.Key, out string fp)) r.ConfigsOnlyTheirs.Add(kv.Key);
                else if (fp != kv.Value) r.ConfigsDiffer.Add(kv.Key);
                else r.ConfigsSame++;
            }
            r.ConfigsOnlyMine = mc.Keys.Count(k => !tc.ContainsKey(k));
            return r;
        }

        /// <summary>A side's mods and configs as the channel's named values: "m0" = "id|name|version", "c0" = "path|fingerprint".</summary>
        public static Dictionary<string, string> Format(IEnumerable<Mod> mods, IDictionary<string, string> configs)
        {
            var d = new Dictionary<string, string>();
            int i = 0;
            foreach (var m in mods) d["m" + (i++).ToString(Ci)] = m.Guid + "|" + Clean(m.Name) + "|" + m.Version;
            d["mods"] = i.ToString(Ci);
            i = 0;
            foreach (var c in configs) d["c" + (i++).ToString(Ci)] = c.Key + "|" + c.Value;
            d["configs"] = i.ToString(Ci);
            return d;
        }

        public static (List<Mod> Mods, Dictionary<string, string> Configs) Parse(IDictionary<string, string> d)
        {
            var mods = new List<Mod>();
            var configs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int mc = (int)Math.Max(0, Num(d, "mods")), cc = (int)Math.Max(0, Num(d, "configs"));
            for (int i = 0; i < mc; i++)
            {
                if (!d.TryGetValue("m" + i.ToString(Ci), out string v)) continue;
                var p = v.Split('|');
                if (p.Length >= 3) mods.Add(new Mod(p[0], p[1], p[2]));
            }
            for (int i = 0; i < cc; i++)
            {
                if (!d.TryGetValue("c" + i.ToString(Ci), out string v)) continue;
                int bar = v.LastIndexOf('|');
                if (bar > 0) configs[v.Substring(0, bar)] = v.Substring(bar + 1);
            }
            return (mods, configs);
        }

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        private static double Num(IDictionary<string, string> d, string key)
        {
            double v = KeyValues.Number(d, key);
            return double.IsNaN(v) ? 0 : v;
        }

        private static string Clean(string s) => (s ?? "").Replace('|', '/');
    }
}
