using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The server wants proof on a real game that "Adventure Backpacks' buffs never stack" works, not
    /// only a rule: the self-test measures the game's own equipment totals three ways (without the
    /// pack's values, as counted before the patch, with the patch) and passes only when the pack was
    /// really counted on top of the cape or trinket and the patch took exactly its buffs back out.
    /// Nothing measured never reads as a pass.
    /// </summary>
    public sealed class PackTotalsTests
    {
        private static readonly string[] Fields = { "m_movementModifier", "m_attackStaminaModifier", "m_blockStaminaModifier", PackOverlap.AdrenalineField };

        /// <summary>Ashen cape (attack -0.1, block -0.2) and a Cinderward Pack (speed -0.15, attack -0.1, block -0.2), measured as the game would.</summary>
        private static PackTotals Cinderward(float[] fixedTotals = null, float[] counted = null) => new PackTotals(
            Fields,
            withoutPack: new[] { 0f, -0.1f, -0.2f, 0f },
            counted: counted ?? new[] { -0.15f, -0.2f, -0.4f, 0f },
            fixedTotals: fixedTotals ?? new[] { -0.15f, -0.1f, -0.2f, 0f },
            pack: new[] { -0.15f, -0.1f, -0.2f, 0f },
            capeWorn: true, trinketWorn: false);

        [Fact]
        public void A_cape_beside_the_Cinderward_Pack_passes_when_block_drops_from_doubled_to_the_capes_own()
        {
            var check = PackOverlap.TotalsCheck("Recorder", Cinderward());

            Assert.Equal(SelfTestReport.Result.Pass, check.Outcome);
            Assert.Contains("block stamina -40 % -> -20 %", check.Detail);
            Assert.Contains("attack stamina -20 % -> -10 %", check.Detail);
            Assert.DoesNotContain("movement", check.Detail);
        }

        [Fact]
        public void The_patch_leaving_the_doubled_total_in_place_fails()
        {
            var check = PackOverlap.TotalsCheck("Recorder", Cinderward(fixedTotals: new[] { -0.15f, -0.2f, -0.4f, 0f }));

            Assert.Equal(SelfTestReport.Result.Fail, check.Outcome);
            Assert.Contains("block stamina", check.Detail);
        }

        [Fact]
        public void The_patch_taking_away_the_packs_penalty_fails()
        {
            var check = PackOverlap.TotalsCheck("Recorder", Cinderward(fixedTotals: new[] { 0f, -0.1f, -0.2f, 0f }));

            Assert.Equal(SelfTestReport.Result.Fail, check.Outcome);
            Assert.Contains("movement", check.Detail);
        }

        [Fact]
        public void A_pack_the_slot_never_added_fails_as_the_patch_would_take_back_what_was_not_there()
        {
            // The slot mod no longer adds the pack: counted equals the total without it.
            var check = PackOverlap.TotalsCheck("Recorder", Cinderward(counted: new[] { 0f, -0.1f, -0.2f, 0f }, fixedTotals: new[] { 0f, 0f, 0f, 0f }));

            Assert.Equal(SelfTestReport.Result.Fail, check.Outcome);
            Assert.Contains("not counted", check.Detail);
        }

        [Fact]
        public void No_pack_in_the_slot_is_not_tested()
        {
            var check = PackOverlap.TotalsCheck("Recorder", null);

            Assert.Equal(SelfTestReport.Result.Info, check.Outcome);
            Assert.StartsWith("not tested", check.Detail);
        }

        [Fact]
        public void A_pack_without_a_cape_or_trinket_is_not_tested()
        {
            var t = new PackTotals(Fields, new[] { 0f, 0f, 0f, 0f }, new[] { -0.15f, -0.1f, -0.2f, 0f }, new[] { -0.15f, -0.1f, -0.2f, 0f },
                new[] { -0.15f, -0.1f, -0.2f, 0f }, capeWorn: false, trinketWorn: false);

            var check = PackOverlap.TotalsCheck("Recorder", t);

            Assert.Equal(SelfTestReport.Result.Info, check.Outcome);
            Assert.Equal("not tested: wear a cape or a trinket beside the pack", check.Detail);
        }

        [Fact]
        public void A_pack_with_no_buff_to_hold_back_is_not_tested_even_when_its_totals_add_up()
        {
            // A Meadows pack only slows its wearer: under a cape there is nothing to take back, so nothing is shown.
            var t = new PackTotals(Fields, new[] { 0f, -0.1f, -0.2f, 0f }, new[] { -0.15f, -0.1f, -0.2f, 0f }, new[] { -0.15f, -0.1f, -0.2f, 0f },
                new[] { -0.15f, 0f, 0f, 0f }, capeWorn: true, trinketWorn: false);

            var check = PackOverlap.TotalsCheck("Recorder", t);

            Assert.Equal(SelfTestReport.Result.Info, check.Outcome);
            Assert.StartsWith("not tested", check.Detail);
        }

        [Fact]
        public void A_trinket_beside_a_level_4_Frostbound_shows_the_bar_back_at_the_trinkets_size()
        {
            var t = new PackTotals(Fields, new[] { 0f, 0f, 0f, 100f }, new[] { -0.15f, 0f, 0f, 150f }, new[] { -0.15f, 0f, 0f, 100f },
                new[] { -0.15f, 0f, 0f, 50f }, capeWorn: false, trinketWorn: true);

            var check = PackOverlap.TotalsCheck("Recorder", t);

            Assert.Equal(SelfTestReport.Result.Pass, check.Outcome);
            Assert.Contains("adrenaline bar 150 -> 100", check.Detail);
        }

        [Fact]
        public void The_full_bar_effect_passes_only_when_hooked_hidden_during_and_put_back_after()
        {
            Assert.Equal(SelfTestReport.Result.Pass, PackOverlap.EffectCheck("Recorder", packHasEffect: true, trinketWorn: true, hooked: true, hiddenDuring: true, backAfter: true).Outcome);
            Assert.Equal(SelfTestReport.Result.Fail, PackOverlap.EffectCheck("Recorder", true, true, hooked: false, hiddenDuring: true, backAfter: true).Outcome);
            Assert.Equal(SelfTestReport.Result.Fail, PackOverlap.EffectCheck("Recorder", true, true, hooked: true, hiddenDuring: false, backAfter: true).Outcome);
            Assert.Equal(SelfTestReport.Result.Fail, PackOverlap.EffectCheck("Recorder", true, true, hooked: true, hiddenDuring: true, backAfter: false).Outcome);
        }

        [Fact]
        public void The_full_bar_effect_is_not_tested_without_a_trinket_or_without_an_effect_on_the_pack()
        {
            var noTrinket = PackOverlap.EffectCheck("Recorder", packHasEffect: true, trinketWorn: false, hooked: true, hiddenDuring: false, backAfter: true);
            var noEffect = PackOverlap.EffectCheck("Recorder", packHasEffect: false, trinketWorn: true, hooked: true, hiddenDuring: false, backAfter: true);

            Assert.Equal(SelfTestReport.Result.Info, noTrinket.Outcome);
            Assert.Equal(SelfTestReport.Result.Info, noEffect.Outcome);
            Assert.StartsWith("not tested", noTrinket.Detail);
        }
    }
}
