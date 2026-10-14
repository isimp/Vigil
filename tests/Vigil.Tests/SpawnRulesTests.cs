using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// "What could spawn here" checks each spawn entry against the game's rules for the spot under
    /// the crosshair. An entry that fails a rule of the spot itself (biome edge, roof, height, slope,
    /// base, forest, lava, water depth, distance from the centre) reads "no"; one that only fails the
    /// world's state now (time of day, weather, world key, how many are about, a world event, another
    /// one close by) reads "not now"; one that passes reads "could", with the distance from players
    /// it still needs. Only the entries for the spot's biome are listed.
    /// </summary>
    public sealed class SpawnRulesTests
    {
        private const int Meadows = 1, BlackForest = 8, Edge = 1, Median = 2;

        // Open meadow, 10 m above the sea, flat, outside forests and bases, by day in clear weather.
        private static SpawnSpot Spot() => new SpawnSpot
        {
            Biome = Meadows, BiomeName = "Meadows", Area = Median, Ground = 40f, NormalY = 1f, FromCentre = 500f,
            Day = true, Weather = "Clear", KeySet = key => false,
        };

        private static SpawnEntryFacts Boar() => new SpawnEntryFacts { Name = "Boar", Prefab = "Boar", Creature = "Boar", Biome = Meadows, MaxSpawned = 3 };

        private static SpawnAnswer Judge(SpawnEntryFacts entry, SpawnSpot spot) => SpawnRules.Judge(entry, spot);

        [Fact]
        public void An_entry_that_meets_every_rule_could_spawn_once_a_player_is_40_to_80_m_away_and_none_nearer()
        {
            var answer = Judge(Boar(), Spot());
            Assert.Equal(SpawnVerdict.Could, answer.Verdict);
            Assert.Equal("could", answer.Now);
            Assert.Equal("when a player is 40 to 80 m away, none nearer", answer.Why);
        }

        [Fact]
        public void An_entry_allowed_close_to_players_and_its_own_spawn_range_show_in_what_it_still_needs()
        {
            var boar = Boar();
            boar.CloseToPlayer = true;
            boar.RadiusMin = 20f;
            boar.RadiusMax = 50f;
            Assert.Equal("when a player is 20 to 50 m away", Judge(boar, Spot()).Why);
        }

        [Fact]
        public void A_night_only_entry_by_day_and_a_day_only_entry_at_night_are_not_now()
        {
            var greyling = Boar();
            greyling.AtDay = false;
            var day = Judge(greyling, Spot());
            Assert.Equal(SpawnVerdict.NotNow, day.Verdict);
            Assert.Equal("not now", day.Now);
            Assert.Equal("night only", day.Why);

            var night = Spot();
            night.Day = false;
            night.Night = true;
            Assert.Equal(SpawnVerdict.Could, Judge(greyling, night).Verdict);

            var dayOnly = Boar();
            dayOnly.AtNight = false;
            Assert.Equal("day only", Judge(dayOnly, night).Why);
        }

        [Fact]
        public void An_entry_that_needs_a_weather_names_it_and_the_weather_now()
        {
            var draugr = Boar();
            draugr.Weathers = new List<string> { "Rain", "ThunderStorm" };
            var answer = Judge(draugr, Spot());
            Assert.Equal(SpawnVerdict.NotNow, answer.Verdict);
            Assert.Equal("needs weather Rain or ThunderStorm; now Clear", answer.Why);

            var rain = Spot();
            rain.Weather = "ThunderStorm";
            Assert.Equal(SpawnVerdict.Could, Judge(draugr, rain).Verdict);
        }

        [Fact]
        public void An_entry_at_its_count_is_not_now_and_a_count_of_zero_means_no_limit()
        {
            var deer = Boar();
            deer.MaxSpawned = 3;
            deer.Nearby = 3;
            var full = Judge(deer, Spot());
            Assert.Equal(SpawnVerdict.NotNow, full.Verdict);
            Assert.Equal("3 of 3 already nearby", full.Why);

            deer.Nearby = 2;
            Assert.Equal(SpawnVerdict.Could, Judge(deer, Spot()).Verdict);

            deer.MaxSpawned = 0;
            deer.Nearby = 40;
            Assert.Equal(SpawnVerdict.Could, Judge(deer, Spot()).Verdict);
        }

        [Fact]
        public void An_entry_waiting_for_a_world_key_names_the_key_until_it_is_set()
        {
            var entry = Boar();
            entry.GlobalKey = "defeated_bonemass";
            var answer = Judge(entry, Spot());
            Assert.Equal(SpawnVerdict.NotNow, answer.Verdict);
            Assert.Equal("needs the world key defeated_bonemass", answer.Why);

            var keyed = Spot();
            keyed.KeySet = key => key == "defeated_bonemass";
            Assert.Equal(SpawnVerdict.Could, Judge(entry, keyed).Verdict);
        }

        [Fact]
        public void An_entry_tied_to_a_world_event_needs_that_event_at_the_spot_whatever_its_case()
        {
            var entry = Boar();
            entry.PersistentEvent = "HildirsQuest";
            Assert.Equal("needs the event HildirsQuest here", Judge(entry, Spot()).Why);

            var during = Spot();
            during.EventHere = "hildirsquest";
            Assert.Equal(SpawnVerdict.Could, Judge(entry, during).Verdict);
        }

        [Fact]
        public void Another_of_the_same_creature_closer_than_its_spawn_distance_is_not_now()
        {
            var entry = Boar();
            entry.SpawnDistance = 10f;
            entry.NearestSame = 6.2f;
            var answer = Judge(entry, Spot());
            Assert.Equal(SpawnVerdict.NotNow, answer.Verdict);
            Assert.Equal("another one is 6 m away; it needs 10 m", answer.Why);

            entry.NearestSame = 10f;
            Assert.Equal(SpawnVerdict.Could, Judge(entry, Spot()).Verdict);
        }

        [Fact]
        public void A_spot_inside_a_base_is_no_unless_the_entry_allows_bases()
        {
            var spot = Spot();
            spot.InsideBase = true;
            var answer = Judge(Boar(), spot);
            Assert.Equal(SpawnVerdict.No, answer.Verdict);
            Assert.Equal("no", answer.Now);
            Assert.Equal("inside a player base, which its entry does not allow", answer.Why);

            var allowed = Boar();
            allowed.InsideBase = true;
            Assert.Equal(SpawnVerdict.Could, Judge(allowed, spot).Verdict);
        }

        [Fact]
        public void A_spot_with_something_solid_above_or_below_is_no()
        {
            var spot = Spot();
            spot.Blocked = true;
            var answer = Judge(Boar(), spot);
            Assert.Equal(SpawnVerdict.No, answer.Verdict);
            Assert.Equal("something solid is above or below the spot", answer.Why);
        }

        [Fact]
        public void The_ground_must_lie_within_the_entrys_heights_above_the_sea_its_limits_included()
        {
            var entry = Boar();
            entry.MinAltitude = 2f;
            var low = Spot();
            low.Ground = 29f;
            Assert.Equal("needs ground 2 m above the sea or higher; here 1 m below the sea", Judge(entry, low).Why);

            var edge = Spot();
            edge.Ground = 32f;
            Assert.Equal(SpawnVerdict.Could, Judge(entry, edge).Verdict);

            var serpent = Boar();
            serpent.MaxAltitude = -20f;
            Assert.Equal("needs ground 20 m below the sea or lower; here 10 m above the sea", Judge(serpent, Spot()).Why);

            var band = Boar();
            band.MinAltitude = 0f;
            band.MaxAltitude = 5f;
            Assert.Equal("needs ground from sea level to 5 m above the sea; here 10 m above the sea", Judge(band, Spot()).Why);
        }

        [Fact]
        public void The_slope_must_lie_within_the_entrys_tilt()
        {
            var steep = Spot();
            steep.NormalY = 0.7071f; // 45 degrees
            var answer = Judge(Boar(), steep);
            Assert.Equal(SpawnVerdict.No, answer.Verdict);
            Assert.Equal("needs a slope of 35° or less; here 45°", answer.Why);

            var cliff = Boar();
            cliff.MinTilt = 20f;
            Assert.Equal("needs a slope of 20° or more; here 0°", Judge(cliff, Spot()).Why);
        }

        [Fact]
        public void Water_depth_counts_only_for_an_entry_with_a_depth_range()
        {
            var neck = Boar();
            neck.MinOceanDepth = 0.5f;
            neck.MaxOceanDepth = 5f;
            var answer = Judge(neck, Spot());
            Assert.Equal(SpawnVerdict.No, answer.Verdict);
            Assert.Equal("needs water 0.5 to 5 m deep; here 0 m", answer.Why);

            var shore = Spot();
            shore.OceanDepth = 1.5f;
            Assert.Equal(SpawnVerdict.Could, Judge(neck, shore).Verdict);

            var deep = Spot();
            deep.OceanDepth = 30f;
            Assert.Equal(SpawnVerdict.Could, Judge(Boar(), deep).Verdict);
        }

        [Fact]
        public void Forest_and_lava_rules_refuse_the_spots_they_name()
        {
            var forest = Spot();
            forest.InForest = true;
            var open = Boar();
            open.InForest = false;
            Assert.Equal("only outside forests", Judge(open, forest).Why);
            var woods = Boar();
            woods.OutsideForest = false;
            Assert.Equal("only in forests", Judge(woods, Spot()).Why);
            Assert.Equal(SpawnVerdict.Could, Judge(woods, forest).Verdict);

            var lava = Spot();
            lava.OnLava = true;
            Assert.Equal("not on lava", Judge(Boar(), lava).Why);
            var lavaOnly = Boar();
            lavaOnly.InLava = true;
            lavaOnly.OutsideLava = false;
            Assert.Equal("only on lava", Judge(lavaOnly, Spot()).Why);
            Assert.Equal(SpawnVerdict.Could, Judge(lavaOnly, lava).Verdict);
        }

        [Fact]
        public void A_biome_edge_rule_and_a_distance_from_the_centre_refuse_the_wrong_spots()
        {
            var edgeOnly = Boar();
            edgeOnly.Area = Edge;
            Assert.Equal("only where biomes meet; this area lies inside one", Judge(edgeOnly, Spot()).Why);
            var atEdge = Spot();
            atEdge.Area = Edge;
            var inner = Boar();
            inner.Area = Median;
            Assert.Equal("only inside a biome, away from where biomes meet", Judge(inner, atEdge).Why);

            var far = Boar();
            far.MinFromCentre = 3000f;
            Assert.Equal("needs to be 3000 m or more from the world's centre; here 500 m", Judge(far, Spot()).Why);
            var near = Boar();
            near.MaxFromCentre = 400f;
            Assert.Equal("needs to be 400 m or less from the world's centre; here 500 m", Judge(near, Spot()).Why);
        }

        [Fact]
        public void A_special_area_that_leaves_an_entry_out_and_a_chance_of_nothing_are_no()
        {
            var spot = Spot();
            spot.LeftOut = new Dictionary<string, string> { ["Boar"] = "Haunted meadow" };
            var answer = Judge(Boar(), spot);
            Assert.Equal(SpawnVerdict.No, answer.Verdict);
            Assert.Equal("Haunted meadow here leaves it out", answer.Why);

            var never = Boar();
            never.Chance = 0f;
            Assert.Equal("its chance is 0 %", Judge(never, Spot()).Why);
        }

        [Fact]
        public void Every_rule_of_the_spot_it_fails_is_named_and_the_worlds_state_then_left_out()
        {
            var spot = Spot();
            spot.Blocked = true;
            spot.NormalY = 0.5f; // 60 degrees
            var entry = Boar();
            entry.AtDay = false;
            var answer = Judge(entry, spot);
            Assert.Equal(SpawnVerdict.No, answer.Verdict);
            Assert.Equal(new[] { "something solid is above or below the spot", "needs a slope of 35° or less; here 60°" }, answer.Reasons);
            Assert.DoesNotContain("night only", answer.Why);
        }

        [Fact]
        public void Every_state_of_the_world_it_waits_for_is_named()
        {
            var entry = Boar();
            entry.AtDay = false;
            entry.GlobalKey = "defeated_eikthyr";
            Assert.Equal("needs the world key defeated_eikthyr; night only", Judge(entry, Spot()).Why);
        }

        [Fact]
        public void With_spawning_switched_off_on_this_game_nothing_could_spawn_now()
        {
            var spot = Spot();
            spot.SpawningOff = true;
            var answer = Judge(Boar(), spot);
            Assert.Equal(SpawnVerdict.NotNow, answer.Verdict);
            Assert.Equal("spawning is switched off on this game (a debug switch)", answer.Why);
        }

        [Fact]
        public void The_list_holds_the_spots_biome_only_could_first_then_not_now_then_no()
        {
            var greyling = new SpawnEntryFacts { Name = "Greyling", Prefab = "Greyling", Creature = "Greyling", Biome = Meadows, AtDay = false };
            var boar = Boar();
            boar.MinOceanDepth = 0.5f;
            boar.MaxOceanDepth = 5f;
            var neck = new SpawnEntryFacts { Name = "Neck", Prefab = "Neck", Creature = "Neck", Biome = Meadows };
            var troll = new SpawnEntryFacts { Name = "Troll", Prefab = "Troll", Creature = "Troll", Biome = BlackForest };
            var deer = new SpawnEntryFacts { Name = "Deer", Prefab = "Deer", Creature = "Deer", Biome = Meadows | BlackForest };
            var answers = SpawnRules.ForSpot(new[] { boar, greyling, troll, neck, deer }, Spot(), out int off);
            Assert.Equal(new[] { "Deer", "Neck", "Greyling", "Boar" }, answers.Select(a => a.Label).ToArray());
            Assert.Equal(new[] { SpawnVerdict.Could, SpawnVerdict.Could, SpawnVerdict.NotNow, SpawnVerdict.No }, answers.Select(a => a.Verdict).ToArray());
            Assert.Equal(0, off);
        }

        [Fact]
        public void Switched_off_entries_are_counted_but_not_listed()
        {
            var off1 = Boar();
            off1.Enabled = false;
            var answers = SpawnRules.ForSpot(new[] { off1, Boar() }, Spot(), out int off);
            Assert.Single(answers);
            Assert.Equal(1, off);
        }

        [Fact]
        public void Two_entries_for_one_creature_carry_their_entry_names_and_raids_and_special_areas_say_so()
        {
            var day = new SpawnEntryFacts { Name = "Greydwarf day", Prefab = "Greydwarf", Creature = "Greydwarf", Biome = Meadows };
            var night = new SpawnEntryFacts { Name = "Greydwarf night", Prefab = "Greydwarf", Creature = "Greydwarf", Biome = Meadows, AtDay = false };
            var raid = new SpawnEntryFacts { Name = "Boar", Prefab = "Boar", Creature = "Boar", Biome = Meadows, List = "Eikthyr's army", Source = SpawnSource.Raid };
            var area = new SpawnEntryFacts { Name = "Ghost", Prefab = "Ghost", Creature = "Ghost", Biome = Meadows, List = "Haunted meadow", Source = SpawnSource.SpecialArea };
            var lone = new SpawnEntryFacts { Name = "Neck shore", Prefab = "Neck", Creature = "Neck", Biome = Meadows };
            var labels = SpawnRules.ForSpot(new[] { day, night, raid, area, lone }, Spot(), out _).Select(a => a.Label).ToArray();
            Assert.Contains("Neck", labels);
            Assert.Contains("Greydwarf · Greydwarf day", labels);
            Assert.Contains("Greydwarf · Greydwarf night", labels);
            Assert.Contains("Boar (raid: Eikthyr's army)", labels);
            Assert.Contains("Ghost (Haunted meadow)", labels);
        }

        [Fact]
        public void The_spot_reads_as_its_biome_height_slope_forest_base_and_sky()
        {
            var spot = Spot();
            spot.Ground = 34f;
            spot.NormalY = 0.9903f; // 8 degrees
            spot.InForest = true;
            spot.InsideBase = true;
            Assert.Equal("Meadows · 4 m above the sea · slope 8° · in a forest · inside a base · open sky", SpawnRules.SpotLine(spot));

            var edge = Spot();
            edge.Area = Edge;
            edge.Blocked = true;
            edge.OceanDepth = 2.34f;
            edge.OnLava = true;
            Assert.Equal("Meadows, where biomes meet · 10 m above the sea · slope 0° · water 2.3 m deep · on lava · no forest · outside bases · something solid above or below",
                SpawnRules.SpotLine(edge));
        }

        [Fact]
        public void Ground_that_is_not_loaded_lists_nothing_and_says_so()
        {
            var spot = new SpawnSpot { Biome = 0 };
            Assert.Empty(SpawnRules.ForSpot(new[] { Boar() }, spot, out _));
            Assert.Equal("the ground there is not loaded", SpawnRules.SpotLine(spot));
        }
    }
}
