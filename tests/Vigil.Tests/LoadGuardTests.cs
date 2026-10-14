using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A load test is there to find the limit, not to wreck the session: it stops on its own when
    /// the server or the player's game is clearly overwhelmed, and the server refuses to start one
    /// that would disturb other players or could not be measured.
    /// </summary>
    public sealed class LoadGuardTests
    {
        [Fact]
        public void A_server_frame_time_over_the_limit_for_some_seconds_in_a_row_stops_the_test()
        {
            var g = new LoadGuard();
            Assert.Null(g.Check(serverFrameMs: 250, staleSeconds: 1, clientFps: 60, rttMs: 50));
            Assert.Null(g.Check(250, 1, 60, 50));
            Assert.Contains("server", g.Check(250, 1, 60, 50));
        }

        [Fact]
        public void One_slow_second_on_the_server_is_not_enough()
        {
            var g = new LoadGuard();
            g.Check(250, 1, 60, 50);
            g.Check(250, 1, 60, 50);
            Assert.Null(g.Check(30, 1, 60, 50));
            Assert.Null(g.Check(250, 1, 60, 50));
        }

        [Fact]
        public void World_updates_this_old_stop_the_test_at_once()
        {
            Assert.Contains("old", new LoadGuard().Check(30, 25, 60, 50));
        }

        [Fact]
        public void A_players_game_this_slow_for_seconds_in_a_row_stops_the_test()
        {
            var g = new LoadGuard();
            string r = null;
            for (int i = 0; i < 5; i++) r = g.Check(30, 1, 3, 50);
            Assert.Contains("fps", r);
        }

        [Fact]
        public void A_round_trip_this_long_stops_the_test()
        {
            Assert.Contains("round trip", new LoadGuard().Check(30, 1, 60, 12000));
        }

        [Fact]
        public void Values_not_known_yet_never_stop_the_test()
        {
            var g = new LoadGuard();
            for (int i = 0; i < 10; i++) Assert.Null(g.Check(double.NaN, double.NaN, double.NaN, double.NaN));
        }

        [Fact]
        public void Another_player_online_refuses_the_test()
        {
            Assert.Contains("1 other player", LoadAdmission.Refusal(otherPlayers: 1, force: false, running: false, metersFromWorldCenter: 3000));
        }

        [Fact]
        public void The_override_runs_it_with_other_players_online()
        {
            Assert.Null(LoadAdmission.Refusal(otherPlayers: 3, force: true, running: false, metersFromWorldCenter: 3000));
        }

        [Fact]
        public void A_second_test_while_one_runs_is_refused_even_with_the_override()
        {
            Assert.Contains("already", LoadAdmission.Refusal(0, force: true, running: true, metersFromWorldCenter: 3000));
        }

        [Fact]
        public void Near_the_world_center_the_test_is_refused()
        {
            // The dedicated server builds the objects around the world's center itself and removes
            // ones it cannot build, which would remove the test's objects.
            Assert.Contains("center", LoadAdmission.Refusal(0, force: true, running: false, metersFromWorldCenter: 150));
        }

        [Fact]
        public void Alone_and_away_from_the_center_the_test_runs()
        {
            Assert.Null(LoadAdmission.Refusal(0, force: false, running: false, metersFromWorldCenter: 3000));
        }
    }
}
