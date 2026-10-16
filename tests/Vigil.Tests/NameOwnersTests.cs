using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A name a mod puts together while the game runs is in no file, so it belongs to the mod that
    /// hashed it first, noted when the name was learned in a world: messages and object values with
    /// such names get their mod instead of "unknown". A name the files tie to one mod keeps that mod.
    /// The look at who hashed a name is capped per minute, so a mod making new names all the time
    /// costs little; past the cap a name is learned without an owner.
    /// </summary>
    public sealed class NameOwnersTests
    {
        private static int H(string s) => ValueNames.Hash(s);

        [Fact]
        public void A_name_hashed_while_playing_belongs_to_the_mod_that_hashed_it()
        {
            var book = new MessageNames();
            book.Learned(H("spawnthat_template_7"), "spawnthat_template_7", "Spawn That!");
            Assert.Equal("Spawn That!", book.ModOf(H("spawnthat_template_7")));
            Assert.Equal("Spawn That!", book.LearnedOwner(H("spawnthat_template_7")));
            Assert.True(book.Knows(H("spawnthat_template_7")));
            Assert.Equal("spawnthat_template_7", book.LearnedText(H("spawnthat_template_7")));
            Assert.Null(book.LearnedText(H("other")));
            Assert.StartsWith("0 of 0 kinds", book.CountLine());
        }

        [Fact]
        public void A_name_the_game_hashed_belongs_to_no_mod()
        {
            var book = new MessageNames();
            book.Learned(H("health"), "health", ValueNames.Game);
            Assert.Null(book.ModOf(H("health")));
            Assert.Equal(ValueNames.Game, book.LearnedOwner(H("health")));
        }

        [Fact]
        public void A_name_the_files_tie_to_one_mod_keeps_that_mod()
        {
            var book = new MessageNames();
            book.Files(ValueNames.Build(new KeyValuePair<string, int>[0], new[] { new KeyValuePair<string, IEnumerable<string>>("Seasonality", new[] { "seasonal_coat" }) }));
            book.Learned(H("seasonal_coat"), "seasonal_coat", "Jotunn");
            Assert.Equal("Seasonality", book.ModOf(H("seasonal_coat")));
        }

        [Fact]
        public void The_book_counts_the_names_whose_owner_it_noted_a_mods_and_the_games()
        {
            var book = new MessageNames();
            book.Learned(1, "a", "Spawn That!");
            book.Learned(2, "b", "Seasonality");
            book.Learned(3, "c", ValueNames.Game);
            book.Learned(4, "d");
            Assert.Equal((2, 1), book.OwnerCounts());
        }

        [Fact]
        public void A_name_learned_without_an_owner_has_none()
        {
            var book = new MessageNames();
            book.Learned(H("x"), "x");
            Assert.Null(book.LearnedOwner(H("x")));
            Assert.Null(book.ModOf(H("x")));
            Assert.False(book.Knows(H("y")));
        }

        [Fact]
        public void The_looks_at_who_hashed_a_name_are_capped_per_minute()
        {
            var looks = new OwnerLooks(most: 3, windowSeconds: 60);
            Assert.True(looks.Allow(0));
            Assert.True(looks.Allow(10));
            Assert.True(looks.Allow(20));
            Assert.False(looks.Allow(30));
            Assert.False(looks.Allow(59.9));
            Assert.True(looks.Allow(60));
            Assert.Equal(2, looks.Refused);
        }
    }
}
