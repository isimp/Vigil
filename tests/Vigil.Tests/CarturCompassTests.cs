using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants a compass that shows what the map shows. HUDCompass's compass draws pins
    /// Cartur's Map Pins hides on the map, all in one colour. With the patch on, the compass leaves
    /// out what Cartur hides and takes its colours.
    /// </summary>
    public sealed class CarturCompassTests
    {
        private const int PinNone = 8;

        private static readonly Relocate.Rgba White = new Relocate.Rgba(1f, 1f, 1f, 1f);
        private static readonly Relocate.Rgba Red = new Relocate.Rgba(0.85f, 0.2f, 0.18f, 0.6f);
        private static readonly Relocate.Rgba OreTint = new Relocate.Rgba(0.8f, 0.5f, 0.2f, 1f);

        [Fact]
        public void A_map_pin_Cartur_hides_by_name_or_with_its_hide_key_is_left_off_the_compass()
        {
            Assert.True(CarturPinRules.HiddenOnCompass(fromMapPins: true, hiddenByName: true, hiddenByKey: false));
            Assert.True(CarturPinRules.HiddenOnCompass(fromMapPins: true, hiddenByName: false, hiddenByKey: true));
            Assert.False(CarturPinRules.HiddenOnCompass(fromMapPins: true, hiddenByName: false, hiddenByKey: false));
        }

        [Fact]
        public void Pins_Cartur_does_not_draw_such_as_players_locations_and_the_death_mark_stay_on_the_compass()
        {
            // Cartur's hiding works on the map's own pin list only; its hide key would say "hide" for any pin.
            Assert.False(CarturPinRules.HiddenOnCompass(fromMapPins: false, hiddenByName: false, hiddenByKey: true));
            Assert.False(CarturPinRules.HiddenOnCompass(fromMapPins: false, hiddenByName: true, hiddenByKey: true));
        }

        [Fact]
        public void A_pin_given_a_colour_in_Cartur_s_editor_has_that_colour_on_the_compass()
        {
            var c = CarturPinRules.Colour(White, Red, OreTint, crossedOff: false, looted: false);
            Assert.Equal(Red, c);
        }

        [Fact]
        public void An_ore_pin_without_its_own_colour_takes_Cartur_s_ore_tint()
        {
            var c = CarturPinRules.Colour(White, null, OreTint, crossedOff: false, looted: false);
            Assert.Equal(OreTint, c);
        }

        [Fact]
        public void A_pin_Cartur_does_not_colour_keeps_HUDCompass_s_pin_colour()
        {
            Assert.Equal(White, CarturPinRules.Colour(White, null, null, crossedOff: false, looted: false));
        }

        [Fact]
        public void A_crossed_off_pin_keeps_HUDCompass_s_colour_as_Cartur_keeps_it_on_the_map()
        {
            Assert.Equal(White, CarturPinRules.Colour(White, Red, OreTint, crossedOff: true, looted: false));
        }

        [Fact]
        public void A_looted_chest_is_half_as_strong_on_the_compass_as_on_the_map()
        {
            var plain = CarturPinRules.Colour(White, null, null, crossedOff: false, looted: true);
            Assert.Equal(0.5f, plain.A, 3);
            var styled = CarturPinRules.Colour(White, Red, null, crossedOff: false, looted: true);
            Assert.Equal(0.3f, styled.A, 3);
            Assert.Equal(Red.R, styled.R, 3);
        }

        [Fact]
        public void A_chest_counts_as_looted_only_while_Cartur_has_a_looted_icon()
        {
            Assert.True(CarturPinRules.Looted(lootedType: 140, pinType: 140, none: PinNone));
            Assert.False(CarturPinRules.Looted(lootedType: 140, pinType: 141, none: PinNone));
            Assert.False(CarturPinRules.Looted(lootedType: PinNone, pinType: PinNone, none: PinNone));
        }

        [Fact]
        public void The_follow_state_line_says_it_needs_the_compass_patch_and_what_it_did()
        {
            Assert.Equal("off: this game's switch (Patches.HudCompassFollowsCartur)", CarturPinRules.FollowLine(false, true, true, null, 0, 0));
            Assert.Equal("not needed: HUDCompass or Cartur's Map Pins is not installed", CarturPinRules.FollowLine(true, false, true, null, 0, 0));
            Assert.Equal("waiting: it works inside HudCompassPins, which is off", CarturPinRules.FollowLine(true, true, false, null, 0, 0));
            Assert.Equal("stepped aside: Cartur's colour code changed (an update)", CarturPinRules.FollowLine(true, true, true, "Cartur's colour code changed (an update)", 0, 0));
            Assert.Equal("working · last frame 4 pins hidden as on the map, 12 in Cartur's colours", CarturPinRules.FollowLine(true, true, true, null, 4, 12));
        }

        // Cartur's map search: while text is typed, a map pin whose name does not hold it is drawn at
        // 15 % and loses its label. Cartur's hide key (2.1.0) steps through all pins, only your own,
        // only auto pins and none (2.0: all, none, only your own). HUDCompass's cart, ship and portal
        // pins are not map pins and stayed as they were; with the patch on they follow both. Nobody
        // places them by hand, so they go with the auto pins.
        private const string AllShown = "All", MineOnly = "Yours", AutoOnly = "Auto", NoneShown = "None";

        [Fact]
        public void While_searching_a_cart_ship_or_portal_pin_that_does_not_match_fades_like_Cartur_s_pins()
        {
            Assert.Equal(0.15f, CarturPinRules.MapIconAlpha(AllShown, searching: true, matches: false), 3);
            Assert.Equal(0f, CarturPinRules.MapLabelAlpha(AllShown, searching: true, matches: false), 3);
        }

        [Fact]
        public void While_searching_a_matching_portal_stays_fully_shown_with_its_name()
        {
            Assert.Equal(1f, CarturPinRules.MapIconAlpha(AllShown, searching: true, matches: true), 3);
            Assert.Equal(1f, CarturPinRules.MapLabelAlpha(AllShown, searching: true, matches: true), 3);
        }

        [Fact]
        public void Without_a_search_or_hiding_every_pin_is_shown_as_HUDCompass_draws_it()
        {
            Assert.Equal(1f, CarturPinRules.MapIconAlpha(AllShown, searching: false, matches: false), 3);
            Assert.Equal(1f, CarturPinRules.MapLabelAlpha(AllShown, searching: false, matches: false), 3);
        }

        [Fact]
        public void The_hide_key_hides_portals_carts_and_ships_with_no_pins_and_with_only_your_own()
        {
            foreach (string mode in new[] { NoneShown, MineOnly })
            {
                Assert.Equal(0f, CarturPinRules.MapIconAlpha(mode, searching: false, matches: true), 3);
                Assert.Equal(0f, CarturPinRules.MapLabelAlpha(mode, searching: false, matches: true), 3);
                // A search, matching or not, does not bring a hidden pin back, not even faded.
                Assert.Equal(0f, CarturPinRules.MapIconAlpha(mode, searching: true, matches: true), 3);
                Assert.Equal(0f, CarturPinRules.MapIconAlpha(mode, searching: true, matches: false), 3);
            }
        }

        [Fact]
        public void With_only_auto_pins_shown_portals_carts_and_ships_stay_and_the_search_still_fades_them()
        {
            Assert.Equal(1f, CarturPinRules.MapIconAlpha(AutoOnly, searching: false, matches: false), 3);
            Assert.Equal(1f, CarturPinRules.MapLabelAlpha(AutoOnly, searching: false, matches: false), 3);
            Assert.Equal(0.15f, CarturPinRules.MapIconAlpha(AutoOnly, searching: true, matches: false), 3);
            Assert.Equal(0f, CarturPinRules.MapLabelAlpha(AutoOnly, searching: true, matches: false), 3);
        }

        [Fact]
        public void A_hide_key_mode_Vigil_does_not_know_leaves_the_pins_shown()
        {
            Assert.False(CarturPinRules.KeyHides("Favourites"));
            Assert.False(CarturPinRules.KeyHides(null));
        }

        [Fact]
        public void The_map_state_line_says_what_the_patch_does_on_this_game()
        {
            Assert.Equal("off: this game's switch (Patches.HudCompassCarturMap)", CarturPinRules.MapLine(false, true, null, AllShown, false, 0, 0));
            Assert.Equal("not needed: HUDCompass or Cartur's Map Pins is not installed", CarturPinRules.MapLine(true, false, null, AllShown, false, 0, 0));
            Assert.Equal("stepped aside: x", CarturPinRules.MapLine(true, true, "x", AllShown, false, 0, 0));
            Assert.Equal("working · no search, pins shown", CarturPinRules.MapLine(true, true, null, AllShown, false, 0, 0));
            Assert.Equal("working · searching: 2 of 30 carts, ships and portals match", CarturPinRules.MapLine(true, true, null, AllShown, true, 2, 30));
            Assert.Equal("working · hidden with the hide key: 30 carts, ships and portals", CarturPinRules.MapLine(true, true, null, NoneShown, false, 0, 30));
            Assert.Equal("working · hidden with the hide key: 30 carts, ships and portals", CarturPinRules.MapLine(true, true, null, MineOnly, true, 0, 30));
            Assert.Equal("working · no search, pins shown", CarturPinRules.MapLine(true, true, null, AutoOnly, false, 0, 30));
        }
    }
}
