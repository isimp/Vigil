using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants to know what holding a build tool and opening and closing its menu cost.
    /// Each frame in the world is put away, tool out or menu open; the frames where the menu
    /// opens or closes count as neither, since they carry the open or close itself. The session
    /// summary and a deep capture compare tool out with put away, and every open and close gets
    /// its own line that splits the frame into the menu's steps.
    /// </summary>
    public sealed class BuildToolTests
    {
        // ---- what a frame counts as

        [Fact]
        public void An_open_menu_wins_over_the_tool_in_hand()
        {
            Assert.Equal(BuildState.Menu, BuildTool.StateOf(placeMode: true, menuOpen: true, menuWasOpen: true));
            Assert.Equal(BuildState.Tool, BuildTool.StateOf(placeMode: true, menuOpen: false, menuWasOpen: false));
            Assert.Equal(BuildState.Away, BuildTool.StateOf(placeMode: false, menuOpen: false, menuWasOpen: false));
        }

        [Fact]
        public void The_frames_where_the_menu_opens_or_closes_count_as_neither()
        {
            Assert.Equal(BuildState.Change, BuildTool.StateOf(placeMode: true, menuOpen: true, menuWasOpen: false));
            Assert.Equal(BuildState.Change, BuildTool.StateOf(placeMode: true, menuOpen: false, menuWasOpen: true));
        }

        [Fact]
        public void A_second_says_menu_when_the_menu_was_open_in_any_frame_and_tool_when_the_tool_was_out_in_most()
        {
            Assert.Equal("menu", BuildTool.SecondLabel(toolFrames: 50, menuFrames: 1, frames: 60));
            Assert.Equal("tool", BuildTool.SecondLabel(toolFrames: 31, menuFrames: 0, frames: 60));
            Assert.Equal("", BuildTool.SecondLabel(toolFrames: 30, menuFrames: 0, frames: 60));
            Assert.Equal("", BuildTool.SecondLabel(0, 0, 0));
        }

        // ---- tool out against put away

        private static BuildCompare.Frames Frames(long away, double awayMs, long tool, double toolMs) =>
            new BuildCompare.Frames { AwayCount = away, AwayMs = away * awayMs, AwayRenderMs = away * 6.0, ToolCount = tool, ToolMs = tool * toolMs, ToolRenderMs = tool * 8.0 };

        [Fact]
        public void The_comparison_gives_the_mean_frame_in_both_and_the_difference()
        {
            var lines = BuildCompare.Lines(Frames(40000, 16.8, 8000, 21.9), new List<BuildCompare.Item>(), 10, 600);
            Assert.Contains("   frame: put away 16.8 ms (40,000 frames), tool out 21.9 ms (8,000 frames), +5.1 ms", lines);
            Assert.Contains("   drawing: put away 6.0 ms, tool out 8.0 ms", lines);
        }

        [Fact]
        public void The_timed_work_that_grows_most_with_the_tool_out_comes_first_and_work_that_does_not_grow_is_left_out()
        {
            var items = new List<BuildCompare.Item>
            {
                new BuildCompare.Item("Player.UpdatePlacement", awayMs: 0, toolMs: 8000 * 0.30),
                new BuildCompare.Item("Hud.Update", awayMs: 40000 * 0.31, toolMs: 8000 * 2.71),
                new BuildCompare.Item("ZNetScene.Update", awayMs: 40000 * 1.0, toolMs: 8000 * 1.01),
                new BuildCompare.Item("MonsterAI.UpdateAI", awayMs: 40000 * 0.5, toolMs: 8000 * 0.2),
            };
            var lines = BuildCompare.Lines(Frames(40000, 16.8, 8000, 21.9), items, 10, 600);
            int hud = lines.FindIndex(l => l.Contains("Hud.Update"));
            int placement = lines.FindIndex(l => l.Contains("Player.UpdatePlacement"));
            Assert.True(hud >= 0 && placement > hud);
            Assert.Equal("      +2.40 ms  Hud.Update   0.31 -> 2.71", lines[hud]);
            Assert.DoesNotContain(lines, l => l.Contains("ZNetScene.Update"));
            Assert.DoesNotContain(lines, l => l.Contains("MonsterAI.UpdateAI"));
        }

        [Fact]
        public void Only_the_given_number_of_growing_items_is_listed()
        {
            var items = Enumerable.Range(1, 30).Select(i => new BuildCompare.Item("M" + i, 0, 8000 * 0.1 * i)).ToList();
            var lines = BuildCompare.Lines(Frames(40000, 16.8, 8000, 21.9), items, 5, 600);
            Assert.Equal(5, lines.Count(l => l.StartsWith("      +")));
            Assert.Contains(lines, l => l.Contains("M30"));
            Assert.DoesNotContain(lines, l => l.EndsWith("M25   0.00 -> 2.50"));
        }

        [Fact]
        public void Too_few_frames_on_either_side_says_so_instead_of_comparing()
        {
            var lines = BuildCompare.Lines(Frames(40000, 16.8, 120, 21.9), new List<BuildCompare.Item> { new BuildCompare.Item("Hud.Update", 1, 500) }, 10, 600);
            Assert.Equal(new[] { "   too few frames to compare: put away 40,000, tool out 120 (each side needs 600)" }, lines);
        }

        [Fact]
        public void Nothing_growing_says_so()
        {
            var lines = BuildCompare.Lines(Frames(40000, 16.8, 8000, 16.8), new List<BuildCompare.Item> { new BuildCompare.Item("Hud.Update", 40000, 8000) }, 10, 600);
            Assert.Contains("   no timed work grows by 0.05 ms or more with the tool out", lines);
        }

        // ---- one open of the menu

        [Fact]
        public void An_open_splits_its_frame_into_the_menus_steps()
        {
            var open = new MenuOpen
            {
                FrameMs = 412, ToggleMs = 380, OpenMs = 350, TagsMs = 140, PiecesMs = 130, Buttons = 1166, SetupMs = 110, ChecksMs = 60, LayoutMs = 25, NextLayoutMs = 12, Handlers = 61206,
            };
            Assert.Equal(
                "opened, frame 412 ms: 1,166 piece buttons 130 ms (setting them up 110 ms, material checks in that 60 ms), tag buttons 10 ms, " +
                "the rest of the open 210 ms, mods' hooks around the key 30 ms, screen layout 25 ms, untimed 7 ms; next frame's layout 12 ms; 61,206 language handlers",
                open.Describe());
        }

        [Fact]
        public void An_open_without_the_key_leaves_out_the_keys_hooks()
        {
            var open = new MenuOpen { FrameMs = 200, ToggleMs = 0, OpenMs = 150, TagsMs = 100, PiecesMs = 100, Buttons = 500, SetupMs = 80, ChecksMs = 40, LayoutMs = 10, NextLayoutMs = 0, Handlers = -1 };
            string s = open.Describe();
            Assert.DoesNotContain("around the key", s);
            Assert.DoesNotContain("language handlers", s);
            Assert.Contains("the rest of the open 50 ms", s);
            Assert.Contains("untimed 40 ms", s);
        }

        [Fact]
        public void Timer_noise_never_shows_a_negative_step()
        {
            var open = new MenuOpen { FrameMs = 100, ToggleMs = 99, OpenMs = 100, TagsMs = 99, PiecesMs = 100, Buttons = 10, SetupMs = 101, ChecksMs = 102, LayoutMs = 5 };
            Assert.DoesNotContain("-", open.Describe().Replace("-frame", ""));
        }

        // ---- one close of the menu

        [Fact]
        public void A_close_by_picking_a_piece_names_the_pick_and_the_new_ghost()
        {
            var close = new MenuClose { FrameMs = 35, CloseMs = 0.4, Picked = true, PickMs = 21, GhostMs = 18, LayoutMs = 2, NextFrameMs = 19 };
            Assert.Equal("closed by picking a piece, frame 35 ms: picking it 21 ms (the placement ghost 18 ms), closing 0.4 ms, screen layout 2 ms; next frame 19 ms", close.Describe());
        }

        [Fact]
        public void A_close_by_the_key_says_so_and_gives_no_pick()
        {
            var close = new MenuClose { FrameMs = 18, CloseMs = 0.3, Picked = false, LayoutMs = 1, NextFrameMs = 17 };
            Assert.Equal("closed by the key or another way, frame 18 ms: closing 0.3 ms, screen layout 1 ms; next frame 17 ms", close.Describe());
        }

        // ---- opens over the session

        [Fact]
        public void The_session_sums_up_the_opens_by_their_middle_and_worst_frame()
        {
            var t = new MenuTally();
            foreach (var ms in new[] { 180.0, 190, 380, 420, 185 }) t.Add(ms, 1166);
            Assert.Equal("5 opens: middle 190 ms, worst 420 ms, up to 1,166 piece buttons", t.Describe());
            Assert.Equal("none", new MenuTally().Describe());
        }

        [Fact]
        public void With_an_even_count_the_middle_is_the_lower_one_and_the_buttons_are_the_most_any_open_had()
        {
            var t = new MenuTally();
            t.Add(100, 400);
            t.Add(400, 1166);
            t.Add(200, 300);
            t.Add(300, 20);
            Assert.Equal("4 opens: middle 200 ms, worst 400 ms, up to 1,166 piece buttons", t.Describe());
        }
    }
}
