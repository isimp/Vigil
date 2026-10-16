using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The names of the values a world object stores. The game keeps each value under a number made
    /// from its name (<see cref="Hash"/>, the game's own rule), so the name has to be found again: first
    /// in the game's one list of them (<c>ZDOVars</c>, a field each, "s_" before the name), then among
    /// every text the game's and the mods' code carries, each hashed the same way. A value found among a
    /// mod's texts names that mod; one the game names too stays the game's. A link to another object is
    /// kept as two numbers under the name with "_u" and "_i" added, so each text names those as well.
    /// A value whose name a mod puts together while it runs is not found and shows its number.
    /// </summary>
    internal sealed class ValueNames
    {
        public const string Game = "the game";
        public const string NotFound = "not found";

        /// <summary>Longer texts are messages, not names, and are left out.</summary>
        public const int LongestName = 100;

        private const int ModsListed = 2;

        private readonly Dictionary<int, Entry> _byHash = new Dictionary<int, Entry>();

        private sealed class Entry
        {
            public string Text;
            public bool Game;
            public List<string> Sources;
        }

        /// <summary>A value's name and where it was found.</summary>
        public readonly struct Name
        {
            public Name(string text, string namedIn, bool found)
            {
                Text = text;
                NamedIn = namedIn;
                Found = found;
            }

            public string Text { get; }

            /// <summary>"the game", the mods whose code carries the name, or "not found".</summary>
            public string NamedIn { get; }

            public bool Found { get; }
        }

        /// <summary>How many numbers have a name.</summary>
        public int Count => _byHash.Count;

        /// <summary>The number the game keeps a value under, made from its name the way the game makes it.</summary>
        public static int Hash(string text)
        {
            unchecked
            {
                int a = 5381, b = a;
                for (int i = 0; i < text.Length && text[i] != '\0'; i += 2)
                {
                    a = ((a << 5) + a) ^ text[i];
                    if (i == text.Length - 1 || text[i + 1] == '\0')
                    {
                        break;
                    }

                    b = ((b << 5) + b) ^ text[i + 1];
                }

                return a + (b * 1566083941);
            }
        }

        /// <summary>
        /// The names from the game's fields (field name, number) and from texts by where they come
        /// from (<see cref="Game"/> for the game's own code, else the mod's name), the game's first.
        /// </summary>
        public static ValueNames Build(IEnumerable<KeyValuePair<string, int>> gameFields, IEnumerable<KeyValuePair<string, IEnumerable<string>>> texts)
        {
            var names = new ValueNames();
            foreach (var f in gameFields)
            {
                if (string.IsNullOrEmpty(f.Key) || names._byHash.ContainsKey(f.Value)) continue;
                string name = f.Key.StartsWith("s_", StringComparison.Ordinal) ? f.Key.Substring(2) : f.Key;
                names._byHash[f.Value] = new Entry { Text = name, Game = true, Sources = new List<string>() };
            }

            foreach (var source in texts)
            {
                if (source.Value == null) continue;
                foreach (var text in source.Value)
                {
                    if (string.IsNullOrEmpty(text) || text.Length > LongestName) continue;
                    names.Add(text, source.Key);
                    names.Add(text + "_u", source.Key);
                    names.Add(text + "_i", source.Key);
                }
            }

            return names;
        }

        public Name Of(int hash)
        {
            if (!_byHash.TryGetValue(hash, out var entry)) return new Name("#" + hash.ToString(CultureInfo.InvariantCulture), NotFound, false);
            if (entry.Game) return new Name(entry.Text, Game, true);
            var mods = entry.Sources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            string namedIn = string.Join(", ", mods.Take(ModsListed).ToArray())
                + (mods.Count > ModsListed ? " +" + (mods.Count - ModsListed).ToString(CultureInfo.InvariantCulture) : "");
            return new Name(entry.Text, namedIn, true);
        }

        /// <summary>The one mod whose code carries the name; null for the game's names, names in several mods' code, or unknown numbers.</summary>
        public string SoleMod(int hash) =>
            _byHash.TryGetValue(hash, out var entry) && !entry.Game && entry.Sources.Count == 1 ? entry.Sources[0] : null;

        // The first text of a number stays; the same text from another mod adds that mod.
        private void Add(string text, string source)
        {
            int hash = Hash(text);
            bool game = source == Game;
            if (!_byHash.TryGetValue(hash, out var entry))
            {
                _byHash[hash] = new Entry { Text = text, Game = game, Sources = new List<string> { source } };
                return;
            }

            if (entry.Game || !string.Equals(entry.Text, text, StringComparison.Ordinal)) return;
            if (game)
            {
                entry.Game = true;
                return;
            }

            if (!entry.Sources.Contains(source)) entry.Sources.Add(source);
        }
    }

    /// <summary>A stored value as the inspector shows it, and the order of the rows.</summary>
    internal static class StoredValue
    {
        private const int LongestText = 60;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public sealed class Row
        {
            public Row(string name, string type, string value)
                : this(name, null, type, value)
            {
            }

            public Row(string name, string namedIn, string type, string value)
            {
                Name = name;
                NamedIn = namedIn;
                Type = type;
                Value = value;
            }

            public string Name { get; }

            /// <summary>Where the name was found (<see cref="ValueNames.Name.NamedIn"/>); null when not looked for.</summary>
            public string NamedIn { get; }

            public string Type { get; }

            public string Value { get; }
        }

        /// <summary>
        /// A value in plain words: numbers with at most two decimals, text in quotes and cut after 60
        /// characters, a position or rotation as its numbers (handed in as floats), data as its size.
        /// </summary>
        public static string Format(object value) => value switch
        {
            null => "",
            float f => Number(f),
            double d => Number(d),
            string s => "\"" + (s.Length > LongestText ? s.Substring(0, LongestText) + "..." : s) + "\"",
            float[] v => "(" + string.Join(", ", v.Select(x => Number(x)).ToArray()) + ")",
            byte[] b => b.Length.ToString(Ci) + " bytes",
            IFormattable n => n.ToString(null, Ci),
            _ => value.ToString(),
        };

        /// <summary>Named values first, by name, then the ones known only by number.</summary>
        public static List<Row> Order(IEnumerable<Row> rows) =>
            rows.OrderBy(r => r.Name.StartsWith("#", StringComparison.Ordinal) ? 1 : 0).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        private static string Number(double value) => Math.Round(value, 2).ToString("0.##", Ci);
    }

    /// <summary>
    /// How often an object changed between two readings of its change counter, and what sending it
    /// takes at most. The game raises an object's counter for every new value and every move or turn,
    /// and sends a changed object with all its values in rounds every 0.05 s, so at most 20 times a
    /// second however often it changes. A counter that went back (the object was made anew) counts nothing.
    /// </summary>
    internal readonly struct ChangeRate
    {
        public const double SendsPerSecondMax = 20;

        private ChangeRate(long changes, double perSecond, double sendsPerSecond, double bytesPerSecond, bool reset)
        {
            Changes = changes;
            PerSecond = perSecond;
            SendsPerSecond = sendsPerSecond;
            BytesPerSecond = bytesPerSecond;
            Reset = reset;
        }

        public long Changes { get; }

        public double PerSecond { get; }

        /// <summary>How often it is sent at most: once a change, at most 20 times a second.</summary>
        public double SendsPerSecond { get; }

        /// <summary>What those sends take at most, its whole size each time.</summary>
        public double BytesPerSecond { get; }

        public bool Reset { get; }

        public static ChangeRate Of(uint startRevision, uint endRevision, double seconds, int sizeBytes)
        {
            bool reset = endRevision < startRevision;
            long changes = reset ? 0 : (long)endRevision - startRevision;
            double perSecond = seconds > 0 ? changes / seconds : 0;
            double sends = Math.Min(perSecond, SendsPerSecondMax);
            return new ChangeRate(changes, perSecond, sends, sends * Math.Max(0, sizeBytes), reset);
        }
    }

    /// <summary>How the game keeps an object, in words: saved with the world or not, loaded from far away, sent first.</summary>
    internal static class ObjectKept
    {
        public static string Describe(bool persistent, bool distant, string type)
        {
            var parts = new List<string> { persistent ? "saved with the world" : "not saved" };
            if (distant) parts.Add("loaded from far away");
            switch (type)
            {
                case "Prioritized": parts.Add("sent first"); break;
                case "Solid": parts.Add("solid"); break;
                case "Terrain": parts.Add("terrain"); break;
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>An object's age in its largest two units: "45 s ago", "2 h 14 min ago".</summary>
    internal static class ObjectAge
    {
        public static string Describe(TimeSpan age)
        {
            if (age < TimeSpan.Zero) return "not known";
            var ci = CultureInfo.InvariantCulture;
            if (age.TotalDays >= 1) return string.Format(ci, "{0} d {1} h ago", (int)age.TotalDays, age.Hours);
            if (age.TotalHours >= 1) return string.Format(ci, "{0} h {1} min ago", (int)age.TotalHours, age.Minutes);
            if (age.TotalMinutes >= 1) return string.Format(ci, "{0} min ago", (int)age.TotalMinutes);
            return string.Format(ci, "{0} s ago", (int)age.TotalSeconds);
        }
    }

    /// <summary>The scripts on an object by the mod whose code they are, the game's (and Unity's) first, then the mods by how many.</summary>
    internal static class ScriptsByMod
    {
        public const string Game = "the game";

        public sealed class Entry
        {
            public string Owner;
            public int Count;
            public string Types;
        }

        /// <summary>The scripts (type name, mod or null for the game's own) grouped; a type on several parts counts each time.</summary>
        public static List<Entry> Group(IEnumerable<KeyValuePair<string, string>> scripts) =>
            scripts.GroupBy(s => s.Value ?? Game)
                .Select(g => new Entry
                {
                    Owner = g.Key,
                    Count = g.Count(),
                    Types = string.Join(", ", g.Select(s => s.Key).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray()),
                })
                .OrderBy(e => e.Owner == Game ? 0 : 1).ThenByDescending(e => e.Count).ThenBy(e => e.Owner, StringComparer.Ordinal)
                .ToList();

        public static string Summary(IList<Entry> groups) =>
            string.Join(", ", groups.Select(g => g.Owner + " " + g.Count.ToString(CultureInfo.InvariantCulture)).ToArray());
    }

    /// <summary>
    /// The game's own record of an object, as the inspector shows it: its id, the number of its
    /// prefab, its owner's id, its two change counters (values, owner), where it is and in which
    /// zone, how it is turned, its flags, and the object it is linked to, when it is.
    /// </summary>
    internal static class ObjectRecord
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public sealed class Input
        {
            public string Id, OwnerName, Type, LinkKind, LinkTo;
            public int Prefab, ZoneX, ZoneY;
            public long Owner;
            public uint DataRevision;
            public ushort OwnerRevision;
            public float X, Y, Z;
            public float[] Turn;
            public bool Persistent, Distant;
        }

        public static List<KeyValuePair<string, string>> Rows(Input r)
        {
            var rows = new List<KeyValuePair<string, string>>();
            void Add(string label, string value) => rows.Add(new KeyValuePair<string, string>(label, value));

            Add("Id", r.Id ?? "");
            Add("Prefab number", r.Prefab.ToString(Ci));
            Add("Owner", r.Owner == 0 ? "none" : r.Owner.ToString(Ci) + (string.IsNullOrEmpty(r.OwnerName) ? "" : " (" + r.OwnerName + ")"));
            Add("Revisions", "data " + r.DataRevision.ToString(Ci) + " · owner " + r.OwnerRevision.ToString(Ci));
            Add("Position", "(" + Place(r.X) + ", " + Place(r.Y) + ", " + Place(r.Z) + "), zone (" + r.ZoneX.ToString(Ci) + ", " + r.ZoneY.ToString(Ci) + ")");
            var turn = r.Turn ?? new float[3];
            Add("Rotation", "(" + string.Join(", ", turn.Select(a => Math.Round(a).ToString("0", Ci)).ToArray()) + ")");
            Add("Flags", "persistent " + YesNo(r.Persistent) + " · distant " + YesNo(r.Distant) + " · type " + (r.Type ?? "?"));
            if (!string.IsNullOrEmpty(r.LinkKind) && r.LinkKind != "None") Add("Linked to", r.LinkKind + " " + (r.LinkTo ?? "?"));
            return rows;
        }

        private static string Place(float value) => Math.Round(value, 1).ToString("0.#", Ci);

        private static string YesNo(bool value) => value ? "yes" : "no";
    }
}
