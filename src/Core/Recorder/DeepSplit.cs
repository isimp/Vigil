using System;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// Splits a patched game method's time into the game's code (measured between the last
    /// prefix and the first postfix, so it includes any transpiler changes) and what the mods'
    /// prefixes and postfixes add around it, and says how often a prefix stopped the original.
    /// </summary>
    public static class DeepSplit
    {
        // A method mods patch that takes this long in one frame is split from then on, so an action
        // repeated after its first stall shows the mods' share; capped, since each split rebuilds
        // the method once more.
        public const double SpikeMs = 20;
        public const int MaxSpikeSplits = 25;

        public static bool SplitOnSpike(double frameMs, bool patched, bool split, bool dropped, int spikeSplits) =>
            patched && !split && !dropped && frameMs >= SpikeMs && spikeSplits < MaxSpikeSplits;

        /// <summary>
        /// How the time measured while the split was on divides between the game's code and the
        /// mods' prefixes and postfixes, as shares: what a stalling action's repeat shows.
        /// </summary>
        public static string DescribeShareOfCalls(long splitWholeTicks, long originalTicks, long calls)
        {
            if (splitWholeTicks <= 0 || calls <= 0) return "";
            double game = Math.Min(1.0, Math.Max(0.0, (double)originalTicks / splitWholeTicks));
            return string.Format(CultureInfo.InvariantCulture, "after the split ({0} call{1}): game code {2:0}%, the mods' prefixes and postfixes {3:0}%",
                calls, calls == 1 ? "" : "s", 100 * game, 100 * (1 - game));
        }

        /// <summary>The method's longest single frame, when it stands out from its average per frame.</summary>
        public static string DescribeWorst(double averageMs, double worstMs) =>
            worstMs >= SpikeMs && worstMs >= 3 * averageMs
                ? string.Format(CultureInfo.InvariantCulture, "worst frame {0:0.0} ms", worstMs)
                : "";

        /// <param name="wholeMs">The method's time per frame, the mods' prefixes and postfixes included.</param>
        /// <param name="originalMs">The part of it spent in the game's own code.</param>
        /// <param name="calls">Calls measured after the split.</param>
        /// <param name="originalRuns">Calls in which the original ran; negative when the method was not split.</param>
        public static string Describe(double wholeMs, double originalMs, long calls, long originalRuns)
        {
            if (calls <= 0 || originalRuns < 0) return "";
            var ci = CultureInfo.InvariantCulture;
            double mods = Math.Max(0, wholeMs - originalMs);
            string text = string.Format(ci, "game code {0:0.000} ms, prefixes/postfixes {1:0.000} ms", originalMs, mods);
            long skipped = calls - Math.Min(calls, originalRuns);
            if (skipped > 0) text += string.Format(ci, ", original skipped in {0:0}% of calls", 100.0 * skipped / calls);
            return text;
        }

        /// <summary>
        /// The split for a method split over part of the capture only: the game's share of the
        /// time measured while the split was on, applied to the method's whole time.
        /// </summary>
        public static string DescribeShare(double wholeMs, long splitWholeTicks, long originalTicks, long calls, long originalRuns)
        {
            if (splitWholeTicks <= 0) return "";
            double share = Math.Min(1.0, Math.Max(0.0, (double)originalTicks / splitWholeTicks));
            return Describe(wholeMs, wholeMs * share, calls, originalRuns);
        }
    }
}
