using System;
using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants a portal's, cart's or ship's map pin where the object is and under its
    /// current name. The game redraws map pins only when something asks for it, HUDCompass writes a
    /// pin's name and colour only when it makes the pin, and TargetPortal names its pins once and
    /// keeps favourites by the place a portal had. With the patches on, the pins follow.
    /// </summary>
    public sealed class PinFollowTests
    {
        // ---- HUDCompass's pins ----

        [Fact]
        public void A_portal_moved_while_the_player_stands_still_asks_for_a_redraw()
        {
            bool moved = PinFollowRules.MovedOnMap(10f, 20f, 14f, 20f);
            Assert.True(PinFollowRules.RedrawWanted(moved, "Home", "Home", drawnActive: true, active: true));
        }

        [Fact]
        public void A_ship_bobbing_in_place_asks_for_no_redraw()
        {
            // Up and down does not show on the map; a few centimetres sideways are less than a map pixel.
            Assert.False(PinFollowRules.MovedOnMap(10f, 20f, 10.1f, 20.1f));
            Assert.False(PinFollowRules.RedrawWanted(false, "Karve", "Karve", drawnActive: false, active: false));
        }

        [Fact]
        public void Half_a_metre_sideways_counts_as_moved()
        {
            Assert.True(PinFollowRules.MovedOnMap(0f, 0f, 0.3f, 0.4f));
            Assert.False(PinFollowRules.MovedOnMap(0f, 0f, 0.3f, 0.39f));
        }

        [Fact]
        public void A_renamed_or_newly_linked_portal_asks_for_a_redraw()
        {
            Assert.True(PinFollowRules.RedrawWanted(false, "Home", "Mine", drawnActive: true, active: true));
            Assert.True(PinFollowRules.RedrawWanted(false, "Home", "Home", drawnActive: false, active: true));
        }

        [Fact]
        public void A_pin_whose_name_tag_shows_an_old_name_is_made_again()
        {
            Assert.True(PinFollowRules.Rebuild(hasNametag: true, nametagText: "Home", label: "Mine", iconActive: true, active: true, portal: true));
            Assert.False(PinFollowRules.Rebuild(hasNametag: true, nametagText: "Mine", label: "Mine", iconActive: true, active: true, portal: true));
        }

        [Fact]
        public void A_pin_without_a_name_whose_tag_is_empty_is_not_made_again_every_redraw()
        {
            // TextMeshPro gives back "" for a text set to nothing.
            Assert.False(PinFollowRules.Rebuild(hasNametag: true, nametagText: "", label: null, iconActive: false, active: false, portal: true));
        }

        [Fact]
        public void A_pin_without_a_name_tag_is_not_made_again_for_its_name()
        {
            // Name tags exist on the large map only; the small map's pins carry no name.
            Assert.False(PinFollowRules.Rebuild(hasNametag: false, nametagText: null, label: "Mine", iconActive: true, active: true, portal: true));
        }

        [Fact]
        public void A_portal_pin_drawn_unlinked_is_made_again_once_the_portal_is_linked()
        {
            Assert.True(PinFollowRules.Rebuild(hasNametag: false, nametagText: null, label: "Home", iconActive: false, active: true, portal: true));
        }

        [Fact]
        public void A_cart_or_ship_pin_is_not_made_again_for_a_link_it_cannot_have()
        {
            Assert.False(PinFollowRules.Rebuild(hasNametag: false, nametagText: null, label: "Cart", iconActive: false, active: true, portal: false));
        }

        [Fact]
        public void A_name_from_the_server_s_list_replaces_the_pin_s_name_only_when_it_has_one()
        {
            Assert.Equal("Mine", PinFollowRules.ServerLabel("Home", "Mine"));
            Assert.Equal("Cart", PinFollowRules.ServerLabel("Cart", ""));
        }

        [Fact]
        public void The_patches_tab_says_what_following_did()
        {
            Assert.Equal("off: this game's switch (Patches.HudCompassPinsFollow)", PinFollowRules.CompassLine(false, true, null, 0, 0));
            Assert.Equal("not needed: HUDCompass is not installed", PinFollowRules.CompassLine(true, false, null, 0, 0));
            Assert.Equal("stepped aside: x", PinFollowRules.CompassLine(true, true, "x", 0, 0));
            Assert.Equal("working · 3 redraws asked for a moved, renamed or linked pin · 2 pins made again", PinFollowRules.CompassLine(true, true, null, 3, 2));
        }

        // ---- TargetPortal's pins ----

        [Fact]
        public void A_TargetPortal_pin_takes_the_portal_s_new_name()
        {
            Assert.True(PinFollowRules.NameChanged("Home", "Mine"));
            Assert.False(PinFollowRules.NameChanged("Home", "Home"));
            Assert.True(PinFollowRules.NameChanged("Home", null));
        }

        [Fact]
        public void A_favourite_follows_its_portal_to_the_new_place()
        {
            string saved = "(1.00, 2.00, 3.00)|(5.00, 6.00, 7.00)";
            Assert.Equal("(1.00, 2.00, 3.00)|(9.00, 6.00, 7.00)", PinFollowRules.FavouritesMoved(saved, "(5.00, 6.00, 7.00)", "(9.00, 6.00, 7.00)"));
        }

        [Fact]
        public void A_portal_that_is_no_favourite_leaves_the_favourites_as_they_are()
        {
            string saved = "(1.00, 2.00, 3.00)";
            Assert.Same(saved, PinFollowRules.FavouritesMoved(saved, "(5.00, 6.00, 7.00)", "(9.00, 6.00, 7.00)"));
            Assert.Null(PinFollowRules.FavouritesMoved(null, "(5.00, 6.00, 7.00)", "(9.00, 6.00, 7.00)"));
        }

        [Fact]
        public void A_favourite_moved_onto_a_place_already_listed_is_kept_once()
        {
            string saved = "(1.00, 2.00, 3.00)|(5.00, 6.00, 7.00)";
            Assert.Equal("(1.00, 2.00, 3.00)", PinFollowRules.FavouritesMoved(saved, "(5.00, 6.00, 7.00)", "(1.00, 2.00, 3.00)"));
        }

        private static readonly VehicleId Home = new VehicleId(-42, 7), Mine = new VehicleId(99, 8);

        [Fact]
        public void A_favourite_follows_its_portal_moved_while_no_TargetPortal_pins_were_shown()
        {
            var book = new Dictionary<VehicleId, string>();
            string favs = "(1.00, 2.00, 3.00)";
            // Seen once where it was marked, then moved: no pin of its old place is needed.
            favs = PinFollowRules.FavouritesResync(favs, new Dictionary<VehicleId, string> { [Home] = "(1.00, 2.00, 3.00)" }, book, out int moved);
            Assert.Equal(0, moved);
            favs = PinFollowRules.FavouritesResync(favs, new Dictionary<VehicleId, string> { [Home] = "(8.00, 2.00, 3.00)" }, book, out moved);
            Assert.Equal("(8.00, 2.00, 3.00)", favs);
            Assert.Equal(1, moved);
        }

        [Fact]
        public void A_favourite_whose_portal_this_game_does_not_know_right_now_is_kept()
        {
            var book = new Dictionary<VehicleId, string> { [Home] = "(1.00, 2.00, 3.00)" };
            string favs = PinFollowRules.FavouritesResync("(1.00, 2.00, 3.00)", new Dictionary<VehicleId, string>(), book, out int moved);
            Assert.Equal("(1.00, 2.00, 3.00)", favs);
            Assert.Equal(0, moved);
            Assert.True(book.ContainsKey(Home));
        }

        [Fact]
        public void A_portal_taken_off_the_favourites_is_forgotten_and_its_moves_change_nothing()
        {
            var book = new Dictionary<VehicleId, string> { [Home] = "(1.00, 2.00, 3.00)" };
            string favs = PinFollowRules.FavouritesResync("", new Dictionary<VehicleId, string> { [Home] = "(8.00, 2.00, 3.00)" }, book, out int moved);
            Assert.Equal("", favs);
            Assert.Equal(0, moved);
            Assert.Empty(book);
        }

        [Fact]
        public void Of_two_favourites_only_the_moved_one_changes()
        {
            var book = new Dictionary<VehicleId, string>();
            string favs = "(1.00, 2.00, 3.00)|(5.00, 6.00, 7.00)";
            var now = new Dictionary<VehicleId, string> { [Home] = "(1.00, 2.00, 3.00)", [Mine] = "(5.00, 6.00, 7.00)" };
            favs = PinFollowRules.FavouritesResync(favs, now, book, out _);
            now[Mine] = "(9.00, 6.00, 7.00)";
            favs = PinFollowRules.FavouritesResync(favs, now, book, out int moved);
            Assert.Equal("(1.00, 2.00, 3.00)|(9.00, 6.00, 7.00)", favs);
            Assert.Equal(1, moved);
        }

        [Fact]
        public void A_portal_that_is_no_favourite_is_not_remembered()
        {
            var book = new Dictionary<VehicleId, string>();
            PinFollowRules.FavouritesResync("(1.00, 2.00, 3.00)", new Dictionary<VehicleId, string> { [Mine] = "(5.00, 6.00, 7.00)" }, book, out _);
            Assert.Empty(book);
        }

        [Fact]
        public void The_remembered_portals_survive_saving_in_the_character()
        {
            var book = new Dictionary<VehicleId, string> { [Home] = "(1.00, -2.50, 3.00)", [Mine] = "(5.00, 6.00, 7.00)" };
            var back = PinFollowRules.ParseFavouriteBook(PinFollowRules.FormatFavouriteBook(book));
            Assert.Equal(book, back);
            Assert.Empty(PinFollowRules.ParseFavouriteBook(null));
            Assert.Empty(PinFollowRules.ParseFavouriteBook("garbage|1:x@y"));
            // No place, or no player before the id: nothing to follow.
            Assert.Empty(PinFollowRules.ParseFavouriteBook("1:2@|:3@(1.00, 2.00, 3.00)"));
        }

        [Fact]
        public void The_patches_tab_says_what_TargetPortal_following_did()
        {
            Assert.Equal("off: this game's switch (Patches.TargetPortalPinsFollow)", PinFollowRules.TargetPortalLine(false, true, null, 0, 0));
            Assert.Equal("not needed: TargetPortal is not installed", PinFollowRules.TargetPortalLine(true, false, null, 0, 0));
            Assert.Equal("stepped aside: x", PinFollowRules.TargetPortalLine(true, true, "x", 0, 0));
            Assert.Equal("working · 4 pins renamed · 1 favourites moved with their portal", PinFollowRules.TargetPortalLine(true, true, null, 4, 1));
        }

        // ---- The server's whole portals ----

        [Fact]
        public void The_server_sends_a_changed_portal_whole_but_not_a_moving_cart_or_ship()
        {
            var portal = new VehicleId(1, 1);
            var cart = new VehicleId(1, 2);
            var changed = new List<KeyValuePair<VehicleId, VehicleState>>
            {
                new KeyValuePair<VehicleId, VehicleState>(portal, new VehicleState(100, 0, 0, 0, 7, true, "Home")),
                new KeyValuePair<VehicleId, VehicleState>(cart, new VehicleState(200, 5, 0, 5, 7, false, "")),
            };
            var send = new List<VehicleId>();
            VehiclePinRules.PortalsToSend(changed, prefab => prefab == 100, send);
            Assert.Equal(new[] { portal }, send);
        }

        [Fact]
        public void Nothing_changed_means_nothing_sent()
        {
            var send = new List<VehicleId>();
            VehiclePinRules.PortalsToSend(new List<KeyValuePair<VehicleId, VehicleState>>(), prefab => true, send);
            Assert.Empty(send);
        }
    }
}
