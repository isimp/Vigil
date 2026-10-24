using System;
using System.Linq;
using System.Reflection;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "Skip winter": when Seasonality's next-season rule would start winter, it is
    /// spring. From 3.8.5 every game turns the season itself when its time is up and the server's
    /// season reaches the others every 2 s; up to 3.8.4 only the dedicated server (or a game that
    /// hosts its world) turned it. A season set by hand (an admin command or Seasonality's own
    /// settings) is left alone.
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

    /// <summary>
    /// Seasonality's next-season rule as the installed version has it: from 3.8.5,
    /// <c>SeasonTime.GetNextSeason()</c> reads the season under way from <c>SeasonTime.m_currentSeason</c>;
    /// up to 3.8.4, <c>SeasonalTimer.GetNextSeason(Season)</c> is given it.
    /// </summary>
    internal sealed class SeasonRule
    {
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public MethodInfo Next { get; private set; }

        public Type Seasons { get; private set; }

        /// <summary>The field the rule reads the season under way from; null when the rule is given it.</summary>
        public FieldInfo Current { get; private set; }

        /// <summary>The rule from Seasonality's types by full name, or null when neither shape is there.</summary>
        public static SeasonRule Find(Func<string, Type> type)
        {
            var time = type("Seasonality.SeasonTime");
            var next = time?.GetMethod("GetNextSeason", Static, null, Type.EmptyTypes, null);
            var current = time?.GetField("m_currentSeason", Static);
            if (next != null && current != null && next.ReturnType == current.FieldType && Usable(next.ReturnType))
            {
                return new SeasonRule { Next = next, Seasons = next.ReturnType, Current = current };
            }

            var timer = type("Seasonality.SeasonalTimer");
            var given = timer?.GetMethods(Static).FirstOrDefault(m => m.Name == "GetNextSeason" && m.GetParameters().Length == 1);
            var seasons = given?.GetParameters()[0].ParameterType;
            if (given != null && given.ReturnType == seasons && Usable(seasons))
            {
                return new SeasonRule { Next = given, Seasons = seasons };
            }

            return null;
        }

        private static bool Usable(Type seasons) =>
            seasons.IsEnum && Enum.IsDefined(seasons, "Fall") && Enum.IsDefined(seasons, "Winter") && Enum.IsDefined(seasons, "Spring");

        /// <summary>Asks the rule for the season after fall; a season under way that the rule reads is put back after.</summary>
        public object AfterFall()
        {
            object fall = Enum.Parse(Seasons, "Fall");
            if (Current == null) return Next.Invoke(null, new[] { fall });
            object was = Current.GetValue(null);
            Current.SetValue(null, fall);
            try
            {
                return Next.Invoke(null, null);
            }
            finally
            {
                Current.SetValue(null, was);
            }
        }
    }
}
