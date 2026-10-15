using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>A creature hostile to the player that came into a base: when, which base, where it was, and its spawn when this game made it.</summary>
    internal sealed class Arrival
    {
        public string Id, Creature, Place;
        public double At;
        public float X, Z;

        /// <summary>This game's record of its spawn, or null.</summary>
        public SpawnRecord Spawn;

        /// <summary>What the game keeps about where it came from, when this game did not spawn it.</summary>
        public string Origin;
    }

    /// <summary>
    /// The creatures that came into a base, each once: the newest <see cref="Max"/>, read back newest
    /// first, those of one kind coming into the same base within <see cref="GroupSeconds"/> as one row.
    /// A row says where it spawned (the spawn log's record, else what the game keeps), and where a pin
    /// goes: where it spawned when known, else where it came in.
    /// </summary>
    internal sealed class BaseArrivals
    {
        public const int Max = 200;
        public const double GroupSeconds = 30;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private readonly List<Arrival> _arrivals = new List<Arrival>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public sealed class Row
        {
            public string When, What, Into, Spawned, PinLabel, Key;
            public float PinX, PinZ;
        }

        /// <summary>Keeps an arrival; false when this creature came in before.</summary>
        public bool Add(Arrival a)
        {
            if (a.Id != null && !_seen.Add(a.Id)) return false;
            _arrivals.Add(a);
            if (_arrivals.Count > Max) _arrivals.RemoveRange(0, _arrivals.Count - Max);
            return true;
        }

        public int Count(double now, double window) => _arrivals.Count(a => now - a.At <= window);

        public List<Row> Recent(double now, double window)
        {
            var rows = new List<Row>();
            List<Arrival> group = null;
            foreach (var a in _arrivals.Where(a => now - a.At <= window))
            {
                if (group != null && group[0].Creature == a.Creature && group[0].Place == a.Place && a.At - group[0].At <= GroupSeconds)
                {
                    group.Add(a);
                    continue;
                }

                if (group != null) rows.Add(RowOf(group, now));
                group = new List<Arrival> { a };
            }

            if (group != null) rows.Add(RowOf(group, now));
            rows.Reverse();
            return rows;
        }

        /// <summary>Where it spawned, in words: "by your game 4 min ago, 62 m out, outside bases · Meadows list, ..., hunts players".</summary>
        public static string Spawned(Arrival a, double now)
        {
            var s = a.Spawn;
            if (s == null) return a.Origin ?? "not seen spawning: another player's game ran that area, or it spawned before this session";
            return "by your game " + SpawnLog.Ago(now - s.At) + " ago, " + SpawnLog.Metres(s.Distance) + " out, " + (s.InsideBase ? "inside a base" : "outside bases")
                + " · " + SpawnLog.How(s) + (s.HuntsPlayers ? ", hunts players" : "");
        }

        private static Row RowOf(List<Arrival> group, double now)
        {
            var first = group[0];
            var spawned = group.FirstOrDefault(a => a.Spawn != null) ?? first;
            bool known = spawned.Spawn != null;
            return new Row
            {
                When = SpawnLog.Ago(now - first.At),
                What = first.Creature + (group.Count > 1 ? " ×" + group.Count.ToString(Ci) : ""),
                Into = first.Place,
                Spawned = Spawned(spawned, now),
                PinX = known ? spawned.Spawn.X : first.X,
                PinZ = known ? spawned.Spawn.Z : first.Z,
                PinLabel = first.Creature + (known ? " spawned here" : " came in here"),
                Key = first.Id ?? first.Creature + "@" + first.At.ToString("0", Ci),
            };
        }
    }

    /// <summary>
    /// A windmill's wind by the game's rules (assembly_valheim 1.0.20, <c>Windmill.GetPowerOutput</c>,
    /// <c>Cover.GetCoverForPoint</c>): output is (1 - cover) times the wind, which counts as none under
    /// <see cref="MinWind"/> and rises to all of it at full strength; the cover is the share of 17 rays
    /// from the propeller's hub, each <see cref="RayMetres"/> long, that hit something solid.
    /// </summary>
    internal static class WindCheck
    {
        public const float RayMetres = 29.5f, MinWind = 0.1f;
        public const int Rays = 17;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>The rays in the game's order: up, then level east, west, north, south and the four between, then the same at 45 degrees up.</summary>
        public static readonly string[] Directions =
        {
            "Up", "E", "W", "N", "S", "SW", "SE", "NW", "NE", "W 45°", "E 45°", "N 45°", "S 45°", "SW 45°", "SE 45°", "NW 45°", "NE 45°",
        };

        public sealed class Ray
        {
            public string Direction, By;
            public bool Blocked;
            public float Metres;
        }

        public static float Wind(float intensity) => Math.Max(0f, Math.Min(1f, (intensity - MinWind) / (1f - MinWind)));

        public static float Output(float cover, float intensity) => (1f - cover) * Wind(intensity);

        /// <summary>"58 % · wind 76 % × open 76 % · one flour every 17 s".</summary>
        public static string OutputLine(float intensity, int blocked, float secondsPerProduct)
        {
            float open = (Rays - blocked) / (float)Rays, wind = Wind(intensity), output = open * wind;
            if (wind <= 0f) return "0 % · the wind is under 10 %, which counts as none";
            if (open <= 0f) return "0 % · every ray is blocked";
            string line = Percent(output) + " · wind " + Percent(wind) + " × open " + Percent(open);
            return secondsPerProduct > 0f ? line + " · one flour every " + (secondsPerProduct / output).ToString("0", Ci) + " s" : line;
        }

        public static string CoverLine(IEnumerable<Ray> rays)
        {
            int blocked = rays.Count(r => r.Blocked);
            return blocked == 0 ? "no ray blocked: full output in full wind" : blocked.ToString(Ci) + " of 17 rays blocked, each costs about 6 %";
        }

        public static string OpenLine(IEnumerable<Ray> rays)
        {
            var open = rays.Where(r => !r.Blocked).Select(r => r.Direction).ToArray();
            return open.Length == 0 ? "none" : string.Join(", ", open);
        }

        /// <summary>The blocked rays, nearest first: direction, what blocks it, how far.</summary>
        public static List<string[]> BlockedRows(IEnumerable<Ray> rays) =>
            rays.Where(r => r.Blocked).OrderBy(r => r.Metres).Select(r => new[] { r.Direction, r.By ?? "something solid", Math.Round(r.Metres).ToString("0", Ci) + " m" }).ToList();

        private static string Percent(float v) => Math.Round(v * 100f).ToString("0", Ci) + " %";
    }
}
