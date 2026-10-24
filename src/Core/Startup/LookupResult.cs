using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>What the memory of failed assembly searches answered during a start: searches spared, and for which assemblies.</summary>
    internal sealed class LookupResult
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public LookupResult(string why, int answered, int assemblies)
        {
            Why = why ?? "";
            Answered = answered;
            Assemblies = assemblies;
        }

        /// <summary>When the start ended, in words: "the menu showed", "the world was up", "the game quit".</summary>
        public string Why { get; }

        /// <summary>Searches answered from memory instead of run.</summary>
        public int Answered { get; }

        /// <summary>Assemblies with at least one search answered.</summary>
        public int Assemblies { get; }

        /// <summary>The assemblies most asked for, most first.</summary>
        public List<KeyValuePair<string, int>> Most { get; } = new List<KeyValuePair<string, int>>();

        /// <summary>The report's line; null when nothing was answered.</summary>
        public string Line()
        {
            if (Answered == 0) return null;
            return "Assembly lookups: " + Answered.ToString("N0", Ci) + (Answered == 1 ? " failed search" : " failed searches")
                + " answered from memory until " + Why + ", for " + Assemblies.ToString("N0", Ci) + (Assemblies == 1 ? " assembly" : " assemblies")
                + " not installed: " + string.Join(", ", Most.Select(m => m.Key + " " + m.Value.ToString("N0", Ci)));
        }

        /// <summary>The result as a startup step's text: why|answered|assemblies|name=count;...</summary>
        public string EventText() => string.Join("|", new[]
        {
            Clean(Why), Answered.ToString(Ci), Assemblies.ToString(Ci), string.Join(";", Most.Select(m => Clean(m.Key) + "=" + m.Value.ToString(Ci))),
        });

        public static LookupResult Parse(string text)
        {
            var f = (text ?? "").Split('|');
            if (f.Length != 4 || !int.TryParse(f[1], NumberStyles.Integer, Ci, out int answered) || !int.TryParse(f[2], NumberStyles.Integer, Ci, out int assemblies)) return null;
            var r = new LookupResult(f[0], answered, assemblies);
            foreach (var m in f[3].Split(';').Where(s => s.Length > 0))
            {
                int eq = m.LastIndexOf('=');
                if (eq > 0 && int.TryParse(m.Substring(eq + 1), NumberStyles.Integer, Ci, out int n)) r.Most.Add(new KeyValuePair<string, int>(m.Substring(0, eq), n));
            }

            return r;
        }

        private static string Clean(string s) => (s ?? "").Replace('|', ' ').Replace(';', ' ').Replace('=', ' ');
    }
}
