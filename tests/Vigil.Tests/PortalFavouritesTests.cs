using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "AdjustablePortals: favourite portals": a player marks portals as favourites in
    /// AdjustablePortals' destination list and on its map of portals, and finds them first in the
    /// list under their own heading and beside the map in a list of their own. Favourites are kept
    /// in the character's data by each portal's world id, so a renamed or moved portal stays one.
    /// </summary>
    public sealed class PortalFavouritesTests
    {
        private static readonly VehicleId Home = new VehicleId(111L, 1u), Mine = new VehicleId(111L, 2u), Camp = new VehicleId(-222L, 3u);

        [Fact]
        public void A_portal_marked_as_a_favourite_is_one_and_marked_again_is_not()
        {
            var favourites = new HashSet<VehicleId>();
            Assert.True(PortalFavourites.Toggle(favourites, Home));
            Assert.Contains(Home, favourites);
            Assert.False(PortalFavourites.Toggle(favourites, Home));
            Assert.Empty(favourites);
        }

        [Fact]
        public void Favourites_are_kept_in_the_character_data_and_read_back()
        {
            var favourites = new HashSet<VehicleId> { Camp, Home };
            string text = PortalFavourites.FormatBook(favourites);
            Assert.Equal("-222:3|111:1", text);
            Assert.Equal(favourites, PortalFavourites.ParseBook(text));
        }

        [Fact]
        public void An_unreadable_entry_in_the_kept_favourites_is_left_out()
        {
            Assert.Equal(new HashSet<VehicleId> { Home }, PortalFavourites.ParseBook("111:1|nonsense|:5|7:|x:1|8:-1"));
            Assert.Empty(PortalFavourites.ParseBook(null));
            Assert.Empty(PortalFavourites.ParseBook(""));
        }

        [Fact]
        public void TargetPortal_favourites_carry_over_by_world_id_and_by_place()
        {
            // Vigil's book of TargetPortal favourites names Home by id; TargetPortal's own list names Camp's place.
            string vigilBook = "111:1@(10.00, 30.00, 5.00)";
            string targetPortalOwn = "(400.50, 31.00, -80.25)|(9.00, 9.00, 9.00)";
            var destinations = new List<(VehicleId, string)>
            {
                (Home, "(10.00, 30.00, 5.00)"),
                (Mine, "(55.00, 30.00, 5.00)"),
                (Camp, "(400.50, 31.00, -80.25)"),
            };
            Assert.Equal(new HashSet<VehicleId> { Home, Camp }, PortalFavourites.Seed(vigilBook, targetPortalOwn, destinations));
        }

        [Fact]
        public void Without_TargetPortal_favourites_nothing_carries_over()
        {
            var destinations = new List<(VehicleId, string)> { (Home, "(1.00, 2.00, 3.00)") };
            Assert.Empty(PortalFavourites.Seed(null, null, destinations));
            Assert.Empty(PortalFavourites.Seed("", "", destinations));
        }

        [Fact]
        public void Favourites_come_first_in_the_list_and_both_groups_keep_its_own_order()
        {
            var list = new[] { "Ashlands", "Copper mine", "Home", "Mistlands camp", "Unnamed portal" };
            var favourite = new HashSet<string> { "Mistlands camp", "Home" };
            Assert.Equal(new[] { "Home", "Mistlands camp", "Ashlands", "Copper mine", "Unnamed portal" },
                PortalFavourites.FavouritesFirst(list, favourite.Contains));
        }

        [Fact]
        public void A_portal_marked_later_takes_its_place_in_order_not_the_end()
        {
            // The list is ordered again from AdjustablePortals' own order each time, never from the last partition.
            var list = new[] { "A", "B", "C", "D" };
            var first = PortalFavourites.FavouritesFirst(list, n => n == "C");
            var again = PortalFavourites.FavouritesFirst(list, n => n == "C" || n == "A");
            Assert.Equal(new[] { "C", "A", "B", "D" }, first);
            Assert.Equal(new[] { "A", "C", "B", "D" }, again);
        }

        [Fact]
        public void The_list_has_headings_only_while_a_favourite_shows()
        {
            Assert.Equal((-1, -1), PortalFavourites.HeadingsAt(new[] { false, false, false }));
            Assert.Equal((-1, -1), PortalFavourites.HeadingsAt(new bool[0]));
            Assert.Equal((0, 2), PortalFavourites.HeadingsAt(new[] { true, true, false }));
            // A filter that leaves only favourites shows only their heading.
            Assert.Equal((0, -1), PortalFavourites.HeadingsAt(new[] { true }));
        }

        [Fact]
        public void A_right_click_on_the_map_takes_the_nearest_portal_within_reach()
        {
            var points = new List<(float, float)> { (0f, 0f), (10f, 0f), (12f, 1f) };
            Assert.Equal(1, PortalFavourites.NearestWithin(points, 11f, 0f, 5f));
            Assert.Equal(2, PortalFavourites.NearestWithin(points, 12f, 2f, 5f));
            Assert.Equal(-1, PortalFavourites.NearestWithin(points, 50f, 50f, 5f));
            Assert.Equal(-1, PortalFavourites.NearestWithin(new List<(float, float)>(), 0f, 0f, 5f));
        }

        [Fact]
        public void The_list_beside_the_map_holds_the_favourites_by_name_with_the_rest_counted()
        {
            var names = new[] { "Zebra", "Home", "Ashlands", "Camp", "Bog" };
            var favs = new[] { true, true, false, true, true };
            var rows = PortalFavourites.SideRows(names, favs, 3, out int more);
            Assert.Equal(new[] { "Bog", "Camp", "Home" }, rows.Select(i => names[i]));
            Assert.Equal(1, more);
            Assert.Empty(PortalFavourites.SideRows(names, new bool[5], 3, out int none));
            Assert.Equal(0, none);
        }

        [Fact]
        public void A_right_click_reads_the_map_pickers_pins_as_they_are_kept()
        {
            // AdjustablePortals keeps its pins in a Dictionary<PinData, Destination>, read here without
            // its types; listing such a dictionary as DictionaryEntry items failed in game.
            System.Collections.IDictionary pins = new Dictionary<string, int> { ["Home"] = 1, ["Mine"] = 2 };
            var entries = PortalFavourites.Entries(pins);
            Assert.Equal(new object[] { "Home", "Mine" }, entries.Select(e => e.Key));
            Assert.Equal(new object[] { 1, 2 }, entries.Select(e => e.Value));
        }

        [Fact]
        public void While_the_portal_map_is_open_only_its_portals_show()
        {
            // HUDCompass's portal markers and the game's own pins would stand on and beside the portals to pick.
            Assert.True(PortalFavourites.PinShown(pickerOpen: true, offered: true));
            Assert.False(PortalFavourites.PinShown(pickerOpen: true, offered: false));
        }

        [Fact]
        public void Once_the_portal_map_closes_every_pin_shows_again()
        {
            Assert.True(PortalFavourites.PinShown(pickerOpen: false, offered: false));
        }

        [Fact]
        public void The_panel_says_what_the_portals_only_map_does_on_this_game()
        {
            Assert.StartsWith("off", PortalFavourites.OnlyPortalsLine(false, true, null, 0));
            Assert.StartsWith("not needed", PortalFavourites.OnlyPortalsLine(true, false, null, 0));
            Assert.Equal("stepped aside: its picker code changed", PortalFavourites.OnlyPortalsLine(true, true, "its picker code changed", 0));
            Assert.Equal("working · 4 portal maps shown with only their portals", PortalFavourites.OnlyPortalsLine(true, true, null, 4));
        }

        [Fact]
        public void The_panel_says_what_the_patch_does_on_this_game()
        {
            Assert.StartsWith("off", PortalFavourites.Line(false, true, null, 0, 0));
            Assert.StartsWith("not needed", PortalFavourites.Line(true, false, null, 0, 0));
            Assert.Equal("stepped aside: its picker code changed", PortalFavourites.Line(true, true, "its picker code changed", 0, 0));
            Assert.Equal("working · 3 favourites marked or unmarked · 2 trips from the list beside the map", PortalFavourites.Line(true, true, null, 3, 2));
        }
    }
}
