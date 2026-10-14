using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>One spawn as the server keeps it: whose game spawned it, when by that game's clock, and the record.</summary>
    internal sealed class SharedSpawn
    {
        public string Player;
        public DateTime Utc;
        public SpawnRecord Record;
    }

    /// <summary>
    /// A player's spawn log on its way to the server: the spawns and cancelled spawns made since the
    /// last send, oldest first, at most <see cref="MaxRows"/> a send, once every
    /// <see cref="EverySeconds"/>; one line each, its values between tabs, the time as UTC seconds by
    /// the player's clock. Also how a base and a spot outside bases are named.
    /// </summary>
    internal static class SpawnShare
    {
        public const int MaxRows = 150;
        public const double EverySeconds = 60;
        private const int Fields = 17;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly string[] Compass = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        /// <summary>The spawns and cancelled spawns after <paramref name="since"/>, oldest first, at most <paramref name="max"/>.</summary>
        public static List<SpawnRecord> Due(IEnumerable<SpawnRecord> spawned, IEnumerable<SpawnRecord> cancelled, double since, int max) =>
            spawned.Concat(cancelled).Where(r => r.At > since).OrderBy(r => r.At).Take(max).ToList();

        /// <summary>The records as lines, their times turned from the game clock (<paramref name="now"/>) into UTC by the machine's clock (<paramref name="utcNow"/>).</summary>
        public static string Encode(IEnumerable<SpawnRecord> records, double now, DateTime utcNow)
        {
            var sb = new StringBuilder();
            foreach (var r in records)
            {
                long utc = (long)Math.Round((utcNow.AddSeconds(r.At - now) - Epoch).TotalSeconds);
                string flags = (r.NightOnly ? "n" : "") + (r.InsideBase ? "b" : "") + (r.AllowedInBase ? "a" : "") + (r.Cancelled ? "c" : "");
                var values = new[]
                {
                    utc.ToString(Ci), Clean(r.Creature), ((int)r.Source).ToString(Ci), Clean(r.Detail), Clean(r.Entry), r.Level.ToString(Ci), flags,
                    Clean(r.CancelledBy), Num(r.X), Num(r.Z), Num(r.Distance), Clean(r.BasePiece), r.BaseBuilderId.ToString(Ci), Clean(r.BaseBuilder),
                    Num(r.BaseX), Num(r.BaseZ), Clean(r.Id),
                };
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(string.Join("\t", values));
            }

            return sb.ToString();
        }

        /// <summary>The spawns in a batch from <paramref name="player"/>, at most <paramref name="max"/>; lines that do not read are skipped.</summary>
        public static List<SharedSpawn> Decode(string text, string player, int max)
        {
            var list = new List<SharedSpawn>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                if (list.Count >= max) break;
                var v = line.Split('\t');
                if (v.Length < Fields || !long.TryParse(v[0], NumberStyles.Integer, Ci, out long utc) || !int.TryParse(v[2], NumberStyles.Integer, Ci, out int source)
                    || !Enum.IsDefined(typeof(SpawnSource), source))
                    continue;
                string flags = v[6];
                list.Add(new SharedSpawn
                {
                    Player = player,
                    Utc = Epoch.AddSeconds(utc),
                    Record = new SpawnRecord
                    {
                        Creature = Text(v[1]) ?? "?", Source = (SpawnSource)source, Detail = Text(v[3]), Entry = Text(v[4]), Level = Int(v[5]),
                        NightOnly = flags.IndexOf('n') >= 0, InsideBase = flags.IndexOf('b') >= 0, AllowedInBase = flags.IndexOf('a') >= 0, Cancelled = flags.IndexOf('c') >= 0,
                        CancelledBy = Text(v[7]), X = Float(v[8]), Z = Float(v[9]), Distance = Float(v[10]), BasePiece = Text(v[11]),
                        BaseBuilderId = long.TryParse(v[12], NumberStyles.Integer, Ci, out long id) ? id : 0, BaseBuilder = Text(v[13]),
                        BaseX = Float(v[14]), BaseZ = Float(v[15]), Id = Text(v[16]),
                    },
                });
            }

            return list;
        }

        /// <summary>A base by the piece whose base area it is and its builder: the name the spawning game knew, else from <paramref name="names"/>, else "a player".</summary>
        public static string Place(SpawnRecord r, Func<long, string> names = null)
        {
            string builder = r.BaseBuilder ?? (r.BaseBuilderId != 0 ? names?.Invoke(r.BaseBuilderId) : null);
            return (r.BasePiece ?? "a base") + " by " + (string.IsNullOrEmpty(builder) ? "a player" : builder);
        }

        /// <summary>A spot by its distance and direction from the world's start (north is +z, as on the game's map).</summary>
        public static string FromStart(float x, float z)
        {
            double d = Math.Sqrt((x * (double)x) + (z * (double)z));
            if (d < 50) return "at the start";
            double angle = Math.Atan2(x, z) * 180.0 / Math.PI;
            string toward = Compass[((int)Math.Round((angle + 360.0) / 45.0)) % 8];
            string far = d < 995 ? (Math.Round(d / 10) * 10).ToString("0", Ci) + " m" : (d / 1000).ToString("0.0", Ci) + " km";
            return far + " " + toward + " of the start";
        }

        private static string Clean(string s) =>
            s == null ? "" : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Replace(';', ',').Replace('=', ':');

        private static string Num(float v) => v.ToString("0.#", Ci);

        private static string Text(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, Ci, out int v) ? v : 0;

        private static float Float(string s) => float.TryParse(s, NumberStyles.Float, Ci, out float v) ? v : 0f;
    }

    /// <summary>
    /// What the server's admins see of everyone's spawn logs over a window: how many spawns appeared
    /// inside bases, those by place, a line per game, and the cancelled ones by the mods that can stop
    /// them. It travels from the server as named values, its lists one line per item.
    /// </summary>
    internal sealed class SpawnView
    {
        /// <summary>The admin channel's command that asks for it.</summary>
        public const string Command = "spawnview";

        public string Verdict;
        public int InsideBases;
        public List<Place> Places = new List<Place>();
        public List<Game> Games = new List<Game>();
        public List<Cancel> Cancelled = new List<Cancel>();

        public sealed class Place
        {
            public string Label, Creatures, Ago;
            public int Count;
            public float X, Z;
        }

        public sealed class Game
        {
            public string Player, LastSent;
            public int Spawns, InBase;
        }

        public sealed class Cancel
        {
            public string Mod, Creatures;
            public int Count;
        }

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public Dictionary<string, object> Encode() => new Dictionary<string, object>
        {
            { "verdict", Verdict }, { "inbase", InsideBases },
            { "places", Lines(Places.Select(p => new[] { p.Label, p.Creatures, p.Ago, p.Count.ToString(Ci), p.X.ToString("0.#", Ci), p.Z.ToString("0.#", Ci) })) },
            { "games", Lines(Games.Select(g => new[] { g.Player, g.Spawns.ToString(Ci), g.InBase.ToString(Ci), g.LastSent })) },
            { "cancelled", Lines(Cancelled.Select(c => new[] { c.Mod, c.Count.ToString(Ci), c.Creatures })) },
        };

        public static SpawnView Decode(IDictionary<string, string> d)
        {
            d.TryGetValue("verdict", out string verdict);
            var view = new SpawnView { Verdict = verdict, InsideBases = (int)Zero(KeyValues.Number(d, "inbase")) };
            foreach (var v in Split(d, "places", 6))
                view.Places.Add(new Place { Label = v[0], Creatures = v[1], Ago = v[2], Count = (int)Number(v[3]), X = (float)Number(v[4]), Z = (float)Number(v[5]) });
            foreach (var v in Split(d, "games", 4))
                view.Games.Add(new Game { Player = v[0], Spawns = (int)Number(v[1]), InBase = (int)Number(v[2]), LastSent = v[3] });
            foreach (var v in Split(d, "cancelled", 3))
                view.Cancelled.Add(new Cancel { Mod = v[0], Count = (int)Number(v[1]), Creatures = v[2] });
            return view;
        }

        /// <summary>One game's spawns, as rows, for the admin channel.</summary>
        public static Dictionary<string, object> EncodeRows(IEnumerable<SpawnLog.Row> rows) => new Dictionary<string, object>
        {
            { "rows", Lines(rows.Select(r => new[] { r.When, r.What, r.How, r.Where, (r.InsideBase ? "b" : "") + (r.Cancelled ? "c" : "") })) },
        };

        public static List<SpawnLog.Row> DecodeRows(IDictionary<string, string> d) =>
            Split(d, "rows", 5).Select(v => new SpawnLog.Row
            {
                When = v[0], What = v[1], How = v[2], Where = v[3], InsideBase = v[4].IndexOf('b') >= 0, Cancelled = v[4].IndexOf('c') >= 0,
            }).ToList();

        private static string Lines(IEnumerable<string[]> items) =>
            string.Join("\n", items.Select(v => string.Join("\t", v.Select(s => (s ?? "").Replace('\t', ' ').Replace('\n', ' ')).ToArray())).ToArray());

        private static IEnumerable<string[]> Split(IDictionary<string, string> d, string key, int fields)
        {
            if (!d.TryGetValue(key, out string text) || string.IsNullOrEmpty(text)) yield break;
            foreach (var line in text.Split('\n'))
            {
                var v = line.Split('\t');
                if (v.Length >= fields) yield return v;
            }
        }

        private static double Number(string s) => double.TryParse(s, NumberStyles.Float, Ci, out double v) ? v : 0;

        private static double Zero(double v) => double.IsNaN(v) ? 0 : v;
    }

    /// <summary>
    /// The server's side of shared spawn logs: everyone's spawns as they arrive, the newest
    /// <see cref="Max"/> kept, when each player last sent, and the builders' names it has seen; read
    /// as a <see cref="SpawnView"/> over a window, or as one game's rows. Spawns inside bases group by
    /// place: the same piece and builder within <see cref="PlaceMetres"/> of the place's first.
    /// </summary>
    internal sealed class SpawnMerge
    {
        public const int Max = 20000, MaxPlaces = 12, MaxCancelled = 8;
        public const float PlaceMetres = 32f;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly List<SharedSpawn> _spawns = new List<SharedSpawn>();
        private readonly Dictionary<string, DateTime> _lastSent = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<long, string> _names = new Dictionary<long, string>();

        public int Count => _spawns.Count;

        public IReadOnlyDictionary<long, string> Names => _names;

        public void Add(string player, IEnumerable<SharedSpawn> spawns, DateTime utcNow)
        {
            _lastSent[player] = utcNow;
            foreach (var s in spawns)
            {
                s.Player = player;
                _spawns.Add(s);
            }

            if (_spawns.Count > Max) _spawns.RemoveRange(0, _spawns.Count - Max);
        }

        /// <summary>Keeps a player's name by their id; true when it is new or changed.</summary>
        public bool Learn(long id, string name)
        {
            if (id == 0 || string.IsNullOrWhiteSpace(name) || (_names.TryGetValue(id, out string had) && had == name)) return false;
            _names[id] = name;
            return true;
        }

        public string NameOf(long id) => _names.TryGetValue(id, out string name) ? name : null;

        /// <summary>Everything within <paramref name="window"/> seconds (all that is kept when 0 or less).</summary>
        public SpawnView View(DateTime utcNow, double window)
        {
            var within = Within(utcNow, window).ToList();
            var spawned = within.Where(s => !s.Record.Cancelled).ToList();
            var view = new SpawnView();

            var places = new List<(SpawnRecord First, List<SharedSpawn> All)>();
            foreach (var s in spawned.Where(s => s.Record.InsideBase).OrderBy(s => s.Utc))
            {
                var r = s.Record;
                int at = places.FindIndex(p => p.First.BasePiece == r.BasePiece && p.First.BaseBuilderId == r.BaseBuilderId
                    && Math.Abs(p.First.BaseX - r.BaseX) <= PlaceMetres && Math.Abs(p.First.BaseZ - r.BaseZ) <= PlaceMetres);
                if (at >= 0) places[at].All.Add(s);
                else places.Add((r, new List<SharedSpawn> { s }));
            }

            view.InsideBases = places.Sum(p => p.All.Count);
            view.Places = places.OrderByDescending(p => p.All.Count).ThenByDescending(p => p.All.Max(s => s.Utc)).Take(MaxPlaces).Select(p => new SpawnView.Place
            {
                Label = SpawnShare.Place(p.First, NameOf),
                Creatures = Creatures(p.All.Select(s => s.Record)),
                Ago = SpawnLog.Ago((utcNow - p.All.Max(s => s.Utc)).TotalSeconds),
                Count = p.All.Count,
                X = p.First.BaseX,
                Z = p.First.BaseZ,
            }).ToList();

            string words = WindowWords(window);
            view.Verdict = view.InsideBases == 0
                ? "No spawns inside bases " + words + "."
                : view.InsideBases + (view.InsideBases == 1 ? " spawn" : " spawns") + " inside bases " + words + ", at " + places.Count + (places.Count == 1 ? " place." : " places.");

            var players = _lastSent.Keys.Where(p => window <= 0 || (utcNow - _lastSent[p]).TotalSeconds <= window || within.Any(s => s.Player == p));
            view.Games = players.Select(p => new SpawnView.Game
            {
                Player = p,
                Spawns = spawned.Count(s => s.Player == p),
                InBase = spawned.Count(s => s.Player == p && s.Record.InsideBase),
                LastSent = SpawnLog.Ago((utcNow - _lastSent[p]).TotalSeconds) + " ago",
            }).OrderByDescending(g => g.InBase).ThenByDescending(g => g.Spawns).ThenBy(g => g.Player, StringComparer.OrdinalIgnoreCase).ToList();

            view.Cancelled = within.Where(s => s.Record.Cancelled).GroupBy(s => string.IsNullOrEmpty(s.Record.CancelledBy) ? "a mod's check" : s.Record.CancelledBy)
                .Select(g => new SpawnView.Cancel { Mod = g.Key, Count = g.Count(), Creatures = string.Join(", ", g.GroupBy(s => s.Record.Creature).OrderByDescending(c => c.Count()).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => c.Key).ToArray()) })
                .OrderByDescending(c => c.Count).ThenBy(c => c.Mod, StringComparer.OrdinalIgnoreCase).Take(MaxCancelled).ToList();
            return view;
        }

        /// <summary>One game's spawns within the window, as in its own spawn log, with places instead of distances.</summary>
        public List<SpawnLog.Row> Rows(string player, DateTime utcNow, double window)
        {
            var log = new SpawnLog();
            foreach (var s in Within(utcNow, window).Where(s => s.Player == player).OrderBy(s => s.Utc))
            {
                var r = s.Record.Copy();
                r.At = (s.Utc - Epoch).TotalSeconds;
                log.Add(r);
            }

            double now = (utcNow - Epoch).TotalSeconds;
            return log.Recent(now, window <= 0 ? double.MaxValue : window, r => r.InsideBase ? SpawnLog.InBase(r, NameOf) : SpawnShare.FromStart(r.X, r.Z));
        }

        /// <summary>The window in words: "in the last hour", "in the last 6 hours", "this session".</summary>
        public static string WindowWords(double window)
        {
            if (window <= 0) return "this session";
            if (window < 3600) return "in the last " + Math.Round(window / 60).ToString("0", CultureInfo.InvariantCulture) + " minutes";
            double hours = Math.Round(window / 3600);
            return hours <= 1 ? "in the last hour" : "in the last " + hours.ToString("0", CultureInfo.InvariantCulture) + " hours";
        }

        private IEnumerable<SharedSpawn> Within(DateTime utcNow, double window) =>
            window <= 0 ? _spawns : _spawns.Where(s => (utcNow - s.Utc).TotalSeconds <= window);

        // "Greyling ×3 (night only), Boar": the most first; night only when every one of them was.
        private static string Creatures(IEnumerable<SpawnRecord> records) =>
            string.Join(", ", records.GroupBy(r => r.Creature).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key + (g.Count() > 1 ? " ×" + g.Count().ToString(CultureInfo.InvariantCulture) : "") + (g.All(r => r.NightOnly) ? " (night only)" : "")).ToArray());
    }
}
