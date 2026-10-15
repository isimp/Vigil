using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>Where a spawn came from: a biome's list, a special area's list, a raid, a spawner structure (a nest) or a fixed spawner (in a location).</summary>
    internal enum SpawnSource
    {
        Biome,
        SpecialArea,
        Raid,
        Structure,
        FixedSpawner,
    }

    /// <summary>One spawn this game made, or one a mod's check cancelled before the creature appeared.</summary>
    internal sealed class SpawnRecord
    {
        /// <summary>When, in seconds of the game's clock.</summary>
        public double At;

        /// <summary>The creature's object id; null for a cancelled spawn.</summary>
        public string Id;

        public string Creature;
        public SpawnSource Source;

        /// <summary>The biome, special area, raid, structure or location.</summary>
        public string Detail;

        /// <summary>The list entry's name, for the lists.</summary>
        public string Entry;

        public bool NightOnly, InsideBase, AllowedInBase, Cancelled;

        /// <summary>The entry sends it after the players (<c>SpawnData.m_huntPlayer</c>).</summary>
        public bool HuntsPlayers;

        /// <summary>The mods with a check that can cancel this kind of spawn, for a cancelled one.</summary>
        public string CancelledBy;

        public int Level;

        /// <summary>How far from this game's player it appeared.</summary>
        public float Distance;

        public float X, Y, Z;

        /// <summary>For a spawn inside a base: the piece whose base area it is, its builder's player id and name (null when this game did not know it), and where the piece stands.</summary>
        public string BasePiece, BaseBuilder;

        public long BaseBuilderId;

        public float BaseX, BaseZ;

        public SpawnRecord Copy() => (SpawnRecord)MemberwiseClone();
    }

    /// <summary>
    /// The spawns this game made, newest kept (<see cref="Max"/>), read back as rows: spawns of one
    /// kind made together (a group, within <see cref="GroupSeconds"/>) as one row with its count,
    /// newest first, then the cancelled spawns as one row for each kind over the whole window, since a
    /// mod's check may cancel the same spawn every few seconds. Cancelled spawns are kept apart, as
    /// many again, so they never push real spawns out.
    /// </summary>
    internal sealed class SpawnLog
    {
        public const int Max = 400;
        public const double GroupSeconds = 2;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private readonly List<SpawnRecord> _records = new List<SpawnRecord>();
        private readonly List<SpawnRecord> _cancelled = new List<SpawnRecord>();

        public sealed class Row
        {
            public string When, What, How, Where;
            public int Count;
            public bool InsideBase, Cancelled;
        }

        /// <summary>The spawns kept, oldest first; cancelled ones not among them.</summary>
        public IReadOnlyList<SpawnRecord> Records => _records;

        /// <summary>The cancelled spawns kept, oldest first.</summary>
        public IReadOnlyList<SpawnRecord> CancelledRecords => _cancelled;

        public void Add(SpawnRecord record)
        {
            var list = record.Cancelled ? _cancelled : _records;
            list.Add(record);
            if (list.Count > Max) list.RemoveRange(0, list.Count - Max);
        }

        /// <summary>The newest spawn of the creature with this object id; null when this game did not spawn it (or it left the log).</summary>
        public SpawnRecord Find(string id) =>
            id == null ? null : _records.LastOrDefault(r => r.Id == id);

        /// <summary>How many spawns within the window appeared inside a base.</summary>
        public int InsideBase(double now, double window) => _records.Count(r => r.InsideBase && now - r.At <= window);

        /// <summary>The rows within the window, newest first; <paramref name="where"/> words where a spawn appeared (<see cref="Where(SpawnRecord)"/> when null).</summary>
        public List<Row> Recent(double now, double window, Func<SpawnRecord, string> where = null)
        {
            var rows = new List<Row>();
            List<SpawnRecord> group = null;
            foreach (var r in _records.Where(r => now - r.At <= window))
            {
                if (group != null && SameKind(group[0], r) && r.At - group[0].At <= GroupSeconds)
                {
                    group.Add(r);
                    continue;
                }

                if (group != null) rows.Add(SpawnRow(group, now, where));
                group = new List<SpawnRecord> { r };
            }

            if (group != null) rows.Add(SpawnRow(group, now, where));
            rows.Reverse();

            foreach (var kind in _cancelled.Where(r => now - r.At <= window).GroupBy(Key))
            {
                var all = kind.ToList();
                var last = all[all.Count - 1];
                rows.Add(new Row
                {
                    When = Ago(now - last.At),
                    What = What(last, all.Count),
                    How = How(last),
                    Where = "cancelled" + (string.IsNullOrEmpty(last.CancelledBy) ? " by a mod's check" : ": " + last.CancelledBy + " can stop it"),
                    Count = all.Count,
                    Cancelled = true,
                });
            }

            return rows;
        }

        /// <summary>Where the spawn came from, in words.</summary>
        public static string How(SpawnRecord r)
        {
            switch (r.Source)
            {
                case SpawnSource.Biome: return r.Detail + " list, " + r.Entry + (r.NightOnly ? ", night only" : "");
                case SpawnSource.SpecialArea: return "special area " + r.Detail + ", " + r.Entry + (r.NightOnly ? ", night only" : "");
                case SpawnSource.Raid: return "raid " + r.Detail;
                case SpawnSource.Structure: return "spawner structure: " + r.Detail;
                default: return string.IsNullOrEmpty(r.Detail) ? "a fixed spawner" : "fixed spawner of " + r.Detail;
            }
        }

        /// <summary>Where it appeared: how far from the player, and inside a base (which, when known) or not.</summary>
        public static string Where(SpawnRecord r) => Metres(r.Distance) + ", " + (r.InsideBase ? InBase(r, null) : "outside bases");

        /// <summary>A spawn inside a base: the base's piece and builder when known, and whether its entry allows bases.</summary>
        public static string InBase(SpawnRecord r, Func<long, string> names) =>
            "inside a base" + (string.IsNullOrEmpty(r.BasePiece) ? "" : ": " + SpawnShare.Place(r, names)) + (r.AllowedInBase ? " (its entry allows that)" : "");

        /// <summary>The spawn as one line of the events file.</summary>
        public static string EventDetail(SpawnRecord r) => How(r) + "; " + Where(r);

        /// <summary>How long ago, in its largest unit: "45 s", "2 min", "1 h 5 min".</summary>
        public static string Ago(double seconds)
        {
            if (seconds < 60) return Math.Max(0, (int)seconds).ToString(Ci) + " s";
            if (seconds < 3600) return ((int)(seconds / 60)).ToString(Ci) + " min";
            return string.Format(Ci, "{0} h {1} min", (int)(seconds / 3600), (int)(seconds % 3600 / 60));
        }

        public static string Metres(float distance) => Math.Round(distance).ToString("0", Ci) + " m";

        /// <summary>A biome as the game names it to players, from its name in the game's code.</summary>
        public static string BiomeName(string code) => code switch
        {
            "BlackForest" => "Black Forest",
            "AshLands" => "Ashlands",
            "DeepNorth" => "Deep North",
            _ => code,
        };

        private static Row SpawnRow(List<SpawnRecord> group, double now, Func<SpawnRecord, string> where)
        {
            var first = group[0];
            var shown = group.FirstOrDefault(r => r.InsideBase) ?? first;
            return new Row
            {
                When = Ago(now - first.At),
                What = What(first, group.Count),
                How = How(first),
                Where = where != null ? where(shown) : Where(shown),
                Count = group.Count,
                InsideBase = group.Any(r => r.InsideBase),
            };
        }

        private static string What(SpawnRecord r, int count)
        {
            string stars = r.Level > 1 ? ", " + (r.Level - 1).ToString(Ci) + (r.Level == 2 ? " star" : " stars") : "";
            return r.Creature + stars + (count > 1 ? " ×" + count.ToString(Ci) : "");
        }

        private static bool SameKind(SpawnRecord a, SpawnRecord b) => Key(a) == Key(b) && a.Level == b.Level;

        private static string Key(SpawnRecord r) => r.Source + "|" + r.Detail + "|" + r.Entry + "|" + r.Creature;
    }

    /// <summary>
    /// Where a creature came from, in words: the spawn log's record when this game spawned it; else
    /// what the game keeps about it, in this order: a raid's creature (the game marks them), one a
    /// fixed spawner made (the spawner keeps a link to it), one born to tamed parents, a tamed one, a
    /// night spawn (marked to go at day). Anything else came from a biome list or a spawner structure
    /// on another player's game, or before this session; the game keeps nothing of either.
    /// </summary>
    internal static class SpawnOrigin
    {
        public sealed class Facts
        {
            public SpawnRecord Record;
            public double Now;
            public bool EventCreature, DespawnInDay, Tamed, Offspring, LinkedToSpawner;
            public string SpawnerLocation;
        }

        public static string Describe(Facts f)
        {
            if (f.Record != null && !f.Record.Cancelled)
                return "Spawned by your game " + SpawnLog.Ago(f.Now - f.Record.At) + " ago: " + SpawnLog.How(f.Record) + ", " + SpawnLog.Metres(f.Record.Distance) + " from you"
                    + (f.Record.InsideBase ? ", inside a base" : "");
            if (f.EventCreature) return "A raid's creature: it leaves when the raid ends";
            if (f.LinkedToSpawner) return string.IsNullOrEmpty(f.SpawnerLocation) ? "From a fixed spawner" : "From the fixed spawner of " + f.SpawnerLocation;
            if (f.Offspring) return "Born to tamed parents; it grows up";
            if (f.Tamed) return "Tamed; where it came from is not kept";
            if (f.DespawnInDay) return "A night spawn from a biome list; it goes at day";
            return "Not seen spawning here: a biome spawn or a spawner structure on another player's game, or from before this session";
        }
    }
}
