using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// What Vigil itself costs, in one place: its own loops every frame, its hooks inside the game's
    /// methods (timed for a few seconds now and then, the timers' own cost taken off), its timers on
    /// the mods' callbacks, its work on other threads, the files it writes and its own network
    /// messages; the panel's extra while it is drawn; memory, which the game's runtime cannot tell
    /// per mod, said to be not measured.
    /// </summary>
    public sealed class OwnCostTests
    {
        private static HookMeasure Measured() => new HookMeasure
        {
            Seconds = 10, Frames = 200, MinutesAgo = 2,
            Hooks = new List<HookCost>
            {
                new HookCost("TextInput.IsVisible", 40000, 0.4), new HookCost("ZRpc.Invoke", 900, 6.0),
                new HookCost("RoutedRPCData.Serialize", 300, 2.0), new HookCost("ZDO.Serialize", 1200, 8.0),
            },
        };

        private static OwnCostFacts Facts(HookMeasure hooks = null) => new OwnCostFacts
        {
            FrameMs = 50, LoopsMs = 0.28, TimerCallsPerFrame = 250, TimerPairMs = 0.0002, Hooks = hooks,
            PanelOpenFrames = 0, ThreadSecondsPerMinute = 1.2, FilesKbPerMinute = 410, NetOutKbs = 0.3, NetInKbs = 0.1,
        };

        [Fact]
        public void The_frame_line_adds_its_loops_hooks_and_timers_and_says_its_share_of_the_frame()
        {
            var f = Facts(Measured());
            Assert.Equal(0.412, OwnCost.TotalMs(f), 3);
            Assert.Equal("0.41 ms per frame, 0.8 % of yours: its loops 0.28 · its hooks in the game 0.08 · its timers 0.05", OwnCost.FrameLine(f));
            Assert.Equal("0.41 ms per frame", OwnCost.Badge(f));
        }

        [Fact]
        public void Before_the_timers_are_calibrated_they_count_as_nothing_rather_than_spoiling_the_sum()
        {
            var f = Facts();
            f.TimerPairMs = double.NaN;
            Assert.Equal(0.28, OwnCost.TotalMs(f), 6);
            Assert.DoesNotContain("NaN", OwnCost.FrameLine(f));
        }

        [Fact]
        public void Before_its_hooks_are_measured_the_frame_line_says_so()
        {
            Assert.Equal("0.33 ms per frame so far, 0.7 % of yours: its loops 0.28 · its timers 0.05 · its hooks not measured yet", OwnCost.FrameLine(Facts()));
        }

        [Fact]
        public void The_hooks_line_names_the_costliest_and_when_they_were_measured()
        {
            Assert.Equal("4 hooks, 4,240 calls/s: ZDO.Serialize 0.04 · ZRpc.Invoke 0.03 · RoutedRPCData.Serialize 0.01 ms per frame; measured 2 min ago over 10 s", OwnCost.HooksLine(Measured()));
            Assert.Equal("not measured yet: Measure times them for 10 s (it also runs once on its own, 2 minutes into a session)", OwnCost.HooksLine(null));
        }

        [Fact]
        public void A_hooks_time_has_the_timers_own_cost_taken_off_and_never_goes_below_nothing()
        {
            Assert.Equal(4.0, OwnCost.Net(measuredMs: 6.0, calls: 1000, perCallMs: 0.002), 6);
            Assert.Equal(0.0, OwnCost.Net(measuredMs: 1.0, calls: 1000, perCallMs: 0.002), 6);
        }

        [Fact]
        public void The_panel_line_gives_its_extra_per_drawn_frame_or_says_it_was_not_open()
        {
            var f = Facts();
            Assert.Equal("the panel was not open", OwnCost.PanelLine(f));
            f.PanelOpenFrames = 300;
            f.PanelMs = 300;
            Assert.Equal("+1.00 ms per frame while the panel is drawn", OwnCost.PanelLine(f));
        }

        [Fact]
        public void Tools_are_shown_apart_per_frame_while_they_ran()
        {
            var f = Facts(Measured());
            f.Tools = new List<ToolCost> { new ToolCost("self-test", ms: 4092, frames: 660, seconds: 41, runs: 2), new ToolCost("deep capture", ms: 3610, frames: 1900, seconds: 95, runs: 1) };
            f.ReportCount = 7;
            f.ReportMs = 126;
            Assert.Equal("while they ran: self-test 6.2 ms per frame over 41 s (2 runs) · deep capture 1.9 ms per frame over 95 s · reports 18 ms each, 7 written", OwnCost.ToolsLine(f));
        }

        [Fact]
        public void With_no_tool_run_the_tools_line_says_so()
        {
            Assert.Equal("none ran this session", OwnCost.ToolsLine(Facts()));
            var started = Facts();
            started.Tools = new List<ToolCost> { new ToolCost("deep capture", ms: 0, frames: 0, seconds: 0, runs: 1) };
            Assert.Equal("none ran this session", OwnCost.ToolsLine(started));
        }

        [Fact]
        public void The_session_line_adds_the_tools_over_all_frames_to_the_steady_cost()
        {
            var f = Facts(Measured());
            f.Tools = new List<ToolCost> { new ToolCost("self-test", ms: 1000, frames: 100, seconds: 5, runs: 1) };
            f.ReportCount = 2;
            f.ReportMs = 40;
            f.FramesAll = 10400;
            f.SessionMinutes = 14;
            Assert.Equal(0.412 + 1040.0 / 10400, OwnCost.SessionMs(f), 6);
            Assert.Equal("0.51 ms per frame with the tools, over 14 min", OwnCost.SessionLine(f));
            Assert.Equal("0.41 ms per frame", OwnCost.Badge(f));
        }

        [Fact]
        public void Threads_files_network_and_memory_read_as_mocked()
        {
            var f = Facts();
            Assert.Equal("1.2 s per minute (reports, names), not on the game's frame", OwnCost.ThreadsLine(f));
            Assert.Equal("410 kB per minute into its recordings", OwnCost.FilesLine(f));
            Assert.Equal("0.3 kB/s out, 0.1 kB/s in (its hello, alerts, reports and shared files)", OwnCost.NetworkLine(f));
            Assert.Equal("not measured: the game's runtime does not tell memory per mod", OwnCost.MemoryLine);
        }
    }
}
