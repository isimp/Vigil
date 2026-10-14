using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The game keeps an object's values under numbers made from their names, so the inspector needs
    /// the names back. It finds them in the game's own list of names and in every text the game and
    /// the mods carry in their code, hashed the game's way; a mod's value then shows its name and
    /// the mod whose code holds that name, as a sealed chest shows Hoard's flag.
    /// </summary>
    public sealed class ValueNameTests
    {
        private static KeyValuePair<string, IEnumerable<string>> Texts(string source, params string[] texts) =>
            new KeyValuePair<string, IEnumerable<string>>(source, texts);

        private static KeyValuePair<string, int> Field(string name, int hash) => new KeyValuePair<string, int>(name, hash);

        [Fact]
        public void Names_hash_to_the_numbers_the_game_stores_them_under()
        {
            // Numbers the game's own hash gave for these names.
            Assert.Equal(-577049338, ValueNames.Hash("isimp.Hoard.sealed"));
            Assert.Equal(-883022132, ValueNames.Hash("health"));
            Assert.Equal(-938864442, ValueNames.Hash("items"));
            Assert.Equal(-813859285, ValueNames.Hash("spawn_id_u"));
            Assert.Equal(372029373, ValueNames.Hash("a"));
            Assert.Equal(371857150, ValueNames.Hash(""));
        }

        [Fact]
        public void A_sealed_chest_shows_Hoards_flag_by_name_and_the_mod_it_comes_from()
        {
            var names = ValueNames.Build(new KeyValuePair<string, int>[0], new[]
            {
                Texts(ValueNames.Game, "items", "Open"),
                Texts("Hoard", "Seal this chest", "isimp.Hoard.sealed"),
            });
            var flag = names.Of(ValueNames.Hash("isimp.Hoard.sealed"));
            Assert.Equal("isimp.Hoard.sealed", flag.Text);
            Assert.Equal("Hoard", flag.NamedIn);
        }

        [Fact]
        public void A_value_the_game_names_shows_its_name_and_any_other_its_number()
        {
            var names = ValueNames.Build(new[] { Field("s_health", 101), Field("s_level", 202) }, new KeyValuePair<string, IEnumerable<string>>[0]);
            Assert.Equal("health", names.Of(101).Text);
            Assert.Equal(ValueNames.Game, names.Of(101).NamedIn);
            Assert.Equal("level", names.Of(202).Text);
            var unknown = names.Of(-1840274);
            Assert.Equal("#-1840274", unknown.Text);
            Assert.Equal(ValueNames.NotFound, unknown.NamedIn);
            Assert.False(unknown.Found);
        }

        [Fact]
        public void When_two_names_share_a_number_the_first_stays()
        {
            var names = ValueNames.Build(new[] { Field("s_tamed", 7), Field("s_tamedAlias", 7) }, new KeyValuePair<string, IEnumerable<string>>[0]);
            Assert.Equal("tamed", names.Of(7).Text);
        }

        [Fact]
        public void The_games_name_for_a_value_wins_over_a_mod_that_reads_the_same_value()
        {
            int items = ValueNames.Hash("items");
            var names = ValueNames.Build(new[] { Field("s_items", items) }, new[] { Texts("Quick Stack", "items"), Texts("Rummage", "items") });
            Assert.Equal("items", names.Of(items).Text);
            Assert.Equal(ValueNames.Game, names.Of(items).NamedIn);

            var fromCode = ValueNames.Build(new KeyValuePair<string, int>[0], new[] { Texts(ValueNames.Game, "items"), Texts("Rummage", "items") });
            Assert.Equal(ValueNames.Game, fromCode.Of(items).NamedIn);
        }

        [Fact]
        public void A_name_several_mods_carry_lists_them_and_how_many_more()
        {
            var two = ValueNames.Build(new KeyValuePair<string, int>[0], new[] { Texts("Zeta", "shared.key"), Texts("Alpha", "shared.key") });
            Assert.Equal("Alpha, Zeta", two.Of(ValueNames.Hash("shared.key")).NamedIn);

            var four = ValueNames.Build(new KeyValuePair<string, int>[0], new[]
            {
                Texts("Delta", "shared.key"), Texts("Alpha", "shared.key"), Texts("Charlie", "shared.key"), Texts("Bravo", "shared.key"), Texts("Alpha", "shared.key"),
            });
            Assert.Equal("Alpha, Bravo +2", four.Of(ValueNames.Hash("shared.key")).NamedIn);
        }

        [Fact]
        public void An_object_link_stored_in_two_halves_shows_both_halves_by_name()
        {
            // The game keeps a link to another object as two numbers, under the name with _u and _i added.
            var names = ValueNames.Build(new KeyValuePair<string, int>[0], new[] { Texts(ValueNames.Game, "spawn_id") });
            Assert.Equal("spawn_id_u", names.Of(-813859285).Text);
            Assert.Equal("spawn_id_i", names.Of(ValueNames.Hash("spawn_id_i")).Text);
        }

        [Fact]
        public void A_long_message_in_a_mod_is_not_taken_for_a_name()
        {
            string message = new string('m', ValueNames.LongestName + 1);
            var names = ValueNames.Build(new KeyValuePair<string, int>[0], new[] { Texts("Chatty", message, "chatty.flag") });
            Assert.False(names.Of(ValueNames.Hash(message)).Found);
            Assert.True(names.Of(ValueNames.Hash("chatty.flag")).Found);
        }

        // ------------------------------------------------------------------ the texts in an assembly

        private const string Marker = "vigil.tests.value-name-marker";

        private const string LongText =
            "A text longer than one hundred and twenty-seven characters takes a two-byte length in the string heap, so this one is long enough to need it, really.";

        [Fact]
        public void The_texts_an_assemblys_code_carries_are_read_from_its_file()
        {
            Assert.True(LongText.Length > 127);
            string path = typeof(ValueNameTests).Assembly.Location;
            List<string> texts;
            using (var file = File.OpenRead(path)) texts = AssemblyTexts.Read(file);
            Assert.NotNull(texts);
            Assert.Contains(UseOf(Marker), texts);
            Assert.Contains(UseOf(LongText), texts);
        }

        // Hands the text on as code that loads it, so the compiler puts it in the string heap.
        private static string UseOf(string text) => text;

        [Fact]
        public void A_file_that_is_not_an_assembly_gives_no_texts_and_no_error()
        {
            Assert.Null(AssemblyTexts.Read(new MemoryStream(new byte[] { 1, 2, 3 })));
            Assert.Null(AssemblyTexts.Read(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("MZ this is only a text file that starts like one"))));
        }

        [Fact]
        public void An_assembly_cut_short_gives_no_texts_and_no_error()
        {
            byte[] whole = File.ReadAllBytes(typeof(ValueNameTests).Assembly.Location);
            Assert.Null(AssemblyTexts.Read(new MemoryStream(whole.Take(600).ToArray())));
        }
    }
}
