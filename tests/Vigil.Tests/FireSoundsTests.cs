using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Fire sounds: a player's own level for the loops of fires, applied to the fires they chose, and
    /// the game's sound rules given to a mod's fire whose loop came without them (the game's fires
    /// play their loop through a sound component that picks the level within a range, fades it in
    /// and lets the game keep only the nearest few of a kind audible).
    /// </summary>
    public sealed class FireSoundsTests
    {
        [Fact]
        public void The_level_is_kept_between_nothing_and_full()
        {
            Assert.Equal(0, FireSounds.ClampPercent(-5));
            Assert.Equal(100, FireSounds.ClampPercent(140));
            Assert.Equal(40, FireSounds.ClampPercent(40));
            Assert.Equal(100, FireSounds.DefaultPercent);
        }

        [Fact]
        public void The_level_reaches_only_the_fires_the_player_chose()
        {
            Assert.Equal(0.4f, FireSounds.Modifier(40, FireSounds.Scope.WithoutRules, hadRules: false), 3);
            Assert.Equal(1f, FireSounds.Modifier(40, FireSounds.Scope.WithoutRules, hadRules: true), 3);
            Assert.Equal(0.4f, FireSounds.Modifier(40, FireSounds.Scope.All, hadRules: true), 3);
            Assert.Equal(0.4f, FireSounds.Modifier(40, FireSounds.Scope.All, hadRules: false), 3);
            Assert.Equal(1f, FireSounds.Modifier(100, FireSounds.Scope.All, hadRules: false), 3);
            Assert.Equal(0f, FireSounds.Modifier(-10, FireSounds.Scope.All, hadRules: false), 3);
        }

        [Fact]
        public void A_fire_that_came_without_rules_counts_as_without_them_even_once_it_got_them()
        {
            // The scope asks what the fire's mod made, not what Vigil did to it, so the switch does not move a fire out of the slider's reach.
            Assert.Equal(0.4f, FireSounds.Modifier(40, FireSounds.Scope.WithoutRules, hadRules: false), 3);
        }

        [Fact]
        public void The_level_reads_as_a_percentage()
        {
            Assert.Equal("40 %", FireSounds.PercentLabel(40));
            Assert.Equal("100 %", FireSounds.PercentLabel(100));
            Assert.Equal("Off", FireSounds.PercentLabel(0));
        }

        [Fact]
        public void The_rules_given_are_the_games_hearths()
        {
            Assert.Equal(0.4f, FireSounds.HearthRules.MinVol);
            Assert.Equal(0.6f, FireSounds.HearthRules.MaxVol);
            Assert.Equal(0.1f, FireSounds.HearthRules.FadeInSeconds);
            Assert.Equal(2, FireSounds.HearthRules.NearestKept);
            Assert.True(FireSounds.HearthRules.DistanceReverb);
        }

        [Fact]
        public void Each_kind_of_fire_gets_one_hash_of_its_own_for_the_games_cap()
        {
            Assert.Equal(FireSounds.KindHash("Hearth_2x3_bal"), FireSounds.KindHash("Hearth_2x3_bal"));
            Assert.NotEqual(FireSounds.KindHash("Hearth_2x3_bal"), FireSounds.KindHash("Hearth_1x3_bal"));
            Assert.NotEqual(0, FireSounds.KindHash("Hearth_2x3_bal"));
            Assert.NotEqual(0, FireSounds.KindHash(""));
            Assert.NotEqual(0, FireSounds.KindHash(null));
        }

        [Fact]
        public void The_near_line_counts_the_loops_and_names_the_kinds_without_rules()
        {
            var loops = new[]
            {
                new FireSounds.LoopFacts("Hearth_2x3_bal", hadRules: false, onMixer: true),
                new FireSounds.LoopFacts("Hearth_2x3_bal", hadRules: false, onMixer: true),
                new FireSounds.LoopFacts("Hearth_1x3_bal", hadRules: false, onMixer: true),
                new FireSounds.LoopFacts("fire_pit", hadRules: true, onMixer: true),
                new FireSounds.LoopFacts("hearth", hadRules: true, onMixer: true),
            };
            Assert.Equal("5 fire loops, 3 without the game's rules (Hearth_2x3_bal ×2, Hearth_1x3_bal)", FireSounds.NearLine(loops, 20f));
        }

        [Fact]
        public void The_near_line_says_when_a_loop_is_off_the_games_mixer()
        {
            var loops = new[]
            {
                new FireSounds.LoopFacts("Hearth_2x3_bal", hadRules: false, onMixer: false),
                new FireSounds.LoopFacts("fire_pit", hadRules: true, onMixer: true),
            };
            Assert.Equal("2 fire loops, 1 without the game's rules (Hearth_2x3_bal), 1 off the game's sound mixer, so the game's SFX volume does not reach it", FireSounds.NearLine(loops, 20f));
        }

        [Fact]
        public void The_near_line_handles_none_one_and_all_with_rules()
        {
            Assert.Equal("no fire loops within 20 m", FireSounds.NearLine(Enumerable.Empty<FireSounds.LoopFacts>(), 20f));
            Assert.Equal("1 fire loop, all with the game's rules", FireSounds.NearLine(new[] { new FireSounds.LoopFacts("fire_pit", hadRules: true, onMixer: true) }, 20f));
            Assert.Equal("2 fire loops, all with the game's rules", FireSounds.NearLine(new[] { new FireSounds.LoopFacts("fire_pit", hadRules: true, onMixer: true), new FireSounds.LoopFacts("hearth", hadRules: true, onMixer: true) }, 20f));
        }

        [Fact]
        public void The_rules_line_says_what_the_switch_did_this_session()
        {
            Assert.Equal("off: a mod's fire plays its loop as the mod made it", FireSounds.RulesLine(on: false, given: 3, problem: null));
            Assert.Equal("on: no fire loop without the game's rules loaded yet", FireSounds.RulesLine(on: true, given: 0, problem: null));
            Assert.Equal("on: 1 fire loop given the game's rules this session", FireSounds.RulesLine(on: true, given: 1, problem: null));
            Assert.Equal("on: 3 fire loops given the game's rules this session", FireSounds.RulesLine(on: true, given: 3, problem: null));
            Assert.Equal("stepped aside: the fire's loading could not be hooked", FireSounds.RulesLine(on: true, given: 3, problem: "the fire's loading could not be hooked"));
        }

        [Fact]
        public void A_loops_level_is_judged_against_what_the_rules_call_for()
        {
            // A loop under the game's rules: its modifier must be the player's; a plain loop: its volume must be its own times the player's.
            Assert.True(FireSounds.LevelAgrees(0.4f, 0.4f));
            Assert.False(FireSounds.LevelAgrees(0.4f, 1f));
            Assert.True(FireSounds.LevelAgrees(0.57f * 0.4f, 0.228f));
        }

        [Fact]
        public void The_badge_shows_a_level_other_than_full()
        {
            Assert.Equal("40 %", FireSounds.Badge(40, rulesOn: true));
            Assert.Equal("Off", FireSounds.Badge(0, rulesOn: false));
            Assert.Equal("rules on", FireSounds.Badge(100, rulesOn: true));
            Assert.Null(FireSounds.Badge(100, rulesOn: false));
        }
    }
}
