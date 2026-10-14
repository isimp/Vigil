using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>How a spawn entry stands at a spot: it could spawn there, not with the world as it is now, or never there.</summary>
    internal enum SpawnVerdict
    {
        Could,
        NotNow,
        No,
    }

    /// <summary>
    /// A spot as the game's spawn check reads it (<c>SpawnSystem.IsSpawnPointGood</c>), and the state
    /// of the world there now (<c>SpawnSystem.UpdateSpawnList</c>).
    /// </summary>
    internal sealed class SpawnSpot
    {
        /// <summary>The biome's flag at the spot (<c>Heightmap.Biome</c>); 0 when the ground there is not loaded.</summary>
        public int Biome;

        public string BiomeName;

        /// <summary>1 where biomes meet, 2 inside one (<c>Heightmap.BiomeArea</c>, per zone).</summary>
        public int Area = 2;

        /// <summary>Something solid above or below (<c>ZoneSystem.IsBlocked</c>).</summary>
        public bool Blocked;

        public bool InsideBase, InForest, OnLava;

        /// <summary>The ground's height; the sea lies at <see cref="SpawnRules.SeaLevel"/>.</summary>
        public float Ground;

        /// <summary>The upward part of the ground's normal; 1 on flat ground.</summary>
        public float NormalY = 1f;

        public float FromCentre, OceanDepth;

        public bool Day, Night;

        /// <summary>The weather now, by the game's name for it.</summary>
        public string Weather;

        /// <summary>Whether a world key is set.</summary>
        public Func<string, bool> KeySet;

        /// <summary>The world event at the spot now, or null.</summary>
        public string EventHere;

        /// <summary>The game's debug switch that stops every spawn (<c>SpawnSystem.m_nospawn</c>).</summary>
        public bool SpawningOff;

        /// <summary>The entry names the spot's special areas leave out, each with that area's name.</summary>
        public Dictionary<string, string> LeftOut;
    }

    /// <summary>One entry of a spawn list as the game holds it now, with what the world around the spot holds for it.</summary>
    internal sealed class SpawnEntryFacts
    {
        /// <summary>The entry's name, its prefab's name and the creature as the game names it.</summary>
        public string Name, Prefab, Creature;

        public SpawnSource Source = SpawnSource.Biome;

        /// <summary>The raid's or the special area's name.</summary>
        public string List;

        public bool Enabled = true;

        public int Biome, Area = 3;

        public float Chance = 100f, MinAltitude = -1000f, MaxAltitude = 1000f, MinTilt, MaxTilt = 35f;

        public float MinFromCentre, MaxFromCentre, RadiusMin, RadiusMax, MinOceanDepth, MaxOceanDepth;

        public bool CloseToPlayer, InsideBase, InForest = true, OutsideForest = true, InLava, OutsideLava = true, AtDay = true, AtNight = true;

        public string GlobalKey, PersistentEvent;

        public IList<string> Weathers;

        /// <summary>The most about at once (0 for no limit), and how many are about now in the area the game counts.</summary>
        public int MaxSpawned = 1, Nearby;

        /// <summary>How near another of the same creature may be, and how near the nearest is.</summary>
        public float SpawnDistance = 10f, NearestSame = float.PositiveInfinity;
    }

    /// <summary>The answer for one entry: its verdict and why, or for one that could, what it still needs.</summary>
    internal sealed class SpawnAnswer
    {
        public SpawnEntryFacts Entry;
        public SpawnVerdict Verdict;
        public List<string> Reasons = new List<string>();

        /// <summary>The entry as listed: the creature, and its raid, special area or entry name where that tells it apart.</summary>
        public string Label;

        public string Now => Verdict == SpawnVerdict.Could ? "could" : Verdict == SpawnVerdict.NotNow ? "not now" : "no";

        public string Why => string.Join("; ", Reasons);
    }

    /// <summary>
    /// The game's rules for a spawn, read for one spot (assembly_valheim 1.0.17). A spot rule
    /// (<c>IsSpawnPointGood</c>: biome area, solid above or below, height above the sea, slope,
    /// distance from the world's centre, base, forest, lava, water depth) failed means the entry
    /// never spawns there: "no", every failed rule named. Otherwise a rule of the world's state
    /// (<c>UpdateSpawnList</c>: world key, weather, day or night, the count about, a world event, another
    /// of the same close by) failed means "not now", each named. An entry passing both could spawn
    /// there once a player stands at its spawn range and no player nearer than its minimum
    /// (<c>FindBaseSpawnPoint</c>). Other mods' own checks are not seen.
    /// </summary>
    internal static class SpawnRules
    {
        public const float SeaLevel = 30f, PlayerMin = 40f, PlayerMax = 80f;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static SpawnAnswer Judge(SpawnEntryFacts e, SpawnSpot s)
        {
            var answer = new SpawnAnswer { Entry = e, Label = e.Creature ?? e.Name };
            var spot = SpotReasons(e, s);
            if (spot.Count > 0)
            {
                answer.Verdict = SpawnVerdict.No;
                answer.Reasons = spot;
                return answer;
            }

            var world = WorldReasons(e, s);
            if (world.Count > 0)
            {
                answer.Verdict = SpawnVerdict.NotNow;
                answer.Reasons = world;
                return answer;
            }

            float min = e.RadiusMin > 0f ? e.RadiusMin : PlayerMin, max = e.RadiusMax > 0f ? e.RadiusMax : PlayerMax;
            answer.Verdict = SpawnVerdict.Could;
            answer.Reasons.Add(string.Format(Ci, "when a player is {0:0} to {1:0} m away", min, max) + (e.CloseToPlayer ? "" : ", none nearer"));
            return answer;
        }

        /// <summary>
        /// The answers for the entries of the spot's biome, those that could first, then those not
        /// now, then those that never spawn there, each by name. Entries the game or a mod switched off
        /// are left out and counted in <paramref name="switchedOff"/>.
        /// </summary>
        public static List<SpawnAnswer> ForSpot(IEnumerable<SpawnEntryFacts> entries, SpawnSpot s, out int switchedOff)
        {
            switchedOff = 0;
            var answers = new List<SpawnAnswer>();
            if (s == null || s.Biome == 0) return answers;
            foreach (var e in entries)
            {
                if (e == null || (e.Biome & s.Biome) == 0) continue;
                if (!e.Enabled)
                {
                    switchedOff++;
                    continue;
                }
                answers.Add(Judge(e, s));
            }

            var twice = new HashSet<string>(answers.GroupBy(a => a.Entry.Prefab ?? a.Label).Where(g => g.Count() > 1).Select(g => g.Key));
            foreach (var a in answers)
            {
                var e = a.Entry;
                if (e.Source == SpawnSource.Raid) a.Label += " (raid: " + e.List + ")";
                else if (e.Source == SpawnSource.SpecialArea) a.Label += " (" + e.List + ")";
                if (twice.Contains(e.Prefab ?? a.Label) && !string.IsNullOrEmpty(e.Name) && e.Name != e.Prefab) a.Label += " · " + e.Name;
            }

            return answers.OrderBy(a => (int)a.Verdict).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The spot in one line: biome, height, slope, water, lava, forest, base, sky.</summary>
        public static string SpotLine(SpawnSpot s)
        {
            if (s == null || s.Biome == 0) return "the ground there is not loaded";
            var parts = new List<string>
            {
                (s.BiomeName ?? "?") + (s.Area == 1 ? ", where biomes meet" : ""),
                Height(s.Ground - SeaLevel),
                "slope " + Tilt(s.NormalY).ToString("0", Ci) + "°",
            };
            if (s.OceanDepth > 0f) parts.Add("water " + s.OceanDepth.ToString("0.#", Ci) + " m deep");
            if (s.OnLava) parts.Add("on lava");
            parts.Add(s.InForest ? "in a forest" : "no forest");
            parts.Add(s.InsideBase ? "inside a base" : "outside bases");
            parts.Add(s.Blocked ? "something solid above or below" : "open sky");
            return string.Join(" · ", parts);
        }

        /// <summary>The slope in degrees from the ground normal's upward part.</summary>
        public static double Tilt(float normalY) => Math.Acos(Math.Max(-1.0, Math.Min(1.0, normalY))) * 180.0 / Math.PI;

        // IsSpawnPointGood's checks, in its order, and the special areas' blocked names before them.
        private static List<string> SpotReasons(SpawnEntryFacts e, SpawnSpot s)
        {
            var r = new List<string>();
            if (s.LeftOut != null && e.Name != null && s.LeftOut.TryGetValue(e.Name, out string area)) r.Add(area + " here leaves it out");
            if (e.Chance <= 0f) r.Add("its chance is 0 %");
            if ((e.Area & s.Area) == 0)
                r.Add(s.Area == 1 ? "only inside a biome, away from where biomes meet" : "only where biomes meet; this area lies inside one");
            if (s.Blocked) r.Add("something solid is above or below the spot");

            float altitude = s.Ground - SeaLevel;
            if (altitude < e.MinAltitude || altitude > e.MaxAltitude) r.Add("needs ground " + HeightRange(e.MinAltitude, e.MaxAltitude) + "; here " + Height(altitude));

            double tilt = Tilt(s.NormalY);
            if (s.NormalY < (float)Math.Cos(Math.PI / 180.0 * e.MaxTilt)) r.Add(string.Format(Ci, "needs a slope of {0:0}° or less; here {1:0}°", e.MaxTilt, tilt));
            else if (s.NormalY > (float)Math.Cos(Math.PI / 180.0 * e.MinTilt)) r.Add(string.Format(Ci, "needs a slope of {0:0}° or more; here {1:0}°", e.MinTilt, tilt));

            if (s.FromCentre < e.MinFromCentre)
                r.Add(string.Format(Ci, "needs to be {0:0} m or more from the world's centre; here {1:0} m", e.MinFromCentre, s.FromCentre));
            if (e.MaxFromCentre > 0f && s.FromCentre > e.MaxFromCentre)
                r.Add(string.Format(Ci, "needs to be {0:0} m or less from the world's centre; here {1:0} m", e.MaxFromCentre, s.FromCentre));

            if (!e.InsideBase && s.InsideBase) r.Add("inside a player base, which its entry does not allow");
            if (!e.InForest && s.InForest) r.Add("only outside forests");
            if (!e.OutsideForest && !s.InForest) r.Add("only in forests");
            if (!e.InLava && s.OnLava) r.Add("not on lava");
            if (!e.OutsideLava && !s.OnLava) r.Add("only on lava");
            if (e.MinOceanDepth != e.MaxOceanDepth && (s.OceanDepth < e.MinOceanDepth || s.OceanDepth > e.MaxOceanDepth))
                r.Add("needs water " + e.MinOceanDepth.ToString("0.#", Ci) + " to " + e.MaxOceanDepth.ToString("0.#", Ci) + " m deep; here " + s.OceanDepth.ToString("0.#", Ci) + " m");
            return r;
        }

        // UpdateSpawnList's checks of the world's state, in its order.
        private static List<string> WorldReasons(SpawnEntryFacts e, SpawnSpot s)
        {
            var r = new List<string>();
            if (s.SpawningOff) r.Add("spawning is switched off on this game (a debug switch)");
            if (!string.IsNullOrEmpty(e.GlobalKey) && (s.KeySet == null || !s.KeySet(e.GlobalKey))) r.Add("needs the world key " + e.GlobalKey);
            if (e.Weathers != null && e.Weathers.Count > 0 && !e.Weathers.Contains(s.Weather))
                r.Add("needs weather " + string.Join(" or ", e.Weathers.ToArray()) + "; now " + (s.Weather ?? "unknown"));
            if (!e.AtDay && s.Day) r.Add("night only");
            if (!e.AtNight && s.Night) r.Add("day only");
            if (e.MaxSpawned > 0 && e.Nearby >= e.MaxSpawned) r.Add(string.Format(Ci, "{0} of {1} already nearby", e.Nearby, e.MaxSpawned));
            if (!string.IsNullOrEmpty(e.PersistentEvent) && !string.Equals(e.PersistentEvent, s.EventHere, StringComparison.OrdinalIgnoreCase))
                r.Add("needs the event " + e.PersistentEvent + " here");
            if (e.SpawnDistance > 0f && e.NearestSame < e.SpawnDistance)
                r.Add(string.Format(Ci, "another one is {0:0} m away; it needs {1:0} m", e.NearestSame, e.SpawnDistance));
            return r;
        }

        private static string HeightRange(float min, float max)
        {
            bool low = min > -1000f, high = max < 1000f;
            if (low && high) return "from " + Height(min) + " to " + Height(max);
            return high ? Height(max) + " or lower" : Height(min) + " or higher";
        }

        private static string Height(float metres)
        {
            string m = Math.Abs(metres).ToString("0.#", Ci);
            return m == "0" ? "sea level" : m + (metres > 0f ? " m above the sea" : " m below the sea");
        }
    }
}
