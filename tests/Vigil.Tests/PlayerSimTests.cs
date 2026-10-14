using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player simulation adds simulated players to the server step by step (1, 2, 4, 8 ...)
    /// and measures how the server and the real player cope; then it removes them and measures the
    /// recovery.
    /// </summary>
    public sealed class PlayerPlanTests
    {
        [Fact]
        public void The_player_count_doubles_each_step_up_to_the_maximum()
        {
            var names = LoadPlan.ForPlayers(16).Stages.Select(s => s.Name).ToArray();
            Assert.Equal(new[] { "baseline", "1 player", "2 players", "4 players", "8 players", "16 players", "players recovery" }, names);
        }

        [Fact]
        public void A_maximum_between_doublings_is_the_last_step()
        {
            var counts = LoadPlan.ForPlayers(10).Stages.Where(s => s.Kind == LoadPlan.Kind.Load).Select(s => s.Players).ToArray();
            Assert.Equal(new[] { 1, 2, 4, 8, 10 }, counts);
        }

        [Fact]
        public void The_maximum_is_kept_between_one_and_the_hard_limit()
        {
            Assert.Equal(1, LoadPlan.ForPlayers(0).Stages.Count(s => s.Kind == LoadPlan.Kind.Load));
            Assert.Equal(LoadPlan.MaxPlayers, LoadPlan.ForPlayers(500).Stages.Where(s => s.Kind == LoadPlan.Kind.Load).Max(s => s.Players));
        }

        [Fact]
        public void An_abort_jumps_to_the_recovery_without_players()
        {
            var plan = LoadPlan.ForPlayers(8);
            var s = new LoadSchedule(plan);
            s.Start(0);
            s.Abort("too slow", LoadPlan.BaselineSeconds + LoadPlan.PlayerStepSeconds + 1);
            Assert.Equal("players recovery", s.Current(LoadPlan.BaselineSeconds + LoadPlan.PlayerStepSeconds + 1).Name);
        }
    }

    /// <summary>A simulated player's connection takes data at a fixed rate; the rest waits in its queue.</summary>
    public sealed class LinkModelTests
    {
        [Fact]
        public void The_queue_drains_at_the_links_rate()
        {
            var l = new LinkModel(100);
            l.Send(10240, 0);
            Assert.Equal(10240, l.Queued(0));
            Assert.Equal(5120, l.Queued(0.05));
            Assert.Equal(0, l.Queued(1));
        }

        [Fact]
        public void An_idle_link_does_not_save_up_capacity()
        {
            var l = new LinkModel(100);
            l.Queued(10);
            l.Send(2048, 10);
            Assert.Equal(2048, l.Queued(10));
        }

        [Fact]
        public void The_first_data_on_a_link_waits_its_turn_even_when_the_link_starts_late()
        {
            var l = new LinkModel(100);
            l.Send(10240, 100);
            Assert.Equal(10240, l.Queued(100));
        }

        [Fact]
        public void Everything_sent_is_counted()
        {
            var l = new LinkModel(100);
            l.Send(300, 0);
            l.Send(200, 5);
            Assert.Equal(500, l.TotalBytes);
        }
    }

    /// <summary>
    /// A player arriving (joining, or teleporting) makes the server send the whole area around them;
    /// the arrival is caught up once the traffic to that player falls back to a trickle.
    /// </summary>
    public sealed class ArrivalTrackerTests
    {
        [Fact]
        public void The_catch_up_ends_when_the_traffic_falls_back()
        {
            var a = new ArrivalTracker();
            a.Arrive(100);
            Assert.Null(a.Second(101, 300));
            Assert.Null(a.Second(102, 250));
            Assert.Null(a.Second(103, 200));
            Assert.Equal(3, a.Second(104, 10).Value, 3);
            Assert.Equal(750 + 10, a.BurstKb, 3);
        }

        [Fact]
        public void An_empty_area_is_caught_up_at_once()
        {
            var a = new ArrivalTracker();
            a.Arrive(50);
            Assert.Equal(0, a.Second(51, 2).Value, 3);
        }

        [Fact]
        public void An_arrival_still_busy_after_the_limit_is_reported_at_the_limit()
        {
            var a = new ArrivalTracker();
            a.Arrive(0);
            double? r = null;
            for (int s = 1; s < ArrivalTracker.LimitSeconds; s++) Assert.Null(a.Second(s, 500));
            r = a.Second(ArrivalTracker.LimitSeconds, 500);
            Assert.Equal(ArrivalTracker.LimitSeconds, r.Value, 3);
        }

        [Fact]
        public void At_a_busy_place_the_arrival_is_over_once_the_link_has_room_again()
        {
            // The objects around a busy base keep changing, so the traffic never falls to a trickle;
            // the area has arrived once the link is no longer full and its queue has drained.
            var a = new ArrivalTracker();
            a.Arrive(0);
            Assert.Null(a.Second(1, 256, queueKb: 30, linkKbps: 256));
            Assert.Null(a.Second(2, 250, queueKb: 25, linkKbps: 256));
            Assert.Equal(2, a.Second(3, 130, queueKb: 0.5, linkKbps: 256).Value, 3);
        }

        [Fact]
        public void A_full_link_with_an_empty_moment_in_its_queue_is_not_caught_up_yet()
        {
            var a = new ArrivalTracker();
            a.Arrive(0);
            Assert.Null(a.Second(1, 250, queueKb: 0.5, linkKbps: 256));
        }

        [Fact]
        public void Traffic_right_at_the_trickle_line_counts_as_caught_up()
        {
            var a = new ArrivalTracker();
            a.Arrive(0);
            Assert.Null(a.Second(1, 30));
            Assert.Equal(1, a.Second(2, ArrivalTracker.TrickleKb).Value, 3);
        }

        [Fact]
        public void Nothing_is_reported_without_an_arrival()
        {
            Assert.Null(new ArrivalTracker().Second(5, 0));
        }
    }

    /// <summary>A travelling simulated player runs from stop to stop at running speed.</summary>
    public sealed class BotPathTests
    {
        [Fact]
        public void It_moves_at_its_speed_towards_the_next_stop()
        {
            var p = new BotPath(0, 0, speed: 7);
            p.Target(100, 0);
            Assert.False(p.Step(1));
            Assert.Equal(7, p.X, 3);
            Assert.Equal(0, p.Z, 3);
        }

        [Fact]
        public void It_stops_at_the_stop_rather_than_running_past()
        {
            var p = new BotPath(0, 0, speed: 7);
            p.Target(3, 4);
            Assert.True(p.Step(1));
            Assert.Equal(3, p.X, 3);
            Assert.Equal(4, p.Z, 3);
        }
    }

    /// <summary>
    /// Spread out, the simulated players stand where the world holds the most objects (the bases),
    /// one per place, apart from each other and away from the world's center.
    /// </summary>
    public sealed class BusySpotsTests
    {
        [Fact]
        public void The_busiest_places_come_first_and_stay_apart()
        {
            var zones = new List<BusySpots.Zone>
            {
                new BusySpots.Zone(50, 50, 900), new BusySpots.Zone(51, 50, 800), new BusySpots.Zone(-40, 30, 500), new BusySpots.Zone(20, -60, 300),
            };
            var picked = BusySpots.Pick(zones, 3, minZoneGap: 3, minMetersFromCenter: 400, zoneSize: 64);
            Assert.Equal(new[] { (50, 50), (-40, 30), (20, -60) }, picked.Select(z => (z.X, z.Z)).ToArray());
        }

        [Fact]
        public void Places_in_line_but_far_apart_are_both_taken()
        {
            var zones = new List<BusySpots.Zone> { new BusySpots.Zone(50, 50, 900), new BusySpots.Zone(51, -40, 800) };
            Assert.Equal(2, BusySpots.Pick(zones, 2, minZoneGap: 3, minMetersFromCenter: 400, zoneSize: 64).Count);
        }

        [Fact]
        public void Places_near_the_world_center_are_left_out()
        {
            var zones = new List<BusySpots.Zone> { new BusySpots.Zone(1, 1, 5000), new BusySpots.Zone(30, 30, 10) };
            Assert.Equal(30, BusySpots.Pick(zones, 2, 3, 400, 64).Single().X);
        }

        [Fact]
        public void Fewer_places_than_players_means_fewer_places()
        {
            var zones = new List<BusySpots.Zone> { new BusySpots.Zone(30, 30, 10) };
            Assert.Single(BusySpots.Pick(zones, 5, 3, 400, 64));
        }
    }

    public sealed class LinearFitTests
    {
        [Fact]
        public void A_straight_line_is_found_exactly()
        {
            var (slope, intercept) = LinearFit.Fit(new double[] { 1, 2, 4, 8 }, new double[] { 3, 5, 9, 17 });
            Assert.Equal(2, slope, 6);
            Assert.Equal(1, intercept, 6);
        }

        [Fact]
        public void Fewer_than_two_distinct_points_give_no_line()
        {
            Assert.True(double.IsNaN(LinearFit.Fit(new double[] { 4 }, new double[] { 2 }).Slope));
            Assert.True(double.IsNaN(LinearFit.Fit(new double[] { 4, 4 }, new double[] { 2, 3 }).Slope));
        }
    }

    /// <summary>
    /// After a player simulation the report says how many simulated players the server kept up
    /// with, what gave first, how much server work each player added and where the frame would
    /// fill up, and how long arriving players waited for their area.
    /// </summary>
    public sealed class PlayerVerdictTests
    {
        private static readonly LoadPlan Plan = LoadPlan.ForPlayers(8);

        private static int StageOf(string name) => Plan.Stages.First(s => s.Name == name).Index;

        private static void Fill(LoadLedger l, string stage, double serverFrame = 33.3, double serverMax = 40, double work = double.NaN,
            double stale = 0.3, double rtt = 50, double botQueue = 1)
        {
            int i = StageOf(stage);
            int players = Plan.Stages[i].Players;
            for (int s = 0; s < 10; s++)
            {
                l.Add(i, s, LoadMetrics.ServerFrame, serverFrame);
                l.Add(i, s, LoadMetrics.ServerFrameMax, serverMax);
                l.Add(i, s, LoadMetrics.ServerWork, double.IsNaN(work) ? 4 + 1.5 * players : work);
                l.Add(i, s, LoadMetrics.DownStale, stale);
                l.Add(i, s, LoadMetrics.Rtt, rtt);
                l.Add(i, s, LoadMetrics.Bots, players);
                l.Add(i, s, LoadMetrics.BotQueue, botQueue);
            }
        }

        private static LoadLedger Calm(params string[] except)
        {
            var l = new LoadLedger();
            foreach (var s in Plan.Stages.Where(s => s.Kind != LoadPlan.Kind.Recovery && !except.Contains(s.Name))) Fill(l, s.Name);
            return l;
        }

        private static string Text(LoadLedger l, string abort = null) => string.Join("\n", LoadVerdict.JudgePlayers(Plan, l, abort, "stay"));

        [Fact]
        public void Keeping_up_at_every_step_says_so()
        {
            Assert.Contains("kept up with 8 simulated players, the most this test added", Text(Calm()));
        }

        [Fact]
        public void An_overloaded_server_is_named_at_the_step_where_it_gave()
        {
            var l = Calm("8 players");
            Fill(l, "8 players", serverFrame: 70, serverMax: 160, work: 65);
            string t = Text(l);
            Assert.Contains("kept up with 4 simulated players", t);
            Assert.Contains("At 8 players", t);
            Assert.Contains("server's main thread", t);
        }

        [Fact]
        public void Old_updates_for_the_real_player_with_a_calm_server_point_at_the_turn_taking()
        {
            var l = Calm("8 players");
            Fill(l, "8 players", stale: 4);
            string t = Text(l);
            Assert.Contains("At 8 players", t);
            Assert.Contains("one player per frame", t);
        }

        [Fact]
        public void The_work_each_player_adds_gives_a_capacity_estimate()
        {
            string t = Text(Calm());
            Assert.Contains("1.50 ms of server work per frame", t);
            Assert.Contains("about 19 players", t);
        }

        [Fact]
        public void Work_that_does_not_grow_gives_no_capacity_guess()
        {
            var l = new LoadLedger();
            foreach (var s in Plan.Stages.Where(s => s.Kind != LoadPlan.Kind.Recovery)) Fill(l, s.Name, work: 5);
            Assert.Contains("barely grew", Text(l));
        }

        [Fact]
        public void Arrivals_report_the_typical_and_the_slowest_wait()
        {
            var l = Calm();
            int i = StageOf("4 players");
            foreach (var v in new double[] { 2, 3, 3, 9 }) l.Add(i, 0, LoadMetrics.BotArrival, v);
            string t = Text(l);
            Assert.Contains("typically 3 s", t);
            Assert.Contains("slowest 9 s", t);
        }

        [Fact]
        public void Full_simulated_links_are_pointed_out()
        {
            var l = Calm("8 players");
            Fill(l, "8 players", botQueue: 9);
            Assert.Contains("links were full", Text(l));
        }

        [Fact]
        public void A_simulation_with_no_measurements_says_so()
        {
            Assert.Contains("no measurements", Text(new LoadLedger()));
        }
    }
}
