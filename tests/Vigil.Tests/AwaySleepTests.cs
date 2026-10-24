using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The game patch "Away players don't hold up sleep": a player whose game menu has stayed open
    /// for the server's minutes is away, and the night is skipped once everyone else is in bed.
    /// Somebody has to be really in bed; an away player gets no morning (no Rested, no "good morning");
    /// the sleepers are told who was not counted; a self-test's report is never counted.
    /// </summary>
    public sealed class AwaySleepTests
    {
        private const double Limit = 300;

        [Fact]
        public void A_player_whose_menu_stays_open_for_the_minutes_is_away()
        {
            var clock = new AwayClock();
            clock.Update(inWorld: true, menuOpen: true, now: 100, limitSeconds: Limit);
            Assert.False(clock.Away);

            clock.Update(true, true, 100 + Limit - 1, Limit);
            Assert.False(clock.Away);
            Assert.Equal(Limit - 1, clock.MenuSeconds(100 + Limit - 1), 3);

            clock.Update(true, true, 100 + Limit, Limit);
            Assert.True(clock.Away);
        }

        [Fact]
        public void Closing_the_menu_or_leaving_the_world_makes_the_player_present_and_starts_the_minutes_over()
        {
            var clock = new AwayClock();
            clock.Update(true, true, 0, Limit);
            clock.Update(true, true, Limit, Limit);
            Assert.True(clock.Away);

            clock.Update(true, false, Limit + 1, Limit);
            Assert.False(clock.Away);
            Assert.True(double.IsNaN(clock.MenuSeconds(Limit + 1)));

            clock.Update(true, true, Limit + 2, Limit);
            clock.Update(true, true, Limit + 2 + Limit - 1, Limit);
            Assert.False(clock.Away);

            clock.Update(true, true, 3 * Limit, Limit);
            Assert.True(clock.Away);
            clock.Update(false, true, 3 * Limit + 1, Limit);
            Assert.False(clock.Away);
        }

        [Fact]
        public void A_shorter_limit_counts_from_when_the_menu_opened()
        {
            var clock = new AwayClock();
            clock.Update(true, true, 50, Limit);
            clock.Update(true, true, 52, 1.5);
            Assert.True(clock.Away);
        }

        [Fact]
        public void The_night_is_skipped_when_everyone_up_is_away_and_somebody_is_in_bed()
        {
            var v = AwaySleep.Judge(new[] { new Sleeper("Ada", true, false), new Sleeper("Bo", false, true), new Sleeper("Cy", true, false) });

            Assert.True(v.Skip);
            Assert.Equal(new[] { "Bo" }, v.AwayNames);
        }

        [Fact]
        public void A_player_up_and_not_away_still_holds_up_the_night()
        {
            var v = AwaySleep.Judge(new[] { new Sleeper("Ada", true, false), new Sleeper("Bo", false, true), new Sleeper("Cy", false, false) });

            Assert.False(v.Skip);
            Assert.Empty(v.AwayNames);
        }

        [Fact]
        public void With_nobody_really_in_bed_the_night_is_not_skipped()
        {
            Assert.False(AwaySleep.Judge(new[] { new Sleeper("Bo", false, true), new Sleeper("Cy", false, true) }).Skip);
            Assert.False(AwaySleep.Judge(new Sleeper[0]).Skip);
        }

        [Fact]
        public void Everyone_in_bed_is_the_game_s_own_skip_with_nobody_left_out()
        {
            var v = AwaySleep.Judge(new[] { new Sleeper("Ada", true, false), new Sleeper("Bo", true, true) });

            Assert.True(v.Skip);
            Assert.Empty(v.AwayNames);
        }

        [Fact]
        public void The_server_counts_an_away_report_until_the_player_is_back_or_gone()
        {
            var book = new AwayBook();
            book.Report(7, "Bo", away: true, trial: false);
            Assert.True(book.IsAway(7));
            Assert.False(book.IsAway(8));

            book.Report(7, "Bo", away: false, trial: false);
            Assert.False(book.IsAway(7));

            book.Report(7, "Bo", true, false);
            book.Report(9, "Cy", true, false);
            book.KeepOnly(new long[] { 9 });
            Assert.False(book.IsAway(7));
            Assert.True(book.IsAway(9));
            Assert.Equal(1, book.Count);
        }

        [Fact]
        public void A_self_test_s_away_report_is_kept_but_never_counted_for_sleep()
        {
            var book = new AwayBook();
            book.Report(7, "Bo", away: true, trial: true);

            Assert.False(book.IsAway(7));
            Assert.True(book.TryGet(7, out var entry));
            Assert.True(entry.Away);
            Assert.True(entry.Trial);
        }

        [Fact]
        public void The_player_s_game_tells_the_server_only_when_its_answer_changes()
        {
            var r = new AwayReporter();
            Assert.Null(r.Next(on: true, away: false, trial: false, serverTakes: true));

            var first = r.Next(true, true, false, true);
            Assert.NotNull(first);
            Assert.True(first.Value.Away);
            Assert.Null(r.Next(true, true, false, true));

            var back = r.Next(true, false, false, true);
            Assert.NotNull(back);
            Assert.False(back.Value.Away);
        }

        [Fact]
        public void With_the_server_s_switch_off_an_away_player_is_not_reported_but_a_self_test_is()
        {
            var r = new AwayReporter();
            Assert.Null(r.Next(on: false, away: true, trial: false, serverTakes: true));

            var trial = r.Next(false, true, true, true);
            Assert.NotNull(trial);
            Assert.True(trial.Value.Away);
            Assert.True(trial.Value.Trial);

            var on = r.Next(true, true, false, true);
            Assert.NotNull(on);
            Assert.False(on.Value.Trial);
        }

        [Fact]
        public void A_new_connection_to_the_server_hears_an_away_player_again()
        {
            var r = new AwayReporter();
            Assert.NotNull(r.Next(true, true, false, true));
            Assert.Null(r.Next(true, true, false, serverTakes: false));

            var again = r.Next(true, true, false, true);
            Assert.NotNull(again);
            Assert.True(again.Value.Away);
        }

        [Fact]
        public void A_server_without_away_reports_hears_nothing()
        {
            var r = new AwayReporter();
            Assert.Null(r.Next(true, true, false, serverTakes: false));
            Assert.Null(r.Next(true, false, false, serverTakes: false));
        }

        [Fact]
        public void An_away_player_out_of_bed_is_left_out_of_the_morning()
        {
            Assert.True(AwaySleep.LeftOutOfSleep(sleep: true, on: true, away: true, inBed: false));
            Assert.False(AwaySleep.LeftOutOfSleep(sleep: true, on: true, away: true, inBed: true));
            Assert.False(AwaySleep.LeftOutOfSleep(sleep: true, on: true, away: false, inBed: false));
            Assert.False(AwaySleep.LeftOutOfSleep(sleep: true, on: false, away: true, inBed: false));
            Assert.False(AwaySleep.LeftOutOfSleep(sleep: false, on: true, away: true, inBed: false));
        }

        [Fact]
        public void The_sleepers_are_told_who_was_away_and_not_counted()
        {
            Assert.Equal("Skipping the night: Bo is away and not counted", AwaySleep.Notice(new[] { "Bo" }));
            Assert.Equal("Skipping the night: Bo and Cy are away and not counted", AwaySleep.Notice(new[] { "Bo", "Cy" }));
            Assert.Equal("Skipping the night: Bo, Cy and Di are away and not counted", AwaySleep.Notice(new[] { "Bo", "Cy", "Di" }));
            Assert.Null(AwaySleep.Notice(new List<string>()));
        }

        [Fact]
        public void The_minutes_stay_between_one_and_sixty()
        {
            Assert.Equal(60, AwaySleep.LimitSeconds(1));
            Assert.Equal(300, AwaySleep.LimitSeconds(5));
            Assert.Equal(60, AwaySleep.LimitSeconds(0));
            Assert.Equal(3600, AwaySleep.LimitSeconds(90));
        }

        [Fact]
        public void The_state_says_off_stepped_aside_lacking_present_counting_or_away()
        {
            Assert.Equal("off: the server's switch", AwaySleep.StateLine(false, null, null, true, double.NaN, Limit, false));
            Assert.Equal("stepped aside: not found", AwaySleep.StateLine(true, "not found", null, true, double.NaN, Limit, false));
            Assert.Equal("join a world", AwaySleep.StateLine(true, null, null, false, double.NaN, Limit, false));
            Assert.Equal("not here: the server's Vigil is older", AwaySleep.StateLine(true, null, "the server's Vigil is older", true, double.NaN, Limit, false));
            Assert.Equal("working · you count for sleep; away after 5:00 in the game menu", AwaySleep.StateLine(true, null, null, true, double.NaN, Limit, false));
            Assert.Equal("working · in the game menu 1:20 of 5:00", AwaySleep.StateLine(true, null, null, true, 80, Limit, false));
            Assert.Equal("working · away: not counted for sleep while the game menu stays open", AwaySleep.StateLine(true, null, null, true, 400, Limit, true));
        }

        [Fact]
        public void Every_player_may_report_away_but_only_admins_ask_for_the_server_s_check()
        {
            Assert.Contains(Greeting.Away, Greeting.Own.Split(','));
            Assert.True(Greeting.ForEveryone(Greeting.Away));
            Assert.False(Greeting.ForEveryone(AwaySleep.CheckCommand));
        }

        [Fact]
        public void A_server_check_passes_when_the_game_s_answer_is_the_expected_one()
        {
            var pass = AwaySleep.CheckLine("you away, a stand-in in bed", expected: true, got: true);
            Assert.True(pass.Passed);
            Assert.Equal("you away, a stand-in in bed: skip, as expected", pass.Text);

            var fail = AwaySleep.CheckLine("you up, a stand-in in bed", expected: false, got: true);
            Assert.False(fail.Passed);
            Assert.Equal("you up, a stand-in in bed: skip, expected no skip", fail.Text);
        }
    }
}
