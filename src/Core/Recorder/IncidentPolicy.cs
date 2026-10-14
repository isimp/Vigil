using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>
    /// Decides whether a detected problem gets its own report: at most one per kind within the
    /// quiet time, and at most a set number per session. A report asked for by hand is always
    /// written. Problems not written are still counted.
    /// </summary>
    public sealed class IncidentPolicy
    {
        private readonly double _cooldown;
        private readonly int _max;
        private readonly Dictionary<string, double> _last = new Dictionary<string, double>();
        private readonly Dictionary<string, int> _seen = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _suppressed = new Dictionary<string, int>();
        private int _written;

        public IncidentPolicy(double cooldownSeconds, int maxPerSession)
        {
            _cooldown = cooldownSeconds;
            _max = maxPerSession;
        }

        public int Written => _written;

        public bool ShouldWrite(string kind, double now, bool manual)
        {
            _seen[kind] = Seen(kind) + 1;
            bool ok = manual || (_written < _max && (!_last.TryGetValue(kind, out double last) || now - last >= _cooldown));
            if (ok)
            {
                _last[kind] = now;
                // Reports asked for by hand never use up the ones written on their own.
                if (!manual) _written++;
            }
            else
            {
                _suppressed[kind] = Suppressed(kind) + 1;
            }
            return ok;
        }

        public int Suppressed(string kind) => _suppressed.TryGetValue(kind, out int n) ? n : 0;
        public int Seen(string kind) => _seen.TryGetValue(kind, out int n) ? n : 0;

        public IEnumerable<KeyValuePair<string, int>> AllSeen => _seen;
    }
}
