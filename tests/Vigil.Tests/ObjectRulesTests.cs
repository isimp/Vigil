using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Objects that follow rules of their own say why they work or not, by the game's rules: a
    /// fermenter (cover and roof, or the batch starts over), a beehive (little cover, its biome,
    /// daylight), a crop (biome, ground, sky, space, its own grow time), a smelter and a fire (fuel,
    /// roof, smoke), a creature being tamed or bred, and where the player stands for rest and comfort.
    /// </summary>
    public sealed class ObjectRulesTests
    {
        private static string Row(RulesPart p, string label) => p.Rows.Single(r => r.Label == label).Value;

        private static ResultTone Tone(RulesPart p, string label) => p.Rows.Single(r => r.Label == label).Tone;

        [Fact]
        public void A_covered_roofed_fermenter_says_how_far_its_batch_is()
        {
            var p = ObjectRules.Fermenting(new FermenterFacts { Content = "Mead base: Medium healing", State = FermenterState.Fermenting, Elapsed = 1512, Duration = 2400, Blocked = 13, Roof = true, Open = "E, NE, E 45°, NE 45°" });
            Assert.Equal("fermenting Mead base: Medium healing · 63 % · ready in 15 min", Row(p, "Now"));
            Assert.Equal(ResultTone.Good, Tone(p, "Now"));
            Assert.Equal("76 % (13 of 17 rays) · roof yes", Row(p, "Cover"));
            Assert.Equal("E, NE, E 45°, NE 45°", Row(p, "Open rays"));
        }

        [Fact]
        public void An_exposed_or_roofless_fermenter_says_the_batch_starts_over()
        {
            var exposed = ObjectRules.Fermenting(new FermenterFacts { Content = "Mead base", State = FermenterState.Fermenting, Elapsed = 5, Duration = 2400, Blocked = 11, Roof = true });
            Assert.Equal("starting over: cover 65 %, it needs 70 %; the game restarts the batch every 10 s until then", Row(exposed, "Now"));
            Assert.Equal(ResultTone.Bad, Tone(exposed, "Now"));
            var roofless = ObjectRules.Fermenting(new FermenterFacts { Content = "Mead base", State = FermenterState.Fermenting, Elapsed = 5, Duration = 2400, Blocked = 15, Roof = false });
            Assert.Equal("starting over: no roof; the game restarts the batch every 10 s until then", Row(roofless, "Now"));
            Assert.Equal("ready: Mead base", Row(ObjectRules.Fermenting(new FermenterFacts { Content = "Mead base", State = FermenterState.Ready, Blocked = 15, Roof = true }), "Now"));
            Assert.Equal("empty", Row(ObjectRules.Fermenting(new FermenterFacts { State = FermenterState.Empty, Blocked = 15, Roof = true }), "Now"));
        }

        [Fact]
        public void A_beehive_works_only_with_little_cover_in_its_biome_by_day_and_says_which_stops_it()
        {
            var ok = new BeehiveFacts { Blocked = 4, MaxCover = 0.25f, InBiome = true, Biome = "Meadows", DaylightOnly = true, Daylight = true, Honey = 2, MaxHoney = 4, SecondsPerHoney = 1200, Product = 840 };
            Assert.Equal("working · next honey in 6 min", Row(ObjectRules.Bees(ok), "Now"));
            Assert.Equal("24 % (4 of 17 rays) · needs under 25 % (at most 4)", Row(ObjectRules.Bees(ok), "Cover"));
            Assert.Equal("biome yes · daylight yes · honey 2 of 4, one every 20 min while working", Row(ObjectRules.Bees(ok), "Also"));

            ok.Blocked = 5;
            Assert.Equal("not working: too covered", Row(ObjectRules.Bees(ok), "Now"));
            Assert.Equal(ResultTone.Caution, Tone(ObjectRules.Bees(ok), "Now"));
            ok.Blocked = 2; ok.Daylight = false;
            Assert.Equal("resting: bees work in daylight only", Row(ObjectRules.Bees(ok), "Now"));
            ok.Daylight = true; ok.InBiome = false;
            Assert.Equal("not working: wrong biome, it needs Meadows", Row(ObjectRules.Bees(ok), "Now"));
            ok.InBiome = true; ok.Honey = 4;
            Assert.Equal("full: 4 of 4, take the honey", Row(ObjectRules.Bees(ok), "Now"));

            var night = new BeehiveFacts { Blocked = 2, MaxCover = 0.25f, InBiome = true, DaylightOnly = false, Daylight = false, Honey = 0, MaxHoney = 4, SecondsPerHoney = 600 };
            Assert.Equal("working · next honey in 10 min", Row(ObjectRules.Bees(night), "Now"));
            var exact = new BeehiveFacts { Blocked = 5, MaxCover = 5f / 17f, InBiome = true, Daylight = true, MaxHoney = 4, SecondsPerHoney = 600 };
            Assert.Equal("not working: too covered", Row(ObjectRules.Bees(exact), "Now"));
            Assert.EndsWith("(at most 4)", Row(ObjectRules.Bees(exact), "Cover"));
        }

        [Fact]
        public void A_crop_names_what_is_in_its_way_and_when_it_is_ready()
        {
            var p = ObjectRules.Growing(new PlantFacts
            {
                Status = "NoSpace", Kind = "Carrot", InWay = new List<string> { "Wood fence (0.4 m)", "Carrot, healthy (0.6 m)" }, GrowSeconds = 4320, MinSeconds = 4000, MaxSeconds = 5000, Since = 2040, DestroyIfCantGrow = true,
            });
            Assert.Equal("no space", Row(p, "Now"));
            Assert.Equal("Wood fence (0.4 m) · Carrot, healthy (0.6 m); unhealthy plants do not count", Row(p, "In its way"));
            Assert.Equal("open (nothing above within 100 m)", Row(p, "Sky"));
            Assert.Equal("in 38 min if it is healthy then; if not, it withers (this kind does) · this one needs 1 h 12 min, Carrot 1 h 6 min to 1 h 23 min", Row(p, "Ready"));
        }

        [Fact]
        public void A_healthy_crop_and_one_whose_sky_is_blocked_read_as_such()
        {
            var healthy = ObjectRules.Growing(new PlantFacts { Status = "Healthy", Kind = "Turnip", GrowSeconds = 3000, MinSeconds = 3000, MaxSeconds = 3000, Since = 3500 });
            Assert.Equal("healthy", Row(healthy, "Now"));
            Assert.Equal(ResultTone.Good, Tone(healthy, "Now"));
            Assert.Equal("now: it grows at the next check, within 10 s · this one needs 50 min, Turnip 50 min", Row(healthy, "Ready"));
            var young = ObjectRules.Growing(new PlantFacts { Status = "Healthy", Kind = "Turnip", GrowSeconds = 3000, MinSeconds = 3000, MaxSeconds = 3000, Since = 960 });
            Assert.Equal("in 34 min · this one needs 50 min, Turnip 50 min", Row(young, "Ready"));
            var dark = ObjectRules.Growing(new PlantFacts { Status = "NoSun", Kind = "Barley", SkyBlocker = "Wood roof 45° (3 m above)", GrowSeconds = 6000, MinSeconds = 6000, MaxSeconds = 6000, Since = 60 });
            Assert.Equal("no sun", Row(dark, "Now"));
            Assert.Equal("blocked by Wood roof 45° (3 m above)", Row(dark, "Sky"));
            Assert.Contains("if not, it waits until it is", Row(dark, "Ready"));
        }

        [Fact]
        public void Comfort_counts_the_best_piece_of_each_group_and_one_of_each_name_only_in_shelter()
        {
            var pieces = new List<ComfortPiece>
            {
                new ComfortPiece { Name = "Raven throne", Group = "Chair", GroupIndex = 4, Comfort = 3 },
                new ComfortPiece { Name = "Wood chair", Group = "Chair", GroupIndex = 4, Comfort = 1 },
                new ComfortPiece { Name = "Bed", Group = "Bed", GroupIndex = 2, Comfort = 1 },
                new ComfortPiece { Name = "Hearth", Group = "Fire", GroupIndex = 1, Comfort = 2 },
                new ComfortPiece { Name = "Trophy", Group = "None", GroupIndex = 0, Comfort = 1 },
                new ComfortPiece { Name = "Trophy", Group = "None", GroupIndex = 0, Comfort = 1 },
            };
            var groups = new[] { "Fire", "Bed", "Banner", "Chair", "Table", "Carpet" };
            var p = ObjectRules.Rested(new ComfortFacts { Blocked = 15, Roof = true, Pieces = pieces, Groups = groups });
            Assert.Equal("yes · cover 88 % (15 of 17) · roof yes", Row(p, "Shelter"));
            Assert.Equal("9 → rested 13 min", Row(p, "Comfort"));
            var adds = p.Table.GroupBy(r => r[0] + "|" + r[1]).ToDictionary(g => g.Key, g => g.First()[2]);
            Assert.Equal("3", adds["Raven throne|Chair"]);
            Assert.Equal("0 · not the best of its group", adds["Wood chair|Chair"]);
            Assert.Equal("1", adds["Trophy|None"]);
            Assert.Contains(p.Table, r => r[0] == "Trophy" && r[2] == "0 · the same piece counts once");
            Assert.Equal("Out of shelter comfort stays 1. Missing groups: Banner, Table, Carpet.", p.Note);
        }

        [Fact]
        public void Out_of_shelter_comfort_stays_one_and_says_why()
        {
            var p = ObjectRules.Rested(new ComfortFacts { Blocked = 12, Roof = true, Pieces = new List<ComfortPiece> { new ComfortPiece { Name = "Bed", Group = "Bed", GroupIndex = 2, Comfort = 1 } }, Groups = new[] { "Bed" } });
            Assert.Equal("no: cover 71 % (12 of 17), it needs 80 % and a roof", Row(p, "Shelter"));
            Assert.Equal(ResultTone.Caution, Tone(p, "Shelter"));
            Assert.Equal("1 → rested 5 min", Row(p, "Comfort"));
            Assert.Equal("no: cover 88 % (15 of 17), no roof", Row(ObjectRules.Rested(new ComfortFacts { Blocked = 15, Roof = false, Pieces = new List<ComfortPiece>(), Groups = new string[0] }), "Shelter"));
        }

        [Fact]
        public void A_smelter_says_what_stops_it_and_what_blocks_its_smoke()
        {
            var ok = new SmelterFacts { Fuel = 12, MaxFuel = 20, Queued = 4, MaxOre = 10 };
            Assert.Equal("working", Row(ObjectRules.Smelting(ok), "Now"));
            Assert.Equal("fuel 12 of 20 · ore 4 queued of 10", Row(ObjectRules.Smelting(ok), "Fill"));
            Assert.Equal("smoke gets out", Row(ObjectRules.Smelting(ok), "Smoke"));
            var blocked = new SmelterFacts { Fuel = 12, MaxFuel = 20, Queued = 4, MaxOre = 10, SmokeBlocked = true, VentBlocker = "Wood roof 45° (0.3 m)" };
            Assert.Equal("stopped: its smoke is blocked", Row(ObjectRules.Smelting(blocked), "Now"));
            Assert.Equal(ResultTone.Bad, Tone(ObjectRules.Smelting(blocked), "Now"));
            Assert.Equal("blocked at the vent by Wood roof 45° (0.3 m)", Row(ObjectRules.Smelting(blocked), "Smoke"));
            Assert.DoesNotContain(ObjectRules.Smelting(new SmelterFacts { Queued = 3, MaxOre = 50, HasSmoke = false }).Rows, r => r.Label == "Smoke");
            Assert.Equal("stopped: no fuel", Row(ObjectRules.Smelting(new SmelterFacts { Fuel = 0, MaxFuel = 20, Queued = 4, MaxOre = 10 }), "Now"));
            Assert.Equal("idle: nothing queued", Row(ObjectRules.Smelting(new SmelterFacts { Fuel = 5, MaxFuel = 20, Queued = 0, MaxOre = 10 }), "Now"));
            Assert.Equal("stopped: it needs a roof", Row(ObjectRules.Smelting(new SmelterFacts { Fuel = 5, MaxFuel = 20, Queued = 2, MaxOre = 10, RequiresRoof = true }), "Now"));
        }

        [Fact]
        public void A_wet_fire_keeps_burning_and_a_blocked_one_goes_out()
        {
            Assert.Equal("burning, low flame: rain and no roof (a wet fire keeps burning)", Row(ObjectRules.Burning(new FireFacts { Fuel = 3, MaxFuel = 10, WetReason = "rain and no roof" }), "Now"));
            Assert.Equal("out: terrain above it", Row(ObjectRules.Burning(new FireFacts { Fuel = 3, MaxFuel = 10, BlockedReason = "terrain above it" }), "Now"));
            Assert.Equal(ResultTone.Bad, Tone(ObjectRules.Burning(new FireFacts { Fuel = 3, MaxFuel = 10, BlockedReason = "terrain above it" }), "Now"));
            Assert.Equal("out: no fuel", Row(ObjectRules.Burning(new FireFacts { Fuel = 0, MaxFuel = 10 }), "Now"));
            Assert.Equal("burning", Row(ObjectRules.Burning(new FireFacts { Fuel = 0, MaxFuel = 10, InfiniteFuel = true }), "Now"));
            Assert.Equal("fuel 3 of 10", Row(ObjectRules.Burning(new FireFacts { Fuel = 3, MaxFuel = 10 }), "Fuel"));
        }

        [Fact]
        public void Taming_says_how_far_it_is_and_what_pauses_it()
        {
            Assert.Equal("64 % · 11 min left · going on", Row(ObjectRules.Taming(new TameFacts { Remaining = 648, Total = 1800 }), "Taming"));
            Assert.Equal("64 % · 11 min left · paused: hungry, feed it", Row(ObjectRules.Taming(new TameFacts { Remaining = 648, Total = 1800, Hungry = true }), "Taming"));
            Assert.Equal(ResultTone.Caution, Tone(ObjectRules.Taming(new TameFacts { Remaining = 648, Total = 1800, Hungry = true }), "Taming"));
            Assert.Equal("64 % · 11 min left · paused: alarmed, it calms only when nothing threatens it", Row(ObjectRules.Taming(new TameFacts { Remaining = 648, Total = 1800, Alerted = true }), "Taming"));
            Assert.Equal("tamed", Row(ObjectRules.Taming(new TameFacts { Tamed = true }), "Taming"));
        }

        [Fact]
        public void Breeding_names_the_first_thing_that_stops_it()
        {
            var b = new BreedFacts { Tamed = true, Love = 2, Required = 4, PartnerNear = true, Count = 4, Max = 4, SkipShare = 0.5f };
            Assert.Equal("none: 4 of 4 of its kind within 10 m, young included", Row(ObjectRules.Breeding(b), "Breeding"));
            Assert.Equal("partner within 3 m yes · love 2 of 4 · half the checks count, one every 10 to 15 s", Row(ObjectRules.Breeding(b), "Also"));
            b.Count = 2;
            Assert.Equal("trying: love 2 of 4", Row(ObjectRules.Breeding(b), "Breeding"));
            Assert.Equal(ResultTone.Good, Tone(ObjectRules.Breeding(b), "Breeding"));
            b.PartnerNear = false;
            Assert.Equal("none: no partner within 3 m", Row(ObjectRules.Breeding(b), "Breeding"));
            b.PartnerNear = true; b.Hungry = true;
            Assert.Equal("paused: hungry", Row(ObjectRules.Breeding(b), "Breeding"));
            b.Hungry = false; b.Pregnant = true; b.PregnantLeft = 180;
            Assert.Equal("expecting: born in 3 min", Row(ObjectRules.Breeding(b), "Breeding"));
            Assert.Equal("only tamed ones breed", Row(ObjectRules.Breeding(new BreedFacts { Tamed = false }), "Breeding"));
        }
    }
}
