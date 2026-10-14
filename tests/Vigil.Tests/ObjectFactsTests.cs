using System;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The inspector shows one world object as the game keeps it: its stored values by name (see
    /// ValueNameTests), how often it changes and what sending it would take, whether it is saved
    /// with the world, how old it is, the game's own record of it, and the scripts on it by the mod
    /// that brought them. It only reads.
    /// </summary>
    public sealed class ObjectFactsTests
    {
        [Fact]
        public void Stored_values_read_plainly()
        {
            Assert.Equal("18.5", StoredValue.Format(18.5f));
            Assert.Equal("0.33", StoredValue.Format(1f / 3f));
            Assert.Equal("2", StoredValue.Format(2));
            Assert.Equal("638123456789", StoredValue.Format(638123456789L));
            Assert.Equal("\"Bacon\"", StoredValue.Format("Bacon"));
            Assert.Equal("(1.2, -3, 40.5)", StoredValue.Format(new[] { 1.2f, -3f, 40.5f }));
            Assert.Equal("128 bytes", StoredValue.Format(new byte[128]));
        }

        [Fact]
        public void A_long_text_is_cut_and_says_so()
        {
            string shown = StoredValue.Format(new string('x', 200));
            Assert.Equal(65, shown.Length);
            Assert.EndsWith("x...\"", shown);
        }

        [Fact]
        public void Named_values_come_first_in_order_then_unnamed_ones()
        {
            var rows = StoredValue.Order(new[]
            {
                new StoredValue.Row("#55", "int", "1"), new StoredValue.Row("level", "int", "2"), new StoredValue.Row("health", "float", "3"), new StoredValue.Row("#-9", "string", "x"),
            });
            Assert.Equal(new[] { "health", "level", "#-9", "#55" }, rows.Select(r => r.Name).ToArray());
        }

        [Fact]
        public void A_change_rate_says_how_often_it_changed_and_what_sending_it_takes_at_most()
        {
            var rate = ChangeRate.Of(100, 134, 10, sizeBytes: 210);
            Assert.Equal(34, rate.Changes);
            Assert.Equal(3.4, rate.PerSecond, 3);
            Assert.Equal(3.4, rate.SendsPerSecond, 3);
            Assert.Equal(714, rate.BytesPerSecond, 3);
            Assert.False(rate.Reset);
        }

        [Fact]
        public void An_object_is_sent_at_most_twenty_times_a_second_however_often_it_changes()
        {
            var rate = ChangeRate.Of(0, 600, 10, sizeBytes: 100);
            Assert.Equal(60, rate.PerSecond, 3);
            Assert.Equal(20, rate.SendsPerSecond, 3);
            Assert.Equal(2000, rate.BytesPerSecond, 3);
        }

        [Fact]
        public void A_counter_that_went_back_or_no_time_passed_counts_nothing()
        {
            var reset = ChangeRate.Of(500, 3, 10, 100);
            Assert.True(reset.Reset);
            Assert.Equal(0, reset.Changes);
            Assert.Equal(0, reset.BytesPerSecond);
            Assert.Equal(0, ChangeRate.Of(1, 9, 0, 100).PerSecond);
        }

        [Fact]
        public void How_the_game_keeps_an_object_reads_in_words()
        {
            Assert.Equal("saved with the world · loaded from far away · sent first", ObjectKept.Describe(true, true, "Prioritized"));
            Assert.Equal("not saved", ObjectKept.Describe(false, false, "Default"));
            Assert.Equal("saved with the world · solid", ObjectKept.Describe(true, false, "Solid"));
        }

        [Fact]
        public void An_age_reads_in_its_largest_two_units()
        {
            Assert.Equal("45 s ago", ObjectAge.Describe(TimeSpan.FromSeconds(45.4)));
            Assert.Equal("12 min ago", ObjectAge.Describe(TimeSpan.FromMinutes(12.2)));
            Assert.Equal("2 h 14 min ago", ObjectAge.Describe(new TimeSpan(2, 14, 30)));
            Assert.Equal("3 d 4 h ago", ObjectAge.Describe(new TimeSpan(3, 4, 59, 0)));
            Assert.Equal("not known", ObjectAge.Describe(TimeSpan.FromSeconds(-5)));
        }

        [Fact]
        public void Scripts_are_listed_by_mod_the_games_first()
        {
            var groups = ScriptsByMod.Group(new[]
            {
                new KeyValuePair<string, string>("Humanoid", null), new KeyValuePair<string, string>("MonsterAI", null), new KeyValuePair<string, string>("ZNetView", null),
                new KeyValuePair<string, string>("TameExtras", "AllTameable"), new KeyValuePair<string, string>("TameExtras", "AllTameable"),
                new KeyValuePair<string, string>("LevelEffects", "CreatureLevelControl"),
            });
            Assert.Equal(new[] { "the game", "AllTameable", "CreatureLevelControl" }, groups.Select(g => g.Owner).ToArray());
            Assert.Equal(3, groups[0].Count);
            Assert.Equal("Humanoid, MonsterAI, ZNetView", groups[0].Types);
            Assert.Equal(2, groups[1].Count);
            Assert.Equal("TameExtras", groups[1].Types);
            Assert.Equal("the game 3, AllTameable 2, CreatureLevelControl 1", ScriptsByMod.Summary(groups));
        }

        [Fact]
        public void The_games_scripts_come_first_even_when_a_mod_adds_more()
        {
            var groups = ScriptsByMod.Group(new[]
            {
                new KeyValuePair<string, string>("ZNetView", null),
                new KeyValuePair<string, string>("A", "BigMod"), new KeyValuePair<string, string>("B", "BigMod"), new KeyValuePair<string, string>("C", "BigMod"),
            });
            Assert.Equal(new[] { "the game", "BigMod" }, groups.Select(g => g.Owner).ToArray());
        }
        [Fact]
        public void The_object_record_shows_the_games_own_bookkeeping_for_it()
        {
            var rows = ObjectRecord.Rows(new ObjectRecord.Input
            {
                Id = "1:390726", Prefab = -1683946734, Owner = 90294866856696836L, OwnerName = "Bjorn",
                DataRevision = 412, OwnerRevision = 3, X = 3091.27f, Y = 35.04f, Z = -3001.5f, ZoneX = 48, ZoneY = -47,
                Turn = new[] { 0f, 90.4f, 359.6f }, Persistent = true, Distant = false, Type = "Default",
                LinkKind = "Spawned", LinkTo = "1:2345",
            });
            Assert.Equal(new[] { "Id", "Prefab number", "Owner", "Revisions", "Position", "Rotation", "Flags", "Linked to" }, rows.Select(r => r.Key).ToArray());
            string Value(string key) => rows.Single(r => r.Key == key).Value;
            Assert.Equal("1:390726", Value("Id"));
            Assert.Equal("-1683946734", Value("Prefab number"));
            Assert.Equal("90294866856696836 (Bjorn)", Value("Owner"));
            Assert.Equal("data 412 · owner 3", Value("Revisions"));
            Assert.Equal("(3091.3, 35, -3001.5), zone (48, -47)", Value("Position"));
            Assert.Equal("(0, 90, 360)", Value("Rotation"));
            Assert.Equal("persistent yes · distant no · type Default", Value("Flags"));
            Assert.Equal("Spawned 1:2345", Value("Linked to"));
        }

        [Fact]
        public void An_object_with_no_owner_and_no_link_says_so_and_leaves_the_link_out()
        {
            var rows = ObjectRecord.Rows(new ObjectRecord.Input { Id = "0:12", Owner = 0, Turn = new[] { 0f, 0f, 0f }, Type = "Prioritized", LinkKind = "None" });
            Assert.Equal("none", rows.Single(r => r.Key == "Owner").Value);
            Assert.Equal("persistent no · distant no · type Prioritized", rows.Single(r => r.Key == "Flags").Value);
            Assert.DoesNotContain(rows, r => r.Key == "Linked to");

            var unnamed = ObjectRecord.Rows(new ObjectRecord.Input { Id = "0:12", Owner = 77, Turn = new[] { 0f, 0f, 0f }, Type = "Default" });
            Assert.Equal("77", unnamed.Single(r => r.Key == "Owner").Value);
            Assert.DoesNotContain(unnamed, r => r.Key == "Linked to");
        }

        [Fact]
        public void A_stored_value_keeps_where_its_name_was_found()
        {
            var row = new StoredValue.Row("isimp.Hoard.sealed", "Hoard", "int", "1");
            Assert.Equal("Hoard", row.NamedIn);
        }
    }
}
