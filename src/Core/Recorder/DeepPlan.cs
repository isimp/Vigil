using System;
using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>What a deep capture times, and what it drops again because timing it costs more than it tells.</summary>
    public static class DeepPlan
    {
        private const double HotCallsPerFrame = 300;
        private const double CheapMicros = 2;

        private static readonly HashSet<string> Once = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "OnDestroy", "OnEnable", "OnDisable", ".ctor", ".cctor",
        };

        private static readonly string[] OncePrefixes = { "Load", "Save", "Setup", "Init", "Register", "Copy" };

        /// <summary>False for methods that run once or at load/save time rather than frame to frame.</summary>
        public static bool WorthTiming(string methodName)
        {
            if (string.IsNullOrEmpty(methodName) || Once.Contains(methodName)) return false;
            foreach (var p in OncePrefixes)
                if (methodName.StartsWith(p, StringComparison.Ordinal) && methodName.Length > p.Length && char.IsUpper(methodName[p.Length]))
                    return false;
            return true;
        }

        /// <summary>True for a method called so often, and so cheap per call, that the timer itself would dominate.</summary>
        public static bool TooHot(double callsPerFrame, double microsPerCall) =>
            callsPerFrame >= HotCallsPerFrame && microsPerCall < CheapMicros;
    }

    /// <summary>
    /// Work spread over frames so that it costs about <c>budgetMs</c> per frame on average. Each
    /// step earns one frame's budget (unused budget does not pile up beyond one frame) and works
    /// while it has budget left; an item that costs more than that is paid back by the next frames
    /// doing nothing. Only the time of the work itself counts.
    /// </summary>
    public sealed class BudgetedQueue<T>
    {
        private readonly Queue<T> _items = new Queue<T>();
        private double _credit;

        public int Count => _items.Count;

        public void Add(IEnumerable<T> items)
        {
            foreach (var i in items) _items.Enqueue(i);
        }

        /// <summary>Drops the work not done yet.</summary>
        public void Clear() => _items.Clear();

        /// <param name="work">Does one item.</param>
        /// <param name="elapsedMs">A clock in milliseconds, read around each item to measure its cost.</param>
        /// <param name="budgetMs">Milliseconds per frame the work may take on average.</param>
        public int Step(Action<T> work, Func<double> elapsedMs, double budgetMs)
        {
            if (_items.Count == 0) return 0;
            _credit = Math.Min(_credit + budgetMs, budgetMs);
            int done = 0;
            while (_items.Count > 0 && _credit > 0)
            {
                double before = elapsedMs();
                work(_items.Dequeue());
                _credit -= elapsedMs() - before;
                done++;
            }
            return done;
        }
    }
}
