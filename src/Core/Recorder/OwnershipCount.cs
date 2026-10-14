using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>What kind of world object a game runs.</summary>
    internal enum OwnedKind
    {
        Creature,
        Item,
        Vehicle,
        Piece,
        Other,
    }

    [System.Flags]
    internal enum MarkKinds
    {
        None = 0,
        Creatures = 1,
        Vehicles = 2,
        Items = 4,
        Pieces = 8,
        Other = 16,
    }

    /// <summary>
    /// Whose game runs the objects around the player, per game and kind, busiest first. A game is
    /// named by the player whose character it runs, "You" for this one, "The server", "Nobody" for
    /// objects no game runs, else as a player out of view. Each game gets a colour from a short
    /// list and keeps it while this count lives; this game's is always <see cref="YouColour"/>.
    /// </summary>
    internal sealed class OwnershipCount
    {
        /// <summary>The colour index of this game's own objects; the others take the next free one.</summary>
        public const int YouColour = 0;

        public const int Colours = 9;

        private readonly Dictionary<long, int> _colours = new Dictionary<long, int>();

        /// <summary>One object: the game that runs it (0 for none) and its kind.</summary>
        public readonly struct Seen
        {
            public Seen(long owner, OwnedKind kind, string type = null)
            {
                Owner = owner;
                Kind = kind;
                Type = type;
            }

            public string Type { get; }

            public long Owner { get; }

            public OwnedKind Kind { get; }
        }

        public sealed class Row
        {
            public long Owner;
            public string Name;
            public OwnerRole Role;
            public int Colour, Creatures, Items, Vehicles, Pieces, Other;

            public int Total => Creatures + Items + Vehicles + Pieces + Other;

            /// <summary>Creatures, ships and carts: what others see late when this game's connection is slow.</summary>
            public int Moving => Creatures + Vehicles;

            internal readonly Dictionary<string, TypeCount> Types = new Dictionary<string, TypeCount>(System.StringComparer.Ordinal);

            /// <summary>The game's most common types, most first, each with its kind.</summary>
            public List<TypeCount> TopTypes(int n) =>
                Types.Values.OrderByDescending(t => t.Count).ThenBy(t => t.Type, System.StringComparer.Ordinal).Take(n).ToList();
        }

        public readonly struct TypeCount
        {
            public TypeCount(string type, OwnedKind kind, int count)
            {
                Type = type;
                Kind = kind;
                Count = count;
            }

            public string Type { get; }

            public OwnedKind Kind { get; }

            public int Count { get; }
        }

        /// <summary>What a game runs in words, moving things first: "42 creatures · 3 vehicles · 210 pieces".</summary>
        public static string Phrase(Row row)
        {
            var parts = new List<string>();
            void Add(int n, string one, string many)
            {
                if (n > 0) parts.Add(n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many));
            }

            Add(row.Creatures, "creature", "creatures");
            Add(row.Vehicles, "vehicle", "vehicles");
            Add(row.Items, "item", "items");
            Add(row.Pieces, "piece", "pieces");
            Add(row.Other, "other object", "other objects");
            return parts.Count == 0 ? "nothing" : string.Join(" · ", parts);
        }

        /// <summary>Whether objects of this kind get marks with these kinds ticked.</summary>
        public static bool Marks(OwnedKind kind, MarkKinds kinds) => (kinds & KindFlag(kind)) != 0;

        private static MarkKinds KindFlag(OwnedKind kind) => kind switch
        {
            OwnedKind.Creature => MarkKinds.Creatures,
            OwnedKind.Vehicle => MarkKinds.Vehicles,
            OwnedKind.Item => MarkKinds.Items,
            OwnedKind.Piece => MarkKinds.Pieces,
            _ => MarkKinds.Other,
        };

        /// <summary>The games running what was seen, busiest first; only the objects <paramref name="filter"/> keeps count when one is given.</summary>
        public List<Row> Count(IEnumerable<Seen> seen, IReadOnlyDictionary<long, string> players, long me, long server, OwnershipFilter filter = null)
        {
            var rows = new Dictionary<long, Row>();
            var names = new Dictionary<long, string>();
            bool filtering = filter != null && !filter.IsEmpty;
            foreach (var s in seen)
            {
                if (filtering)
                {
                    if (!names.TryGetValue(s.Owner, out string name)) names[s.Owner] = name = Name(s.Owner, players, me, server);
                    if (!filter.Keeps(s.Kind, s.Type, name)) continue;
                }

                if (!rows.TryGetValue(s.Owner, out var row))
                {
                    rows[s.Owner] = row = new Row { Owner = s.Owner, Name = Name(s.Owner, players, me, server), Role = RoleOf(s.Owner, me, server), Colour = ColourOf(s.Owner, me) };
                }

                switch (s.Kind)
                {
                    case OwnedKind.Creature: row.Creatures++; break;
                    case OwnedKind.Item: row.Items++; break;
                    case OwnedKind.Vehicle: row.Vehicles++; break;
                    case OwnedKind.Piece: row.Pieces++; break;
                    default: row.Other++; break;
                }

                string type = string.IsNullOrEmpty(s.Type) ? "unknown" : s.Type;
                row.Types[type] = new TypeCount(type, s.Kind, row.Types.TryGetValue(type, out var known) ? known.Count + 1 : 1);
            }

            return rows.Values.OrderByDescending(r => r.Moving).ThenByDescending(r => r.Total).ThenBy(r => r.Name, System.StringComparer.Ordinal).ToList();
        }

        /// <summary>A game's name: the player whose character it runs, "You", "The server", "Nobody", else a player out of view.</summary>
        public static string Name(long owner, IReadOnlyDictionary<long, string> players, long me, long server)
        {
            if (owner == 0) return "Nobody";
            if (owner == me) return "You";
            if (owner == server) return "The server";
            return players.TryGetValue(owner, out string name) && !string.IsNullOrEmpty(name)
                ? name
                : "A player out of view (" + (owner % 10000).ToString(CultureInfo.InvariantCulture) + ")";
        }

        public static OwnerRole RoleOf(long owner, long me, long server) =>
            owner == 0 ? OwnerRole.Nobody : owner == me ? OwnerRole.You : owner == server ? OwnerRole.Server : OwnerRole.Player;

        /// <summary>A game's colour, the same one its line and marks have; a game not seen before gets the next free one.</summary>
        public int ColourOf(long owner, long me)
        {
            if (owner == me) return YouColour;
            if (_colours.TryGetValue(owner, out int c)) return c;
            var used = new HashSet<int>(_colours.Values) { YouColour };
            c = Enumerable.Range(1, Colours - 1).FirstOrDefault(i => !used.Contains(i));
            if (c == 0) c = 1 + (_colours.Count % (Colours - 1));
            _colours[owner] = c;
            return c;
        }
    }

    /// <summary>
    /// What "Who runs what" shows: one kind of object or all, and a text that either a game's player
    /// name or an object's type holds (any case), so "boar" finds the games running boars and "ole"
    /// all of Bjorn's objects. With a kind chosen, Mark marks that kind; else the kinds ticked.
    /// </summary>
    internal sealed class OwnershipFilter
    {
        public OwnedKind? Kind;
        public string Find;

        public bool IsEmpty => Kind == null && string.IsNullOrWhiteSpace(Find);

        public bool Keeps(OwnedKind kind, string type, string ownerName) =>
            (Kind == null || kind == Kind.Value) && (string.IsNullOrWhiteSpace(Find) || Holds(ownerName) || Holds(type));

        public bool MarksIt(OwnedKind kind, MarkKinds ticked) => Kind != null ? kind == Kind.Value : OwnershipCount.Marks(kind, ticked);

        /// <summary>"Games running creatures that match "boar" within 64 m, busiest first".</summary>
        public string Heading(float radius)
        {
            string what = Kind switch
            {
                OwnedKind.Creature => "creatures",
                OwnedKind.Vehicle => "vehicles",
                OwnedKind.Item => "items",
                OwnedKind.Piece => "pieces",
                OwnedKind.Other => "other objects",
                _ => "things",
            };
            string match = string.IsNullOrWhiteSpace(Find) ? "" : " that match \"" + Find.Trim() + "\"";
            return "Games running " + what + match + " within " + radius.ToString("0", CultureInfo.InvariantCulture) + " m, busiest first";
        }

        private bool Holds(string text) => text != null && text.IndexOf(Find.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
