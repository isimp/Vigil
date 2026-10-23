using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// Fire sounds, a player's own setting. The game's fires play their loop through its sound
    /// component (ZSFX), which picks the level between the fire's lowest and highest each time the
    /// loop starts, fades it in, adds reverb with distance, and lets the game keep only the nearest
    /// few loops of one kind audible. A mod's fire whose loop is a plain source plays at the
    /// source's own level, every one of them, however many stand together. Vigil gives such a loop
    /// the game's rules with the values of the game's hearth, and applies the player's level to
    /// the loops of the fires they chose: under the rules through the component's own modifier,
    /// which no fire code writes, on a plain loop as its own level times the player's.
    /// </summary>
    internal static class FireSounds
    {
        /// <summary>Which fires the level reaches.</summary>
        public enum Scope
        {
            /// <summary>Fires whose loop came without the game's sound rules, as their mod made them.</summary>
            WithoutRules,

            /// <summary>Every fire's loop.</summary>
            All,
        }

        /// <summary>The words for each <see cref="Scope"/>, in its order.</summary>
        public static readonly string[] ScopeWords = { "Fires without the game's sound rules", "All fires" };

        /// <summary>The level that changes nothing.</summary>
        public const int DefaultPercent = 100;

        /// <summary>How far around the player the panel counts fire loops: the game's fire loops reach 10 to 20 m.</summary>
        public const float NearMetres = 20f;

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>The game's hearth's values for its loop, given to a loop that came without the rules.</summary>
        public static class HearthRules
        {
            public const float MinVol = 0.4f, MaxVol = 0.6f, FadeInSeconds = 0.1f;

            /// <summary>Loops of one kind kept audible, nearest first (the game lets one more through).</summary>
            public const int NearestKept = 2;

            public const bool DistanceReverb = true;
        }

        /// <summary>One loaded fire loop, as the panel and the self-test see it.</summary>
        public sealed class LoopFacts
        {
            public LoopFacts(string kind, bool hadRules, bool onMixer)
            {
                Kind = kind ?? "";
                HadRules = hadRules;
                OnMixer = onMixer;
            }

            /// <summary>The fire's prefab name.</summary>
            public string Kind { get; }

            /// <summary>Whether the loop came with the game's sound component.</summary>
            public bool HadRules { get; }

            /// <summary>Whether the loop plays through one of the game's mixer groups, where the SFX volume applies.</summary>
            public bool OnMixer { get; }
        }

        public static int ClampPercent(int percent) => Math.Max(0, Math.Min(100, percent));

        /// <summary>The factor a loop gets: the player's level where the scope reaches the fire, 1 elsewhere.</summary>
        public static float Modifier(int percent, Scope scope, bool hadRules) =>
            scope == Scope.All || !hadRules ? ClampPercent(percent) / 100f : 1f;

        /// <summary>"40 %", or "Off" at nothing.</summary>
        public static string PercentLabel(int percent)
        {
            int p = ClampPercent(percent);
            return p == 0 ? "Off" : p.ToString(Ci) + " %";
        }

        /// <summary>
        /// The hash the game groups loops of one kind by for its cap; a component the game's files
        /// made carries one of its own, so one given at load gets one made from the fire's kind.
        /// </summary>
        public static int KindHash(string kind)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in kind ?? "")
                {
                    h = h * 31 + c;
                }

                return h;
            }
        }

        /// <summary>Whether a loop's level is the one the player's setting calls for.</summary>
        public static bool LevelAgrees(float actual, float expected) => Math.Abs(actual - expected) <= 0.005f;

        /// <summary>"5 fire loops, 3 without the game's rules (Hearth_2x3_bal ×2, Hearth_1x3_bal)".</summary>
        public static string NearLine(IEnumerable<LoopFacts> loops, float metres)
        {
            var list = loops?.ToList() ?? new List<LoopFacts>();
            if (list.Count == 0) return "no fire loops within " + metres.ToString("0", Ci) + " m";
            var plain = list.Where(l => !l.HadRules).ToList();
            string head = list.Count.ToString(Ci) + (list.Count == 1 ? " fire loop, " : " fire loops, ");
            string rules = plain.Count == 0 ? "all with the game's rules"
                : plain.Count.ToString(Ci) + " without the game's rules (" + string.Join(", ", plain.GroupBy(l => l.Kind, StringComparer.Ordinal)
                    .Select(g => g.Count() > 1 ? g.Key + " ×" + g.Count().ToString(Ci) : g.Key)) + ")";
            int off = list.Count(l => !l.OnMixer);
            string mixer = off == 0 ? "" : ", " + off.ToString(Ci) + " off the game's sound mixer, so the game's SFX volume does not reach " + (off == 1 ? "it" : "them");
            return head + rules + mixer;
        }

        /// <summary>What the switch does on this game, in words.</summary>
        public static string RulesLine(bool on, int given, string problem)
        {
            if (problem != null) return "stepped aside: " + problem;
            if (!on) return "off: a mod's fire plays its loop as the mod made it";
            if (given == 0) return "on: no fire loop without the game's rules loaded yet";
            return "on: " + given.ToString(Ci) + (given == 1 ? " fire loop" : " fire loops") + " given the game's rules this session";
        }

        /// <summary>The section's badge: the level when it is not full, else that the rules are on, else none.</summary>
        public static string Badge(int percent, bool rulesOn)
        {
            int p = ClampPercent(percent);
            if (p != DefaultPercent) return PercentLabel(p);
            return rulesOn ? "rules on" : null;
        }
    }
}
