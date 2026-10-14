using System;
using System.Collections.Generic;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The seconds of a load test filed per step and measurement. A step can be summarised on its
    /// steady part, leaving out its first seconds, in which the traffic is still building up.
    /// </summary>
    public sealed class LoadLedger
    {
        public sealed class Summary
        {
            public int Count;
            public double Mean = double.NaN, P95 = double.NaN, Max = double.NaN, Min = double.NaN, Last = double.NaN, Sum;
        }

        private readonly Dictionary<int, Dictionary<string, List<KeyValuePair<int, double>>>> _data =
            new Dictionary<int, Dictionary<string, List<KeyValuePair<int, double>>>>();

        public void Add(int stage, int second, string metric, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            if (!_data.TryGetValue(stage, out var byMetric)) _data[stage] = byMetric = new Dictionary<string, List<KeyValuePair<int, double>>>();
            if (!byMetric.TryGetValue(metric, out var list)) byMetric[metric] = list = new List<KeyValuePair<int, double>>();
            list.Add(new KeyValuePair<int, double>(second, value));
        }

        /// <summary>The values in second order.</summary>
        public IReadOnlyList<double> Series(int stage, string metric)
        {
            if (!_data.TryGetValue(stage, out var byMetric) || !byMetric.TryGetValue(metric, out var list)) return new double[0];
            return list.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        }

        /// <param name="stage">The step of the plan.</param>
        /// <param name="metric">The measurement's name.</param>
        /// <param name="skipSeconds">Seconds at the start of the step to leave out, unless that would leave nothing.</param>
        public Summary Get(int stage, string metric, int skipSeconds = 0)
        {
            var s = new Summary();
            if (!_data.TryGetValue(stage, out var byMetric) || !byMetric.TryGetValue(metric, out var list) || list.Count == 0) return s;
            var ordered = list.OrderBy(kv => kv.Key).ToList();
            var steady = ordered.Where(kv => kv.Key >= skipSeconds).Select(kv => kv.Value).ToList();
            var values = steady.Count > 0 ? steady : ordered.Select(kv => kv.Value).ToList();
            var sorted = values.OrderBy(v => v).ToList();
            s.Count = values.Count;
            s.Sum = values.Sum();
            s.Mean = s.Sum / s.Count;
            s.Min = sorted[0];
            s.Max = sorted[sorted.Count - 1];
            s.P95 = sorted[Math.Max(0, (int)Math.Ceiling(0.95 * sorted.Count) - 1)];
            s.Last = values[values.Count - 1];
            return s;
        }

        public bool Has(int stage) => _data.ContainsKey(stage);
    }

    /// <summary>
    /// Time per timed method within each step of a load test, per frame of that step, to show what
    /// costs the most under load and what grew the most compared with the baseline.
    /// </summary>
    public sealed class StageCosts
    {
        public sealed class Cost
        {
            public string Label;
            public double MsPerFrame, CallsPerFrame, BaselineMsPerFrame;
        }

        private readonly Dictionary<int, Dictionary<string, double[]>> _ms = new Dictionary<int, Dictionary<string, double[]>>();
        private readonly Dictionary<int, long> _frames = new Dictionary<int, long>();

        public void AddFrames(int stage, long frames)
        {
            _frames.TryGetValue(stage, out long f);
            _frames[stage] = f + frames;
        }

        public void Add(int stage, string label, double ms, long calls)
        {
            if (!_ms.TryGetValue(stage, out var d)) _ms[stage] = d = new Dictionary<string, double[]>();
            if (!d.TryGetValue(label, out var v)) d[label] = v = new double[2];
            v[0] += ms; v[1] += calls;
        }

        private double PerFrame(int stage, string label)
        {
            if (!_frames.TryGetValue(stage, out long f) || f <= 0) return 0;
            return _ms.TryGetValue(stage, out var d) && d.TryGetValue(label, out var v) ? v[0] / f : 0;
        }

        public List<Cost> Top(int stage, int n)
        {
            if (!_frames.TryGetValue(stage, out long f) || f <= 0 || !_ms.TryGetValue(stage, out var d)) return new List<Cost>();
            return d.Select(kv => new Cost { Label = kv.Key, MsPerFrame = kv.Value[0] / f, CallsPerFrame = kv.Value[1] / f })
                .OrderByDescending(c => c.MsPerFrame).ThenBy(c => c.Label, StringComparer.Ordinal).Take(n).ToList();
        }

        /// <summary>The methods whose time per frame grew the most from <paramref name="baseStage"/> to <paramref name="stage"/>.</summary>
        public List<Cost> Growth(int baseStage, int stage, int n)
        {
            if (!_frames.TryGetValue(stage, out long f) || f <= 0 || !_ms.TryGetValue(stage, out var d)) return new List<Cost>();
            return d.Select(kv => new Cost
                {
                    Label = kv.Key, MsPerFrame = kv.Value[0] / f, CallsPerFrame = kv.Value[1] / f, BaselineMsPerFrame = PerFrame(baseStage, kv.Key),
                })
                .OrderByDescending(c => c.MsPerFrame - c.BaselineMsPerFrame).ThenBy(c => c.Label, StringComparer.Ordinal).Take(n).ToList();
        }
    }
}
