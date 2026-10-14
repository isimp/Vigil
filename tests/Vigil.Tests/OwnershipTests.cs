using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Every world object is run by one game: a player's or the server's. The ownership view says,
    /// for the objects around you, whose game runs how many creatures, dropped items, ships and
    /// carts, building pieces and other objects, and colours each game the same way while it shows,
    /// so a player whose game carries a whole base's creatures stands out.
    /// </summary>
    public sealed class OwnershipTests
    {
        private const long Me = 11, Server = 99, Friend = 22, Stranger = 33;

        private static List<OwnershipCount.Seen> World()
        {
            var seen = new List<OwnershipCount.Seen>();
            for (int i = 0; i < 43; i++) seen.Add(new OwnershipCount.Seen(Friend, OwnedKind.Creature));
            for (int i = 0; i < 5; i++) seen.Add(new OwnershipCount.Seen(Me, OwnedKind.Creature));
            seen.Add(new OwnershipCount.Seen(Me, OwnedKind.Item));
            seen.Add(new OwnershipCount.Seen(Server, OwnedKind.Piece));
            seen.Add(new OwnershipCount.Seen(Stranger, OwnedKind.Vehicle));
            seen.Add(new OwnershipCount.Seen(0, OwnedKind.Other));
            return seen;
        }

        [Fact]
        public void Each_game_is_counted_by_kind_with_the_busiest_first()
        {
            var rows = new OwnershipCount().Count(World(), new Dictionary<long, string> { { Friend, "D'Arcy" } }, Me, Server);
            Assert.Equal("D'Arcy", rows[0].Name);
            Assert.Equal(43, rows[0].Creatures);
            Assert.Equal(43, rows[0].Total);
            var me = rows.Single(r => r.Owner == Me);
            Assert.Equal(5, me.Creatures);
            Assert.Equal(1, me.Items);
            Assert.Equal(6, me.Total);
        }

        [Fact]
        public void Your_game_the_server_nobody_and_players_out_of_view_are_named()
        {
            var rows = new OwnershipCount().Count(World(), new Dictionary<long, string>(), Me, Server);
            Assert.Equal("You", rows.Single(r => r.Owner == Me).Name);
            Assert.Equal("The server", rows.Single(r => r.Owner == Server).Name);
            Assert.Equal("Nobody", rows.Single(r => r.Owner == 0).Name);
            Assert.StartsWith("A player out of view", rows.Single(r => r.Owner == Stranger).Name);
        }

        [Fact]
        public void A_game_keeps_its_colour_while_the_view_shows_and_no_two_games_share_one()
        {
            var count = new OwnershipCount();
            var names = new Dictionary<long, string> { { Friend, "D'Arcy" } };
            var first = count.Count(World(), names, Me, Server);
            var fewer = World().Where(s => s.Owner != Stranger).ToList();
            var second = count.Count(fewer, names, Me, Server);
            Assert.Equal(first.Single(r => r.Owner == Friend).Colour, second.Single(r => r.Owner == Friend).Colour);
            Assert.Equal(first.Count, first.Select(r => r.Colour).Distinct().Count());
        }

        [Fact]
        public void Your_own_game_is_always_the_accent_colour()
        {
            var rows = new OwnershipCount().Count(World(), new Dictionary<long, string>(), Me, Server);
            Assert.Equal(OwnershipCount.YouColour, rows.Single(r => r.Owner == Me).Colour);
        }
    }
}
