using System;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The game patch on CycleChests: the mouse wheel over a chest's grid that has a scroll bar
    /// scrolls the grid; anywhere else it cycles chests as CycleChests does.
    /// </summary>
    public sealed class ChestScrollTests
    {
        [Fact]
        public void The_wheel_over_a_chest_grid_with_a_bar_scrolls_the_grid()
        {
            Assert.True(ChestScroll.KeepsWheel(on: true, wheelTurned: true, containerOpen: true, overGrid: true, hasBar: true));
        }

        [Fact]
        public void The_wheel_cycles_chests_off_the_grid_without_a_bar_with_no_chest_open_or_with_the_switch_off()
        {
            Assert.False(ChestScroll.KeepsWheel(on: true, wheelTurned: true, containerOpen: true, overGrid: false, hasBar: true));
            Assert.False(ChestScroll.KeepsWheel(on: true, wheelTurned: true, containerOpen: true, overGrid: true, hasBar: false));
            Assert.False(ChestScroll.KeepsWheel(on: true, wheelTurned: true, containerOpen: false, overGrid: true, hasBar: true));
            Assert.False(ChestScroll.KeepsWheel(on: false, wheelTurned: true, containerOpen: true, overGrid: true, hasBar: true));
        }

        [Fact]
        public void Frames_without_a_wheel_turn_reach_CycleChests_so_its_arrow_keys_and_its_wheel_memory_keep_working()
        {
            // CycleChests cycles only when the wheel's direction differs from the one it remembers, and
            // forgets it only in frames it sees without a turn; a frame kept from it freezes that memory.
            Assert.False(ChestScroll.KeepsWheel(on: true, wheelTurned: false, containerOpen: true, overGrid: true, hasBar: true));
        }

        [Fact]
        public void The_trace_writes_one_line_for_each_burst_of_wheel_turns()
        {
            Assert.True(ChestScroll.StartsBurst(lastTurnAt: 10.0, now: 10.5));
            Assert.False(ChestScroll.StartsBurst(lastTurnAt: 10.0, now: 10.1));
        }

        [Fact]
        public void The_trace_line_names_the_pointer_the_grid_the_bar_and_the_decision()
        {
            Assert.Equal("wheel at 812,430 · in grid yes · on bar no · slots 960 px, view 560 px (bar) · CycleChests skipped · chest piece_chest",
                ChestScroll.TraceLine(812.4f, 430f, inGrid: true, onBar: false, slotsHeight: 960f, viewHeight: 560f, kept: true, chest: "piece_chest"));
            Assert.Equal("wheel at 100,50 · in grid no · on bar no · slots 300 px, view 560 px (no bar) · CycleChests ran · chest piece_chest_wood",
                ChestScroll.TraceLine(100f, 50f, inGrid: false, onBar: false, slotsHeight: 300f, viewHeight: 560f, kept: false, chest: "piece_chest_wood"));
        }

        [Fact]
        public void The_frame_after_says_whether_the_chest_changed_and_the_grid_scrolled()
        {
            Assert.Equal("a frame later: same chest · scroll 1.00 → 0.86", ChestScroll.AfterLine(sameChest: true, before: 1f, after: 0.86f));
            Assert.Equal("a frame later: another chest · scroll 1.00 → 1.00 (unmoved)", ChestScroll.AfterLine(sameChest: false, before: 1f, after: 1f));
        }

        [Fact]
        public void A_grid_has_a_bar_when_its_slots_are_taller_than_its_view_as_the_game_lays_it_out()
        {
            Assert.True(ChestScroll.HasBar(slotsHeight: 700f, viewHeight: 480f));
            Assert.False(ChestScroll.HasBar(slotsHeight: 480f, viewHeight: 480f));
            Assert.False(ChestScroll.HasBar(slotsHeight: 300f, viewHeight: 480f));
        }

        [Fact]
        public void The_state_says_what_the_patch_does_on_this_game()
        {
            Assert.Equal("off: this game's switch (Patches.ChestGridScroll)", ChestScroll.StateLine(on: false, found: true, version: "1.0.1", problem: null, kept: 0));
            Assert.Equal("not needed: CycleChests is not installed", ChestScroll.StateLine(on: true, found: false, version: null, problem: null, kept: 0));
            Assert.Equal("stepped aside: its wheel handling changed (an update)", ChestScroll.StateLine(on: true, found: true, version: "1.0.1", problem: "its wheel handling changed (an update)", kept: 0));
            Assert.Equal("working · CycleChests 1.0.1 · the wheel over a chest grid with a bar scrolls it", ChestScroll.StateLine(on: true, found: true, version: "1.0.1", problem: null, kept: 0));
            Assert.Equal("working · CycleChests 1.0.1 · the wheel over a chest grid with a bar scrolls it · 12 wheel turns kept in a chest", ChestScroll.StateLine(on: true, found: true, version: "1.0.1", problem: null, kept: 12));
        }

        [Fact]
        public void CycleChests_101s_input_step_is_found()
        {
            var step = ChestScroll.FindInputStep(name => name == ChestScroll.TypeName ? typeof(Cc101.CycleChests) : null);

            Assert.NotNull(step);
            Assert.Equal("Prefix", step.Name);
            Assert.Equal("Player_Update_Patch", step.DeclaringType.Name);
        }

        [Fact]
        public void Another_shape_or_no_CycleChests_is_not_taken()
        {
            Assert.Null(ChestScroll.FindInputStep(name => null));
            Assert.Null(ChestScroll.FindInputStep(name => name == ChestScroll.TypeName ? typeof(Other.CycleChests) : null));
            Assert.Null(ChestScroll.FindInputStep(name => name == ChestScroll.TypeName ? typeof(Returns.CycleChests) : null));
        }

        // Stand-ins with the shapes of CycleChests 1.0.1's members (the game's type as a plain class).
        private sealed class Player { }

        private static class Cc101
        {
            public class CycleChests
            {
                public static class Player_Update_Patch
                {
                    private static void Prefix(Player __instance) { }
                }
            }
        }

        // The input step under another name, or deciding whether the game's update runs.
        private static class Other
        {
            public class CycleChests
            {
                public static class Player_Update_Patch
                {
                    private static void Postfix(Player __instance) { }
                }
            }
        }

        private static class Returns
        {
            public class CycleChests
            {
                public static class Player_Update_Patch
                {
                    private static bool Prefix(Player __instance) => true;
                }
            }
        }
    }
}
