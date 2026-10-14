using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// When a connection ends, Vigil says how: the player left, their line died, their game froze,
    /// their game was closed without a goodbye, or this side closed it. It reads that from how the
    /// connection ended and from the last minute's traffic, second by second.
    /// </summary>
    public sealed class DropAnalysisTests
    {
        private static List<DropAnalysis.Sample> Steady(int seconds, double inKbs, double gapMs = 60, double queueKb = 0)
        {
            var list = new List<DropAnalysis.Sample>();
            for (int i = 0; i < seconds; i++) list.Add(new DropAnalysis.Sample { At = i, InKbs = inKbs, OutKbs = 20, QueueKb = queueKb, PingMs = 40, MaxGapMs = gapMs });
            return list;
        }

        private static void Then(List<DropAnalysis.Sample> list, int seconds, double inKbs, double gapMs, double queueFrom, double queueTo)
        {
            double start = list.Count;
            for (int i = 0; i < seconds; i++)
                list.Add(new DropAnalysis.Sample { At = start + i, InKbs = inKbs, OutKbs = 20, QueueKb = queueFrom + (queueTo - queueFrom) * i / (seconds - 1.0), PingMs = 40, MaxGapMs = gapMs });
        }

        [Fact]
        public void A_connection_that_went_silent_at_once_while_data_piled_up_for_the_player_reads_as_their_line_dying()
        {
            var s = Steady(40, 12);
            Then(s, 29, 0, 1000, 0, 70);
            var d = DropAnalysis.Read("D'Arcy", false, s, DropAnalysis.Ending.Timeout, 30.2, null);
            Assert.Equal(DropAnalysis.Verdict.LineDied, d.Kind);
            Assert.Contains("D'Arcy", d.Headline);
            Assert.Contains("30 s", d.Headline);
            Assert.Contains(d.Lines, l => l.Contains("70 kB"));
        }

        [Fact]
        public void A_game_that_slowed_down_before_it_went_silent_reads_as_the_game_freezing()
        {
            var s = Steady(30, 12);
            Then(s, 10, 2, 900, 0, 5);
            Then(s, 30, 0, 1000, 5, 40);
            var d = DropAnalysis.Read("Bjorn", false, s, DropAnalysis.Ending.Timeout, 30.1, null);
            Assert.Equal(DropAnalysis.Verdict.GameFroze, d.Kind);
        }

        [Fact]
        public void Traffic_that_fell_away_before_the_silence_reads_as_the_game_freezing_even_with_steady_pauses()
        {
            var s = Steady(30, 12);
            Then(s, 10, 3, 60, 0, 0);
            Then(s, 30, 0, 60, 0, 40);
            Assert.Equal(DropAnalysis.Verdict.GameFroze, DropAnalysis.Read("Bjorn", false, s, DropAnalysis.Ending.Timeout, 30, null).Kind);
        }

        [Fact]
        public void Packets_coming_further_apart_before_the_silence_read_as_the_game_freezing_even_with_steady_traffic()
        {
            var s = Steady(30, 12);
            Then(s, 10, 12, 800, 0, 0);
            Then(s, 30, 0, 1000, 0, 40);
            Assert.Equal(DropAnalysis.Verdict.GameFroze, DropAnalysis.Read("Bjorn", false, s, DropAnalysis.Ending.Timeout, 30, null).Kind);
        }

        [Fact]
        public void A_player_whose_game_said_goodbye_left_normally()
        {
            var d = DropAnalysis.Read("Bjorn", false, Steady(60, 10), DropAnalysis.Ending.Goodbye, 0.1, null);
            Assert.Equal(DropAnalysis.Verdict.Left, d.Kind);
        }

        [Fact]
        public void A_game_that_closed_the_connection_without_a_goodbye_was_closed_hard_and_Steams_words_are_kept()
        {
            var d = DropAnalysis.Read("Bjorn", false, Steady(60, 10), DropAnalysis.Ending.ClosedByPeer, 0.2, "1000: Closed by peer");
            Assert.Equal(DropAnalysis.Verdict.ClosedWithoutGoodbye, d.Kind);
            Assert.Contains(d.Lines, l => l.Contains("1000: Closed by peer"));
        }

        [Fact]
        public void A_connection_this_side_closed_is_named_as_such()
        {
            var d = DropAnalysis.Read("Bjorn", false, Steady(60, 10), DropAnalysis.Ending.ClosedHere, 0.2, null);
            Assert.Equal(DropAnalysis.Verdict.ClosedHere, d.Kind);
        }

        [Fact]
        public void Steam_giving_up_on_a_silent_line_reads_like_the_games_own_timeout()
        {
            var s = Steady(40, 12);
            Then(s, 15, 0, 1000, 0, 30);
            var d = DropAnalysis.Read("Bjorn", false, s, DropAnalysis.Ending.SteamProblem, 15, "4001: Timed out");
            Assert.Equal(DropAnalysis.Verdict.LineDied, d.Kind);
            Assert.Contains(d.Lines, l => l.Contains("4001: Timed out"));
        }

        [Fact]
        public void A_drop_right_after_joining_says_how_it_ended_without_guessing_at_the_line()
        {
            var d = DropAnalysis.Read("Bjorn", false, new List<DropAnalysis.Sample>(), DropAnalysis.Ending.Timeout, 30, null);
            Assert.Equal(DropAnalysis.Verdict.Unclear, d.Kind);
            Assert.Contains("30 s", d.Headline);
        }

        [Fact]
        public void On_a_players_game_the_other_side_is_the_server()
        {
            var s = Steady(40, 12);
            Then(s, 29, 0, 1000, 0, 70);
            var d = DropAnalysis.Read("the server", true, s, DropAnalysis.Ending.Timeout, 30, null);
            Assert.StartsWith("The server", d.Headline);
            Assert.Equal(DropAnalysis.Verdict.LineDied, d.Kind);
        }
    }
}
