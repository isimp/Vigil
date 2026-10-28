using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants the build menu to open faster without anything in it changing. Opening it
    /// rebuilds one button per piece; when the pieces it would show are the ones its buttons already
    /// show, in the same order and with the same repair button, the buttons stay and only their
    /// material colours and favourite stars are refreshed. Anything else rebuilds as the game does.
    /// The menu's navigation is set up only once it is shown, since while hidden it finds no buttons.
    /// </summary>
    public sealed class MenuReuseTests
    {
        private sealed class P
        {
            public P(string name, bool special = false) { Name = name; Special = special; }
            public string Name { get; }
            public bool Special { get; }
        }

        private static readonly P Wall = new P("wall"), Floor = new P("floor"), Door = new P("door"), Repair = new P("repair", special: true);

        private static bool Reuse(IList<P> wanted, IList<P> shown, P shownSpecial, bool showTags = true, bool allShown = true) =>
            MenuReuse.Reuse(wanted, shown, p => p.Special, showTags, shownSpecial, allShown);

        [Fact]
        public void The_same_pieces_in_the_same_order_with_the_same_repair_button_keep_the_buttons()
        {
            Assert.True(Reuse(new[] { Repair, Wall, Floor, Door }, new[] { Wall, Floor, Door }, Repair));
        }

        [Fact]
        public void A_piece_more_or_less_rebuilds()
        {
            Assert.False(Reuse(new[] { Repair, Wall, Floor, Door }, new[] { Wall, Floor }, Repair));
            Assert.False(Reuse(new[] { Repair, Wall }, new[] { Wall, Floor }, Repair));
        }

        [Fact]
        public void Another_order_rebuilds_as_the_recent_tab_reorders_after_building()
        {
            Assert.False(Reuse(new[] { Repair, Floor, Wall }, new[] { Wall, Floor }, Repair));
        }

        [Fact]
        public void Another_piece_in_the_same_place_rebuilds()
        {
            Assert.False(Reuse(new[] { Repair, Wall, Door }, new[] { Wall, Floor }, Repair));
        }

        [Fact]
        public void A_repair_button_that_comes_or_goes_rebuilds()
        {
            Assert.False(Reuse(new[] { Wall, Floor }, new[] { Wall, Floor }, Repair));
            Assert.False(Reuse(new[] { Repair, Wall, Floor }, new[] { Wall, Floor }, shownSpecial: null));
        }

        [Fact]
        public void Without_tags_the_game_shows_no_repair_button_so_none_is_expected()
        {
            Assert.True(Reuse(new[] { Repair, Wall, Floor }, new[] { Wall, Floor }, shownSpecial: null, showTags: false));
            Assert.False(Reuse(new[] { Repair, Wall, Floor }, new[] { Wall, Floor }, Repair, showTags: false));
        }

        [Fact]
        public void With_several_repair_pieces_the_last_one_is_the_repair_button_as_in_the_game()
        {
            // The game makes a repair button for each and keeps only the last as its repair button.
            var scroll = new P("GB_Repair_Scroll", special: true);
            var hammer = new P("OP_Repair_Hammer", special: true);
            Assert.True(Reuse(new[] { Repair, Wall, scroll, hammer }, new[] { Wall }, hammer));
            Assert.False(Reuse(new[] { Repair, Wall, scroll, hammer }, new[] { Wall }, Repair));
        }

        [Fact]
        public void A_button_gone_or_hidden_rebuilds()
        {
            Assert.False(Reuse(new[] { Repair, Wall, Floor }, new[] { Wall, Floor }, Repair, allShown: false));
        }

        [Fact]
        public void An_empty_menu_is_never_kept()
        {
            Assert.False(Reuse(new P[0], new P[0], null));
        }

        // ---- why the buttons were rebuilt, for the patch's line and the game's log

        private static MenuReuse.Diff Why(IList<P> wanted, IList<P> shown, P shownSpecial, bool showTags = true, bool allShown = true) =>
            MenuReuse.Compare(wanted, shown, p => p.Special, showTags, shownSpecial, allShown, out _);

        [Fact]
        public void Each_rebuild_names_its_reason()
        {
            Assert.Equal(MenuReuse.Diff.Same, Why(new[] { Repair, Wall }, new[] { Wall }, Repair));
            Assert.Equal(MenuReuse.Diff.NotShown, Why(new[] { Repair, Wall }, new[] { Wall }, Repair, allShown: false));
            Assert.Equal(MenuReuse.Diff.Empty, Why(new P[0], new P[0], null));
            Assert.Equal(MenuReuse.Diff.MorePieces, Why(new[] { Repair, Wall, Floor }, new[] { Wall }, Repair));
            Assert.Equal(MenuReuse.Diff.FewerPieces, Why(new[] { Repair, Wall }, new[] { Wall, Floor }, Repair));
            Assert.Equal(MenuReuse.Diff.OtherPiece, Why(new[] { Repair, Door }, new[] { Wall }, Repair));
            Assert.Equal(MenuReuse.Diff.RepairChanged, Why(new[] { Wall }, new[] { Wall }, Repair));
        }

        [Fact]
        public void Another_piece_gives_the_button_where_it_differs()
        {
            MenuReuse.Compare(new[] { Repair, Wall, Door }, new[] { Wall, Floor }, p => p.Special, true, Repair, true, out int at);
            Assert.Equal(1, at);
        }

        [Fact]
        public void Each_reason_reads_as_words()
        {
            Assert.Equal("the repair button came, went or changed", MenuReuse.Words(MenuReuse.Diff.RepairChanged));
            Assert.Equal("another piece in a button's place", MenuReuse.Words(MenuReuse.Diff.OtherPiece));
            Assert.Equal("a button hidden or gone", MenuReuse.Words(MenuReuse.Diff.NotShown));
        }

        [Fact]
        public void The_state_line_names_the_last_reason_for_a_rebuild()
        {
            Assert.Equal("working · buttons kept on 0 of 13 opens · last rebuild: more pieces than buttons",
                MenuReuse.StateLine(true, null, 0, 13, MenuReuse.Diff.MorePieces));
            Assert.Equal("working · buttons kept on 3 of 7 opens", MenuReuse.StateLine(true, null, 3, 4, MenuReuse.Diff.Same));
        }

        // ---- stray repair buttons: the game makes one per repair piece and removes only its own

        [Fact]
        public void Every_repair_button_but_the_games_own_is_a_stray()
        {
            var own = new P("own"); var stray1 = new P("stray1"); var stray2 = new P("stray2");
            Assert.Equal(new[] { stray1, stray2 }, MenuReuse.Strays(new[] { stray1, own, stray2 }, own, c => true));
        }

        [Fact]
        public void Anything_in_the_corner_that_is_no_repair_button_stays()
        {
            var own = new P("own"); var other = new P("a mod's label");
            Assert.Empty(MenuReuse.Strays(new[] { own, other }, own, c => c != other));
        }

        [Fact]
        public void Without_a_repair_button_of_the_game_every_repair_button_is_a_stray()
        {
            var a = new P("a");
            Assert.Equal(new[] { a }, MenuReuse.Strays(new[] { a }, null, c => true));
        }

        [Fact]
        public void The_state_line_counts_the_strays_removed()
        {
            Assert.Equal("working · buttons kept on 3 of 7 opens · 26 stray repair buttons removed", MenuReuse.StateLine(true, null, 3, 4, MenuReuse.Diff.Same, 26));
        }

        [Fact]
        public void Navigation_is_set_up_only_while_the_menu_is_shown()
        {
            Assert.True(MenuReuse.SkipNavigation(on: true, menuShown: false));
            Assert.False(MenuReuse.SkipNavigation(on: true, menuShown: true));
            Assert.False(MenuReuse.SkipNavigation(on: false, menuShown: false));
        }

        [Fact]
        public void The_state_line_says_whether_it_works_and_how_often_the_buttons_were_kept()
        {
            Assert.Equal("off: this game's switch (Patches.BuildMenuFaster)", MenuReuse.StateLine(false, null, 3, 4));
            Assert.Equal("stepped aside: the build menu changed (an update)", MenuReuse.StateLine(true, "the build menu changed (an update)", 0, 0));
            Assert.Equal("working · buttons kept on 3 of 7 opens", MenuReuse.StateLine(true, null, 3, 4));
            Assert.Equal("working · no open yet", MenuReuse.StateLine(true, null, 0, 0));
        }

        // ---- the open's line says when the buttons were kept

        [Fact]
        public void An_open_that_kept_its_buttons_says_so_with_the_refresh_time()
        {
            var open = new MenuOpen { FrameMs = 160, ToggleMs = 150, OpenMs = 150, TagsMs = 30, PiecesMs = 20, Kept = 1169, ChecksMs = 15, LayoutMs = 40, NavigationMs = 70 };
            string s = open.Describe();
            Assert.Contains("1,169 piece buttons kept as they were, refreshed in 20 ms (material checks 15 ms)", s);
            Assert.Contains("navigation setup 70 ms in all", s);
        }
    }
}
