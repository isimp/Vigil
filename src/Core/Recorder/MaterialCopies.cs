using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// Which component types sit on the objects whose renderers draw with material copies of their
    /// own, compared with the objects that share their materials. A type found on most objects with
    /// copies and on few without is the likeliest cause; a type on every object says nothing.
    /// </summary>
    public sealed class CopySuspects
    {
        public sealed class Suspect
        {
            public string Component;
            public int OnCopied, OnClean;
            public double Lead;   // share among objects with copies minus share among the others
        }

        private readonly Dictionary<string, int[]> _counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private int _copied, _clean;

        public void Add(bool copied, IEnumerable<string> components)
        {
            if (copied) _copied++; else _clean++;
            foreach (var c in (components ?? Enumerable.Empty<string>()).Distinct())
            {
                if (!_counts.TryGetValue(c, out var n)) _counts[c] = n = new int[2];
                n[copied ? 0 : 1]++;
            }
        }

        public List<Suspect> Ranked()
        {
            if (_copied == 0) return new List<Suspect>();
            return _counts.Select(kv => new Suspect
                {
                    Component = kv.Key, OnCopied = kv.Value[0], OnClean = kv.Value[1],
                    Lead = (double)kv.Value[0] / _copied - (_clean > 0 ? (double)kv.Value[1] / _clean : 0),
                })
                .Where(s => s.Lead >= 0.2)
                .OrderByDescending(s => s.Lead).ThenByDescending(s => s.OnCopied).ThenBy(s => s.Component, StringComparer.Ordinal)
                .ToList();
        }

        public List<string> Report(int top)
        {
            var ci = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                string.Format(ci, "{0} of {1} objects draw with material copies of their own.", _copied, _copied + _clean),
            };
            var ranked = Ranked();
            if (_copied == 0) return lines;
            if (ranked.Count == 0)
            {
                lines.Add("No component type sets them apart: the copies are likely made by code patched into every object of a kind.");
                return lines;
            }
            lines.Add("Component types on those objects more often than on the others (on objects with copies / on the others):");
            foreach (var s in ranked.Take(top))
                lines.Add(string.Format(ci, "   {0,-40} {1,6} of {2}  /  {3,6} of {4}", s.Component, s.OnCopied, _copied, s.OnClean, _clean));
            return lines;
        }
    }

    /// <summary>
    /// The code that made material copies, while the tracer ran: per caller, how many copies, the
    /// mod it belongs to and the objects it made them for. Bounded, so a busy caller cannot grow it.
    /// </summary>
    public sealed class CopyTrace
    {
        private sealed class Caller
        {
            public string Owner;
            public int Count;
            public readonly Dictionary<string, int> Objects = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, Caller> _callers = new Dictionary<string, Caller>(StringComparer.Ordinal);
        private readonly int _maxCallers;
        private int _other;

        public int Total { get; private set; }

        public CopyTrace(int maxCallers = 200) { _maxCallers = maxCallers; }

        public void Add(string caller, string owner, string objectName)
        {
            Total++;
            caller = caller ?? "?";
            if (!_callers.TryGetValue(caller, out var c))
            {
                if (_callers.Count >= _maxCallers) { _other++; return; }
                _callers[caller] = c = new Caller { Owner = owner ?? "?" };
            }
            c.Count++;
            string o = objectName ?? "?";
            if (c.Objects.TryGetValue(o, out int n) || c.Objects.Count < 50) c.Objects[o] = n + 1;
        }

        /// <summary>The callers with most copies first, each with its mod and the object it copied for most, for the panel.</summary>
        public List<(string Caller, string Owner, int Count, string TopObject)> Callers() =>
            _callers.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key, StringComparer.Ordinal)
                .Select(k => (k.Key, k.Value.Owner, k.Value.Count,
                    k.Value.Objects.OrderByDescending(o => o.Value).ThenBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Key).FirstOrDefault())).ToList();

        /// <summary>Copies from callers past the ones kept apart.</summary>
        public int Other => _other;

        public List<string> Report(int top)
        {
            var ci = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            foreach (var kv in _callers.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key, StringComparer.Ordinal).Take(top))
            {
                var objects = kv.Value.Objects.OrderByDescending(o => o.Value).ThenBy(o => o.Key, StringComparer.Ordinal).Take(8)
                    .Select(o => o.Key + " " + o.Value.ToString(ci));
                lines.Add(string.Format(ci, "{0,7} copies  {1}: {2}", kv.Value.Count, kv.Value.Owner, kv.Key));
                lines.Add("            on " + string.Join(", ", objects.ToArray()));
            }
            if (_other > 0) lines.Add(string.Format(ci, "{0,7} copies from other callers (only the first {1} are kept apart)", _other, _maxCallers));
            return lines;
        }
    }

    /// <summary>The graphics settings in force as short words, the same text for the same settings.</summary>
    public static class GraphicsSignature
    {
        public static string Format(IEnumerable<(string Name, int Value)> ints, IEnumerable<(string Name, bool On)> bools)
        {
            var parts = ints.Select(i => i.Name + i.Value.ToString(CultureInfo.InvariantCulture))
                .Concat(bools.Select(b => b.Name + (b.On ? "+" : "-")));
            return string.Join(" ", parts.ToArray());
        }
    }
}
