using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>Why the weather is what it is: one of the game's overrides, or the roll from the biome's list.</summary>
    internal enum WeatherCauseKind
    {
        SetInVigil,
        DevCommand,
        Intro,
        Event,
        SpecialArea,
        WorldEvent,
        Zone,
        Rolled,
    }

    /// <summary>What each of the game's weather overrides holds now (null or empty for none).</summary>
    internal sealed class WeatherSources
    {
        public string DebugEnv, IntroEnv, EventEnv, EventName, AreaEnv, WorldEventEnv, ZoneEnv;
        public bool SetByVigil, InIntro;
    }

    /// <summary>
    /// Why the weather is what it is, in the game's own order: the debug weather (a dev command's or
    /// an admin's in Vigil), the intro, a raid, a special area of a biome, a world event, a place
    /// with weather of its own (<c>EnvZone</c>); otherwise a roll from the biome's list once a period.
    /// </summary>
    internal static class WeatherCause
    {
        public sealed class Cause
        {
            public WeatherCauseKind Kind;
            public string Words;
        }

        public static Cause Of(WeatherSources s, string biome, long period, long secondsToNext)
        {
            if (!string.IsNullOrEmpty(s.DebugEnv))
                return s.SetByVigil ? Make(WeatherCauseKind.SetInVigil, "set by an admin in Vigil, on this game only") : Make(WeatherCauseKind.DevCommand, "set by a dev command (env), on this game only");
            if (s.InIntro && !string.IsNullOrEmpty(s.IntroEnv)) return Make(WeatherCauseKind.Intro, "the intro's weather");
            if (!string.IsNullOrEmpty(s.EventEnv)) return Make(WeatherCauseKind.Event, string.IsNullOrEmpty(s.EventName) ? "a raid" : "the raid " + s.EventName);
            if (!string.IsNullOrEmpty(s.AreaEnv)) return Make(WeatherCauseKind.SpecialArea, "this area always has it");
            if (!string.IsNullOrEmpty(s.WorldEventEnv)) return Make(WeatherCauseKind.WorldEvent, "a world event");
            if (!string.IsNullOrEmpty(s.ZoneEnv)) return Make(WeatherCauseKind.Zone, "this place has its own weather");
            return Make(WeatherCauseKind.Rolled, string.Format(CultureInfo.InvariantCulture, "rolled from the {0} list for period {1}, next roll in {2}", biome, period, Span(secondsToNext)));
        }

        /// <summary>A weather change as the events file writes it.</summary>
        public static string ChangeLine(string from, string to, string why) =>
            (string.IsNullOrEmpty(from) ? "" : from + " -> ") + to + " (" + why + ")";

        private static Cause Make(WeatherCauseKind kind, string words) => new Cause { Kind = kind, Words = words };

        private static string Span(long seconds)
        {
            var ci = CultureInfo.InvariantCulture;
            long m = seconds / 60, s = seconds % 60;
            if (m == 0) return s.ToString(ci) + " s";
            return s == 0 ? m.ToString(ci) + " min" : m.ToString(ci) + " min " + s.ToString(ci) + " s";
        }
    }

    /// <summary>The game's roll: one per period of <c>m_environmentDuration</c> world seconds, weighted by the biome's list.</summary>
    internal static class WeatherRoll
    {
        public sealed class Entry
        {
            public string Name;
            public float Weight;
            public double Chance;
            public bool Now;
        }

        public static long Period(long worldSeconds, long duration) => duration > 0 ? worldSeconds / duration : 0;

        public static long SecondsToNext(long worldSeconds, long duration) => duration > 0 ? duration - worldSeconds % duration : 0;

        /// <summary>Each weather of the list with its chance in percent, and whether it is the one now.</summary>
        public static List<Entry> Chances(IEnumerable<KeyValuePair<string, float>> list, string current)
        {
            var entries = list.Select(kv => new Entry { Name = kv.Key, Weight = kv.Value, Now = string.Equals(kv.Key, current, StringComparison.Ordinal) }).ToList();
            double total = entries.Sum(e => Math.Max(0f, e.Weight));
            foreach (var e in entries) e.Chance = total > 0 ? Math.Max(0f, e.Weight) / total * 100 : 0;
            return entries;
        }
    }

    /// <summary>
    /// The changes the card allows and how they are read: an admin of the server, or the player whose
    /// game hosts the world, may set the weather, the fog drawn, the wind and the time of day drawn,
    /// on their own game; anyone may cap the fog (the fog limiter). Fog an admin set is drawn as set;
    /// otherwise the limiter, when on, caps the weather's fog.
    /// </summary>
    internal static class WeatherControl
    {
        private const int Listed = 6;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>Why this player may not change the weather here; null when they may.</summary>
        public static string Refusal(bool inWorld, bool hostsWorld, bool serverAdmin)
        {
            if (!inWorld) return "join a world first";
            return hostsWorld || serverAdmin ? null : "only the server's admins can change these here";
        }

        /// <summary>The known weather meant: its exact name in any case, else the only one containing what was typed.</summary>
        public static string Match(string typed, IReadOnlyList<string> registered, out string why)
        {
            why = null;
            string t = (typed ?? "").Trim();
            var exact = registered.FirstOrDefault(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var fits = t.Length == 0 ? new List<string>() : registered.Where(n => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (fits.Count == 1) return fits[0];
            why = fits.Count == 0
                ? string.Format(Ci, "no weather is called \"{0}\"; {1} are known", t, registered.Count)
                : string.Format(Ci, "\"{0}\" fits {1}: {2}{3}", t, fits.Count, string.Join(", ", fits.Take(Listed).ToArray()), fits.Count > Listed ? ", ..." : "");
            return null;
        }

        /// <summary>A fog density to draw: a number from 0 to 1 ("off" is 0).</summary>
        public static bool TryFog(string text, out float density)
        {
            density = 0;
            string t = (text ?? "").Trim();
            if (string.Equals(t, "off", StringComparison.OrdinalIgnoreCase)) return true;
            if (!float.TryParse(t, NumberStyles.Float, Ci, out float v)) return false;
            density = Math.Min(1f, Math.Max(0f, v));
            return true;
        }

        /// <summary>The fog limiter: "off" (or nothing) for no cap, else a density from 0 to 1.</summary>
        public static bool TryLimit(string text, out float? cap)
        {
            cap = null;
            string t = (text ?? "").Trim();
            if (t.Length == 0 || string.Equals(t, "off", StringComparison.OrdinalIgnoreCase)) return true;
            if (!float.TryParse(t, NumberStyles.Float, Ci, out float v)) return false;
            cap = Math.Min(1f, Math.Max(0f, v));
            return true;
        }

        /// <summary>
        /// The fog cap in force: the server's when it sets one (it reaches every player through
        /// ServerSync), else the player's own; a server value that cannot be read counts as none.
        /// </summary>
        public static (float? Cap, bool FromServer) Limit(string serverText, string ownText)
        {
            if (TryLimit(serverText, out float? server) && server.HasValue) return (server, true);
            return (TryLimit(ownText, out float? own) ? own : null, false);
        }

        /// <summary>A time of day to draw: hours and minutes with a colon between them, or the day's fraction from 0 to 1.</summary>
        public static bool TryTime(string text, out float fraction)
        {
            fraction = 0;
            string t = (text ?? "").Trim();
            int colon = t.IndexOf(':');
            if (colon > 0)
            {
                if (!int.TryParse(t.Substring(0, colon), NumberStyles.Integer, Ci, out int h) || !int.TryParse(t.Substring(colon + 1), NumberStyles.Integer, Ci, out int m)) return false;
                if (h < 0 || h > 23 || m < 0 || m > 59) return false;
                fraction = (h + m / 60f) / 24f;
                return true;
            }

            if (!float.TryParse(t, NumberStyles.Float, Ci, out float f) || f < 0 || f > 1) return false;
            fraction = f;
            return true;
        }

        /// <summary>A wind: its direction in degrees (taken round) and its strength in percent (0 to 100), as the game's debug wind takes them.</summary>
        public static bool TryWind(string angleText, string strengthText, out float angle, out float strength)
        {
            angle = strength = 0;
            if (!float.TryParse((angleText ?? "").Trim(), NumberStyles.Float, Ci, out float a) || !float.TryParse((strengthText ?? "").Trim(), NumberStyles.Float, Ci, out float s)) return false;
            angle = ((a % 360f) + 360f) % 360f;
            strength = Math.Min(100f, Math.Max(0f, s)) / 100f;
            return true;
        }

        /// <summary>The fog to draw: as an admin set it, else the weather's capped by the limiter when it is on.</summary>
        public static double FogDrawn(double weatherFog, double? set, double? limit)
        {
            if (set.HasValue) return set.Value;
            return limit.HasValue ? Math.Min(weatherFog, limit.Value) : weatherFog;
        }
    }

    /// <summary>
    /// An environment report: what one player's game shows of the weather and light, written to
    /// <c>Vigil-environment-&lt;time&gt;.txt</c> as one "key = value" line each, sent through sharing like
    /// an incident (with its button only), and compared with this game's line by line.
    /// </summary>
    internal sealed class EnvironmentReport
    {
        public const string Header = "Vigil environment report";

        /// <summary>The keys in the order they are written and compared, with their labels.</summary>
        public static readonly (string Key, string Label)[] Fields =
        {
            ("player", "Player"), ("written", "Written"), ("day", "Day"), ("time", "Time of day"), ("biome", "Biome"),
            ("weather", "Weather"), ("cause", "Why"), ("period", "Period"), ("list", "The list"),
            ("fog_drawn", "Fog drawn"), ("fog_weather", "Fog of the weather"), ("fog_limit", "Fog limiter"), ("fog_colour", "Fog colour"),
            ("wind", "Wind"), ("on_you", "On you"), ("set", "Set by an admin"), ("mods", "Mods on weather and light"),
            ("bloom", "Bloom"), ("dof", "Depth of field"), ("sun_shafts", "Sun shafts"), ("ssao", "Ambient occlusion"),
            ("aa", "Anti-aliasing"), ("motion_blur", "Motion blur"), ("chromatic", "Chromatic aberration"),
            ("quality", "Graphics quality"), ("gpu", "Graphics card"), ("api", "Graphics API"),
        };

        private static readonly HashSet<string> NotCompared = new HashSet<string> { "player", "written" };

        /// <summary>Values that are lists, "; " between their items, compared item by item.</summary>
        private static readonly HashSet<string> Lists = new HashSet<string> { "list", "mods" };

        public sealed class Row
        {
            public string Key, Label, Mine, Theirs;
            public bool Differs, HasMine, HasTheirs;

            /// <summary>For a list, its items compared; null for a single value.</summary>
            public ItemDiff Items;

            /// <summary>
            /// The row in short lines, a tag and its words: a difference as yours and theirs (a list as
            /// what only one side has, what both have unlike, and how many alike), a match once as both.
            /// </summary>
            public List<KeyValuePair<string, string>> Lines()
            {
                var lines = new List<KeyValuePair<string, string>>();
                void Add(string tag, string text) => lines.Add(new KeyValuePair<string, string>(tag, text));
                if (!Differs)
                {
                    Add("Both", Mine);
                }
                else if (Items != null && HasMine && HasTheirs)
                {
                    if (Items.OnlyMine.Count > 0) Add("Only you", string.Join(", ", Items.OnlyMine.ToArray()));
                    if (Items.OnlyTheirs.Count > 0) Add("Only them", string.Join(", ", Items.OnlyTheirs.ToArray()));
                    if (Items.Unlike.Count > 0) Add("Unlike", string.Join(", ", Items.Unlike.ToArray()));
                    if (Items.Same > 0) Add("Same", Items.Same.ToString(CultureInfo.InvariantCulture) + " on both");
                }
                else
                {
                    Add("You", HasMine ? Mine : "not on this game");
                    Add("Them", HasTheirs ? Theirs : "not in their report");
                }

                return lines;
            }
        }

        /// <summary>Two lists item by item, each item named by what comes before its ": " (the whole item when it has none).</summary>
        public sealed class ItemDiff
        {
            public List<string> OnlyMine = new List<string>(), OnlyTheirs = new List<string>(), Unlike = new List<string>();
            public int Same;

            public bool Differs => OnlyMine.Count + OnlyTheirs.Count + Unlike.Count > 0;

            public static ItemDiff Of(string mine, string theirs)
            {
                var a = Items(mine);
                var b = Items(theirs);
                var diff = new ItemDiff();
                foreach (var kv in a)
                {
                    if (!b.TryGetValue(kv.Key, out string other)) diff.OnlyMine.Add(kv.Key);
                    else if (string.Equals(kv.Value, other, StringComparison.Ordinal)) diff.Same++;
                    else diff.Unlike.Add(kv.Key);
                }

                diff.OnlyTheirs.AddRange(b.Keys.Where(k => !a.ContainsKey(k)));
                return diff;
            }

            private static Dictionary<string, string> Items(string list)
            {
                var items = new Dictionary<string, string>(StringComparer.Ordinal);
                if (string.IsNullOrEmpty(list) || list == "none") return items;
                foreach (var raw in list.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string item = raw.Trim();
                    int colon = item.IndexOf(": ", StringComparison.Ordinal);
                    string name = colon > 0 ? item.Substring(0, colon) : item;
                    if (!items.ContainsKey(name)) items[name] = item;
                }

                return items;
            }
        }

        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();

        public List<string> Lines()
        {
            var lines = new List<string> { Header };
            foreach (var key in Fields.Select(f => f.Key).Concat(Values.Keys.Where(k => Fields.All(f => f.Key != k)).OrderBy(k => k, StringComparer.Ordinal)))
            {
                if (Values.TryGetValue(key, out string v) && v != null) lines.Add(key + " = " + Flat(v));
            }

            return lines;
        }

        /// <summary>A report read back; null for a file that is not one.</summary>
        public static EnvironmentReport Parse(IEnumerable<string> lines)
        {
            var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (list.Count == 0 || list[0].Trim() != Header) return null;
            var report = new EnvironmentReport();
            foreach (var line in list.Skip(1))
            {
                int at = line.IndexOf(" = ", StringComparison.Ordinal);
                if (at > 0) report.Values[line.Substring(0, at).Trim()] = line.Substring(at + 3);
            }

            return report;
        }

        /// <summary>
        /// The two reports side by side, every key either has, what differs first and then what is the
        /// same, each in the order of <see cref="Fields"/>. Lists are compared item by item, so the
        /// same items in another order are the same. Who wrote them and when are not compared.
        /// </summary>
        public static List<Row> Compare(EnvironmentReport mine, EnvironmentReport theirs)
        {
            var rows = new List<Row>();
            foreach (var (key, label) in Fields)
            {
                bool a = mine.Values.TryGetValue(key, out string m), b = theirs.Values.TryGetValue(key, out string t);
                if (!a && !b) continue;
                var row = new Row { Key = key, Label = label, Mine = a ? m : "-", Theirs = b ? t : "-", HasMine = a, HasTheirs = b };
                if (Lists.Contains(key)) row.Items = ItemDiff.Of(a ? m : null, b ? t : null);
                bool alike = a && b && (row.Items != null ? !row.Items.Differs : string.Equals(m, t, StringComparison.Ordinal));
                row.Differs = !NotCompared.Contains(key) && !alike;
                rows.Add(row);
            }

            return rows.Where(r => r.Differs).Concat(rows.Where(r => !r.Differs)).ToList();
        }

        /// <summary>A value on one line: its lines joined with "; ".</summary>
        private static string Flat(string value) => string.Join("; ", value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Select(s => s.Trim()).ToArray());
    }
}
