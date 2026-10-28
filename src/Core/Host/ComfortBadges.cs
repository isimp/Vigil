using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "ComfortMe redraws its badges only on a change": ComfortMe 1.3.1 redraws the
    /// badge on every piece button of the build menu on every frame the menu is open. A badge shows
    /// what its comfort snapshot says about its piece (ComfortMe keeps a snapshot for 0.2 s, or until
    /// the player moves 25 cm), under ComfortMe's own settings, for the player and the buttons the
    /// menu shows. The redraw runs only when one of those differs from the last redraw, and always
    /// right after the menu's buttons were rebuilt or one of ComfortMe's settings changed.
    /// </summary>
    internal static class ComfortBadges
    {
        public const string Guid = "hardwire99.comfortme";

        /// <summary>What a redraw depends on, apart from the forced cases.</summary>
        public readonly struct Key
        {
            public Key(object hud, object player, object snapshot, bool menuOpen, int buttons)
            {
                Hud = hud;
                Player = player;
                Snapshot = snapshot;
                MenuOpen = menuOpen;
                Buttons = buttons;
            }

            public object Hud { get; }
            public object Player { get; }
            public object Snapshot { get; }
            public bool MenuOpen { get; }
            public int Buttons { get; }

            public bool Same(Key other) =>
                ReferenceEquals(Hud, other.Hud) && ReferenceEquals(Player, other.Player) && ReferenceEquals(Snapshot, other.Snapshot) &&
                MenuOpen == other.MenuOpen && Buttons == other.Buttons;
        }

        public static bool Skip(Key last, Key now, bool hasLast, bool forced) => hasLast && !forced && last.Same(now);

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool found, string version, string problem, long ran, long skipped, double ranMs)
        {
            if (!on) return "off: this game's switch (Patches.ComfortMeBadges)";
            if (!found) return "not needed: ComfortMe is not installed";
            if (problem != null) return "stepped aside: " + problem;
            string line = "working · ComfortMe " + version;
            long all = ran + skipped;
            if (all == 0) return line + " · no badge redraw yet";
            return line + string.Format(CultureInfo.InvariantCulture, " · {0} of {1} badge redraws skipped, {2:0.00} ms each when run", skipped, all, ran > 0 ? ranMs / ran : 0);
        }
    }
}
