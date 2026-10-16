using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The self-test's check of Vigil's object readings against the game's own: where Vigil works a
    /// fact out by itself (its 17 cover rays, the ray up from a crop, what stands in a crop's way,
    /// what blocks a smelter's vent, the comfort it counts), it should come to what the game decides.
    /// The cover and a beehive's space are compared the same moment, so a difference is a
    /// failure; the game checks a crop's status and a smelter's smoke only every few seconds, so a
    /// difference there is a warning. A mod may change comfort, so a different count is a warning of its own.
    /// </summary>
    internal static class RuleAgreement
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>One object compared: its kind, its name, and how Vigil and the game differ (null when they agree).</summary>
        public sealed class Finding
        {
            public Finding(string kind, string name, string disagreement, bool hard)
            {
                Kind = kind;
                Name = name;
                Disagreement = disagreement;
                Hard = hard;
            }

            public string Kind { get; }
            public string Name { get; }
            public string Disagreement { get; }

            /// <summary>Compared the same moment as the game decides, so a difference is no matter of timing.</summary>
            public bool Hard { get; }
        }

        public static string Cover(int vigilBlocked, float gameShare)
        {
            int game = (int)Math.Round(gameShare * ObjectRules.Rays);
            return game == vigilBlocked ? null
                : "Vigil's rays find " + vigilBlocked.ToString(Ci) + " of " + ObjectRules.Rays.ToString(Ci) + " blocked, the game's cover is " + game.ToString(Ci) + " of " + ObjectRules.Rays.ToString(Ci);
        }

        public static string Bees(int blocked, float maxCover, bool gameHasSpace)
        {
            // A hive with no cover limit always has space, as the game has it.
            bool space = maxCover <= 0f || blocked / (float)ObjectRules.Rays < maxCover;
            if (space == gameHasSpace) return null;
            string rays = blocked.ToString(Ci) + " of " + ObjectRules.Rays.ToString(Ci) + " blocked, " + (space ? "under " : "") + Math.Round(maxCover * 100).ToString("0", Ci) + " %" + (space ? "" : " or more");
            return space ? "Vigil says it has space (" + rays + "), the game says it lacks it" : "Vigil says it lacks space (" + rays + "), the game says it has it";
        }

        public static string Crop(string status, bool skyBlocked, bool inWay)
        {
            switch (status)
            {
                case "NoSun": return skyBlocked ? null : "the game says no sun, Vigil's ray up finds nothing";
                case "NoSpace": return inWay ? null : "the game says no space, Vigil finds nothing in the way";
                case "Healthy":
                    if (skyBlocked) return "the game says healthy, Vigil's ray up is blocked";
                    return inWay ? "the game says healthy, Vigil finds something in the way" : null;
                default: return null;
            }
        }

        public static string Smoke(bool blocked, string ventBlocker) =>
            !blocked || !string.IsNullOrEmpty(ventBlocker) ? null : "the game says its smoke is blocked, Vigil names nothing";

        public static string Comfort(int vigil, int game) =>
            vigil == game ? null : "Vigil counts " + vigil.ToString(Ci) + ", the game " + game.ToString(Ci);

        /// <summary>The check's result and words for everything compared within <paramref name="radius"/> metres.</summary>
        public static (SelfTestReport.Result, string) Verdict(IList<Finding> findings, float radius)
        {
            if (findings.Count == 0)
                return (SelfTestReport.Result.Info, "no fermenter, beehive, crop, smelter or fire within " + radius.ToString("0", Ci) + " m");
            var differ = findings.Where(f => f.Disagreement != null).ToList();
            if (differ.Count == 0)
            {
                var kinds = findings.GroupBy(f => f.Kind).Select(g => g.Key + " " + g.Count().ToString(Ci));
                return (SelfTestReport.Result.Pass, "all " + findings.Count.ToString(Ci) + " agree: " + string.Join(", ", kinds.ToArray()));
            }
            bool hard = differ.Any(f => f.Hard);
            string list = string.Join("; ", differ.Select(f => f.Kind + " " + f.Name + ": " + f.Disagreement).ToArray());
            return (hard ? SelfTestReport.Result.Fail : SelfTestReport.Result.Warn,
                differ.Count.ToString(Ci) + " of " + findings.Count.ToString(Ci) + " disagree" + (hard ? "" : " (the game checks crops and smoke only every few seconds)") + ": " + list);
        }
    }

    /// <summary>
    /// The server's weather tried on the player's own game by the self-test: another weather than the
    /// one now, a storm where the game has one; a freeze an hour of the game's day earlier, so the hour
    /// drawn moves a little and is read back. The trial ends on its own after <see cref="Seconds"/>.
    /// </summary>
    internal static class WeatherTrial
    {
        public const float Seconds = 10f;

        public static string Pick(IList<string> known, string current)
        {
            var others = (known ?? new List<string>()).Where(n => !string.IsNullOrEmpty(n) && n != current).ToList();
            if (others.Contains("ThunderStorm")) return "ThunderStorm";
            if (others.Contains("Rain")) return "Rain";
            return others.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        public static double HeldSecond(double now, long dayLengthSec) => now - dayLengthSec / 24.0;
    }
}
