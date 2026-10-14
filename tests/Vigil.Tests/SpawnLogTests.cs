using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The spawn log records every creature this game spawns: from which biome list and entry, a
    /// raid, a spawner structure or a fixed spawner, where it appeared and whether inside a base, and
    /// the spawns another mod's check cancelled. The inspector then says where a creature came from:
    /// the log's record when this game spawned it, else what the game itself keeps about it.
    /// </summary>
    public sealed class SpawnLogTests
    {
        private static SpawnRecord Neck(double at, string id = "1:10") => new SpawnRecord
        {
            At = at, Id = id, Creature = "Neck", Source = SpawnSource.Biome, Detail = "Meadows", Entry = "Neck", Distance = 58.3f,
        };

        [Fact]
        public void A_spawn_reads_as_when_what_how_and_where()
        {
            var log = new SpawnLog();
            log.Add(Neck(at: 100));
            var row = log.Recent(now: 220, window: 1800).Single();
            Assert.Equal("2 min", row.When);
            Assert.Equal("Neck", row.What);
            Assert.Equal("Meadows list, Neck", row.How);
            Assert.Equal("58 m, outside bases", row.Where);
        }

        [Fact]
        public void A_group_spawned_together_reads_as_one_row_with_its_count()
        {
            var log = new SpawnLog();
            log.Add(Neck(100, "1:1"));
            log.Add(Neck(100.2, "1:2"));
            log.Add(Neck(100.4, "1:3"));
            var row = log.Recent(110, 1800).Single();
            Assert.Equal("Neck ×3", row.What);
            Assert.Equal(3, row.Count);
        }

        [Fact]
        public void The_same_creature_spawned_minutes_apart_stays_two_rows_newest_first()
        {
            var log = new SpawnLog();
            log.Add(Neck(100, "1:1"));
            log.Add(Neck(400, "1:2"));
            var rows = log.Recent(410, 1800);
            Assert.Equal(new[] { "10 s", "5 min" }, rows.Select(r => r.When).ToArray());
        }

        [Fact]
        public void A_spawn_inside_a_base_is_counted_and_says_whether_its_entry_allows_that()
        {
            var log = new SpawnLog();
            var boar = new SpawnRecord { At = 50, Id = "1:5", Creature = "Boar", Source = SpawnSource.Biome, Detail = "Meadows", Entry = "Boar", Distance = 12f, InsideBase = true, AllowedInBase = true };
            log.Add(boar);
            log.Add(Neck(60));
            var row = log.Recent(100, 1800).Single(r => r.What == "Boar");
            Assert.True(row.InsideBase);
            Assert.Equal("12 m, inside a base (its entry allows that)", row.Where);
            Assert.Equal(1, log.InsideBase(100, 1800));
        }

        [Fact]
        public void Each_kind_of_spawn_names_where_it_came_from()
        {
            Assert.Equal("Meadows list, Greyling, night only", SpawnLog.How(new SpawnRecord { Source = SpawnSource.Biome, Detail = "Meadows", Entry = "Greyling", NightOnly = true }));
            Assert.Equal("special area Ashlands rim, Charred", SpawnLog.How(new SpawnRecord { Source = SpawnSource.SpecialArea, Detail = "Ashlands rim", Entry = "Charred" }));
            Assert.Equal("raid army_eikthyr", SpawnLog.How(new SpawnRecord { Source = SpawnSource.Raid, Detail = "army_eikthyr" }));
            Assert.Equal("spawner structure: Greydwarf nest", SpawnLog.How(new SpawnRecord { Source = SpawnSource.Structure, Detail = "Greydwarf nest" }));
            Assert.Equal("fixed spawner of Crypt2", SpawnLog.How(new SpawnRecord { Source = SpawnSource.FixedSpawner, Detail = "Crypt2" }));
            Assert.Equal("a fixed spawner", SpawnLog.How(new SpawnRecord { Source = SpawnSource.FixedSpawner }));
        }

        [Fact]
        public void Biomes_read_by_their_names_in_the_game()
        {
            Assert.Equal("Black Forest", SpawnLog.BiomeName("BlackForest"));
            Assert.Equal("Ashlands", SpawnLog.BiomeName("AshLands"));
            Assert.Equal("Deep North", SpawnLog.BiomeName("DeepNorth"));
            Assert.Equal("Meadows", SpawnLog.BiomeName("Meadows"));
        }

        [Fact]
        public void A_creature_with_stars_shows_them()
        {
            var log = new SpawnLog();
            log.Add(new SpawnRecord { At = 10, Id = "1:7", Creature = "Greydwarf", Source = SpawnSource.Structure, Detail = "Greydwarf nest", Level = 3, Distance = 90 });
            Assert.Equal("Greydwarf, 2 stars", log.Recent(20, 1800).Single().What);
        }

        [Fact]
        public void Spawns_a_mods_check_cancelled_gather_into_one_row_naming_the_mods_that_can()
        {
            var log = new SpawnLog();
            for (int i = 0; i < 12; i++)
                log.Add(new SpawnRecord { At = 100 + (i * 30), Creature = "Greydwarf", Source = SpawnSource.Biome, Detail = "Black Forest", Entry = "Greydwarf", Cancelled = true, CancelledBy = "Seasonality" });
            log.Add(Neck(500));
            var rows = log.Recent(600, 1800);
            Assert.Equal("Neck", rows[0].What);
            var cancelled = rows.Single(r => r.Cancelled);
            Assert.Equal("Greydwarf ×12", cancelled.What);
            Assert.Equal("cancelled: Seasonality can stop it", cancelled.Where);
            Assert.Equal(0, log.InsideBase(600, 1800));
        }

        [Fact]
        public void Cancelled_spawns_never_push_real_spawns_out_of_the_log()
        {
            var log = new SpawnLog();
            log.Add(Neck(0, "1:0"));
            for (int i = 0; i < SpawnLog.Max * 2; i++)
                log.Add(new SpawnRecord { At = i, Creature = "Greydwarf", Source = SpawnSource.Structure, Detail = "Greydwarf nest", Cancelled = true });
            Assert.NotNull(log.Find("1:0"));
            Assert.Single(log.Records);
        }

        [Fact]
        public void Old_spawns_leave_the_window_and_the_log_keeps_its_newest()
        {
            var log = new SpawnLog();
            log.Add(Neck(0, "1:0"));
            log.Add(Neck(5000, "1:1"));
            Assert.Single(log.Recent(5010, 1800));
            for (int i = 0; i < SpawnLog.Max + 10; i++) log.Add(Neck(6000 + (i * 10), "2:" + i));
            Assert.Equal(SpawnLog.Max, log.Records.Count);
            Assert.Null(log.Find("1:0"));
            Assert.NotNull(log.Find("2:" + (SpawnLog.Max + 9)));
        }

        [Fact]
        public void A_spawn_reads_as_one_events_line()
        {
            Assert.Equal("Meadows list, Neck; 58 m, outside bases", SpawnLog.EventDetail(Neck(0)));
        }

        // ------------------------------------------------------------------ where a creature came from

        [Fact]
        public void A_creature_this_game_spawned_says_how_from_the_log()
        {
            string words = SpawnOrigin.Describe(new SpawnOrigin.Facts { Record = Neck(100), Now = 220, EventCreature = true });
            Assert.Equal("Spawned by your game 2 min ago: Meadows list, Neck, 58 m from you", words);
        }

        [Fact]
        public void A_creature_not_seen_spawning_says_what_the_game_keeps_about_it()
        {
            Assert.Equal("A raid's creature: it leaves when the raid ends", SpawnOrigin.Describe(new SpawnOrigin.Facts { EventCreature = true, DespawnInDay = true }));
            Assert.Equal("From the fixed spawner of Crypt2", SpawnOrigin.Describe(new SpawnOrigin.Facts { LinkedToSpawner = true, SpawnerLocation = "Crypt2", Tamed = true }));
            Assert.Equal("From a fixed spawner", SpawnOrigin.Describe(new SpawnOrigin.Facts { LinkedToSpawner = true }));
            Assert.Equal("Born to tamed parents; it grows up", SpawnOrigin.Describe(new SpawnOrigin.Facts { Offspring = true, Tamed = true }));
            Assert.Equal("Tamed; where it came from is not kept", SpawnOrigin.Describe(new SpawnOrigin.Facts { Tamed = true }));
            Assert.Equal("A night spawn from a biome list; it goes at day", SpawnOrigin.Describe(new SpawnOrigin.Facts { DespawnInDay = true }));
            Assert.Equal("Not seen spawning here: a biome spawn or a spawner structure on another player's game, or from before this session",
                SpawnOrigin.Describe(new SpawnOrigin.Facts()));
        }

        [Fact]
        public void A_cancelled_record_is_not_taken_for_the_creatures_origin()
        {
            var cancelled = Neck(100);
            cancelled.Cancelled = true;
            Assert.Equal("A night spawn from a biome list; it goes at day", SpawnOrigin.Describe(new SpawnOrigin.Facts { Record = cancelled, Now = 120, DespawnInDay = true }));
        }
    }
}
