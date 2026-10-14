using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// Measurements exchanged between the two machines as one line of named values ("a=1;b=x"),
    /// so either side can add a value without breaking the other. Numbers use the invariant culture.
    /// </summary>
    public static class KeyValues
    {
        public static string Encode(IEnumerable<KeyValuePair<string, object>> values)
        {
            var sb = new StringBuilder();
            foreach (var kv in values)
            {
                string v = Text(kv.Value);
                if (v == null) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(Clean(kv.Key)).Append('=').Append(v);
            }
            return sb.ToString();
        }

        private static string Text(object value)
        {
            switch (value)
            {
                case null: return null;
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? null : d.ToString("0.####", CultureInfo.InvariantCulture);
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? null : f.ToString("0.####", CultureInfo.InvariantCulture);
                case IFormattable n: return n.ToString(null, CultureInfo.InvariantCulture);
                default: return Clean(value.ToString());
            }
        }

        private static string Clean(string s) => s.Replace('=', ':').Replace(';', ',');

        public static Dictionary<string, string> Decode(string line)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(line)) return d;
            foreach (var part in line.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                d[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return d;
        }

        public static double Number(IDictionary<string, string> d, string key) =>
            d != null && d.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }
}
