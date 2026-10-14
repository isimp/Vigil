using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The World tab's "Weather and light" card says which weather the player is in and why: the
    /// game takes the first of its overrides (a dev command, the intro, a raid, a special area, a
    /// world event, a place with weather of its own) and otherwise rolls from the biome's list once
    /// per period, the same roll on every game with the same list. Admins can set the weather, the
    /// fog drawn, the wind and the time of day drawn, on their own game only; an environment report
    /// can be written, sent to the server (by hand, or on its own when the player ticks it) and
    /// compared with another player's, differences first and lists item by item.
    /// </summary>
    public sealed class WeatherTests
    {
        [Fact]
        public void A_rolled_weather_names_its_biome_its_period_and_the_next_roll()
        {
            var cause = WeatherCause.Of(new WeatherSources(), "Meadows", period: 18342, secondsToNext: 430);
            Assert.Equal(WeatherCauseKind.Rolled, cause.Kind);
            Assert.Equal("rolled from the Meadows list for period 18342, next roll in 7 min 10 s", cause.Words);
        }

        [Fact]
        public void The_first_override_in_the_games_order_is_the_cause()
        {
            var s = new WeatherSources { ZoneEnv = "Crypt", EventEnv = "ThunderStorm", EventName = "army_eikthyr", WorldEventEnv = "Ashrain" };
            Assert.Equal(WeatherCauseKind.Event, WeatherCause.Of(s, "Meadows", 1, 1).Kind);
            Assert.Equal("the raid army_eikthyr", WeatherCause.Of(s, "Meadows", 1, 1).Words);
            s.DebugEnv = "Clear";
            Assert.Equal(WeatherCauseKind.DevCommand, WeatherCause.Of(s, "Meadows", 1, 1).Kind);
            s.SetByVigil = true;
            Assert.Equal("set by an admin in Vigil, on this game only", WeatherCause.Of(s, "Meadows", 1, 1).Words);
        }

        [Fact]
        public void Each_override_reads_in_words()
        {
            Assert.Equal("the intro's weather", WeatherCause.Of(new WeatherSources { InIntro = true, IntroEnv = "ThunderStorm" }, "Meadows", 1, 1).Words);
            Assert.Equal("this area always has it", WeatherCause.Of(new WeatherSources { AreaEnv = "Mistlands_clear" }, "Mistlands", 1, 1).Words);
            Assert.Equal("a world event", WeatherCause.Of(new WeatherSources { WorldEventEnv = "Ashrain" }, "Meadows", 1, 1).Words);
            Assert.Equal("this place has its own weather", WeatherCause.Of(new WeatherSources { ZoneEnv = "Crypt" }, "Meadows", 1, 1).Words);
            Assert.Equal("set by a dev command (env), on this game only", WeatherCause.Of(new WeatherSources { DebugEnv = "Clear" }, "Meadows", 1, 1).Words);
        }

        [Fact]
        public void A_roll_comes_once_per_period_like_the_games()
        {
            Assert.Equal(18342, WeatherRoll.Period(18342 * 1200L + 770, 1200));
            Assert.Equal(430, WeatherRoll.SecondsToNext(18342 * 1200L + 770, 1200));
            Assert.Equal(0, WeatherRoll.Period(5000, 0));
        }

        [Fact]
        public void The_list_shows_each_weathers_chance_and_which_is_now()
        {
            var list = WeatherRoll.Chances(new[]
            {
                new KeyValuePair<string, float>("WarmSnow", 1), new KeyValuePair<string, float>("Warm_Twilight_SnowStorm", 1), new KeyValuePair<string, float>("WarmSnowStorm", 2),
            }, "Warm_Twilight_SnowStorm");
            Assert.Equal(new[] { 25.0, 25.0, 50.0 }, list.Select(e => e.Chance).ToArray());
            Assert.True(list[1].Now);
            Assert.False(list[0].Now);
            Assert.Equal(0, WeatherRoll.Chances(new[] { new KeyValuePair<string, float>("Clear", 0) }, "Clear")[0].Chance);
        }

        [Fact]
        public void Only_an_admin_or_the_worlds_host_may_change_the_weather()
        {
            Assert.Null(WeatherControl.Refusal(inWorld: true, hostsWorld: false, serverAdmin: true));
            Assert.Null(WeatherControl.Refusal(true, true, false));
            Assert.Equal("only the server's admins can change these here", WeatherControl.Refusal(true, false, false));
            Assert.Equal("join a world first", WeatherControl.Refusal(false, false, true));
        }

        [Fact]
        public void A_weather_is_found_by_its_name_or_a_part_only_it_has()
        {
            var names = new[] { "Clear", "Snow", "SnowStorm", "Twilight_Snow", "WarmSnow" };
            Assert.Equal("SnowStorm", WeatherControl.Match("snowstorm", names, out _));
            Assert.Equal("Twilight_Snow", WeatherControl.Match("twilight", names, out _));
            Assert.Null(WeatherControl.Match("snow s", names, out string none));
            Assert.Equal("no weather is called \"snow s\"; 5 are known", none);
            Assert.Null(WeatherControl.Match("snow", names.Where(n => n != "Snow").ToArray(), out string many));
            Assert.Equal("\"snow\" fits 3: SnowStorm, Twilight_Snow, WarmSnow", many);
        }

        [Fact]
        public void Fog_time_and_wind_are_read_from_what_was_typed()
        {
            Assert.True(WeatherControl.TryFog("0.05", out float fog));
            Assert.Equal(0.05f, fog, 3);
            Assert.True(WeatherControl.TryFog("off", out fog));
            Assert.Equal(0f, fog);
            Assert.True(WeatherControl.TryFog("3", out fog));
            Assert.Equal(1f, fog);
            Assert.False(WeatherControl.TryFog("thick", out _));

            Assert.True(WeatherControl.TryTime("14:30", out float day));
            Assert.Equal(14.5f / 24f, day, 4);
            Assert.True(WeatherControl.TryTime("0.25", out day));
            Assert.Equal(0.25f, day, 4);
            Assert.False(WeatherControl.TryTime("25:00", out _));

            Assert.True(WeatherControl.TryWind("45", "80", out float angle, out float strength));
            Assert.Equal(45f, angle);
            Assert.Equal(0.8f, strength, 3);
            Assert.True(WeatherControl.TryWind("400", "150", out angle, out strength));
            Assert.Equal(40f, angle);
            Assert.Equal(1f, strength);
            Assert.False(WeatherControl.TryWind("north", "50", out _, out _));
        }

        [Fact]
        public void The_fog_limiter_caps_thick_fog_and_leaves_thinner_fog_alone()
        {
            Assert.Equal(0.05, WeatherControl.FogDrawn(0.18, set: null, limit: 0.05), 6);
            Assert.Equal(0.02, WeatherControl.FogDrawn(0.02, null, 0.05), 6);
            Assert.Equal(0.18, WeatherControl.FogDrawn(0.18, null, null), 6);
        }

        [Fact]
        public void Fog_an_admin_set_is_drawn_as_set_past_the_limiter()
        {
            Assert.Equal(0.2, WeatherControl.FogDrawn(0.18, set: 0.2, limit: 0.05), 6);
            Assert.Equal(0.0, WeatherControl.FogDrawn(0.18, 0.0, null), 6);
        }

        [Fact]
        public void The_limiter_is_off_or_a_cap_from_what_was_typed()
        {
            Assert.True(WeatherControl.TryLimit("off", out float? cap));
            Assert.Null(cap);
            Assert.True(WeatherControl.TryLimit("", out cap));
            Assert.Null(cap);
            Assert.True(WeatherControl.TryLimit("0.04", out cap));
            Assert.Equal(0.04f, cap.Value, 3);
            Assert.True(WeatherControl.TryLimit("2", out cap));
            Assert.Equal(1f, cap.Value);
            Assert.False(WeatherControl.TryLimit("thin", out _));
        }

        [Fact]
        public void The_servers_fog_limit_wins_when_it_is_set_and_the_players_own_counts_otherwise()
        {
            var server = WeatherControl.Limit(serverText: "0.05", ownText: "0.02");
            Assert.Equal(0.05f, server.Cap.Value, 3);
            Assert.True(server.FromServer);

            var own = WeatherControl.Limit(serverText: "off", ownText: "0.02");
            Assert.Equal(0.02f, own.Cap.Value, 3);
            Assert.False(own.FromServer);

            Assert.Null(WeatherControl.Limit("off", "off").Cap);

            // A server value that cannot be read counts as off, so the player's own limiter still works.
            var garbled = WeatherControl.Limit("thick", "0.02");
            Assert.Equal(0.02f, garbled.Cap.Value, 3);
            Assert.False(garbled.FromServer);
        }

        [Fact]
        public void An_environment_report_reads_back_what_was_written()
        {
            var report = new EnvironmentReport();
            report.Values["player"] = "Sigrun";
            report.Values["weather"] = "WarmSnow";
            report.Values["fog_drawn"] = "0.042";
            report.Values["list"] = "WarmSnow 1; WarmSnowStorm 1\nsecond line";
            var back = EnvironmentReport.Parse(report.Lines());
            Assert.NotNull(back);
            Assert.Equal("Sigrun", back.Values["player"]);
            Assert.Equal("0.042", back.Values["fog_drawn"]);
            Assert.Equal("WarmSnow 1; WarmSnowStorm 1; second line", back.Values["list"]);
        }

        [Fact]
        public void Another_file_is_not_taken_for_an_environment_report()
        {
            Assert.Null(EnvironmentReport.Parse(new[] { "Vigil incident: hitch", "weather = Clear" }));
            Assert.Null(EnvironmentReport.Parse(new string[0]));
        }

        [Fact]
        public void A_comparison_marks_what_differs_but_not_who_wrote_it_or_when()
        {
            var mine = new EnvironmentReport();
            mine.Values["player"] = "Sigrun";
            mine.Values["weather"] = "WarmSnow";
            mine.Values["fog_drawn"] = "0.042";
            mine.Values["bloom"] = "off";
            var theirs = new EnvironmentReport();
            theirs.Values["player"] = "D'Arcy";
            theirs.Values["weather"] = "WarmSnow";
            theirs.Values["fog_drawn"] = "0.180";
            var rows = EnvironmentReport.Compare(mine, theirs);
            Assert.False(rows.Single(r => r.Key == "player").Differs);
            Assert.False(rows.Single(r => r.Key == "weather").Differs);
            Assert.True(rows.Single(r => r.Key == "fog_drawn").Differs);
            var bloom = rows.Single(r => r.Key == "bloom");
            Assert.True(bloom.Differs);
            Assert.Equal("-", bloom.Theirs);
            Assert.Equal("Fog drawn", rows.Single(r => r.Key == "fog_drawn").Label);
            Assert.DoesNotContain(rows, r => r.Key == "gpu");
        }

        [Fact]
        public void A_weather_change_reads_as_one_line()
        {
            Assert.Equal("WarmSnow -> WarmSnowStorm (rolled from the Meadows list for period 7, next roll in 20 min)",
                WeatherCause.ChangeLine("WarmSnow", "WarmSnowStorm", "rolled from the Meadows list for period 7, next roll in 20 min"));
            Assert.Equal("Clear (set by a dev command (env), on this game only)", WeatherCause.ChangeLine(null, "Clear", "set by a dev command (env), on this game only"));
        }
        private static EnvironmentReport ReportOf(params (string Key, string Value)[] values)
        {
            var report = new EnvironmentReport();
            foreach (var (key, value) in values) report.Values[key] = value;
            return report;
        }

        [Fact]
        public void A_comparison_shows_what_differs_first_then_what_is_the_same()
        {
            var mine = ReportOf(("weather", "Clear"), ("cause", "set by an admin"), ("fog_drawn", "0.110"), ("bloom", "off"));
            var theirs = ReportOf(("weather", "Clear"), ("cause", "rolled from the Meadows list"), ("fog_drawn", "0.003"), ("bloom", "off"));
            var rows = EnvironmentReport.Compare(mine, theirs);
            Assert.Equal(new[] { "cause", "fog_drawn", "weather", "bloom" }, rows.Select(r => r.Key).ToArray());
            Assert.Equal(new[] { true, true, false, false }, rows.Select(r => r.Differs).ToArray());
        }

        [Fact]
        public void Lists_are_compared_item_by_item_by_their_names()
        {
            var mine = ReportOf(("mods", "Alpha: A.Prefix (prefix); Bravo: B.Postfix (postfix); Charlie: C.Prefix (prefix)"));
            var theirs = ReportOf(("mods", "Bravo: B.Other (postfix); Alpha: A.Prefix (prefix); Delta: D.Prefix (prefix)"));
            var mods = EnvironmentReport.Compare(mine, theirs).Single(r => r.Key == "mods");
            Assert.True(mods.Differs);
            Assert.Equal(new[] { "Charlie" }, mods.Items.OnlyMine);
            Assert.Equal(new[] { "Delta" }, mods.Items.OnlyTheirs);
            Assert.Equal(new[] { "Bravo" }, mods.Items.Unlike);
            Assert.Equal(1, mods.Items.Same);
        }

        [Fact]
        public void The_same_list_in_another_order_is_the_same()
        {
            var mine = ReportOf(("list", "Clear 5; Rain 0.2; Misty 0.2"));
            var theirs = ReportOf(("list", "Misty 0.2; Clear 5; Rain 0.2"));
            var list = EnvironmentReport.Compare(mine, theirs).Single(r => r.Key == "list");
            Assert.False(list.Differs);
            Assert.Equal(3, list.Items.Same);
        }

        [Fact]
        public void A_difference_reads_as_short_lines_of_yours_and_theirs()
        {
            var mine = ReportOf(("fog_drawn", "0.110"), ("list", "Clear 5; Rain 0.2"), ("mods", "Alpha: x; Bravo: y"), ("bloom", "off"));
            var theirs = ReportOf(("fog_drawn", "0.003"), ("list", "Clear 5; Rain 0.5"), ("mods", "Alpha: x; Bravo: z"));
            var rows = EnvironmentReport.Compare(mine, theirs);
            string Lines(string key) => string.Join(" | ", rows.Single(r => r.Key == key).Lines().Select(l => l.Key + ": " + l.Value));
            Assert.Equal("You: 0.110 | Them: 0.003", Lines("fog_drawn"));
            Assert.Equal("Only you: Rain 0.2 | Only them: Rain 0.5 | Same: 1 on both", Lines("list"));
            Assert.Equal("Unlike: Bravo | Same: 1 on both", Lines("mods"));
            Assert.Equal("You: off | Them: not in their report", Lines("bloom"));

            var same = EnvironmentReport.Compare(ReportOf(("weather", "Clear")), ReportOf(("weather", "Clear"))).Single();
            Assert.Equal("Both: Clear", string.Join(" | ", same.Lines().Select(l => l.Key + ": " + l.Value)));
        }
    }
}
