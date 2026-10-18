using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// Sums a measured value (for example milliseconds) per label for the current second, the
    /// current report window and the whole session, and lists the labels that cost the most with
    /// how often they occurred and their worst single value. Without a unit it only counts.
    /// </summary>
    public sealed class ValueTally
    {
        private sealed class Agg { public long Count; public double Total; public double Max; }

        private readonly Dictionary<string, Agg> _second = new Dictionary<string, Agg>();
        private readonly Dictionary<string, Agg> _window = new Dictionary<string, Agg>();
        private readonly Dictionary<string, Agg> _run = new Dictionary<string, Agg>();
        private readonly string _unit;

        /// <param name="unit">Unit of the value, such as "ms"; null when only the count matters.</param>
        public ValueTally(string unit) { _unit = unit; }

        public void Add(string label, double value)
        {
            Bump(_second, label, 1, value, value);
            Bump(_window, label, 1, value, value);
        }

        /// <summary>The label with the largest total (or count) in the second that just ended, then starts the next second.</summary>
        public void EndSecond(out string top, out double value, out long count)
        {
            top = ""; value = 0; count = 0;
            double best = -1;
            foreach (var kv in _second)
            {
                count += kv.Value.Count;
                double key = Rank(kv.Value);
                if (key > best || (key == best && string.CompareOrdinal(kv.Key, top) < 0)) { best = key; top = kv.Key; value = kv.Value.Total; }
            }
            _second.Clear();
        }

        public string EndWindow(float seconds, int top)
        {
            string text = Format(_window, seconds, top);
            Fold();
            return text;
        }

        public string Peek(float seconds, int top) => Format(_window, seconds, top);

        /// <summary>The whole session so far, the window in progress included; that window goes on whole, since a lag report reads it.</summary>
        public string Summary(float seconds, int top)
        {
            var all = new Dictionary<string, Agg>(_run.Count + _window.Count);
            foreach (var kv in _run) Bump(all, kv.Key, kv.Value.Count, kv.Value.Total, kv.Value.Max);
            foreach (var kv in _window) Bump(all, kv.Key, kv.Value.Count, kv.Value.Total, kv.Value.Max);
            return Format(all, seconds, top);
        }

        private void Fold()
        {
            foreach (var kv in _window) Bump(_run, kv.Key, kv.Value.Count, kv.Value.Total, kv.Value.Max);
            _window.Clear();
        }

        private static void Bump(Dictionary<string, Agg> d, string label, long count, double total, double max)
        {
            if (!d.TryGetValue(label, out var a)) d[label] = a = new Agg();
            a.Count += count; a.Total += total;
            if (max > a.Max) a.Max = max;
        }

        private double Rank(Agg a) => _unit == null ? a.Count : a.Total;

        private string Format(Dictionary<string, Agg> d, float seconds, int top)
        {
            var ci = CultureInfo.InvariantCulture;
            float s = Math.Max(seconds, 0.001f);
            long count = d.Values.Sum(a => a.Count);
            var sb = new StringBuilder();
            if (_unit == null) sb.AppendFormat(ci, "{0} in all ({1:0.0}/s), {2} kinds\r\n", count, count / s, d.Count);
            else sb.AppendFormat(ci, "{0} calls ({1:0.0}/s), {2:0.0} {3} in all, {4} kinds\r\n", count, count / s, d.Values.Sum(a => a.Total), _unit, d.Count);
            foreach (var kv in d.OrderByDescending(k => Rank(k.Value)).ThenBy(k => k.Key, StringComparer.Ordinal).Take(top))
            {
                var a = kv.Value;
                if (_unit == null)
                    sb.AppendFormat(ci, "   {0,-70} {1,7}  {2,7:0.0}/s\r\n", kv.Key, a.Count, a.Count / s);
                else
                    sb.AppendFormat(ci, "   {0,-60} {1,9:0.0} {5} in {2} calls ({3:0.00} {5} each, worst {4:0.0} {5})\r\n",
                        kv.Key, a.Total, a.Count, a.Total / Math.Max(1, a.Count), a.Max, _unit);
            }
            if (d.Count > top) sb.AppendFormat(ci, "   ... {0} more kinds\r\n", d.Count - top);
            return sb.ToString();
        }
    }
}
