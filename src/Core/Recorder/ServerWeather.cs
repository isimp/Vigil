using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The server's weather, for every player running Vigil. No game sends its weather to another: each
    /// rolls it from the world's time and the biome it is in. So the server keeps what its admins chose
    /// (a config entry ServerSync passes on) and each game follows it: a weather <see cref="Set"/> for
    /// every biome, which the game's own overrides (a raid, a dungeon, the intro) still beat; or a
    /// <see cref="Freeze"/> holding the roll and the time of day of one world second, so each biome keeps
    /// the weather it had then, while the world's time, and so its age, goes on. Everyone sleeping
    /// through a night ends a freeze (<see cref="SleptThrough"/>); <see cref="None"/> is the game's own roll.
    /// </summary>
    internal sealed class ServerWeather
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>The weather set for every biome; null for the roll.</summary>
        public string Weather { get; private set; }

        public string SetBy { get; private set; }

        /// <summary>The world second the weather was set.</summary>
        public double SetAt { get; private set; }

        /// <summary>The world second whose roll and time of day a freeze holds; null when nothing is frozen.</summary>
        public double? HeldAt { get; private set; }

        public string HeldBy { get; private set; }

        public bool IsEmpty => Weather == null && HeldAt == null;

        public static ServerWeather None => new ServerWeather();

        public ServerWeather Set(string weather, string by, double worldSecond)
        {
            var w = Copy();
            w.Weather = string.IsNullOrEmpty(weather) ? null : weather;
            w.SetBy = by;
            w.SetAt = worldSecond;
            return w;
        }

        public ServerWeather Freeze(string by, double worldSecond)
        {
            var w = Copy();
            w.HeldAt = worldSecond;
            w.HeldBy = by;
            return w;
        }

        /// <summary>Everyone slept through a night: the freeze ends, a weather set for every biome stays.</summary>
        public ServerWeather SleptThrough()
        {
            var w = Copy();
            w.HeldAt = null;
            w.HeldBy = null;
            return w;
        }

        /// <summary>The weather to show when the game's own overrides give <paramref name="gameOverride"/>: theirs when they have one, else the server's (null for the roll).</summary>
        public string WeatherFor(string gameOverride) => !string.IsNullOrEmpty(gameOverride) ? gameOverride : Weather;

        /// <summary>The world second the game rolls its weather for: the frozen one, else now.</summary>
        public long RollSecond(long now) => HeldAt.HasValue ? (long)HeldAt.Value : now;

        /// <summary>The time of day a freeze holds, as the game's day fraction; null when nothing is frozen.</summary>
        public float? HeldDayFraction(long dayLengthSec) => HeldAt.HasValue ? DayClock.Fraction(HeldAt.Value, dayLengthSec) : (float?)null;

        public string Encode() => IsEmpty ? "" : KeyValues.Encode(new[]
        {
            new KeyValuePair<string, object>("weather", Weather), new KeyValuePair<string, object>("by", Weather != null ? SetBy : null),
            new KeyValuePair<string, object>("at", Weather != null ? SetAt : (object)null), new KeyValuePair<string, object>("held", HeldAt),
            new KeyValuePair<string, object>("heldby", HeldAt.HasValue ? HeldBy : null),
        });

        /// <summary>The server's line read back; whatever cannot be read is the game's own roll.</summary>
        public static ServerWeather Decode(string line)
        {
            var d = KeyValues.Decode(line);
            var w = new ServerWeather();
            if (d.TryGetValue("weather", out string weather) && weather.Length > 0)
            {
                w.Weather = weather;
                w.SetBy = d.TryGetValue("by", out string by) ? by : "?";
                double at = KeyValues.Number(d, "at");
                w.SetAt = double.IsNaN(at) ? 0 : at;
            }
            double held = KeyValues.Number(d, "held");
            if (!double.IsNaN(held))
            {
                w.HeldAt = held;
                w.HeldBy = d.TryGetValue("heldby", out string heldBy) ? heldBy : "?";
            }
            return w;
        }

        /// <summary>What the server holds now, who did it and when, in the world's days and hours.</summary>
        public string NowLine(long dayLengthSec)
        {
            if (IsEmpty) return "the game's own roll";
            string set = Weather != null ? Weather + " everywhere, set by " + SetBy + " " + When(SetAt, dayLengthSec) : null;
            if (!HeldAt.HasValue) return set + "; kept over restarts until changed";
            string held = "frozen by " + HeldBy + " " + When(HeldAt.Value, dayLengthSec);
            if (set == null) return held + ": each biome's weather and the time of day as then; sleeping through a night ends it";
            return set + ", and the time of day " + held + "; sleeping through a night ends the freeze";
        }

        /// <summary>The world day and the hour of a world second, as "on day N at hours:minutes".</summary>
        public static string When(double worldSecond, long dayLengthSec) =>
            "on day " + DayClock.DayOf(worldSecond, dayLengthSec).ToString(Ci) + " at " + DayClock.Clock(DayClock.Fraction(worldSecond, dayLengthSec));

        private ServerWeather Copy() => (ServerWeather)MemberwiseClone();
    }

    /// <summary>
    /// The game's clock: a day of <c>m_dayLengthSec</c> world seconds, whose fraction the game stretches
    /// so that daylight (a quarter to three quarters of the drawn day) takes 70 % of it; the drawn day
    /// fraction is what its day, night and sleeping rules read.
    /// </summary>
    internal static class DayClock
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>The day fraction the game draws and judges by for a world second.</summary>
        public static float Fraction(double worldSecond, long dayLengthSec)
        {
            if (dayLengthSec <= 0) return 0f;
            double len = dayLengthSec;
            float f = (float)Math.Max(0.0, Math.Min(1.0, (worldSecond % len + len) % len / len));
            if (f >= 0.15f && f <= 0.85f) return 0.25f + (f - 0.15f) / 0.7f * 0.5f;
            if (f < 0.5f) return f / 0.15f * 0.25f;
            return 0.75f + (f - 0.85f) / 0.15f * 0.25f;
        }

        public static long DayOf(double worldSecond, long dayLengthSec) => dayLengthSec > 0 ? (long)(worldSecond / dayLengthSec) : 0;

        /// <summary>The hour of a day fraction as hours:minutes, to the nearest minute.</summary>
        public static string Clock(float fraction)
        {
            int minutes = (int)Math.Round(fraction * 1440.0) % 1440;
            return (minutes / 60).ToString("00", Ci) + ":" + (minutes % 60).ToString("00", Ci);
        }

        /// <summary>Whether a bed takes a sleeper at this day fraction: in the afternoon or at night, as the game's rule has it.</summary>
        public static bool SleepTime(float fraction) => (fraction >= 0.5f && fraction <= 0.75f) || fraction <= 0.25f || fraction >= 0.75f;
    }

    /// <summary>A group of the weather picker's list: its title and its weathers, each with a note (a chance).</summary>
    internal sealed class PickGroup
    {
        public string Title;
        public List<PickItem> Items = new List<PickItem>();
    }

    internal sealed class PickItem
    {
        public string Name, Note;
    }

    /// <summary>The weather picker's list: the biome's own weathers with their chances first, then every weather the game knows, both narrowed to those containing what is typed.</summary>
    internal static class WeatherPick
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static List<PickGroup> Groups(string biome, IList<WeatherRoll.Entry> biomeList, IList<string> known, string typed)
        {
            string t = (typed ?? "").Trim();
            bool Fits(string name) => t.Length == 0 || (name ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
            var groups = new List<PickGroup>();
            var here = (biomeList ?? new List<WeatherRoll.Entry>()).Where(e => Fits(e.Name)).Select(e => new PickItem { Name = e.Name, Note = Math.Round(e.Chance).ToString("0", Ci) + " %" }).ToList();
            if (here.Count > 0) groups.Add(new PickGroup { Title = "In the " + biome + " list now", Items = here });
            var all = (known ?? new List<string>()).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList();
            var fits = all.Where(Fits).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal).Select(n => new PickItem { Name = n }).ToList();
            groups.Add(new PickGroup { Title = "Every weather the game knows (" + fits.Count.ToString(Ci) + " of " + all.Count.ToString(Ci) + ")", Items = fits });
            return groups;
        }
    }
}
