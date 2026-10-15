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

        // Steam's incoming rate is an estimate that changes in steps of about 5 s and decays after the
        // packets stop; the rows below are shaped like the server's and the player's own report of
        // one drop where both games kept running and the line between them died.
        private static List<DropAnalysis.Sample> Stepped(double[] steps, int stopAt, int length, bool gapGrows, bool unackedGrows)
        {
            var list = new List<DropAnalysis.Sample>();
            for (int i = 0; i < length; i++)
            {
                double inKbs = i / 5 < steps.Length ? steps[i / 5] : 0;
                double gap = i < stopAt ? 70 : gapGrows ? 800 + 1010 * (i - stopAt) : 0;
                double unacked = i < stopAt ? (i % 2 == 0 ? 3.4 : 0.5) : unackedGrows ? 30 + 1.4 * (i - stopAt) : 0;
                list.Add(new DropAnalysis.Sample { At = i, InKbs = inKbs, OutKbs = 100, QueueKb = unacked, UnackedKb = unacked, PingMs = 18, MaxGapMs = gap });
            }
            return list;
        }

        [Fact]
        public void A_sudden_stop_whose_rate_estimate_decays_afterwards_reads_as_the_line_dying_on_the_server()
        {
            // Packets stop at 31; the estimate still says 61 then 8.7 kB/s for a while; nothing is confirmed from then on.
            var s = Stepped(new[] { 48.8, 74.2, 68.5, 70.1, 72.0, 74.3, 61.0, 8.7 }, stopAt: 31, length: 61, gapGrows: false, unackedGrows: true);
            var d = DropAnalysis.Read("D'Arcy", false, s, DropAnalysis.Ending.Timeout, 30, null);
            Assert.Equal(DropAnalysis.Verdict.LineDied, d.Kind);
            Assert.Contains("connection died", d.Headline);
        }

        [Fact]
        public void On_a_players_game_a_pause_growing_by_the_second_is_the_silence_itself_not_a_slowdown()
        {
            var s = Stepped(new[] { 110.0, 115.0, 112.0, 118.0, 112.5, 112.5, 79.7 }, stopAt: 31, length: 61, gapGrows: true, unackedGrows: false);
            var d = DropAnalysis.Read("the server", true, s, DropAnalysis.Ending.Timeout, 30, null);
            Assert.Equal(DropAnalysis.Verdict.LineDied, d.Kind);
            Assert.Contains("The server's connection died", d.Headline);
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
