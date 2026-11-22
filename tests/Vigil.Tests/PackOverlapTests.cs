using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "Adventure Backpacks' buffs never stack": with Extra Slots Custom Slots a backpack has
    /// its own slot, so a player can wear a cape too. Some packs carry a cape's buffs (the Cinderward
    /// Pack the Ashen cape's block and attack stamina, the Frostbound Haversack the Deep North cape's
    /// run and attack stamina) and its level 4 an adrenaline bar with its own full-bar effect, as a
    /// trinket has. The server wants no buff counted twice: while a cape is worn, the pack's buffs do
    /// not count; while a trinket is worn, the pack's adrenaline does not count and its full-bar effect
    /// stays quiet. The pack's penalties always count, and a pack worn alone works as built.
    /// </summary>
    public sealed class PackOverlapTests
    {
        private static readonly string[] StaminaKinds =
        {
            "m_homeItemsStaminaModifier", "m_jumpStaminaModifier", "m_attackStaminaModifier", "m_blockStaminaModifier",
            "m_dodgeStaminaModifier", "m_swimStaminaModifier", "m_sneakStaminaModifier", "m_runStaminaModifier",
        };

        [Fact]
        public void The_Cinderward_Packs_block_and_attack_stamina_do_not_count_while_a_cape_is_worn()
        {
            Assert.Equal(-0.2f, PackOverlap.TakeBack("m_blockStaminaModifier", -0.2f, capeWorn: true, trinketWorn: false));
            Assert.Equal(-0.1f, PackOverlap.TakeBack("m_attackStaminaModifier", -0.1f, capeWorn: true, trinketWorn: false));
        }

        [Fact]
        public void A_pack_worn_without_a_cape_keeps_all_its_buffs()
        {
            Assert.Equal(0f, PackOverlap.TakeBack("m_blockStaminaModifier", -0.2f, capeWorn: false, trinketWorn: false));
            Assert.Equal(0f, PackOverlap.TakeBack("m_runStaminaModifier", -0.2f, capeWorn: false, trinketWorn: true));
            Assert.Equal(0f, PackOverlap.TakeBack("m_heatResistanceModifier", 0.1f, capeWorn: false, trinketWorn: false));
        }

        [Fact]
        public void Every_kind_of_stamina_saving_from_the_pack_stops_counting_under_a_cape()
        {
            foreach (string kind in StaminaKinds)
            {
                Assert.Equal(-0.2f, PackOverlap.TakeBack(kind, -0.2f, capeWorn: true, trinketWorn: false));
                // A pack that made this kind dearer keeps doing so under a cape.
                Assert.Equal(0f, PackOverlap.TakeBack(kind, 0.1f, capeWorn: true, trinketWorn: false));
            }
        }

        [Fact]
        public void The_packs_penalties_count_even_while_a_cape_is_worn()
        {
            // The packs slow their wearer; a cape must never take that away.
            Assert.Equal(0f, PackOverlap.TakeBack("m_movementModifier", -0.15f, capeWorn: true, trinketWorn: true));
            Assert.Equal(0f, PackOverlap.TakeBack("m_blockStaminaModifier", 0.1f, capeWorn: true, trinketWorn: false));
            Assert.Equal(0f, PackOverlap.TakeBack("m_heatResistanceModifier", -0.1f, capeWorn: true, trinketWorn: false));
        }

        [Fact]
        public void More_speed_or_heat_resistance_from_the_pack_does_not_count_under_a_cape()
        {
            Assert.Equal(0.05f, PackOverlap.TakeBack("m_movementModifier", 0.05f, capeWorn: true, trinketWorn: false));
            Assert.Equal(0.1f, PackOverlap.TakeBack("m_heatResistanceModifier", 0.1f, capeWorn: true, trinketWorn: false));
        }

        [Fact]
        public void The_packs_adrenaline_does_not_count_while_a_trinket_is_worn_and_counts_without_one()
        {
            Assert.Equal(50f, PackOverlap.TakeBack(PackOverlap.AdrenalineField, 50f, capeWorn: false, trinketWorn: true));
            Assert.Equal(0f, PackOverlap.TakeBack(PackOverlap.AdrenalineField, 50f, capeWorn: true, trinketWorn: false));
            Assert.Equal(0f, PackOverlap.TakeBack(PackOverlap.AdrenalineField, 50f, capeWorn: false, trinketWorn: false));
        }

        [Fact]
        public void The_packs_full_bar_effect_stays_quiet_only_while_a_trinket_is_worn()
        {
            Assert.False(PackOverlap.PackEffectFires(trinketWorn: true));
            Assert.True(PackOverlap.PackEffectFires(trinketWorn: false));
        }

        [Fact]
        public void A_stat_a_game_update_adds_does_not_count_from_the_pack_under_a_cape_either_way()
        {
            // Not knowing whether more of it helps, the pack's whole value is left out rather than risk a buff twice.
            Assert.Equal(0.3f, PackOverlap.TakeBack("m_someNewModifier", 0.3f, capeWorn: true, trinketWorn: false));
            Assert.Equal(-0.3f, PackOverlap.TakeBack("m_someNewModifier", -0.3f, capeWorn: true, trinketWorn: false));
            Assert.Equal(0f, PackOverlap.TakeBack("m_someNewModifier", 0.3f, capeWorn: false, trinketWorn: true));
        }

        [Fact]
        public void A_value_of_nothing_takes_nothing_back()
        {
            Assert.Equal(0f, PackOverlap.TakeBack("m_blockStaminaModifier", 0f, capeWorn: true, trinketWorn: true));
            Assert.Equal(0f, PackOverlap.TakeBack("m_someNewModifier", 0f, capeWorn: true, trinketWorn: true));
        }

        [Fact]
        public void The_state_says_off_working_stepped_aside_or_not_needed()
        {
            Assert.Equal("off: the server's switch (Patches.AdventureBackpacksBuffsOnce)", PackOverlap.StateLine(false, true, "1.0.23", null));
            Assert.Equal("not needed: no backpack slot of Extra Slots Custom Slots", PackOverlap.StateLine(true, false, null, null));
            Assert.Equal("stepped aside: its backpack slot code changed (an update)", PackOverlap.StateLine(true, true, "1.0.24", "its backpack slot code changed (an update)"));
            Assert.Equal("working · Extra Slots Custom Slots 1.0.23 · a backpack's buffs do not add to a worn cape's or trinket's", PackOverlap.StateLine(true, true, "1.0.23", null));
        }
    }
}
