using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// A connection's last seconds as named values for the admin channel: one comma list per
    /// measurement, oldest first ("ping" = "40,41,43"), a value not measured left empty. The reader
    /// takes as many seconds as its shortest list holds.
    /// </summary>
    internal static class LinkHistory
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        private static readonly (string Key, Func<DropAnalysis.Sample, double> Get, Action<DropAnalysis.Sample[], int, double> Set)[] Fields =
        {
            ("in", s => s.InKbs, (a, i, v) => a[i].InKbs = v),
            ("out", s => s.OutKbs, (a, i, v) => a[i].OutKbs = v),
            ("queue", s => s.QueueKb, (a, i, v) => a[i].QueueKb = v),
            ("unacked", s => s.UnackedKb, (a, i, v) => a[i].UnackedKb = v),
            ("ping", s => s.PingMs, (a, i, v) => a[i].PingMs = v),
            ("gap", s => s.MaxGapMs, (a, i, v) => a[i].MaxGapMs = v),
            ("rate", s => s.RateKbs, (a, i, v) => a[i].RateKbs = v),
            ("ql", s => s.QualityLocal, (a, i, v) => a[i].QualityLocal = v),
            ("qr", s => s.QualityRemote, (a, i, v) => a[i].QualityRemote = v),
        };

        public static Dictionary<string, string> Format(IReadOnlyList<DropAnalysis.Sample> samples)
        {
            var d = new Dictionary<string, string> { { "n", samples.Count.ToString(Ci) } };
            foreach (var f in Fields)
                d[f.Key] = string.Join(",", samples.Select(s => { double v = f.Get(s); return double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("0.###", Ci); }).ToArray());
            return d;
        }

        public static List<DropAnalysis.Sample> Parse(IDictionary<string, string> values)
        {
            if (values == null || !values.TryGetValue("n", out string n) || !int.TryParse(n, NumberStyles.Integer, Ci, out int count) || count <= 0)
                return new List<DropAnalysis.Sample>();
            var lists = Fields.Select(f => values.TryGetValue(f.Key, out string v) ? (v ?? "").Split(',') : new string[0]).ToList();
            count = Math.Min(count, lists.Where(l => l.Length > 0).Select(l => l.Length).DefaultIfEmpty(0).Min());
            var samples = new DropAnalysis.Sample[count];
            for (int i = 0; i < count; i++)
            {
                samples[i].At = i;
                for (int k = 0; k < Fields.Length; k++)
                    Fields[k].Set(samples, i, i < lists[k].Length && double.TryParse(lists[k][i], NumberStyles.Float, Ci, out double v) ? v : double.NaN);
            }
            return samples.ToList();
        }
    }
}
