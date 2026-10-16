using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The server's weather, for every player running Vigil: an admin sets one weather for every biome,
    /// or freezes each biome's weather and the time of day as they are, while the world's time goes on;
    /// the raids, dungeons and the intro keep their own weather; everyone sleeping through the night
    /// ends a freeze; "back" gives the game's own roll again. The weather is picked from a list: the
    /// biome's list first, then every weather the game knows, both narrowed by what is typed.
    /// </summary>
    public sealed class ServerWeatherTests
    {
        private const long Day = 1200;

        [Fact]
        public void An_admins_weather_is_kept_as_one_line_and_read_back()
        {
            var set = ServerWeather.None.Set("DeepForest Mist", "Olebole", 412 * Day + 700).Freeze("Dom", 413 * Day + 100);
            var back = ServerWeather.Decode(set.Encode());
            Assert.Equal("DeepForest Mist", back.Weather);
            Assert.Equal("Olebole", back.SetBy);
            Assert.Equal(412 * Day + 700, back.SetAt);
            Assert.Equal(413 * Day + 100, back.HeldAt);
            Assert.Equal("Dom", back.HeldBy);
            Assert.False(back.IsEmpty);
        }

        [Fact]
        public void An_empty_or_broken_line_is_the_games_own_roll()
        {
            Assert.True(ServerWeather.Decode("").IsEmpty);
            Assert.True(ServerWeather.Decode(null).IsEmpty);
            Assert.True(ServerWeather.Decode("held=soon;weather=").IsEmpty);
            Assert.Equal("", ServerWeather.None.Encode());
            Assert.True(ServerWeather.None.Set("", "Olebole", 5).IsEmpty);
        }

        [Fact]
        public void A_set_weather_shows_in_every_biome_below_raids_dungeons_and_the_intro()
        {
            var set = ServerWeather.None.Set("ThunderStorm", "Olebole", 100);
            Assert.Equal("ThunderStorm", set.WeatherFor(null));
            Assert.Equal("ThunderStorm", set.WeatherFor(""));
            Assert.Equal("Crypt", set.WeatherFor("Crypt"));
            Assert.Null(ServerWeather.None.WeatherFor(null));
            Assert.Null(ServerWeather.None.Freeze("Olebole", 100).WeatherFor(null));
        }

        [Fact]
        public void A_freeze_holds_each_biomes_roll_and_the_time_of_day_while_the_world_time_goes_on()
        {
            var held = ServerWeather.None.Freeze("Olebole", 412 * Day + 600);
            Assert.Equal(412 * Day + 600, held.RollSecond(415 * Day + 3));
            Assert.Equal(0.5f, held.HeldDayFraction(Day).Value, 3);
            Assert.Equal(415 * Day + 3, ServerWeather.None.RollSecond(415 * Day + 3));
            Assert.Null(ServerWeather.None.Set("Rain", "Olebole", 5).HeldDayFraction(Day));
        }

        [Fact]
        public void Sleeping_through_the_night_ends_a_freeze_but_not_a_set_weather()
        {
            var both = ServerWeather.None.Set("Rain", "Olebole", 5).Freeze("Olebole", 10);
            var slept = both.SleptThrough();
            Assert.Null(slept.HeldAt);
            Assert.Equal("Rain", slept.Weather);
            Assert.True(ServerWeather.None.Freeze("Olebole", 10).SleptThrough().IsEmpty);
        }

        [Fact]
        public void Setting_a_weather_keeps_a_freeze_and_freezing_keeps_a_set_weather()
        {
            var held = ServerWeather.None.Freeze("Dom", 10).Set("Snow", "Olebole", 20);
            Assert.Equal(10, held.HeldAt);
            Assert.Equal("Snow", held.Weather);
            Assert.Equal("Snow", ServerWeather.None.Set("Snow", "Olebole", 20).Freeze("Dom", 30).Weather);
        }

        [Fact]
        public void The_line_now_says_what_the_server_holds_who_did_it_and_when()
        {
            Assert.Equal("the game's own roll", ServerWeather.None.NowLine(Day));
            Assert.Equal("ThunderStorm everywhere, set by Olebole on day 412 at 14:00; kept over restarts until changed",
                ServerWeather.None.Set("ThunderStorm", "Olebole", 412 * Day + 740).NowLine(Day));
            Assert.Equal("frozen by Dom on day 413 at 12:00: each biome's weather and the time of day as then; sleeping through a night ends it",
                ServerWeather.None.Freeze("Dom", 413 * Day + 600).NowLine(Day));
            Assert.Equal("ThunderStorm everywhere, set by Olebole on day 412 at 14:00, and the time of day frozen by Dom on day 413 at 12:00; sleeping through a night ends the freeze",
                ServerWeather.None.Set("ThunderStorm", "Olebole", 412 * Day + 740).Freeze("Dom", 413 * Day + 600).NowLine(Day));
        }

        [Fact]
        public void The_clock_follows_the_games_day_with_its_long_daylight()
        {
            Assert.Equal(0f, DayClock.Fraction(0, Day), 4);
            Assert.Equal(0.125f, DayClock.Fraction(Day * 0.075, Day), 4);
            Assert.Equal("12:00", DayClock.Clock(0.49999f));
            Assert.Equal(0.25f, DayClock.Fraction(Day * 0.15, Day), 4);
            Assert.Equal(0.5f, DayClock.Fraction(Day * 0.5, Day), 4);
            Assert.Equal(0.75f, DayClock.Fraction(Day * 0.85, Day), 4);
            Assert.Equal(0.875f, DayClock.Fraction(Day * 0.925, Day), 4);
            Assert.Equal(0.25f, DayClock.Fraction(7 * Day + Day * 0.15, Day), 4);
            Assert.Equal("14:00", DayClock.Clock(DayClock.Fraction(740, Day)));
            Assert.Equal(412, DayClock.DayOf(412 * Day + 1199, Day));
        }

        [Fact]
        public void Beds_work_in_the_afternoon_and_at_night_as_the_game_has_it()
        {
            Assert.True(DayClock.SleepTime(0.6f));
            Assert.True(DayClock.SleepTime(0.9f));
            Assert.True(DayClock.SleepTime(0.1f));
            Assert.False(DayClock.SleepTime(0.3f));
            Assert.False(DayClock.SleepTime(0.45f));
        }

        private static readonly List<WeatherRoll.Entry> Meadows = WeatherRoll.Chances(new[]
        {
            new KeyValuePair<string, float>("Clear", 5f), new KeyValuePair<string, float>("Rain", 0.2f), new KeyValuePair<string, float>("ThunderStorm", 0.2f),
        }, "Clear");

        private static readonly string[] Known = { "Twilight_Clear", "rain_light", "Clear", "Rain", "Ashlands_storm", "ThunderStorm", "Crypt" };

        [Fact]
        public void The_list_offers_the_biomes_weathers_first_then_every_weather_the_game_knows()
        {
            var groups = WeatherPick.Groups("Meadows", Meadows, Known, "");
            Assert.Equal(new[] { "In the Meadows list now", "Every weather the game knows (7 of 7)" }, groups.Select(g => g.Title));
            Assert.Equal(new[] { "Clear", "Rain", "ThunderStorm" }, groups[0].Items.Select(i => i.Name));
            Assert.Equal(new[] { "93 %", "4 %", "4 %" }, groups[0].Items.Select(i => i.Note));
            Assert.Equal(new[] { "Ashlands_storm", "Clear", "Crypt", "Rain", "rain_light", "ThunderStorm", "Twilight_Clear" }, groups[1].Items.Select(i => i.Name));
        }

        [Fact]
        public void Typing_narrows_both_lists_to_the_weathers_containing_it()
        {
            var groups = WeatherPick.Groups("Meadows", Meadows, Known, "RAIN");
            Assert.Equal(new[] { "Rain" }, groups[0].Items.Select(i => i.Name));
            Assert.Equal("Every weather the game knows (2 of 7)", groups[1].Title);
            Assert.Equal(new[] { "Rain", "rain_light" }, groups[1].Items.Select(i => i.Name));
            var none = WeatherPick.Groups("Meadows", Meadows, Known, "crypt");
            Assert.Equal(new[] { "Every weather the game knows (1 of 7)" }, none.Select(g => g.Title));
        }
    }
}
