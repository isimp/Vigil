using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// How map pins follow the portal, cart or ship they stand for. The game redraws its map pins
    /// only when something asks for it (walking, dragging, zooming, a pin added or removed), and
    /// HUDCompass 1.2.1 writes a pin's name tag and colour only when it makes the pin; TargetPortal
    /// names its pins once and keeps favourites by the place a portal had when it was marked.
    /// </summary>
    internal static class PinFollowRules
    {
        /// <summary>Sideways metres a pin must be off before a redraw is asked for: less than a map pixel at any zoom the map draws names at.</summary>
        public const float MapMoveMetres = 0.5f;

        /// <summary>Whether the object has moved sideways far enough to show on the map; up and down never shows.</summary>
        public static bool MovedOnMap(float fromX, float fromZ, float toX, float toZ)
        {
            float dx = toX - fromX, dz = toZ - fromZ;
            return dx * dx + dz * dz >= MapMoveMetres * MapMoveMetres;
        }

        /// <summary>Whether the pin, as the last redraw left it, differs from the object: moved, renamed or (a portal) linked or unlinked.</summary>
        public static bool RedrawWanted(bool moved, string drawnLabel, string label, bool drawnActive, bool active) =>
            moved || !string.Equals(drawnLabel, label, StringComparison.Ordinal) || drawnActive != active;

        /// <summary>
        /// Whether HUDCompass's pin must be made again: its name tag (large map only) shows another
        /// name, or a portal's icon was coloured for the other link state. Made again, it takes the
        /// current name and colour.
        /// </summary>
        public static bool Rebuild(bool hasNametag, string nametagText, string label, bool iconActive, bool active, bool portal) =>
            hasNametag && !string.Equals(nametagText ?? "", label ?? "", StringComparison.Ordinal) || portal && iconActive != active;

        /// <summary>The name a pin takes from the server's list: the listed one when it has one, else the pin's own (a cart's or ship's type).</summary>
        public static string ServerLabel(string current, string told) => string.IsNullOrEmpty(told) ? current : told;

        /// <summary>Whether a TargetPortal pin's name differs from the portal's tag.</summary>
        public static bool NameChanged(string pinName, string tag) => !string.Equals(pinName ?? "", tag ?? "", StringComparison.Ordinal);

        /// <summary>
        /// TargetPortal's favourites ("place|place", each as Vector3 text) with a portal's old place
        /// replaced by its new one; kept once when the new place is listed already. Unchanged (the
        /// same string) when the old place is not a favourite.
        /// </summary>
        public static string FavouritesMoved(string saved, string from, string to)
        {
            if (string.IsNullOrEmpty(saved) || from == to) return saved;
            var list = new List<string>(saved.Split('|'));
            int at = list.IndexOf(from);
            if (at < 0) return saved;
            if (list.Contains(to)) list.RemoveAt(at);
            else list[at] = to;
            return string.Join("|", list.ToArray());
        }

        /// <summary>
        /// Keeps TargetPortal's favourites with their portals. <paramref name="book"/> remembers, by
        /// the portal's world id (which a move does not change), the place text each favourite
        /// portal had when last seen; <paramref name="now"/> holds the place text of every portal
        /// this game knows right now. A remembered portal standing elsewhere has its favourite moved
        /// (<see cref="FavouritesMoved"/>); a remembered place no longer among the favourites is
        /// forgotten; a known portal standing on a favourite's place is remembered. A portal this
        /// game does not know right now keeps its entry. Returns the favourites, the same string when
        /// nothing moved.
        /// </summary>
        public static string FavouritesResync(string favs, Dictionary<VehicleId, string> now, Dictionary<VehicleId, string> book, out int moved)
        {
            moved = 0;
            foreach (var id in new List<VehicleId>(book.Keys))
            {
                if (!now.TryGetValue(id, out string place) || place == book[id]) continue;
                string next = FavouritesMoved(favs, book[id], place);
                if (!ReferenceEquals(next, favs)) moved++;
                favs = next;
                book[id] = place;
            }
            var listed = new HashSet<string>(string.IsNullOrEmpty(favs) ? Array.Empty<string>() : favs.Split('|'));
            foreach (var id in new List<VehicleId>(book.Keys))
                if (!listed.Contains(book[id])) book.Remove(id);
            foreach (var pair in now)
                if (listed.Contains(pair.Value) && !book.ContainsKey(pair.Key)) book[pair.Key] = pair.Value;
            return favs;
        }

        /// <summary>The remembered favourite portals as text for the character's data: "user:id@place" joined by '|'.</summary>
        public static string FormatFavouriteBook(Dictionary<VehicleId, string> book)
        {
            var parts = new List<string>();
            foreach (var pair in book)
                parts.Add(pair.Key.User.ToString(CultureInfo.InvariantCulture) + ":" + pair.Key.Id.ToString(CultureInfo.InvariantCulture) + "@" + pair.Value);
            return string.Join("|", parts.ToArray());
        }

        /// <summary>Reads <see cref="FormatFavouriteBook"/>'s text; unreadable entries are left out.</summary>
        public static Dictionary<VehicleId, string> ParseFavouriteBook(string text)
        {
            var book = new Dictionary<VehicleId, string>();
            if (string.IsNullOrEmpty(text)) return book;
            foreach (string part in text.Split('|'))
            {
                int colon = part.IndexOf(':'), at = part.IndexOf('@');
                if (colon <= 0 || at <= colon + 1 || at == part.Length - 1) continue;
                if (!long.TryParse(part.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out long user)) continue;
                if (!uint.TryParse(part.Substring(colon + 1, at - colon - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id)) continue;
                book[new VehicleId(user, id)] = part.Substring(at + 1);
            }
            return book;
        }

        /// <summary>What "HUDCompass's pins follow" does on this game, in words.</summary>
        public static string CompassLine(bool on, bool found, string problem, long redraws, long rebuilt)
        {
            if (!on) return "off: this game's switch (Patches.HudCompassPinsFollow)";
            if (!found) return "not needed: HUDCompass is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return string.Format(CultureInfo.InvariantCulture, "working · {0} redraws asked for a moved, renamed or linked pin · {1} pins made again", redraws, rebuilt);
        }

        /// <summary>What "TargetPortal's pins follow" does on this game, in words.</summary>
        public static string TargetPortalLine(bool on, bool found, string problem, long renamed, long favouritesMoved)
        {
            if (!on) return "off: this game's switch (Patches.TargetPortalPinsFollow)";
            if (!found) return "not needed: TargetPortal is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return string.Format(CultureInfo.InvariantCulture, "working · {0} pins renamed · {1} favourites moved with their portal", renamed, favouritesMoved);
        }
    }
}
