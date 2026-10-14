using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// "Who runs what" can be narrowed to one kind of object and to a text that a game's player name
    /// or an object's type holds; the lines, their counts and order, and what Mark marks all follow it.
    /// </summary>
    public sealed class OwnershipFilterTests
    {
        private const long Me = 1, Bjorn = 2, Server = 9;
        private static readonly Dictionary<long, string> Players = new Dictionary<long, string> { [Bjorn] = "Bjorn" };

        private static IEnumerable<OwnershipCount.Seen> Around() => new[]
        {
            new OwnershipCount.Seen(Bjorn, OwnedKind.Creature, "Boar"), new OwnershipCount.Seen(Bjorn, OwnedKind.Creature, "Boar"),
            new OwnershipCount.Seen(Bjorn, OwnedKind.Creature, "Deer"), new OwnershipCount.Seen(Bjorn, OwnedKind.Piece, "Wood wall"),
            new OwnershipCount.Seen(Me, OwnedKind.Creature, "Boar"), new OwnershipCount.Seen(Me, OwnedKind.Vehicle, "Cart"),
            new OwnershipCount.Seen(Me, OwnedKind.Piece, "Wood wall"), new OwnershipCount.Seen(Me, OwnedKind.Piece, "Wood wall"),
        };

        private static List<OwnershipCount.Row> Count(OwnershipFilter filter) => new OwnershipCount().Count(Around(), Players, Me, Server, filter);

        [Fact]
        public void Without_a_filter_every_object_counts()
        {
            Assert.True(new OwnershipFilter().IsEmpty);
            Assert.Equal(8, Count(new OwnershipFilter()).Sum(r => r.Total));
        }

        [Fact]
        public void A_kind_counts_only_objects_of_that_kind_and_drops_games_without_any()
        {
            var rows = Count(new OwnershipFilter { Kind = OwnedKind.Vehicle });
            Assert.Equal("You", rows.Single().Name);
            Assert.Equal(1, rows.Single().Total);
        }

        [Fact]
        public void A_text_counts_the_objects_whose_type_holds_it_whatever_its_case()
        {
            var rows = Count(new OwnershipFilter { Find = "BOAR" });
            Assert.Equal(new[] { "Bjorn", "You" }, rows.Select(r => r.Name).ToArray());
            Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.Total).ToArray());
            Assert.Equal("Boar", rows[0].TopTypes(5).Single().Type);
        }

        [Fact]
        public void A_text_that_names_a_player_keeps_all_of_that_games_objects()
        {
            var rows = Count(new OwnershipFilter { Find = "jor" });
            Assert.Equal("Bjorn", rows.Single().Name);
            Assert.Equal(4, rows.Single().Total);
        }

        [Fact]
        public void A_kind_and_a_text_together_both_have_to_hold()
        {
            var rows = Count(new OwnershipFilter { Kind = OwnedKind.Piece, Find = "bjorn" });
            Assert.Equal(1, rows.Single().Pieces);
            Assert.Equal(1, rows.Single().Total);
        }

        [Fact]
        public void Mark_follows_the_chosen_kind_and_else_the_kinds_ticked()
        {
            var vehicles = new OwnershipFilter { Kind = OwnedKind.Vehicle };
            Assert.True(vehicles.MarksIt(OwnedKind.Vehicle, MarkKinds.Creatures));
            Assert.False(vehicles.MarksIt(OwnedKind.Creature, MarkKinds.Creatures));
            var none = new OwnershipFilter();
            Assert.True(none.MarksIt(OwnedKind.Creature, MarkKinds.Creatures));
            Assert.False(none.MarksIt(OwnedKind.Piece, MarkKinds.Creatures));
        }

        [Fact]
        public void The_heading_says_what_the_list_holds()
        {
            Assert.Equal("Games running things within 64 m, busiest first", new OwnershipFilter().Heading(64));
            Assert.Equal("Games running creatures within 64 m, busiest first", new OwnershipFilter { Kind = OwnedKind.Creature }.Heading(64));
            Assert.Equal("Games running things that match \"boar\" within 30 m, busiest first", new OwnershipFilter { Find = " boar " }.Heading(30));
            Assert.Equal("Games running pieces that match \"wall\" within 64 m, busiest first", new OwnershipFilter { Kind = OwnedKind.Piece, Find = "wall" }.Heading(64));
        }
    }
}
