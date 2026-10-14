using System;

namespace Vigil.Core
{
    /// <summary>
    /// Vigil's own work in the per-mod timing. Its callbacks cannot be timed with hooks like other
    /// mods' (the recorder closes each frame inside its own Update), so the plugin times each area's
    /// call itself and names it like a mod's callback; the part the recorder already counts as its
    /// own frame work (the timeline's probe_ms) is left out.
    /// </summary>
    internal static class OwnTime
    {
        public const string Mod = "Vigil";

        public static string Label(string area, string call) => Mod + " :: " + area + "." + call;

        /// <summary>The ticks one call took, less <paramref name="timedElsewhere"/> (never below zero).</summary>
        public static long Counted(long start, long end, long timedElsewhere) => Math.Max(0, end - start - Math.Max(0, timedElsewhere));
    }
}
