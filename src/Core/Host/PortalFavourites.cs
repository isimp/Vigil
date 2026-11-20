using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "AdjustablePortals: favourite portals". AdjustablePortals (0.7.0 and 0.8.0) lets
    /// a player choose where a portal sends them, from a list or on the map, and has no favourites.
    /// The player's favourite portals are kept by each portal's world id in the character's data, so
    /// a renamed or moved portal stays a favourite; a portal torn down and built again is a new one.
    /// In the list they come first under their own heading, in the list's own order; beside the map
    /// they stand in a list of their own, by name.
    /// </summary>
    internal static class PortalFavourites
    {
        public const string AdjustablePortalsGuid = "MidnightsFX.AdjustablePortals";

        /// <summary>The character's data entry holding the favourites.</summary>
        public const string BookKey = "Vigil AdjustablePortals favourite portals";

        /// <summary>Vigil's own book of TargetPortal favourites by world id, and TargetPortal's list of them by place.</summary>
        public const string TargetPortalBookKey = "Vigil TargetPortal favourite portals", TargetPortalFavouritesKey = "TargetPortal Favorites";

        /// <summary>Marks a portal as a favourite, or unmarks one that was; true when it is one now.</summary>
        public static bool Toggle(HashSet<VehicleId> favourites, VehicleId portal)
        {
            if (favourites.Remove(portal)) return false;
            favourites.Add(portal);
            return true;
        }

        /// <summary>The favourites as text for the character's data: "user:id" joined by '|', in order.</summary>
        public static string FormatBook(IEnumerable<VehicleId> favourites) =>
            string.Join("|", favourites.OrderBy(v => v.User).ThenBy(v => v.Id).Select(v => v.ToString()).ToArray());

        /// <summary>Reads <see cref="FormatBook"/>'s text; unreadable entries are left out.</summary>
        public static HashSet<VehicleId> ParseBook(string text)
        {
            var favourites = new HashSet<VehicleId>();
            if (string.IsNullOrEmpty(text)) return favourites;
            foreach (string part in text.Split('|'))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0 || colon == part.Length - 1) continue;
                if (!long.TryParse(part.Substring(0, colon), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long user)) continue;
                if (!uint.TryParse(part.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) continue;
                favourites.Add(new VehicleId(user, id));
            }
            return favourites;
        }

        /// <summary>
        /// The first favourites of a character that has none kept yet: TargetPortal's, by the world
        /// ids in Vigil's book of them (<paramref name="targetPortalBook"/>) and by the places in
        /// TargetPortal's own list (<paramref name="targetPortalFavourites"/>) matched with the
        /// <paramref name="destinations"/>' places as the game writes a place out.
        /// </summary>
        public static HashSet<VehicleId> Seed(string targetPortalBook, string targetPortalFavourites, IEnumerable<(VehicleId Id, string Place)> destinations)
        {
            var seeded = new HashSet<VehicleId>(PinFollowRules.ParseFavouriteBook(targetPortalBook).Keys);
            if (string.IsNullOrEmpty(targetPortalFavourites)) return seeded;
            var places = new HashSet<string>(targetPortalFavourites.Split('|'), StringComparer.Ordinal);
            foreach (var (id, place) in destinations)
                if (place != null && places.Contains(place)) seeded.Add(id);
            return seeded;
        }

        /// <summary>The list with its favourites first; both groups keep the list's own order.</summary>
        public static List<T> FavouritesFirst<T>(IReadOnlyList<T> list, Func<T, bool> isFavourite)
        {
            var ordered = new List<T>(list.Count);
            foreach (var item in list) if (isFavourite(item)) ordered.Add(item);
            foreach (var item in list) if (!isFavourite(item)) ordered.Add(item);
            return ordered;
        }

        /// <summary>
        /// Where the list's two headings go, by the shown rows (favourites first): before the first
        /// favourite row, and before the first other row when a favourite row stands above it; -1
        /// for a heading not shown. With no favourite shown the list has no headings.
        /// </summary>
        public static (int Favourites, int Others) HeadingsAt(IReadOnlyList<bool> favouriteRows)
        {
            if (favouriteRows.Count == 0 || !favouriteRows[0]) return (-1, -1);
            for (int i = 1; i < favouriteRows.Count; i++)
                if (!favouriteRows[i]) return (0, i);
            return (0, -1);
        }

        /// <summary>
        /// A dictionary's entries, key and value, read without its types. A generic dictionary
        /// lists itself as KeyValuePair items through IEnumerable, so only its own
        /// IDictionaryEnumerator gives DictionaryEntry items.
        /// </summary>
        public static List<(object Key, object Value)> Entries(System.Collections.IDictionary dictionary)
        {
            var entries = new List<(object, object)>(dictionary.Count);
            var e = dictionary.GetEnumerator();
            while (e.MoveNext()) entries.Add((e.Key, e.Value));
            return entries;
        }

        /// <summary>The point nearest to (x, z) within <paramref name="radius"/>, as AdjustablePortals picks a portal on its map; -1 for none.</summary>
        public static int NearestWithin(IReadOnlyList<(float X, float Z)> points, float x, float z, float radius)
        {
            int best = -1;
            float bestSq = radius * radius;
            for (int i = 0; i < points.Count; i++)
            {
                float dx = points[i].X - x, dz = points[i].Z - z, sq = dx * dx + dz * dz;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>What the list beside the map shows: favourites, then all other portals, and how many did not fit.</summary>
        public sealed class SideListing
        {
            public readonly List<int> Favourites = new List<int>(), Others = new List<int>();
            public int More;
        }

        /// <summary>
        /// The list beside AdjustablePortals' map: the portals whose name holds <paramref name="filter"/>
        /// (ignoring case; all for none), favourites first, each group by name and among one name
        /// nearest first, as AdjustablePortals orders its own list; at most <paramref name="max"/> rows,
        /// favourites kept first, the rest counted.
        /// </summary>
        public static SideListing SideList(IReadOnlyList<string> names, IReadOnlyList<bool> favourite, IReadOnlyList<float> distance, string filter, int max)
        {
            var shown = new List<int>();
            for (int i = 0; i < names.Count; i++)
                if (string.IsNullOrEmpty(filter) || (names[i] ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) shown.Add(i);
            shown.Sort((a, b) =>
            {
                int byName = string.Compare(names[a], names[b], StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : distance[a].CompareTo(distance[b]);
            });
            var listing = new SideListing();
            foreach (int i in shown) if (favourite[i]) listing.Favourites.Add(i);
            foreach (int i in shown) if (!favourite[i]) listing.Others.Add(i);
            int total = listing.Favourites.Count + listing.Others.Count;
            listing.More = Math.Max(0, total - max);
            int cut = listing.More;
            int fromOthers = Math.Min(cut, listing.Others.Count);
            listing.Others.RemoveRange(listing.Others.Count - fromOthers, fromOthers);
            cut -= fromOthers;
            if (cut > 0) listing.Favourites.RemoveRange(listing.Favourites.Count - cut, cut);
            return listing;
        }

        /// <summary>
        /// Whether a map pin shows under the game patch "AdjustablePortals' map shows only the
        /// portals": while its map of portals is open (<paramref name="pickerOpen"/>), only the pins
        /// it put on the portals to pick (<paramref name="offered"/>); otherwise every pin.
        /// </summary>
        public static bool PinShown(bool pickerOpen, bool offered) => !pickerOpen || offered;

        /// <summary>What "AdjustablePortals' map shows only the portals" does on this game, in words.</summary>
        public static string OnlyPortalsLine(bool on, bool found, string problem, long maps)
        {
            if (!on) return "off: this game's switch (Patches.AdjustablePortalsOnlyPortals)";
            if (!found) return "not needed: AdjustablePortals is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return string.Format(CultureInfo.InvariantCulture, "working · {0} portal maps shown with only their portals", maps);
        }

        /// <summary>What "AdjustablePortals: favourite portals" does on this game, in words.</summary>
        public static string Line(bool on, bool found, string problem, long toggled, long trips)
        {
            if (!on) return "off: this game's switch (Patches.AdjustablePortalsFavourites)";
            if (!found) return "not needed: AdjustablePortals is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return string.Format(CultureInfo.InvariantCulture, "working · {0} favourites marked or unmarked · {1} trips from the list beside the map", toggled, trips);
        }
    }
}
