using System;
using System.Collections.Generic;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// A simulated player's connection: it takes data at a fixed rate and the rest waits in its
    /// queue. The game sends a player more world updates only while that queue is short, so the
    /// rate sets how fast the server can feed the player, as with a real connection.
    /// </summary>
    public sealed class LinkModel
    {
        private double _queued;
        private double _last = double.NaN;

        public LinkModel(double rateKbps) { RateKbps = rateKbps; }

        public double RateKbps { get; }
        public long TotalBytes { get; private set; }

        private void Drain(double now)
        {
            if (double.IsNaN(_last)) { _last = now; return; }
            double dt = now - _last;
            _last = now;
            if (dt > 0) _queued = Math.Max(0, _queued - RateKbps * 1024 * dt);
        }

        public void Send(int bytes, double now)
        {
            Drain(now);
            _queued += bytes;
            TotalBytes += bytes;
        }

        public int Queued(double now)
        {
            Drain(now);
            return (int)Math.Round(_queued);
        }
    }

    /// <summary>
    /// A player arriving (joining, or teleporting) makes the server send the whole area around them
    /// at once. The arrival is caught up once the traffic to that player falls back to a trickle
    /// (at most <see cref="TrickleKb"/> kB in a second, or a fifth of the busiest second), or, at a
    /// place whose objects keep changing so the traffic never gets that low, once the player's link
    /// has room again: under nine tenths of its rate, with its queue drained.
    /// </summary>
    public sealed class ArrivalTracker
    {
        public const double LimitSeconds = 60, TrickleKb = 8, TrickleShare = 0.2, DrainedQueueKb = 2, FullLinkShare = 0.9;

        private double _arrived = double.NaN;
        private double _peak;

        public double BurstKb { get; private set; }
        public bool Active => !double.IsNaN(_arrived);

        public void Arrive(double now)
        {
            _arrived = now;
            _peak = 0;
            BurstKb = 0;
        }

        /// <summary>
        /// The kB sent to the player in the second ending at <paramref name="now"/>, with the player's
        /// queue at its end and its link's rate when known; the catch-up seconds once it is over.
        /// </summary>
        public double? Second(double now, double kb, double queueKb = double.NaN, double linkKbps = double.NaN)
        {
            if (!Active) return null;
            BurstKb += kb;
            _peak = Math.Max(_peak, kb);
            double since = now - _arrived;
            bool trickle = kb <= Math.Max(TrickleKb, TrickleShare * _peak);
            bool roomAgain = !double.IsNaN(queueKb) && !double.IsNaN(linkKbps) && queueKb <= DrainedQueueKb && kb < FullLinkShare * linkKbps;
            if (trickle || roomAgain)
            {
                _arrived = double.NaN;
                // The calm second began a second before it was reported.
                return Math.Max(0, since - 1);
            }
            if (since >= LimitSeconds)
            {
                _arrived = double.NaN;
                return LimitSeconds;
            }
            return null;
        }
    }

    /// <summary>A travelling simulated player running from stop to stop.</summary>
    public sealed class BotPath
    {
        private double _tx, _tz;

        public BotPath(double x, double z, double speed)
        {
            X = _tx = x;
            Z = _tz = z;
            Speed = speed;
        }

        public double X { get; private set; }
        public double Z { get; private set; }
        public double Speed { get; }

        public void Target(double x, double z) { _tx = x; _tz = z; }

        /// <summary>Moves for <paramref name="dt"/> seconds; true once at the stop.</summary>
        public bool Step(double dt)
        {
            double dx = _tx - X, dz = _tz - Z, d = Math.Sqrt(dx * dx + dz * dz), step = Speed * dt;
            if (d <= step) { X = _tx; Z = _tz; return true; }
            X += dx / d * step;
            Z += dz / d * step;
            return false;
        }
    }

    /// <summary>
    /// Where simulated players spread out: the zones holding the most world objects (the bases),
    /// one player per place, apart from each other and away from the world's center.
    /// </summary>
    public static class BusySpots
    {
        public struct Zone
        {
            public int X, Z, Objects;
            public Zone(int x, int z, int objects) { X = x; Z = z; Objects = objects; }
        }

        public static List<Zone> Pick(IEnumerable<Zone> zones, int count, int minZoneGap, double minMetersFromCenter, double zoneSize)
        {
            var picked = new List<Zone>();
            var ordered = zones
                .Where(z => Math.Sqrt((double)z.X * z.X + (double)z.Z * z.Z) * zoneSize >= minMetersFromCenter)
                .OrderByDescending(z => z.Objects).ThenBy(z => z.X).ThenBy(z => z.Z);
            foreach (var z in ordered)
            {
                if (picked.Count >= count) break;
                if (picked.Any(p => Math.Abs(p.X - z.X) < minZoneGap && Math.Abs(p.Z - z.Z) < minZoneGap)) continue;
                picked.Add(z);
            }
            return picked;
        }
    }

    /// <summary>Least-squares line through points; NaN when fewer than two distinct x values.</summary>
    public static class LinearFit
    {
        public static (double Slope, double Intercept) Fit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        {
            int n = Math.Min(xs.Count, ys.Count);
            if (n < 2) return (double.NaN, double.NaN);
            double mx = 0, my = 0;
            for (int i = 0; i < n; i++) { mx += xs[i]; my += ys[i]; }
            mx /= n; my /= n;
            double sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++) { sxx += (xs[i] - mx) * (xs[i] - mx); sxy += (xs[i] - mx) * (ys[i] - my); }
            if (sxx <= 0) return (double.NaN, double.NaN);
            double slope = sxy / sxx;
            return (slope, my - slope * mx);
        }
    }
}
