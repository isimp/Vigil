namespace Vigil.Core
{
    /// <summary>
    /// The game patch "Skip winter": Seasonality (3.8.x) picks the next season in
    /// <c>SeasonalTimer.GetNextSeason</c>, called only where the season changes (the dedicated
    /// server, or a game that hosts its world); when that would be winter, it is spring. A season set
    /// by hand (an admin command or Seasonality's own settings) is left alone.
    /// </summary>
    internal static class SeasonSkip
    {
        /// <summary>The season to start instead of <paramref name="next"/>, by Seasonality's name for it.</summary>
        public static string Next(string next) => next == "Winter" ? "Spring" : next;

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool found, string version, string problem)
        {
            if (!on) return "off: the server's switch";
            if (!found) return "not needed: Seasonality is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return "working · Seasonality " + version + " hooked, checked: fall now goes to spring";
        }
    }
}
