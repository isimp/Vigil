using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The World tab's "Who runs what" card shows one line per game, the game that runs the most
    /// moving things (creatures, ships and carts) first, since those are what others see late when
    /// that game's connection is slow. A line says what the game runs in words, opens to its most
    /// common types, and warns when a slow connection runs moving things. Building pieces count in
    /// the words but get marks only when asked for, and only one game's objects are marked at a time.
    /// </summary>
    public sealed class OwnershipLinesTests
    {
        private const long Me = 11, Server = 99, Friend = 22, Builder = 44;

        private static List<OwnershipCount.Seen> Base()
        {
            var seen = new List<OwnershipCount.Seen>();
            for (int i = 0; i < 30; i++) seen.Add(new OwnershipCount.Seen(Friend, OwnedKind.Creature, "Chicken"));
            for (int i = 0; i < 8; i++) seen.Add(new OwnershipCount.Seen(Friend, OwnedKind.Creature, "Boar"));
            for (int i = 0; i < 4; i++) seen.Add(new OwnershipCount.Seen(Friend, OwnedKind.Creature, "Wolf"));
            for (int i = 0; i < 3; i++) seen.Add(new OwnershipCount.Seen(Friend, OwnedKind.Vehicle, "Cart"));
            for (int i = 0; i < 210; i++) seen.Add(new OwnershipCount.Seen(Friend, OwnedKind.Piece, "Wood wall"));
            for (int i = 0; i < 1840; i++) seen.Add(new OwnershipCount.Seen(Builder, OwnedKind.Piece, "Stone floor"));
            seen.Add(new OwnershipCount.Seen(Builder, OwnedKind.Creature, "Lox"));
            for (int i = 0; i < 12; i++) seen.Add(new OwnershipCount.Seen(Me, OwnedKind.Creature, "Neck"));
            seen.Add(new OwnershipCount.Seen(Me, OwnedKind.Item, "Wood"));
            return seen;
        }

        private static List<OwnershipCount.Row> Lines() =>
            new OwnershipCount().Count(Base(), new Dictionary<long, string> { { Friend, "D'Arcy" }, { Builder, "Mason" } }, Me, Server);

        [Fact]
        public void The_game_running_the_most_moving_things_comes_first_not_the_one_with_the_most_pieces()
        {
            var lines = Lines();
            Assert.Equal(new[] { "D'Arcy", "You", "Mason" }, lines.Select(l => l.Name).ToArray());
            Assert.Equal(45, lines[0].Moving);
            Assert.Equal(1, lines[2].Moving);
        }

        [Fact]
        public void A_line_says_in_words_what_its_game_runs()
        {
            var lines = Lines();
            Assert.Equal("42 creatures · 3 vehicles · 210 pieces", OwnershipCount.Phrase(lines[0]));
            Assert.Equal("12 creatures · 1 item", OwnershipCount.Phrase(lines[1]));
            Assert.Equal("1 creature · 1840 pieces", OwnershipCount.Phrase(lines[2]));
        }

        [Fact]
        public void Other_objects_are_named_as_such_and_an_empty_game_runs_nothing()
        {
            var row = new OwnershipCount().Count(new[] { new OwnershipCount.Seen(Friend, OwnedKind.Other, "Beehive") }, new Dictionary<long, string>(), Me, Server)[0];
            Assert.Equal("1 other object", OwnershipCount.Phrase(row));
            Assert.Equal("nothing", OwnershipCount.Phrase(new OwnershipCount.Row()));
        }

        [Fact]
        public void An_opened_line_lists_its_most_common_types_with_their_kind()
        {
            var top = Lines()[0].TopTypes(4);
            Assert.Equal(new[] { "Wood wall", "Chicken", "Boar", "Wolf" }, top.Select(t => t.Type).ToArray());
            Assert.Equal(210, top[0].Count);
            Assert.Equal(OwnedKind.Piece, top[0].Kind);
            Assert.Equal(OwnedKind.Creature, top[1].Kind);
        }

        [Fact]
        public void Objects_of_an_unknown_type_are_listed_together()
        {
            var seen = new[] { new OwnershipCount.Seen(Friend, OwnedKind.Other, null), new OwnershipCount.Seen(Friend, OwnedKind.Other, "") };
            var top = new OwnershipCount().Count(seen, new Dictionary<long, string>(), Me, Server)[0].TopTypes(5);
            Assert.Single(top);
            Assert.Equal("unknown", top[0].Type);
            Assert.Equal(2, top[0].Count);
        }

        [Fact]
        public void Creatures_and_vehicles_are_marked_and_pieces_only_when_asked_for()
        {
            var kinds = MarkKinds.Creatures | MarkKinds.Vehicles;
            Assert.True(OwnershipCount.Marks(OwnedKind.Creature, kinds));
            Assert.True(OwnershipCount.Marks(OwnedKind.Vehicle, kinds));
            Assert.False(OwnershipCount.Marks(OwnedKind.Piece, kinds));
            Assert.False(OwnershipCount.Marks(OwnedKind.Item, kinds));
            Assert.False(OwnershipCount.Marks(OwnedKind.Other, kinds));
            Assert.True(OwnershipCount.Marks(OwnedKind.Piece, kinds | MarkKinds.Pieces));
            Assert.False(OwnershipCount.Marks(OwnedKind.Creature, MarkKinds.None));
        }

        [Fact]
        public void A_slow_connection_running_moving_things_is_worth_a_look()
        {
            var friend = Lines()[0];
            var v = OwnershipVerdict.Of(friend, pingMs: 240, pingLimitMs: 300);
            Assert.Equal(ResultTone.Caution, v.Tone);
            Assert.Equal("D'Arcy's game runs 45 moving things here on a slow connection (ping 240 ms), so everyone else sees them late.", v.Text);
            Assert.Equal(ResultTone.Bad, OwnershipVerdict.Of(friend, 300, 300).Tone);
        }

        [Fact]
        public void Your_own_slow_connection_is_named_as_yours()
        {
            var you = Lines()[1];
            Assert.StartsWith("Your game runs 12 moving things here on a slow connection (ping 160 ms)", OwnershipVerdict.Of(you, 160, 300).Text);
        }

        [Fact]
        public void No_verdict_without_a_slow_connection_moving_things_or_a_known_ping()
        {
            var lines = Lines();
            Assert.Null(OwnershipVerdict.Of(lines[0], 149, 300));
            Assert.Null(OwnershipVerdict.Of(lines[0], double.NaN, 300));
            var piecesOnly = new OwnershipCount().Count(new[] { new OwnershipCount.Seen(Builder, OwnedKind.Piece, "Stone floor") }, new Dictionary<long, string>(), Me, Server)[0];
            Assert.Null(OwnershipVerdict.Of(piecesOnly, 900, 300));
        }

        [Fact]
        public void The_server_and_nobody_get_no_verdict()
        {
            var seen = new[] { new OwnershipCount.Seen(Server, OwnedKind.Creature, "Troll"), new OwnershipCount.Seen(0, OwnedKind.Creature, "Deer") };
            foreach (var row in new OwnershipCount().Count(seen, new Dictionary<long, string>(), Me, Server))
            {
                Assert.Null(OwnershipVerdict.Of(row, 900, 300));
            }
        }

        [Fact]
        public void The_card_badge_names_a_worrying_game_first_else_the_game_running_most_moving_things()
        {
            var lines = Lines();
            var worried = OwnershipVerdict.Badge(lines, row => row.Owner == Friend ? OwnershipVerdict.Of(row, 240, 300) : null);
            Assert.Equal("D'Arcy: 45 moving", worried.Text);
            Assert.Equal(ResultTone.Caution, worried.Tone);

            var calm = OwnershipVerdict.Badge(lines, row => null);
            Assert.Equal("most: D'Arcy", calm.Text);
            Assert.Equal(ResultTone.Quiet, calm.Tone);
        }

        [Fact]
        public void The_card_has_no_badge_when_nothing_moves_or_only_nobody_runs_moving_things()
        {
            var pieces = new OwnershipCount().Count(new[] { new OwnershipCount.Seen(Builder, OwnedKind.Piece, "Stone floor") }, new Dictionary<long, string>(), Me, Server);
            Assert.Null(OwnershipVerdict.Badge(pieces, row => null));
            var nobody = new OwnershipCount().Count(new[] { new OwnershipCount.Seen(0, OwnedKind.Creature, "Deer") }, new Dictionary<long, string>(), Me, Server);
            Assert.Null(OwnershipVerdict.Badge(nobody, row => null));
        }
    }
}
