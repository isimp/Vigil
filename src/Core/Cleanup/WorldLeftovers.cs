using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vigil.Core
{
    /// <summary>One kind of object no installed mod provides, as the scan of the whole world found it.</summary>
    internal sealed class LeftoverKind
    {
        public int Hash;
        public string Name, Where;
        public int Count;
    }

    /// <summary>
    /// The server's scan of every object the world keeps, read one at a time: how many there are, and
    /// those whose prefab no installed mod provides (prefab 0 is no prefab and left out), by prefab, most
    /// first, named from <see cref="KnownPrefabs"/> or by number, each with where they lie: up to two
    /// places, the <see cref="Square"/> m squares that hold most of them, by distance and direction from
    /// the world's start, and how many more. Each object's id is kept for a removal.
    /// </summary>
    internal sealed class WorldScan
    {
        public const float Square = 256f;
        public const int PlacesNamed = 2;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private readonly Dictionary<int, List<(float X, float Z, object Id)>> _byHash = new Dictionary<int, List<(float X, float Z, object Id)>>();

        public int Objects { get; private set; }

        public int Leftovers { get; private set; }

        public void Read(int hash, float x, float z, bool exists, object id = null)
        {
            Objects++;
            if (exists || hash == 0) return;
            if (!_byHash.TryGetValue(hash, out var list)) _byHash[hash] = list = new List<(float X, float Z, object Id)>();
            list.Add((x, z, id));
            Leftovers++;
        }

        public IEnumerable<object> IdsOf(int hash) =>
            _byHash.TryGetValue(hash, out var list) ? list.Select(p => p.Id).Where(id => id != null) : Enumerable.Empty<object>();

        public List<LeftoverKind> Kinds() =>
            _byHash.Select(kv => new LeftoverKind { Hash = kv.Key, Name = KnownPrefabs.NameOf(kv.Key), Count = kv.Value.Count, Where = Where(kv.Value) })
                .OrderByDescending(k => k.Count).ThenBy(k => k.Hash).ToList();

        public string Verdict
        {
            get
            {
                if (Leftovers == 0) return "No objects of removed mods in the world.";
                int kinds = _byHash.Count;
                return Leftovers.ToString("N0", Ci) + (Leftovers == 1 ? " object" : " objects") + " of " + kinds.ToString(Ci) + (kinds == 1 ? " kind" : " kinds")
                    + " that no installed mod provides. The game warns about " + (Leftovers == 1 ? "it" : "each") + " whenever its area loads.";
            }
        }

        private static string Where(List<(float X, float Z, object Id)> points)
        {
            var order = new List<(int, int)>();
            var cells = new Dictionary<(int, int), List<(float X, float Z, object Id)>>();
            foreach (var p in points)
            {
                var key = ((int)Math.Floor(p.X / Square), (int)Math.Floor(p.Z / Square));
                if (!cells.TryGetValue(key, out var list))
                {
                    cells[key] = list = new List<(float X, float Z, object Id)>();
                    order.Add(key);
                }
                list.Add(p);
            }

            var places = order.Select(k => cells[k]).OrderByDescending(c => c.Count).ToList();
            string Place(List<(float X, float Z, object Id)> c) => SpawnShare.FromStart(c.Average(p => p.X), c.Average(p => p.Z));
            if (places.Count == 1) return Place(places[0]);
            var named = places.Take(PlacesNamed).Select(c => Place(c) + " (" + c.Count.ToString("N0", Ci) + ")");
            string more = places.Count > PlacesNamed ? ", and " + (places.Count - PlacesNamed).ToString(Ci) + " more" : "";
            return places.Count.ToString(Ci) + " places: " + string.Join(", ", named.ToArray()) + more;
        }
    }

    /// <summary>
    /// The world's player history: everyone who ever joined, which the game keeps in the world file
    /// and sends to every player. Load tests join simulated players named "NetProbe player N" (before
    /// Vigil) or "Vigil player N", whose ids (a platform's prefix, then the name) the history keeps too.
    /// </summary>
    internal static class PlayerHistory
    {
        private static readonly Regex Bot = new Regex(@"^(?:[A-Za-z]+_)?(NetProbe|Vigil) player (\d+)$", RegexOptions.CultureInvariant);
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static bool IsBot(string id) => id != null && Bot.IsMatch(id);

        public static int Bots(IEnumerable<string> ids) => ids.Count(IsBot);

        /// <summary>"36 entries: 4 players and 32 load-test bots (NetProbe player 1–16, Vigil player 1–16)".</summary>
        public static string Summary(IEnumerable<string> ids)
        {
            var all = ids.ToList();
            var bots = all.Select(id => id == null ? null : Bot.Match(id)).Where(m => m != null && m.Success).ToList();
            int players = all.Count - bots.Count;
            string entries = all.Count.ToString(Ci) + (all.Count == 1 ? " entry" : " entries");
            if (bots.Count == 0) return entries + ", all players";
            var groups = bots.GroupBy(m => m.Groups[1].Value).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + " player " + Ranges(g.Select(m => int.Parse(m.Groups[2].Value, Ci))));
            return entries + ": " + (players > 0 ? players.ToString(Ci) + (players == 1 ? " player and " : " players and ") : "")
                + bots.Count.ToString(Ci) + (bots.Count == 1 ? " load-test bot" : " load-test bots") + " (" + string.Join(", ", groups.ToArray()) + ")";
        }

        // "1–3, 5": runs of numbers that follow each other.
        private static string Ranges(IEnumerable<int> numbers)
        {
            var sorted = numbers.Distinct().OrderBy(n => n).ToList();
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                parts.Add(j > i ? sorted[i].ToString(Ci) + "–" + sorted[j].ToString(Ci) : sorted[i].ToString(Ci));
                i = j + 1;
            }
            return string.Join(", ", parts.ToArray());
        }
    }

    /// <summary>What the server's scan says, as it travels to an admin: where the scan stands, what it found, the player history.</summary>
    internal sealed class LeftoverReport
    {
        public const string None = "none", Scanning = "scanning", Removing = "removing", Done = "done";

        public string State = None;
        public int Read, Total, Bots;
        public double Seconds, Ago;
        public string Verdict, History, Note;
        public List<LeftoverKind> Kinds = new List<LeftoverKind>();

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public string StateLine()
        {
            switch (State)
            {
                case Scanning: return "Reading the world: " + Read.ToString("N0", Ci) + " of " + Total.ToString("N0", Ci) + " objects";
                case Removing: return "Removing: " + Read.ToString("N0", Ci) + " of " + Total.ToString("N0", Ci) + " objects";
                case Done: return Total.ToString("N0", Ci) + " objects, read in " + Seconds.ToString("0", Ci) + " s, " + SpawnLog.Ago(Ago) + " ago";
                default: return "Not read yet: Scan reads every object the server keeps";
            }
        }

        public Dictionary<string, object> Encode() => new Dictionary<string, object>
        {
            { "state", State }, { "read", Read }, { "total", Total }, { "seconds", Seconds }, { "ago", Ago }, { "verdict", Verdict }, { "history", History },
            { "bots", Bots }, { "note", Note },
            { "kinds", string.Join("\n", Kinds.Select(k => k.Hash.ToString(Ci) + "\t" + Clean(k.Name) + "\t" + k.Count.ToString(Ci) + "\t" + Clean(k.Where)).ToArray()) },
        };

        public static LeftoverReport Decode(IDictionary<string, string> d)
        {
            string Text(string key) => d.TryGetValue(key, out string v) && v.Length > 0 ? v : null;
            int Int(string key) => (int)Zero(KeyValues.Number(d, key));
            var r = new LeftoverReport
            {
                State = Text("state") ?? None, Read = Int("read"), Total = Int("total"), Bots = Int("bots"), Seconds = Zero(KeyValues.Number(d, "seconds")),
                Ago = Zero(KeyValues.Number(d, "ago")), Verdict = Text("verdict"), History = Text("history"), Note = Text("note"),
            };
            foreach (var line in (Text("kinds") ?? "").Split('\n'))
            {
                var v = line.Split('\t');
                if (v.Length < 4 || !int.TryParse(v[0], NumberStyles.Integer, Ci, out int hash)) continue;
                r.Kinds.Add(new LeftoverKind { Hash = hash, Name = v[1], Count = int.TryParse(v[2], NumberStyles.Integer, Ci, out int n) ? n : 0, Where = v[3] });
            }
            return r;
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ');

        private static double Zero(double v) => double.IsNaN(v) ? 0 : v;
    }
}
