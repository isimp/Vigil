using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "Summon Commander: the next summon from a radial": with the Dead Raiser or the
    /// Spirit Caller in hand, the staff's secondary attack (none in the game) opens the game's radial
    /// menu with what the next summon can be, and the pick is written to Summon Commander's own
    /// settings, which decide what the staff summons.
    /// </summary>
    public sealed class SummonChoiceTests
    {
        private static readonly string[] AllBow = { "Bow", "Bow", "Bow", "Bow" };

        private static SummonOpen Ready(SummonStaff staff) => new SummonOpen
        {
            SwitchOn = true,
            Ready = true,
            Staff = staff,
            TakesInput = true,
            InRadial = false,
            Building = false,
            Pressed = true,
        };

        [Fact]
        public void The_Dead_Raiser_offers_sword_and_shield_bow_or_any_weapon()
        {
            Assert.Equal(SummonStaff.DeadRaiser, SummonChoice.StaffOf("StaffSkeleton"));
            var options = SummonChoice.Options(SummonStaff.DeadRaiser);
            Assert.Equal(new[] { "SwordAndShield", "Bow", "Random" }, options.Select(o => o.Value));
            Assert.All(options, o => Assert.False(string.IsNullOrEmpty(o.Label)));
            Assert.All(options, o => Assert.False(string.IsNullOrEmpty(o.Icon)));
        }

        [Fact]
        public void The_Spirit_Caller_offers_each_species_or_any()
        {
            Assert.Equal(SummonStaff.SpiritCaller, SummonChoice.StaffOf("StaffSpiritCaller"));
            var options = SummonChoice.Options(SummonStaff.SpiritCaller);
            Assert.Equal(new[] { "Wolf", "Boar", "Moose", "Bear", "Random" }, options.Select(o => o.Value));
            Assert.Equal(new[] { "TrophyWolf", "TrophyBoar", "TrophyMoose", "TrophyBjorn", "StaffSpiritCaller" }, options.Select(o => o.Icon));
        }

        [Fact]
        public void Other_staffs_and_weapons_offer_nothing()
        {
            foreach (var name in new[] { "StaffRedTroll", "StaffFireball", "StaffShield", "SwordIron", "", null })
            {
                Assert.Equal(SummonStaff.None, SummonChoice.StaffOf(name));
                Assert.Empty(SummonChoice.Options(SummonChoice.StaffOf(name)));
            }
        }

        [Fact]
        public void Picking_a_species_sets_Summon_Commanders_next_spirit_and_nothing_else()
        {
            var writes = SummonChoice.Picks(SummonStaff.SpiritCaller, "Moose");
            var w = Assert.Single(writes);
            Assert.Equal(("Summon", "NextSpirit", "Moose"), (w.Section, w.Key, w.Value));
        }

        [Fact]
        public void Picking_a_weapon_gives_every_skeleton_slot_that_weapon_so_the_next_skeleton_has_it()
        {
            var writes = SummonChoice.Picks(SummonStaff.DeadRaiser, "Bow");
            Assert.Contains(writes, w => w.Section == "SkeletonTeam" && w.Key == "Enabled" && w.Value == "true");
            for (int i = 1; i <= SummonChoice.SkeletonSlots; i++)
                Assert.Contains(writes, w => w.Section == "SkeletonTeam" && w.Key == "Slot" + i && w.Value == "Bow");
            Assert.Equal(SummonChoice.SkeletonSlots + 1, writes.Count);
            Assert.DoesNotContain(writes, w => w.Key == "NextSpirit");
        }

        [Fact]
        public void A_pick_the_staff_does_not_offer_writes_nothing()
        {
            Assert.Empty(SummonChoice.Picks(SummonStaff.DeadRaiser, "Wolf"));
            Assert.Empty(SummonChoice.Picks(SummonStaff.SpiritCaller, "Bow"));
            Assert.Empty(SummonChoice.Picks(SummonStaff.None, "Random"));
        }

        [Fact]
        public void The_radial_opens_on_what_the_next_summon_is_now()
        {
            Assert.Equal(2, SummonChoice.Current(SummonStaff.SpiritCaller, "Moose", true, AllBow));
            Assert.Equal(4, SummonChoice.Current(SummonStaff.SpiritCaller, "Random", true, AllBow));
            Assert.Equal(1, SummonChoice.Current(SummonStaff.DeadRaiser, "Wolf", true, AllBow));
            Assert.Equal(0, SummonChoice.Current(SummonStaff.DeadRaiser, "Wolf", true, new[] { "SwordAndShield", "SwordAndShield", "SwordAndShield", "SwordAndShield" }));
        }

        [Fact]
        public void A_switched_off_team_means_any_weapon_and_a_mixed_team_has_no_single_choice()
        {
            Assert.Equal(2, SummonChoice.Current(SummonStaff.DeadRaiser, "Random", false, AllBow));
            Assert.Equal(-1, SummonChoice.Current(SummonStaff.DeadRaiser, "Random", true, new[] { "SwordAndShield", "Bow", "SwordAndShield", "Bow" }));
            Assert.Equal(-1, SummonChoice.Current(SummonStaff.DeadRaiser, "Random", true, new string[0]));
            Assert.Equal(-1, SummonChoice.Current(SummonStaff.SpiritCaller, "Dragon", true, AllBow));
            Assert.Equal(-1, SummonChoice.Current(SummonStaff.None, "Wolf", true, AllBow));
        }

        [Fact]
        public void The_secondary_attack_opens_the_radial_with_a_summon_staff_in_hand()
        {
            Assert.True(SummonChoice.ShouldOpen(Ready(SummonStaff.DeadRaiser)));
            Assert.True(SummonChoice.ShouldOpen(Ready(SummonStaff.SpiritCaller)));
        }

        [Fact]
        public void The_radial_stays_shut_whenever_one_condition_is_missing()
        {
            var off = Ready(SummonStaff.SpiritCaller); off.SwitchOn = false;
            var notReady = Ready(SummonStaff.SpiritCaller); notReady.Ready = false;
            var otherItem = Ready(SummonStaff.None);
            var typing = Ready(SummonStaff.SpiritCaller); typing.TakesInput = false;
            var radial = Ready(SummonStaff.SpiritCaller); radial.InRadial = true;
            var building = Ready(SummonStaff.SpiritCaller); building.Building = true;
            var notPressed = Ready(SummonStaff.SpiritCaller); notPressed.Pressed = false;
            foreach (var o in new[] { off, notReady, otherItem, typing, radial, building, notPressed })
                Assert.False(SummonChoice.ShouldOpen(o));
        }

        [Fact]
        public void A_pick_is_confirmed_with_the_staffs_summon_and_the_choice()
        {
            var bow = SummonChoice.Options(SummonStaff.DeadRaiser)[1];
            Assert.Equal("Next skeleton: Bow", SummonChoice.Message(SummonStaff.DeadRaiser, bow));
            var any = SummonChoice.Options(SummonStaff.SpiritCaller).Last();
            Assert.Equal("Next spirit: any species", SummonChoice.Message(SummonStaff.SpiritCaller, any));
        }

        [Fact]
        public void The_state_line_says_what_the_patch_does_on_this_game()
        {
            Assert.StartsWith("off", SummonChoice.StateLine(false, true, "1.0.1", null), System.StringComparison.Ordinal);
            Assert.StartsWith("not needed", SummonChoice.StateLine(true, false, null, null), System.StringComparison.Ordinal);
            Assert.Equal("stepped aside: its code changed", SummonChoice.StateLine(true, true, "1.0.2", "its code changed"));
            string working = SummonChoice.StateLine(true, true, "1.0.1", null);
            Assert.StartsWith("working", working, System.StringComparison.Ordinal);
            Assert.Contains("1.0.1", working, System.StringComparison.Ordinal);
        }
    }
}
