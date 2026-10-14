using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The steps of a load test: a quiet baseline, then world-update traffic from the player to the
    /// server ("up") rising step by step, a quiet stretch that shows how fast the server catches
    /// up, then the same from the server to the player ("down").
    /// </summary>
    public sealed class LoadPlan
    {
        public enum Kind { Baseline, Load, Recovery }

        public sealed class Stage
        {
            public int Index;
            public Kind Kind;
            public string Direction;   // "up", "down", "players", or null for the baseline
            public double RateKbps;
            public int Players;        // simulated players during this step
            public double Start, Seconds;

            public string Name =>
                Kind == Kind.Baseline ? "baseline"
                : Kind == Kind.Recovery ? Direction + " recovery"
                : Direction == "players" ? Players + (Players == 1 ? " player" : " players")
                : Direction + " " + RateKbps.ToString("0", CultureInfo.InvariantCulture) + " kB/s";
        }

        public static readonly double[] Rates = { 50, 100, 200, 400, 800 };
        public const double BaselineSeconds = 15, StepSeconds = 20, RecoverySeconds = 30;
        public const double PlayerStepSeconds = 30;
        public const int MaxPlayers = 32;

        /// <summary>Simulated players doubling each step (1, 2, 4 ...) up to <paramref name="maxPlayers"/>, then a recovery without them.</summary>
        public static LoadPlan ForPlayers(int maxPlayers)
        {
            int max = Math.Max(1, Math.Min(MaxPlayers, maxPlayers));
            var plan = new LoadPlan { Directions = "players" };
            plan.Add(Kind.Baseline, null, 0, BaselineSeconds);
            int n = 1;
            for (; n < max; n *= 2) plan.Add(Kind.Load, "players", 0, PlayerStepSeconds, n);
            plan.Add(Kind.Load, "players", 0, PlayerStepSeconds, max);
            plan.Add(Kind.Recovery, "players", 0, RecoverySeconds);
            return plan;
        }
        // Each test object is changed this often; the game sends a changed object at most 20 times a second.
        public const double ObjectHz = 10;

        private readonly List<Stage> _stages = new List<Stage>();

        public IReadOnlyList<Stage> Stages => _stages;
        public double TotalSeconds { get; private set; }
        public string Directions { get; private set; }

        /// <param name="directions">"up", "down" or "both" (anything else).</param>
        public static LoadPlan For(string directions)
        {
            string d = directions == "up" || directions == "down" ? directions : "both";
            var plan = new LoadPlan { Directions = d };
            plan.Add(Kind.Baseline, null, 0, BaselineSeconds);
            foreach (var dir in d == "both" ? new[] { "up", "down" } : new[] { d })
            {
                foreach (var r in Rates) plan.Add(Kind.Load, dir, r, StepSeconds);
                plan.Add(Kind.Recovery, dir, 0, RecoverySeconds);
            }
            return plan;
        }

        private void Add(Kind kind, string dir, double rate, double seconds, int players = 0)
        {
            _stages.Add(new Stage { Index = _stages.Count, Kind = kind, Direction = dir, RateKbps = rate, Players = players, Start = TotalSeconds, Seconds = seconds });
            TotalSeconds += seconds;
        }

        /// <summary>The stage at <paramref name="t"/> seconds into the plan, or null once it is over.</summary>
        public Stage At(double t)
        {
            if (t < 0) return null;
            foreach (var s in _stages) if (t < s.Start + s.Seconds) return s;
            return null;
        }

        /// <summary>The recovery that follows the given direction's steps.</summary>
        public Stage RecoveryFor(string direction) =>
            _stages.FirstOrDefault(s => s.Kind == Kind.Recovery && s.Direction == direction);

        /// <summary>Objects to change <see cref="ObjectHz"/> times a second for <paramref name="rateKbps"/> of traffic.</summary>
        public static int ObjectsFor(double rateKbps, int payloadBytes) =>
            rateKbps <= 0 ? 0 : (int)Math.Ceiling(rateKbps * 1024 / (payloadBytes * ObjectHz));
    }

    /// <summary>
    /// A plan running from a start time. An abort ends the load at once: the recovery of the
    /// current direction still runs, so the catch-up is measured, and nothing after it.
    /// </summary>
    public sealed class LoadSchedule
    {
        private readonly LoadPlan _plan;
        private double _start = double.NaN;
        private double _offset;
        private int _lastAllowed;

        public LoadSchedule(LoadPlan plan)
        {
            _plan = plan;
            _lastAllowed = plan.Stages.Count - 1;
        }

        public LoadPlan Plan => _plan;
        public bool Started => !double.IsNaN(_start);
        public string AbortReason { get; private set; }

        public void Start(double now) { if (!Started) _start = now; }

        private double Elapsed(double now) => now - _start + _offset;

        public LoadPlan.Stage Current(double now)
        {
            if (!Started) return null;
            var s = _plan.At(Elapsed(now));
            return s == null || s.Index > _lastAllowed ? null : s;
        }

        public double SecondsIntoStage(double now)
        {
            var s = Current(now);
            return s == null ? double.NaN : Elapsed(now) - s.Start;
        }

        public bool Finished(double now) => Started && Current(now) == null;

        public void Abort(string reason, double now)
        {
            if (AbortReason != null) return;
            AbortReason = reason;
            var cur = Current(now);
            if (cur == null) { _lastAllowed = -1; return; }
            if (cur.Kind == LoadPlan.Kind.Baseline) { _lastAllowed = -1; return; }
            if (cur.Kind == LoadPlan.Kind.Recovery) { _lastAllowed = cur.Index; return; }
            var rec = _plan.RecoveryFor(cur.Direction);
            if (rec == null) { _lastAllowed = -1; return; }
            _offset += rec.Start - Elapsed(now);
            _lastAllowed = rec.Index;
        }
    }
}
