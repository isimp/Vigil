using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants HUDCompass's pins not to cost more with every pin on the map. HUDCompass 1.2.1
    /// rebuilds its pin list on every frame: it names every pin's icon by its position as text, finds
    /// each icon by that name and removes the name from a list one by one, for every pin of a shown
    /// type on the whole map, near or far; a pin that moves gets a new icon object and the old one is
    /// destroyed. With the patch on, an icon is kept per position without text, an icon whose pin is
    /// gone is kept aside and reused for the next new position, and the pin rules give the answers
    /// HUDCompass's own give.
    /// </summary>
    public sealed class CompassPinsTests
    {
        private sealed class Icon
        {
            public int Id;
        }

        private static int s_made;
        private static Icon Make() => new Icon { Id = ++s_made };

        [Fact]
        public void A_pin_that_stays_put_keeps_its_icon_from_frame_to_frame()
        {
            var book = new CompassPinBook<Icon>();
            var key = PinKey.Of(10f, 30f, -5f);
            book.BeginFrame();
            var first = book.Place(key, Make, out bool reused);
            Assert.False(reused);
            book.EndFrame(new List<Icon>());

            book.BeginFrame();
            Assert.True(book.TryGet(key, out var again));
            Assert.Same(first, again);
            var released = new List<Icon>();
            book.EndFrame(released);
            Assert.Empty(released);
            Assert.Equal(1, book.Live);
        }

        [Fact]
        public void A_pin_that_moves_reuses_the_icon_it_left_instead_of_making_a_new_one()
        {
            var book = new CompassPinBook<Icon>();
            book.BeginFrame();
            var first = book.Place(PinKey.Of(0f, 0f, 0f), Make, out _);
            book.EndFrame(new List<Icon>());

            // The next frame the pin is a metre further; its old position is not seen.
            book.BeginFrame();
            Assert.False(book.TryGet(PinKey.Of(1f, 0f, 0f), out _));
            var released = new List<Icon>();
            book.EndFrame(released);
            Assert.Equal(new[] { first }, released);

            book.BeginFrame();
            var moved = book.Place(PinKey.Of(1f, 0f, 0f), Make, out bool reused);
            Assert.True(reused);
            Assert.Same(first, moved);
            book.EndFrame(new List<Icon>());
            Assert.Equal(1, book.Live);
            Assert.Equal(0, book.Spare);
        }

        [Fact]
        public void An_icon_seen_but_not_placed_this_frame_is_kept_like_a_hidden_far_pin()
        {
            // HUDCompass keeps a far pin's icon switched off rather than destroying it.
            var book = new CompassPinBook<Icon>();
            var key = PinKey.Of(500f, 0f, 500f);
            book.BeginFrame();
            book.Place(key, Make, out _);
            book.EndFrame(new List<Icon>());

            book.BeginFrame();
            Assert.True(book.TryGet(key, out _));
            var released = new List<Icon>();
            book.EndFrame(released);
            Assert.Empty(released);
        }

        [Fact]
        public void Two_pins_on_one_spot_share_one_icon_as_HUDCompass_names_them_by_position()
        {
            var book = new CompassPinBook<Icon>();
            book.BeginFrame();
            var a = book.Place(PinKey.Of(3.001f, 2f, 1f), Make, out _);
            Assert.True(book.TryGet(PinKey.Of(3.002f, 2f, 1f), out var b));
            Assert.Same(a, b);
            book.EndFrame(new List<Icon>());
            Assert.Equal(1, book.Live);
        }

        [Fact]
        public void Positions_a_centimetre_apart_get_icons_of_their_own()
        {
            Assert.NotEqual(PinKey.Of(3.00f, 2f, 1f), PinKey.Of(3.01f, 2f, 1f));
            Assert.NotEqual(PinKey.Of(3f, 2f, 1f), PinKey.Of(3f, 2f, -1f));
            Assert.Equal(PinKey.Of(-7.499f, 0f, 0f), PinKey.Of(-7.501f, 0f, 0f));
        }

        [Fact]
        public void Spare_icons_are_kept_up_to_a_limit_and_the_rest_are_handed_back_to_be_destroyed()
        {
            var book = new CompassPinBook<Icon>(spareLimit: 2);
            book.BeginFrame();
            for (int i = 0; i < 5; i++) book.Place(PinKey.Of(i, 0f, 0f), Make, out _);
            book.EndFrame(new List<Icon>());

            book.BeginFrame();
            var released = new List<Icon>();
            var destroyed = new List<Icon>();
            book.EndFrame(released, destroyed);
            Assert.Equal(5, released.Count);
            Assert.Equal(3, destroyed.Count);
            Assert.Equal(2, book.Spare);
            Assert.Equal(0, book.Live);
        }

        [Fact]
        public void Forgetting_drops_every_icon_for_a_new_HUD()
        {
            var book = new CompassPinBook<Icon>();
            // One icon in use at (2,2,2), one kept aside from (1,1,1).
            book.BeginFrame();
            book.Place(PinKey.Of(1f, 1f, 1f), Make, out _);
            book.Place(PinKey.Of(2f, 2f, 2f), Make, out _);
            book.EndFrame(new List<Icon>());
            book.BeginFrame();
            book.TryGet(PinKey.Of(2f, 2f, 2f), out _);
            book.EndFrame(new List<Icon>());
            Assert.Equal(1, book.Live);
            Assert.Equal(1, book.Spare);

            book.Forget();
            Assert.Equal(0, book.Live);
            Assert.Equal(0, book.Spare);
            book.BeginFrame();
            Assert.False(book.TryGet(PinKey.Of(2f, 2f, 2f), out _));
            book.Place(PinKey.Of(2f, 2f, 2f), Make, out bool reused);
            Assert.False(reused);
        }

        [Fact]
        public void Ignored_names_match_exactly_by_prefix_or_by_suffix_like_HUDCompass()
        {
            var exact = new List<string> { "Copper" };
            var prefixes = new List<string> { "Silver" };
            var suffixes = new List<string> { "Raspberry" };
            Assert.True(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "Copper"));
            Assert.False(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "copper"));
            Assert.False(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "Copper ore"));
            Assert.True(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "Silver vein"));
            Assert.True(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "Silver"));
            Assert.False(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "Silve"));
            Assert.True(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "5 Raspberry"));
            Assert.False(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "Raspberry bush"));
            Assert.False(CompassPinRules.NameIgnored(exact, prefixes, suffixes, "Tin"));
        }

        [Fact]
        public void A_blank_name_is_never_ignored_and_an_empty_prefix_matches_nothing()
        {
            var none = new List<string>();
            var emptyPrefix = new List<string> { "" };
            Assert.False(CompassPinRules.NameIgnored(new List<string> { "" }, none, none, ""));
            Assert.False(CompassPinRules.NameIgnored(none, none, none, null));
            Assert.False(CompassPinRules.NameIgnored(none, none, new List<string> { "" }, "   "));
            Assert.False(CompassPinRules.NameIgnored(none, emptyPrefix, none, "Anything"));
        }

        [Fact]
        public void An_empty_suffix_from_a_lone_star_matches_every_named_pin_as_in_HUDCompass()
        {
            var none = new List<string>();
            Assert.True(CompassPinRules.NameIgnored(none, none, new List<string> { "" }, "Anything"));
        }

        [Fact]
        public void A_pin_type_is_named_by_its_icon_alias_or_else_by_its_type()
        {
            var alias = new Dictionary<string, string> { { "mapicon_portal", "Portal" }, { "mapicon_anchor", "Ship" } };
            Assert.Equal("Portal", CompassPinRules.TypeWord("mapicon_portal", "Icon4", alias));
            Assert.Equal("Shout", CompassPinRules.TypeWord("mapicon_shout", "Shout", alias));
            Assert.Equal("Ping", CompassPinRules.TypeWord(null, "Ping", alias));
        }

        [Fact]
        public void A_type_rule_or_a_name_rule_with_no_entries_matches_nothing()
        {
            var alias = new Dictionary<string, string>();
            var none = new List<string>();
            Assert.False(CompassPinRules.TypeMatches(none, "Shout", "Shout", alias));
            Assert.False(CompassPinRules.TypeOrNameMatches(none, "Shout", "Shout", "Shout", alias));
            var always = new List<string> { "Boss", "Home" };
            Assert.True(CompassPinRules.TypeOrNameMatches(always, "mapicon_x", "Boss", "Eikthyr", alias));
            Assert.True(CompassPinRules.TypeOrNameMatches(always, "mapicon_x", "Icon0", "Home", alias));
            Assert.False(CompassPinRules.TypeOrNameMatches(always, "mapicon_x", "Icon0", "home", alias));
        }

        [Fact]
        public void A_pin_is_in_range_from_the_minimum_to_the_maximum_both_included()
        {
            Assert.True(CompassPinRules.InBounds(1f, 1f, 300f));
            Assert.True(CompassPinRules.InBounds(300f, 1f, 300f));
            Assert.False(CompassPinRules.InBounds(0.99f, 1f, 300f));
            Assert.False(CompassPinRules.InBounds(300.01f, 1f, 300f));
        }

        [Fact]
        public void The_state_line_says_what_the_patch_does_on_this_game()
        {
            Assert.Equal("off: this game's switch (Patches.HudCompassPins)", CompassPinRules.StateLine(false, true, "1.2.1", null, 0, 0, 0, 0, 0, 0, 0));
            Assert.Equal("off: this game's switch (Patches.HudCompassPins) · HUDCompass's own pins 3.20 ms a frame", CompassPinRules.StateLine(false, true, "1.2.1", null, 0, 0, 0, 0, 0, 100, 320));
            Assert.Equal("not needed: HUDCompass is not installed", CompassPinRules.StateLine(true, false, null, null, 0, 0, 0, 0, 0, 0, 0));
            Assert.Equal("stepped aside: its compass code changed (an update)", CompassPinRules.StateLine(true, true, "1.3.0", "its compass code changed (an update)", 0, 0, 0, 0, 0, 0, 0));
            Assert.Equal("working · HUDCompass 1.2.1 · no frame yet", CompassPinRules.StateLine(true, true, "1.2.1", null, 0, 0, 0, 0, 0, 0, 0));
            Assert.Equal("working · HUDCompass 1.2.1 · 0.40 ms a frame for 412 pins · 6 icons made, 90 reused", CompassPinRules.StateLine(true, true, "1.2.1", null, 1000, 400, 412, 6, 90, 0, 0));
        }
    }
}
