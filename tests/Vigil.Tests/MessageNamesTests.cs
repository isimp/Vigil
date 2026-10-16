using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Every message in the traffic tables carries its name, not a bare number: the name the game or a
    /// mod hashed while the game ran (the game names messages by the hash of their name), a mod's
    /// config sync message ("id ConfigSync"), or a name found in the game's or the mods' files; a
    /// message whose name was never seen stays "message N". A message no handler here explains (the
    /// server has none for a message between players) belongs to the mod whose code holds its name.
    /// </summary>
    public sealed class MessageNamesTests
    {
        private static int H(string s) => ValueNames.Hash(s);

        private static ValueNames Files(params (string Mod, string Text)[] texts)
        {
            var bySource = new Dictionary<string, List<string>>();
            foreach (var (mod, text) in texts)
            {
                if (!bySource.TryGetValue(mod, out var list)) bySource[mod] = list = new List<string>();
                list.Add(text);
            }
            var sources = new List<KeyValuePair<string, IEnumerable<string>>>();
            foreach (var kv in bySource) sources.Add(new KeyValuePair<string, IEnumerable<string>>(kv.Key, kv.Value));
            return ValueNames.Build(new KeyValuePair<string, int>[0], sources);
        }

        [Fact]
        public void A_message_hashed_while_the_game_runs_is_named_by_that_name()
        {
            var book = new MessageNames();
            book.Learned(H("PW_OnlinePlayerRoster"), "PW_OnlinePlayerRoster");
            Assert.True(book.LearnedWhileRunning(H("PW_OnlinePlayerRoster")));
            Assert.Equal("0 of 0 kinds of message seen named: 0 learned while the game ran, 0 from the files, 0 config sync", book.CountLine());
            Assert.Equal("PW_OnlinePlayerRoster", book.Label(H("PW_OnlinePlayerRoster")));
            Assert.False(book.LearnedWhileRunning(H("Vigil_Admin")));
        }

        [Fact]
        public void A_mods_config_sync_message_is_named_and_belongs_to_that_mod()
        {
            var book = new MessageNames();
            book.Sync(new[] { new KeyValuePair<string, string>("RustyMods.Seasonality", "Seasonality") });
            Assert.Equal("RustyMods.Seasonality ConfigSync", book.Label(846180935));
            Assert.Equal("Seasonality", book.ModOf(846180935));
        }

        [Fact]
        public void A_name_from_the_files_names_a_message_nothing_else_knows_and_its_one_mod_owns_it()
        {
            var book = new MessageNames();
            book.Files(Files(("Shield Sledding", "ShieldSledding_State"), ("Spawn That!", "RPC_ReceiveLocationsSpawnThat"), ("A", "Shared"), ("B", "Shared")));
            Assert.Equal("ShieldSledding_State", book.Label(-1183998207));
            Assert.Equal("Shield Sledding", book.ModOf(-1183998207));
            Assert.Equal("RPC_ReceiveLocationsSpawnThat", book.Label(-187796147));
            Assert.Null(book.ModOf(H("Shared")));
        }

        [Fact]
        public void A_name_hashed_while_the_game_runs_wins_over_the_files()
        {
            var book = new MessageNames();
            book.Files(Files(("A", "Ping")));
            book.Learned(H("Ping"), "Ping (learned)");
            Assert.Equal("Ping (learned)", book.Label(H("Ping")));
        }

        [Fact]
        public void A_message_whose_name_was_never_seen_stays_a_number()
        {
            var book = new MessageNames();
            Assert.Equal("message 977998204", book.Label(977998204));
            Assert.Null(book.ModOf(977998204));
            Assert.False(book.LearnedWhileRunning(977998204));
        }

        [Fact]
        public void A_number_hashed_without_a_name_leaves_the_files_name_in_place()
        {
            var book = new MessageNames();
            book.Files(Files(("Shield Sledding", "ShieldSledding_State")));
            book.Learned(-1183998207, null);
            Assert.Equal("ShieldSledding_State", book.Label(-1183998207));
        }

        [Fact]
        public void A_name_only_the_games_own_code_carries_belongs_to_no_mod()
        {
            var book = new MessageNames();
            book.Files(Files((ValueNames.Game, "SetEvent")));
            Assert.Equal("SetEvent", book.Label(H("SetEvent")));
            Assert.Null(book.ModOf(H("SetEvent")));
        }

        [Fact]
        public void The_book_stops_learning_at_its_limit_and_keeps_what_it_has()
        {
            var book = new MessageNames(most: 2);
            book.Learned(1, "one");
            book.Learned(2, "two");
            book.Learned(3, "three");
            Assert.Equal("one", book.Label(1));
            Assert.Equal("message 3", book.Label(3));
        }

        [Fact]
        public void The_book_says_how_many_messages_on_the_wire_it_named_and_from_where()
        {
            var book = new MessageNames();
            book.Learned(H("SetEvent"), "SetEvent");
            book.Sync(new[] { new KeyValuePair<string, string>("Azumatt.AzuAutoStore", "AzuAutoStore") });
            book.Files(Files(("Shield Sledding", "ShieldSledding_State")));
            book.Label(H("SetEvent"));
            book.Label(H("Azumatt.AzuAutoStore ConfigSync"));
            book.Label(-1183998207);
            book.Label(977998204);
            book.Label(977998204);
            Assert.Equal("3 of 4 kinds of message seen named: 1 learned while the game ran, 1 from the files, 1 config sync", book.CountLine());
        }

        [Fact]
        public void A_message_no_handler_here_explains_belongs_to_the_mod_whose_code_holds_its_name()
        {
            var guids = new Dictionary<string, string> { ["isimp.Vigil"] = "Vigil" };
            var assemblies = new Dictionary<string, string>();
            Assert.Equal("Shield Sledding", NetOwner.Resolve("ShieldSledding_State", null, guids, assemblies, namedIn: "Shield Sledding"));
            Assert.Equal("unknown", NetOwner.Resolve("ShieldSledding_State", null, guids, assemblies, namedIn: null));
            Assert.Equal("Vigil", NetOwner.Resolve("isimp.Vigil ConfigSync", null, guids, assemblies, namedIn: "Other"));
            Assert.Equal("unknown (SomeLibrary)", NetOwner.Resolve("ShieldSledding_State", "SomeLibrary", guids, assemblies, namedIn: "Shield Sledding"));
        }
    }
}
