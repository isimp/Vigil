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
