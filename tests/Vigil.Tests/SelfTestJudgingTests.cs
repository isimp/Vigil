using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The self-test checks Vigil's own readings against the game's on the player's game: the cover
    /// rays against the game's cover, a beehive's space, a crop's sky and space, a smelter's vent and
    /// the comfort level against what the game decides; the server's weather tried on this game for a
    /// few seconds (another weather than the one now, an hour held a little earlier); and
    /// OdinOnDemand's level against the game's master and music volume, with a music volume tried
    /// that differs from the player's.
    /// </summary>
    public sealed class SelfTestJudgingTests
    {
        [Fact]
        public void Vigils_cover_rays_agree_with_the_games_cover_when_they_count_the_same_rays()
        {
            Assert.Null(RuleAgreement.Cover(12, 12f / 17f));
            Assert.Null(RuleAgreement.Cover(0, 0f));
            Assert.Equal("Vigil's rays find 12 of 17 blocked, the game's cover is 11 of 17", RuleAgreement.Cover(12, 11f / 17f));
        }

        [Fact]
        public void A_beehives_space_agrees_when_vigil_and_the_game_say_the_same()
        {
            Assert.Null(RuleAgreement.Bees(4, 0.25f, gameHasSpace: true));
            Assert.Null(RuleAgreement.Bees(5, 0.25f, gameHasSpace: false));
            Assert.Null(RuleAgreement.Bees(4, 4f / 17f, gameHasSpace: false));
            Assert.Null(RuleAgreement.Bees(17, 0f, gameHasSpace: true));
            Assert.Equal("Vigil says it has space (4 of 17 blocked, under 25 %), the game says it lacks it", RuleAgreement.Bees(4, 0.25f, gameHasSpace: false));
            Assert.Equal("Vigil says it lacks space (5 of 17 blocked, 25 % or more), the game says it has it", RuleAgreement.Bees(5, 0.25f, gameHasSpace: true));
        }

        [Fact]
        public void A_crops_status_agrees_when_vigil_finds_what_the_game_complains_of()
        {
            Assert.Null(RuleAgreement.Crop("NoSun", skyBlocked: true, inWay: false));
            Assert.Null(RuleAgreement.Crop("NoSpace", skyBlocked: false, inWay: true));
            Assert.Null(RuleAgreement.Crop("Healthy", skyBlocked: false, inWay: false));
            Assert.Null(RuleAgreement.Crop("WrongBiome", skyBlocked: true, inWay: true));
            Assert.Equal("the game says no sun, Vigil's ray up finds nothing", RuleAgreement.Crop("NoSun", false, false));
            Assert.Equal("the game says no space, Vigil finds nothing in the way", RuleAgreement.Crop("NoSpace", false, false));
            Assert.Equal("the game says healthy, Vigil's ray up is blocked", RuleAgreement.Crop("Healthy", true, false));
            Assert.Equal("the game says healthy, Vigil finds something in the way", RuleAgreement.Crop("Healthy", false, true));
        }

        [Fact]
        public void A_blocked_smelter_agrees_when_vigil_names_what_blocks_its_vent()
        {
            Assert.Null(RuleAgreement.Smoke(false, null));
            Assert.Null(RuleAgreement.Smoke(true, "Wood beam"));
            Assert.Equal("the game says its smoke is blocked, Vigil names nothing", RuleAgreement.Smoke(true, null));
        }

        [Fact]
        public void The_comfort_level_agrees_when_both_count_the_same()
        {
            Assert.Null(RuleAgreement.Comfort(9, 9));
            Assert.Equal("Vigil counts 9, the game 8", RuleAgreement.Comfort(9, 8));
        }

        [Fact]
        public void The_check_passes_when_all_agree_says_so_when_nothing_was_near_and_lists_the_ones_that_do_not()
        {
            var agree = new List<RuleAgreement.Finding>
            {
                new RuleAgreement.Finding("cover", "Fermenter", null, hard: true), new RuleAgreement.Finding("crop", "Carrot", null, hard: false),
                new RuleAgreement.Finding("crop", "Turnip", null, hard: false),
            };
            Assert.Equal((SelfTestReport.Result.Pass, "all 3 agree: cover 1, crop 2"), RuleAgreement.Verdict(agree, 30));
            Assert.Equal((SelfTestReport.Result.Info, "no fermenter, beehive, crop, smelter or fire within 30 m"), RuleAgreement.Verdict(new List<RuleAgreement.Finding>(), 30));

            agree.Add(new RuleAgreement.Finding("crop", "Onion", "the game says no sun, Vigil's ray up finds nothing", hard: false));
            Assert.Equal((SelfTestReport.Result.Warn, "1 of 4 disagree (the game checks crops and smoke only every few seconds): crop Onion: the game says no sun, Vigil's ray up finds nothing"),
                RuleAgreement.Verdict(agree, 30));
            agree.Add(new RuleAgreement.Finding("cover", "Beehive", "Vigil's rays find 3 of 17 blocked, the game's cover is 2 of 17", hard: true));
            Assert.Equal(SelfTestReport.Result.Fail, RuleAgreement.Verdict(agree, 30).Item1);
        }

        [Fact]
        public void A_weather_tried_on_this_game_is_another_than_now_preferring_a_storm()
        {
            var known = new[] { "Clear", "Rain", "ThunderStorm", "Misty" };
            Assert.Equal("ThunderStorm", WeatherTrial.Pick(known, "Clear"));
            Assert.Equal("Rain", WeatherTrial.Pick(known, "ThunderStorm"));
            Assert.Equal("Clear", WeatherTrial.Pick(new[] { "Misty", "Clear" }, "Misty"));
            Assert.Null(WeatherTrial.Pick(new[] { "Clear" }, "Clear"));
        }

        [Fact]
        public void A_frozen_hour_tried_on_this_game_is_an_hour_earlier_and_read_back_across_midnight()
        {
            Assert.Equal(950.0, WeatherTrial.HeldSecond(1000, 1200), 3);
            Assert.True(DayClock.Near(0.999f, 0.001f, 0.005f));
            Assert.True(DayClock.Near(0.5f, 0.503f, 0.005f));
            Assert.False(DayClock.Near(0.5f, 0.51f, 0.005f));
        }

        [Fact]
        public void A_music_volume_tried_differs_from_the_players_and_the_mixer_is_judged_against_master_times_music()
        {
            Assert.Equal(0.45f, OdinVolume.TrialMusic(0.7f), 3);
            Assert.Equal(0.55f, OdinVolume.TrialMusic(0.3f), 3);
            Assert.Equal(0.75f, OdinVolume.TrialMusic(1.4f), 3);
            Assert.True(OdinVolume.MixerAgrees(OdinVolume.Db(0.8f, 0.5f) + 0.04f, 0.8f, 0.5f));
            Assert.False(OdinVolume.MixerAgrees(OdinVolume.Db(0.8f, 0.5f) + 0.2f, 0.8f, 0.5f));
        }
    }
}
