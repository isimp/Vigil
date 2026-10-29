using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants every cart, ship and portal on the map where it really is: old ones anywhere
    /// in the world, none left behind once destroyed, and moved ones at their new place. A player's
    /// game hears only of objects near it and keeps a far one at the spot it last saw; the server
    /// holds them all. With the switch on, the server tells each player its whole list on joining and
    /// every few seconds what was added, moved, renamed or removed, and each game draws a far object
    /// at the server's place and a near one at its own.
    /// </summary>
    public sealed class VehiclePinsTests
    {
        private static readonly VehicleId Cart = new VehicleId(7, 100), Ship = new VehicleId(7, 101), Portal = new VehicleId(9, 5);

        private static VehicleState At(float x, float z, string label = "Cart", bool active = false, long creator = 7) =>
            new VehicleState(1234, x, 30f, z, creator, active, label);

        private static (List<KeyValuePair<VehicleId, VehicleState>> changed, List<VehicleId> gone) Tick(VehicleBook book, params (VehicleId id, VehicleState s)[] seen)
        {
            book.Begin();
            foreach (var (id, s) in seen) book.See(id, s);
            var changed = new List<KeyValuePair<VehicleId, VehicleState>>();
            var gone = new List<VehicleId>();
            book.End(changed, gone);
            return (changed, gone);
        }

        [Fact]
        public void Every_object_the_server_has_is_sent_the_first_time_even_ones_nobody_was_near()
        {
            var book = new VehicleBook();
            var (changed, gone) = Tick(book, (Cart, At(10, 10)), (Ship, At(-3000, 4000)), (Portal, At(500, 500, "home", true)));
            Assert.Equal(new[] { Cart, Ship, Portal }.OrderBy(i => i.Id), changed.Select(c => c.Key).OrderBy(i => i.Id));
            Assert.Empty(gone);
        }

        [Fact]
        public void An_object_that_stays_put_is_not_sent_again()
        {
            var book = new VehicleBook();
            Tick(book, (Cart, At(10, 10)));
            var (changed, gone) = Tick(book, (Cart, At(10.4f, 10)));
            Assert.Empty(changed);
            Assert.Empty(gone);
        }

        [Fact]
        public void A_cart_moved_far_from_every_player_is_sent_at_its_new_place()
        {
            var book = new VehicleBook();
            Tick(book, (Cart, At(10, 10)));
            var (changed, _) = Tick(book, (Cart, At(10, 12)));
            var c = Assert.Single(changed);
            Assert.Equal(Cart, c.Key);
            Assert.Equal(12f, c.Value.Z);
        }

        [Fact]
        public void Small_moves_add_up_until_they_are_worth_sending()
        {
            var book = new VehicleBook();
            Tick(book, (Cart, At(0, 0)));
            Assert.Empty(Tick(book, (Cart, At(0.6f, 0))).changed);
            // Measured from where the players were last told it is, not from the last look.
            Assert.Single(Tick(book, (Cart, At(1.2f, 0))).changed);
        }

        [Fact]
        public void A_renamed_portal_or_one_newly_linked_is_sent_again()
        {
            var book = new VehicleBook();
            Tick(book, (Portal, At(5, 5, "home", false)));
            Assert.Single(Tick(book, (Portal, At(5, 5, "mine", false))).changed);
            Assert.Single(Tick(book, (Portal, At(5, 5, "mine", true))).changed);
        }

        [Fact]
        public void A_destroyed_object_is_sent_as_gone_once()
        {
            var book = new VehicleBook();
            Tick(book, (Cart, At(1, 1)), (Ship, At(2, 2)));
            var (changed, gone) = Tick(book, (Ship, At(2, 2)));
            Assert.Empty(changed);
            Assert.Equal(new[] { Cart }, gone);
            Assert.Empty(Tick(book, (Ship, At(2, 2))).gone);
        }

        [Fact]
        public void A_joining_player_gets_the_whole_list_as_last_sent()
        {
            var book = new VehicleBook();
            Tick(book, (Cart, At(1, 1)), (Ship, At(2, 2)));
            var all = new List<KeyValuePair<VehicleId, VehicleState>>();
            book.All(all);
            Assert.Equal(2, all.Count);
            Assert.Equal(2, book.Count);
        }

        [Fact]
        public void A_far_object_is_drawn_at_the_server_s_place_and_a_near_one_at_the_game_s_own()
        {
            // Near: the game has the object loaded and moves it itself.
            Assert.False(VehiclePinRules.UseServer(haveLocal: true, localLoaded: true, haveServer: true));
            // Far: the game's copy stopped at the spot it last saw.
            Assert.True(VehiclePinRules.UseServer(haveLocal: true, localLoaded: false, haveServer: true));
            // Never seen by this game: only the server knows it.
            Assert.True(VehiclePinRules.UseServer(haveLocal: false, localLoaded: false, haveServer: true));
            // No word from the server: HUDCompass's own rules.
            Assert.False(VehiclePinRules.UseServer(haveLocal: true, localLoaded: false, haveServer: false));
        }

        [Fact]
        public void On_joining_pins_the_server_does_not_know_are_dropped()
        {
            var known = new[] { Cart, Ship, Portal };
            var full = new HashSet<VehicleId> { Ship };
            var drop = new List<VehicleId>();
            VehiclePinRules.NotOnServer(known, full, drop);
            Assert.Equal(new[] { Cart, Portal }, drop);
        }

        [Fact]
        public void Ids_are_equal_by_player_and_number()
        {
            Assert.Equal(new VehicleId(7, 100), Cart);
            Assert.NotEqual(new VehicleId(8, 100), Cart);
            Assert.NotEqual(new VehicleId(7, 99), Cart);
        }

        [Fact]
        public void The_state_lines_say_what_happens_on_the_server_and_on_this_game()
        {
            Assert.Equal("off: the server's switch (Patches.VehiclePinsFromServer)", VehiclePinRules.ServerLine(false, true, null, 0, 0));
            Assert.Equal("not needed: HUDCompass is not installed", VehiclePinRules.ServerLine(true, false, null, 0, 0));
            Assert.Equal("stepped aside: x", VehiclePinRules.ServerLine(true, true, "x", 0, 0));
            Assert.Equal("working · 41 carts, ships and portals · 3 changes sent in the last refresh", VehiclePinRules.ServerLine(true, true, null, 41, 3));

            Assert.Equal("off: the server's switch (Patches.VehiclePinsFromServer)", VehiclePinRules.GameLine(false, true, null, false, 0, 0));
            Assert.Equal("not needed: HUDCompass is not installed", VehiclePinRules.GameLine(true, false, null, false, 0, 0));
            Assert.Equal("waiting: no list from the server yet (it needs Vigil 0.43.19 or later with the switch on)", VehiclePinRules.GameLine(true, true, null, false, 0, 0));
            Assert.Equal("working · 41 from the server's list · 12 far ones drawn at the server's place", VehiclePinRules.GameLine(true, true, null, true, 41, 12));
        }
    }
}
