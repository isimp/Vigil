using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// When a direction falls behind while neither machine is busy, the report names the part of the
    /// connection that was full, from Steam's own numbers on the sending machine: its send rate (and
    /// whether that rate is only the configured floor), the game's world-update batch limit, or the
    /// line itself. Without those numbers it says it cannot tell. The player's frame rate is only
    /// compared between steps in which the game was in the same state (focused, same frame cap), and
    /// a slow step that other programs caused says so.
    /// </summary>
    public sealed class LoadLinkVerdictTests
    {
        private static int StageOf(LoadPlan plan, string name) => plan.Stages.First(s => s.Name == name).Index;

        // One value per stage and measurement, the same for ten steady seconds; a later Set replaces it.
        private sealed class Values
        {
            public readonly Dictionary<(int, string), double> Map = new Dictionary<(int, string), double>();

            public LoadLedger Ledger()
            {
                var l = new LoadLedger();
                foreach (var kv in Map)
                    for (int s = 0; s < 10; s++) l.Add(kv.Key.Item1, s, kv.Key.Item2, kv.Value);
                return l;
            }
        }

        // Every non-recovery stage with the values a calm session shows.
        private static Values Calm(LoadPlan plan)
        {
            var v = new Values();
            foreach (var st in plan.Stages.Where(s => s.Kind != LoadPlan.Kind.Recovery))
            {
                v.Map[(st.Index, st.Direction == "down" ? LoadMetrics.DownRecv : LoadMetrics.UpRecv)] = st.RateKbps;
                v.Map[(st.Index, st.Direction == "down" ? LoadMetrics.DownStale : LoadMetrics.UpStale)] = 0.2;
                v.Map[(st.Index, LoadMetrics.ServerFrame)] = 33.3;
                v.Map[(st.Index, LoadMetrics.ServerFrameMax)] = 34;
                v.Map[(st.Index, LoadMetrics.ClientFrame)] = 22;
                v.Map[(st.Index, LoadMetrics.ClientFps)] = 45;
            }
            return v;
        }

        private static void Set(Values l, LoadPlan plan, string stage, string metric, double v) => l.Map[(StageOf(plan, stage), metric)] = v;

        // Steam's view on the sending machine ("cli" for up, "srv" for down) for every second of a stage.
        private static void Link(Values l, LoadPlan plan, string stage, string side, double outKbs, double rateKbs, double floorKbs,
            double pendingKb, double unackedKb, double queueKb, double pingMs, double batchKb = 32)
        {
            Set(l, plan, stage, side + "_out_kbs", outKbs);
            Set(l, plan, stage, side + "_rate_kbs", rateKbs);
            Set(l, plan, stage, side + "_floor_kbs", floorKbs);
            Set(l, plan, stage, side + "_pending_kb", pendingKb);
            Set(l, plan, stage, side + "_unacked_kb", unackedKb);
            Set(l, plan, stage, side + "_queue_kb", queueKb);
            Set(l, plan, stage, side + "_ping_ms", pingMs);
            Set(l, plan, stage, side + "_batch_kb", batchKb);
        }

        private static string Text(LoadPlan plan, Values l) => string.Join("\n", LoadVerdict.Judge(plan, l.Ledger(), null));

        // ------------------------------------------------------------------ what was full

        [Fact]
        public void An_upload_held_at_the_floor_setting_names_the_floor_as_the_limit_and_the_games_own_traffic()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            Link(l, plan, "baseline", "cli", outKbs: 125, rateKbs: 150, floorKbs: 150, pendingKb: 2, unackedKb: 4, queueKb: 9, pingMs: 30);
            foreach (var st in new[] { "up 100 kB/s", "up 200 kB/s", "up 400 kB/s", "up 800 kB/s" })
            {
                Set(l, plan, st, LoadMetrics.UpRecv, 80);
                Link(l, plan, st, "cli", outKbs: 150, rateKbs: 150, floorKbs: 150, pendingKb: 25, unackedKb: 6, queueKb: 32, pingMs: 30);
            }
            string t = Text(plan, l);
            Assert.Contains("kept up through 50 kB/s", t);
            Assert.Contains("Steam's send rate on your game was the limit", t);
            Assert.Contains("Send Rate Floor", t);
            Assert.Contains("Before the test your game already sent 125 kB/s", t);
            Assert.DoesNotContain("line was full", t);
            Assert.DoesNotContain("batch limit", t);
        }

        [Fact]
        public void A_full_send_rate_that_Steam_chose_above_the_floor_is_not_blamed_on_the_floor()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            Link(l, plan, "baseline", "cli", outKbs: 60, rateKbs: 400, floorKbs: 150, pendingKb: 0, unackedKb: 2, queueKb: 2, pingMs: 30);
            Set(l, plan, "up 800 kB/s", LoadMetrics.UpRecv, 330);
            Link(l, plan, "up 800 kB/s", "cli", outKbs: 396, rateKbs: 400, floorKbs: 150, pendingKb: 20, unackedKb: 8, queueKb: 28, pingMs: 31);
            string t = Text(plan, l);
            Assert.Contains("Steam's send rate on your game was the limit", t);
            Assert.DoesNotContain("Send Rate Floor", t);
        }

        [Fact]
        public void A_full_batch_window_on_a_calm_line_names_the_batch_limit_not_the_line()
        {
            var plan = LoadPlan.For("down");
            var l = Calm(plan);
            Link(l, plan, "baseline", "srv", outKbs: 5, rateKbs: 48828, floorKbs: 48828, pendingKb: 0, unackedKb: 0.2, queueKb: 0.2, pingMs: 30);
            Set(l, plan, "down 800 kB/s", LoadMetrics.DownRecv, 406);
            Link(l, plan, "down 800 kB/s", "srv", outKbs: 409, rateKbs: 48828, floorKbs: 48828, pendingKb: 1, unackedKb: 30, queueKb: 31.6, pingMs: 31);
            string t = Text(plan, l);
            Assert.Contains("batch limit on the server was the limit", t);
            Assert.Contains("32 kB", t);
            Assert.Contains("Batch Size", t);
            Assert.DoesNotContain("line was full", t);
            Assert.DoesNotContain("send rate on the server was the limit", t);
        }

        [Fact]
        public void A_line_ping_that_climbs_under_the_load_names_the_line()
        {
            var plan = LoadPlan.For("down");
            var l = Calm(plan);
            Link(l, plan, "baseline", "srv", outKbs: 5, rateKbs: 48828, floorKbs: 48828, pendingKb: 0, unackedKb: 0.2, queueKb: 0.2, pingMs: 30);
            Set(l, plan, "down 800 kB/s", LoadMetrics.DownRecv, 300);
            Link(l, plan, "down 800 kB/s", "srv", outKbs: 300, rateKbs: 48828, floorKbs: 48828, pendingKb: 0.5, unackedKb: 20, queueKb: 21, pingMs: 140);
            string t = Text(plan, l);
            Assert.Contains("The line was full", t);
            Assert.Contains("30 to 140 ms", t);
            Assert.DoesNotContain("batch limit", t);
        }

        [Fact]
        public void Without_Steams_numbers_the_verdict_says_it_cannot_tell_which_part_was_full()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            Set(l, plan, "up 800 kB/s", LoadMetrics.UpRecv, 150);
            Set(l, plan, "up 800 kB/s", LoadMetrics.ClientQueue, 32);
            string t = Text(plan, l);
            Assert.Contains("cannot tell", t);
            Assert.DoesNotContain("send rate on your game was the limit", t);
            Assert.DoesNotContain("The line was full", t);
            Assert.DoesNotContain("batch limit on", t);
        }

        [Fact]
        public void A_busy_server_is_still_named_before_any_part_of_the_connection()
        {
            var plan = LoadPlan.For("down");
            var l = Calm(plan);
            Link(l, plan, "baseline", "srv", outKbs: 5, rateKbs: 48828, floorKbs: 48828, pendingKb: 0, unackedKb: 0.2, queueKb: 0.2, pingMs: 30);
            Set(l, plan, "down 800 kB/s", LoadMetrics.DownRecv, 300);
            Set(l, plan, "down 800 kB/s", LoadMetrics.ServerFrame, 70);
            Link(l, plan, "down 800 kB/s", "srv", outKbs: 300, rateKbs: 48828, floorKbs: 48828, pendingKb: 0, unackedKb: 8, queueKb: 8, pingMs: 31);
            string t = Text(plan, l);
            Assert.Contains("server's main thread", t);
            Assert.DoesNotContain("batch limit", t);
        }

        // ------------------------------------------------------------------ the player's frame rate

        private static void View(Values l, LoadPlan plan, string stage, bool focused, double cap, double fps)
        {
            Set(l, plan, stage, LoadMetrics.ClientFocused, focused ? 1 : 0);
            Set(l, plan, stage, LoadMetrics.ClientFrameCap, cap);
            Set(l, plan, stage, LoadMetrics.ClientFps, fps);
            Set(l, plan, stage, LoadMetrics.ClientFrame, 1000 / fps);
        }

        [Fact]
        public void A_game_sent_to_the_background_is_not_compared_with_its_focused_baseline()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            View(l, plan, "baseline", true, 61, 45);
            foreach (var st in plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load)) View(l, plan, st.Name, false, 30, 29);
            Set(l, plan, "up 800 kB/s", LoadMetrics.UpRecv, 150);
            string t = Text(plan, l);
            Assert.Contains("in the background", t);
            Assert.DoesNotContain("slowest step", t);
            Assert.DoesNotContain("Your game was the limit", t);
        }

        [Fact]
        public void A_game_in_the_background_at_the_same_frame_cap_is_still_set_apart()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            View(l, plan, "baseline", true, 61, 45);
            foreach (var st in plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load)) View(l, plan, st.Name, false, 61, 40);
            Assert.Contains("in the background during 5 steps", Text(plan, l));
        }

        [Fact]
        public void A_changed_frame_cap_in_front_is_set_apart_as_another_frame_cap()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            View(l, plan, "baseline", true, 61, 45);
            foreach (var st in plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load)) View(l, plan, st.Name, true, 30, 29);
            string t = Text(plan, l);
            Assert.Contains("at another frame cap during 5 steps (frame cap 30 fps there, 61 at baseline)", t);
            Assert.DoesNotContain("slowest step", t);
        }

        [Fact]
        public void Steps_in_the_same_state_as_the_baseline_are_still_compared()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            View(l, plan, "baseline", true, 61, 45);
            View(l, plan, "up 50 kB/s", true, 61, 44);
            View(l, plan, "up 100 kB/s", true, 61, 30);
            View(l, plan, "up 200 kB/s", true, 61, 43);
            View(l, plan, "up 400 kB/s", false, 30, 20);
            View(l, plan, "up 800 kB/s", false, 30, 20);
            string t = Text(plan, l);
            Assert.Contains("30 fps at its slowest step (up 100 kB/s)", t);
            Assert.Contains("2 steps", t);
            Assert.Contains("in the background", t);
        }

        [Fact]
        public void A_slow_step_while_other_programs_kept_the_computer_busy_says_so()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            View(l, plan, "baseline", true, 61, 45);
            foreach (var st in plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load)) View(l, plan, st.Name, true, 61, 44);
            View(l, plan, "up 200 kB/s", true, 61, 19);
            Set(l, plan, "up 200 kB/s", LoadMetrics.ClientCpu, 20);
            Set(l, plan, "up 200 kB/s", LoadMetrics.ClientMachineCpu, 87);
            Set(l, plan, "up 200 kB/s", LoadMetrics.ClientMemory, 96);
            string t = Text(plan, l);
            Assert.Contains("19 fps at its slowest step (up 200 kB/s)", t);
            Assert.Contains("other programs", t);
            Assert.Contains("87%", t);
            Assert.Contains("96%", t);
        }

        [Fact]
        public void A_slow_step_on_a_quiet_computer_does_not_blame_other_programs()
        {
            var plan = LoadPlan.For("up");
            var l = Calm(plan);
            View(l, plan, "baseline", true, 61, 45);
            foreach (var st in plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load)) View(l, plan, st.Name, true, 61, 44);
            View(l, plan, "up 200 kB/s", true, 61, 19);
            Set(l, plan, "up 200 kB/s", LoadMetrics.ClientCpu, 30);
            Set(l, plan, "up 200 kB/s", LoadMetrics.ClientMachineCpu, 45);
            Set(l, plan, "up 200 kB/s", LoadMetrics.ClientMemory, 70);
            Assert.DoesNotContain("other programs", Text(plan, l));
        }
    }
}
