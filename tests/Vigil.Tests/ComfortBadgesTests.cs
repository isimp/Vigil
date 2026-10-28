using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants the build menu to stay smooth with ComfortMe. ComfortMe redraws the badge
    /// on every piece button on every frame the menu is open, though what a badge shows comes only
    /// from its comfort snapshot (new at least every 0.2 s, and when the player moves), its own
    /// settings, the player and the menu's buttons. The redraw runs only when one of those changed
    /// since the last one, and always right after the buttons were rebuilt or a setting changed.
    /// </summary>
    public sealed class ComfortBadgesTests
    {
        private static readonly object Hud = new object(), Player = new object(), SnapA = new object(), SnapB = new object();

        private static ComfortBadges.Key Key(object snapshot = null, object player = null, bool open = true, int buttons = 1169, object hud = null) =>
            new ComfortBadges.Key(hud ?? Hud, player ?? Player, snapshot ?? SnapA, open, buttons);

        [Fact]
        public void Nothing_changed_skips_the_redraw()
        {
            Assert.True(ComfortBadges.Skip(Key(), Key(), hasLast: true, forced: false));
        }

        [Fact]
        public void The_first_redraw_always_runs()
        {
            Assert.False(ComfortBadges.Skip(Key(), Key(), hasLast: false, forced: false));
        }

        [Fact]
        public void A_new_snapshot_redraws()
        {
            Assert.False(ComfortBadges.Skip(Key(SnapA), Key(SnapB), true, false));
        }

        [Fact]
        public void Opening_or_closing_the_menu_redraws()
        {
            Assert.False(ComfortBadges.Skip(Key(open: true), Key(open: false), true, false));
        }

        [Fact]
        public void Another_player_or_hud_redraws()
        {
            Assert.False(ComfortBadges.Skip(Key(), Key(player: new object()), true, false));
            Assert.False(ComfortBadges.Skip(Key(), Key(hud: new object()), true, false));
        }

        [Fact]
        public void Another_number_of_buttons_redraws()
        {
            Assert.False(ComfortBadges.Skip(Key(buttons: 1169), Key(buttons: 64), true, false));
        }

        [Fact]
        public void A_rebuild_or_a_setting_change_forces_the_redraw()
        {
            Assert.False(ComfortBadges.Skip(Key(), Key(), true, forced: true));
        }

        [Fact]
        public void The_state_line_gives_the_share_skipped_and_what_a_redraw_costs()
        {
            Assert.Equal("off: this game's switch (Patches.ComfortMeBadges)", ComfortBadges.StateLine(false, true, "1.3.1", null, 10, 90, 20));
            Assert.Equal("not needed: ComfortMe is not installed", ComfortBadges.StateLine(true, false, null, null, 0, 0, 0));
            Assert.Equal("stepped aside: its badge code changed (an update)", ComfortBadges.StateLine(true, true, "1.3.1", "its badge code changed (an update)", 0, 0, 0));
            Assert.Equal("working · ComfortMe 1.3.1 · no badge redraw yet", ComfortBadges.StateLine(true, true, "1.3.1", null, 0, 0, 0));
            Assert.Equal("working · ComfortMe 1.3.1 · 90 of 100 badge redraws skipped, 2.00 ms each when run", ComfortBadges.StateLine(true, true, "1.3.1", null, 10, 90, 20));
        }
    }
}
