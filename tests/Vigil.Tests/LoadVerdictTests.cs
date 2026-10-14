using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// After a load test the report says how much traffic each direction carried before falling
    /// behind, what held it back (the server's main thread, the player's game, or the connection),
    /// how long the server needed to catch up once the load stopped, and how much frame time the
    /// server had left.
    /// </summary>
    public sealed class LoadVerdictTests
    {
        private static readonly LoadPlan Plan = LoadPlan.For("up");

        private static int StageOf(string name) => Plan.Stages.First(s => s.Name == name).Index;

        // Ten steady seconds per stage, with the values a calm session shows unless overridden.
        private static void Fill(LoadLedger l, string stage, double recvKbs = double.NaN, double stale = 0.2,
            double serverFrame = 20, double serverMax = 30, double clientFrame = 16, double clientQueue = 1, double clientFps = 60)
        {
            int i = StageOf(stage);
            for (int s = 0; s < 10; s++)
            {
                l.Add(i, s, LoadMetrics.UpRecv, double.IsNaN(recvKbs) ? Plan.Stages[i].RateKbps : recvKbs);
                l.Add(i, s, LoadMetrics.UpStale, stale);
                l.Add(i, s, LoadMetrics.ServerFrame, serverFrame);
                l.Add(i, s, LoadMetrics.ServerFrameMax, serverMax);
                l.Add(i, s, LoadMetrics.ClientFrame, clientFrame);
                l.Add(i, s, LoadMetrics.ClientQueue, clientQueue);
                l.Add(i, s, LoadMetrics.ClientFps, clientFps);
            }
        }

        private static LoadLedger Calm(params string[] except)
        {
            var l = new LoadLedger();
            foreach (var s in Plan.Stages.Where(s => s.Kind != LoadPlan.Kind.Recovery && !except.Contains(s.Name))) Fill(l, s.Name);
            return l;
        }

        private static string Text(LoadLedger l, string abort = null) => string.Join("\n", LoadVerdict.Judge(Plan, l, abort));

        [Fact]
        public void A_connection_that_fills_up_while_both_machines_idle_is_named_as_the_limit()
        {
            var l = Calm("up 200 kB/s", "up 400 kB/s", "up 800 kB/s");
            Fill(l, "up 200 kB/s", recvKbs: 150, stale: 3, clientQueue: 9);
            Fill(l, "up 400 kB/s", recvKbs: 152, stale: 6, clientQueue: 9);
            Fill(l, "up 800 kB/s", recvKbs: 151, stale: 9, clientQueue: 9);
            string t = Text(l);
            Assert.Contains("kept up through 100 kB/s", t);
            Assert.Contains("At 200 kB/s", t);
            Assert.Contains("connection", t);
            Assert.Contains("152 kB/s", t);
        }

        [Fact]
        public void A_server_frame_time_that_climbs_with_the_load_names_the_server()
        {
            var l = Calm("up 400 kB/s");
            Fill(l, "up 400 kB/s", recvKbs: 300, stale: 4, serverFrame: 60, serverMax: 140);
            string t = Text(l);
            Assert.Contains("kept up through 200 kB/s", t);
            Assert.Contains("server's main thread", t);
            Assert.DoesNotContain("connection", t);
        }

        [Fact]
        public void A_players_game_that_slows_down_is_named()
        {
            var l = Calm("up 800 kB/s");
            Fill(l, "up 800 kB/s", recvKbs: 500, stale: 4, clientFrame: 70, clientFps: 14);
            Assert.Contains("Your game was the limit", Text(l));
        }

        [Fact]
        public void Keeping_up_at_every_step_says_so()
        {
            Assert.Contains("kept up through 800 kB/s, the highest step", Text(Calm()));
        }

        [Fact]
        public void Falling_behind_at_the_first_step_says_so()
        {
            var l = Calm("up 50 kB/s");
            Fill(l, "up 50 kB/s", recvKbs: 20, stale: 5);
            Assert.Contains("did not keep up even at 50 kB/s", Text(l));
        }

        [Fact]
        public void The_catch_up_time_is_how_long_the_updates_stayed_old_after_the_load_stopped()
        {
            var l = Calm();
            int rec = StageOf("up recovery");
            double[] stale = { 8, 5, 2, 0.3, 0.2, 0.2, 0.2 };
            for (int s = 0; s < stale.Length; s++) l.Add(rec, s, LoadMetrics.UpStale, stale[s]);
            Assert.Contains("caught up 3 s after the load stopped", Text(l));
        }

        [Fact]
        public void Updates_still_old_at_the_end_of_the_recovery_say_it_had_not_caught_up()
        {
            var l = Calm();
            int rec = StageOf("up recovery");
            for (int s = 0; s < 30; s++) l.Add(rec, s, LoadMetrics.UpStale, 6);
            Assert.Contains("had not caught up after 30 s", Text(l));
        }

        [Fact]
        public void Updates_fresh_right_after_the_load_say_there_was_nothing_to_catch_up()
        {
            var l = Calm();
            int rec = StageOf("up recovery");
            for (int s = 0; s < 10; s++) l.Add(rec, s, LoadMetrics.UpStale, 0.2);
            Assert.Contains("nothing to catch up", Text(l));
        }

        [Fact]
        public void The_servers_frame_time_is_compared_with_its_baseline_and_its_budget()
        {
            var l = Calm("up 800 kB/s");
            Fill(l, "up 800 kB/s", serverFrame: 28, serverMax: 90);
            string t = Text(l);
            Assert.Contains("20.0 ms at baseline", t);
            Assert.Contains("28.0 ms", t);
            Assert.Contains("33 ms", t);
        }

        [Fact]
        public void Less_than_85_percent_getting_through_is_falling_behind_even_with_fresh_copies()
        {
            var l = Calm("up 200 kB/s");
            Fill(l, "up 200 kB/s", recvKbs: 150, stale: 0.2);
            Assert.Contains("kept up through 100 kB/s", Text(l));
        }

        [Fact]
        public void Copies_that_grow_old_count_as_falling_behind_even_when_the_bytes_get_through()
        {
            var l = Calm("up 400 kB/s");
            Fill(l, "up 400 kB/s", stale: 4);
            Assert.Contains("kept up through 200 kB/s", Text(l));
        }

        [Fact]
        public void A_server_frame_time_half_again_above_its_baseline_already_names_the_server()
        {
            var l = Calm("up 200 kB/s");
            Fill(l, "up 200 kB/s", recvKbs: 100, serverFrame: 35);
            Assert.Contains("server's main thread", Text(l));
        }

        [Fact]
        public void A_players_frame_time_half_again_above_its_baseline_already_names_the_game()
        {
            var l = Calm("up 200 kB/s");
            Fill(l, "up 200 kB/s", recvKbs: 100, clientFrame: 30);
            Assert.Contains("Your game was the limit", Text(l));
        }

        [Fact]
        public void One_fresh_second_between_old_ones_is_not_caught_up_yet()
        {
            var l = Calm();
            int rec = StageOf("up recovery");
            double[] stale = { 8, 0.5, 3, 0.3, 0.2, 0.2 };
            for (int s = 0; s < stale.Length; s++) l.Add(rec, s, LoadMetrics.UpStale, stale[s]);
            Assert.Contains("caught up 3 s after the load stopped", Text(l));
        }

        [Fact]
        public void A_game_that_kept_its_frame_rate_under_the_load_says_so()
        {
            var l = new LoadLedger();
            foreach (var st in Plan.Stages.Where(x => x.Kind != LoadPlan.Kind.Recovery))
                Fill(l, st.Name, clientFps: st.Kind == LoadPlan.Kind.Baseline ? 34 : 54);
            string t = Text(l);
            Assert.Contains("Your game kept its frame rate under the load", t);
            Assert.DoesNotContain("slowest step", t);
        }

        [Fact]
        public void A_game_that_slowed_down_names_its_slowest_step()
        {
            var l = Calm("up 800 kB/s");
            Fill(l, "up 800 kB/s", clientFps: 30);
            Assert.Contains("30 fps at its slowest step (up 800 kB/s)", Text(l));
        }

        [Fact]
        public void A_stopped_test_says_why()
        {
            Assert.Contains("Stopped early: world updates 25 s old", Text(Calm(), "world updates 25 s old"));
        }

        [Fact]
        public void Without_the_other_sides_counts_the_senders_own_count_is_used()
        {
            var l = new LoadLedger();
            int i = StageOf("up 50 kB/s");
            for (int s = 0; s < 10; s++) l.Add(i, s, LoadMetrics.UpSent, 49);
            Assert.Contains("kept up", Text(l));
        }

        [Fact]
        public void A_direction_with_no_measurements_says_so_instead_of_guessing()
        {
            Assert.Contains("no measurements", Text(new LoadLedger()));
        }

        [Fact]
        public void The_step_table_lists_every_step_with_its_values()
        {
            var lines = LoadVerdict.Table(Plan, Calm(), new[] { LoadMetrics.UpRecv, LoadMetrics.ServerFrame });
            Assert.Contains(lines, x => x.Contains("up 400 kB/s") && x.Contains("400"));
            Assert.Contains(lines, x => x.Contains("baseline"));
        }
    }

    /// <summary>
    /// The server's report lists, per step, the timed methods that cost the most, and the ones
    /// that grew the most compared with the quiet baseline.
    /// </summary>
    public sealed class StageCostsTests
    {
        [Fact]
        public void Costs_are_per_frame_within_each_step()
        {
            var c = new StageCosts();
            c.AddFrames(1, 100);
            c.Add(1, "ZDOMan.Update", ms: 250, calls: 100);
            var top = c.Top(1, 5).Single();
            Assert.Equal("ZDOMan.Update", top.Label);
            Assert.Equal(2.5, top.MsPerFrame, 3);
            Assert.Equal(1.0, top.CallsPerFrame, 3);
        }

        [Fact]
        public void The_biggest_growth_against_the_baseline_comes_first()
        {
            var c = new StageCosts();
            c.AddFrames(0, 100); c.AddFrames(5, 100);
            c.Add(0, "A", 100, 100); c.Add(5, "A", 150, 100);
            c.Add(0, "B", 50, 100); c.Add(5, "B", 450, 100);
            c.Add(5, "C", 30, 10);
            var g = c.Growth(0, 5, 3);
            Assert.Equal(new[] { "B", "A", "C" }, g.Select(x => x.Label).ToArray());
            Assert.Equal(4.0, g[0].MsPerFrame - g[0].BaselineMsPerFrame, 3);
        }

        [Fact]
        public void Growth_ranks_by_the_increase_not_by_the_cost()
        {
            var c = new StageCosts();
            c.AddFrames(0, 100); c.AddFrames(5, 100);
            c.Add(0, "Big", 300, 100); c.Add(5, "Big", 350, 100);
            c.Add(0, "Grower", 50, 100); c.Add(5, "Grower", 250, 100);
            Assert.Equal(new[] { "Grower", "Big" }, c.Growth(0, 5, 2).Select(x => x.Label).ToArray());
        }

        [Fact]
        public void A_step_without_frames_has_no_costs()
        {
            var c = new StageCosts();
            c.Add(2, "A", 10, 1);
            Assert.Empty(c.Top(2, 5));
        }
    }
}
