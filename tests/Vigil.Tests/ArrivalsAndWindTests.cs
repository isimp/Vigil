using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Creatures that came into a base: each once, with which base and where it spawned (from the
    /// spawn log when this game spawned it), pinnable where it spawned. And the wind check of a
    /// windmill: its output by the game's rule, its cover as rays blocked of 17, and what blocks each.
    /// </summary>
    public sealed class ArrivalsAndWindTests
    {
        private static SpawnRecord Shaman(double at) => new SpawnRecord
        {
            At = at, Id = "1:5", Creature = "Greydwarf Shaman", Source = SpawnSource.Biome, Detail = "Meadows", Entry = "Greydwarf Shaman",
            NightOnly = true, HuntsPlayers = true, Distance = 62f, X = 300f, Z = 40f,
        };

        private static Arrival Came(double at, string id, string creature, SpawnRecord spawn = null) => new Arrival
        {
            At = at, Id = id, Creature = creature, Place = "Workbench by Sigrun", X = 10f, Z = 20f, Spawn = spawn,
        };

        [Fact]
        public void A_creature_that_came_into_a_base_says_where_and_when_it_spawned_when_this_game_spawned_it()
        {
            var log = new BaseArrivals();
            log.Add(Came(1000, "1:5", "Greydwarf Shaman", Shaman(880)));
            var row = log.Recent(1120, 1800).Single();
            Assert.Equal("2 min", row.When);
            Assert.Equal("Greydwarf Shaman", row.What);
            Assert.Equal("Workbench by Sigrun", row.Into);
            Assert.Equal("by your game 4 min ago, 62 m out, outside bases · Meadows list, Greydwarf Shaman, night only, hunts players", row.Spawned);
            Assert.Equal(300f, row.PinX);
            Assert.Equal(40f, row.PinZ);
            Assert.Equal("Greydwarf Shaman spawned here", row.PinLabel);
        }

        [Fact]
        public void One_this_game_did_not_spawn_says_so_and_pins_where_it_came_in()
        {
            var log = new BaseArrivals();
            log.Add(Came(1000, "2:9", "Greyling"));
            var row = log.Recent(1010, 1800).Single();
            Assert.Equal("not seen spawning: another player's game ran that area, or it spawned before this session", row.Spawned);
            Assert.Equal(10f, row.PinX);
            Assert.Equal("Greyling came in here", row.PinLabel);
            var raid = Came(1000, "2:10", "Troll");
            raid.Origin = "A raid's creature: it leaves when the raid ends";
            log.Add(raid);
            Assert.Equal("A raid's creature: it leaves when the raid ends", log.Recent(1010, 1800).First().Spawned);
        }

        [Fact]
        public void Each_creature_counts_once_and_ones_of_a_kind_coming_in_together_read_as_one_row()
        {
            var log = new BaseArrivals();
            Assert.True(log.Add(Came(1000, "3:1", "Skeleton")));
            Assert.False(log.Add(Came(1005, "3:1", "Skeleton")));
            Assert.True(log.Add(Came(1010, "3:2", "Skeleton")));
            Assert.True(log.Add(Came(1200, "3:3", "Skeleton")));
            var rows = log.Recent(1210, 1800);
            Assert.Equal(new[] { "Skeleton", "Skeleton ×2" }, rows.Select(r => r.What).ToArray());
            Assert.Equal(3, log.Count(1210, 1800));
            Assert.Equal(1, log.Count(1210, 60));
        }

        [Fact]
        public void A_window_leaves_older_arrivals_out()
        {
            var log = new BaseArrivals();
            log.Add(Came(100, "4:1", "Greyling"));
            Assert.Empty(log.Recent(100 + 1801, 1800));
        }

        [Fact]
        public void Wind_under_a_tenth_counts_as_none_and_full_wind_as_all_of_it()
        {
            Assert.Equal(0f, WindCheck.Wind(0.05f));
            Assert.Equal(0f, WindCheck.Wind(0.1f));
            Assert.Equal(1f, WindCheck.Wind(1f));
            Assert.Equal(0.5f, WindCheck.Wind(0.55f), 3);
            Assert.Equal(0.5f, WindCheck.Output(0.5f, 1f), 3);
        }

        [Fact]
        public void The_output_line_gives_the_power_its_parts_and_how_often_flour_comes()
        {
            Assert.Equal("58 % · wind 76 % × open 76 % · one flour every 17 s", WindCheck.OutputLine(0.784f, 4, 10f));
            Assert.Equal("0 % · the wind is under 10 %, which counts as none", WindCheck.OutputLine(0.08f, 0, 10f));
            Assert.Equal("0 % · every ray is blocked", WindCheck.OutputLine(1f, 17, 10f));
            Assert.Equal("100 % · wind 100 % × open 100 % · one flour every 10 s", WindCheck.OutputLine(1f, 0, 10f));
        }

        [Fact]
        public void The_cover_names_the_open_rays_in_the_games_order_and_the_blocked_ones_nearest_first()
        {
            var rays = WindCheck.Directions.Select(d => new WindCheck.Ray { Direction = d }).ToList();
            rays[8].Blocked = true; rays[8].By = "Wood roof 45°"; rays[8].Metres = 6.2f;
            rays[16].Blocked = true; rays[16].By = "Pine"; rays[16].Metres = 14f;
            rays[2].Blocked = true; rays[2].By = "terrain"; rays[2].Metres = 21f;
            Assert.Equal(17, WindCheck.Directions.Length);
            Assert.Equal("3 of 17 rays blocked, each costs about 6 %", WindCheck.CoverLine(rays));
            Assert.Equal("Up, E, N, S, SW, SE, NW, W 45°, E 45°, N 45°, S 45°, SW 45°, SE 45°, NW 45°", WindCheck.OpenLine(rays));
            Assert.Equal(new[] { "NE|Wood roof 45°|6 m", "NE 45°|Pine|14 m", "W|terrain|21 m" }, WindCheck.BlockedRows(rays).Select(r => string.Join("|", r)).ToArray());
            Assert.Equal("no ray blocked: full output in full wind", WindCheck.CoverLine(WindCheck.Directions.Select(d => new WindCheck.Ray { Direction = d })));
        }
    }
}
