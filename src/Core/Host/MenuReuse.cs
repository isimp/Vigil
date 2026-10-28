using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "The build menu opens faster": opening the menu rebuilds one button per piece,
    /// every button set up again. When the pieces the menu would show are the ones its buttons show
    /// already, in the same order, and the repair button is the same, the buttons stay and only their
    /// material colours and favourite stars are refreshed. The menu's navigation (which button the
    /// arrow keys and a controller go to) is set up only while the menu is shown: while it is hidden
    /// the game finds no buttons to link and the setup only forces every canvas to update.
    /// </summary>
    internal static class MenuReuse
    {
        /// <param name="wanted">The pieces the game would show now, in its order, repair pieces among them.</param>
        /// <param name="shown">The pieces on the buttons now, in their order, without the repair button.</param>
        /// <param name="special">True for a repair or remove piece, which gets the button of its own.</param>
        /// <param name="showTags">The current list shows tags; without them the game shows no repair button.</param>
        /// <param name="shownSpecial">The repair button's piece, or null when there is none.</param>
        /// <param name="allShown">Every button still exists and is shown.</param>
        public static bool Reuse<T>(IList<T> wanted, IList<T> shown, Func<T, bool> special, bool showTags, T shownSpecial, bool allShown) where T : class
        {
            if (!allShown || wanted.Count == 0) return false;
            T expectedSpecial = null;
            int specials = 0, next = 0;
            foreach (var piece in wanted)
            {
                if (special(piece))
                {
                    if (!showTags) continue;
                    specials++;
                    expectedSpecial = piece;
                    continue;
                }
                if (next >= shown.Count || !ReferenceEquals(shown[next], piece)) return false;
                next++;
            }
            if (next != shown.Count || specials > 1) return false;
            return ReferenceEquals(expectedSpecial, shownSpecial);
        }

        public static bool SkipNavigation(bool on, bool menuShown) => on && !menuShown;

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, string problem, long kept, long rebuilt)
        {
            if (!on) return "off: this game's switch (Patches.BuildMenuFaster)";
            if (problem != null) return "stepped aside: " + problem;
            long opens = kept + rebuilt;
            return opens == 0
                ? "working · no open yet"
                : string.Format(CultureInfo.InvariantCulture, "working · buttons kept on {0} of {1} opens", kept, opens);
        }
    }
}
