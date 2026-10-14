using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>
    /// A quiet time after something starts (a server, a player joining): until it has passed,
    /// that thing has not settled, and problems seen meanwhile are not worth a report.
    /// Anything never seen starting counts as settled.
    /// </summary>
    public sealed class Settling
    {
        private readonly double _quiet;
        private readonly Dictionary<string, double> _started = new Dictionary<string, double>();

        public Settling(double quietSeconds) { _quiet = quietSeconds; }

        public void Started(string key, double now) => _started[key] = now;

        public bool Settled(string key, double now) =>
            !_started.TryGetValue(key, out double t) || now - t >= _quiet;
    }
}
