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
        /// <summary>Whether the buttons can stay, and if not, why.</summary>
        public enum Diff { Same, NotShown, Empty, MorePieces, FewerPieces, OtherPiece, RepairChanged }

        /// <param name="wanted">The pieces the game would show now, in its order, repair pieces among them.</param>
        /// <param name="shown">The pieces on the buttons now, in their order, without the repair button.</param>
        /// <param name="special">True for a repair or remove piece, which gets the button of its own.</param>
        /// <param name="showTags">The current list shows tags; without them the game shows no repair button.</param>
        /// <param name="shownSpecial">The repair button's piece, or null when there is none.</param>
        /// <param name="allShown">Every button still exists and is shown.</param>
        /// <param name="at">The button where another piece stands, or -1.</param>
        public static Diff Compare<T>(IList<T> wanted, IList<T> shown, Func<T, bool> special, bool showTags, T shownSpecial, bool allShown, out int at) where T : class
        {
            at = -1;
            if (!allShown) return Diff.NotShown;
            if (wanted.Count == 0) return Diff.Empty;
            // The game makes a button for every repair or remove piece and keeps the last as its
            // repair button, so with several the last one is the one to compare.
            T expectedSpecial = null;
            int next = 0;
            foreach (var piece in wanted)
            {
                if (special(piece))
                {
                    if (showTags) expectedSpecial = piece;
                    continue;
                }
                if (next >= shown.Count) return Diff.MorePieces;
                if (!ReferenceEquals(shown[next], piece))
                {
                    at = next;
                    return Diff.OtherPiece;
                }
                next++;
            }
            if (next != shown.Count) return Diff.FewerPieces;
            return ReferenceEquals(expectedSpecial, shownSpecial) ? Diff.Same : Diff.RepairChanged;
        }

        public static bool Reuse<T>(IList<T> wanted, IList<T> shown, Func<T, bool> special, bool showTags, T shownSpecial, bool allShown) where T : class =>
            Compare(wanted, shown, special, showTags, shownSpecial, allShown, out _) == Diff.Same;

        public static string Words(Diff diff)
        {
            switch (diff)
            {
                case Diff.Same: return "the same pieces";
                case Diff.NotShown: return "a button hidden or gone";
                case Diff.Empty: return "no pieces";
                case Diff.MorePieces: return "more pieces than buttons";
                case Diff.FewerPieces: return "fewer pieces than buttons";
                case Diff.OtherPiece: return "another piece in a button's place";
                default: return "the repair button came, went or changed";
            }
        }

        public static bool SkipNavigation(bool on, bool menuShown) => on && !menuShown;

        /// <summary>What the patch does on this game, in words, with why the last rebuild happened.</summary>
        public static string StateLine(bool on, string problem, long kept, long rebuilt, Diff last = Diff.Same)
        {
            if (!on) return "off: this game's switch (Patches.BuildMenuFaster)";
            if (problem != null) return "stepped aside: " + problem;
            long opens = kept + rebuilt;
            if (opens == 0) return "working · no open yet";
            string line = string.Format(CultureInfo.InvariantCulture, "working · buttons kept on {0} of {1} opens", kept, opens);
            return last == Diff.Same ? line : line + " · last rebuild: " + Words(last);
        }
    }
}
