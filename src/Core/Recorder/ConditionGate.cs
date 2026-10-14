using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>
    /// Turns a lasting state into one event: <see cref="Update"/> is true only when the state
    /// begins, and a state counts as having ended only after it has been off for
    /// <c>clearSeconds</c>, so a brief dip does not make it begin again.
    /// </summary>
    public sealed class ConditionGate
    {
        private sealed class State { public bool On; public double Since; public double LastActive; }

        private readonly double _clear;
        private readonly Dictionary<string, State> _states = new Dictionary<string, State>();

        public ConditionGate(double clearSeconds) { _clear = clearSeconds; }

        public bool Update(string kind, bool active, double now)
        {
            if (!_states.TryGetValue(kind, out var s)) _states[kind] = s = new State();
            if (s.On && !active && now - s.LastActive >= _clear) s.On = false;
            if (!active) return false;
            s.LastActive = now;
            if (s.On) return false;
            s.On = true;
            s.Since = now;
            return true;
        }

        /// <summary>Seconds since the state began, 0 when it is not on.</summary>
        public double ActiveFor(string kind, double now) =>
            _states.TryGetValue(kind, out var s) && s.On ? now - s.Since : 0;
    }
}
