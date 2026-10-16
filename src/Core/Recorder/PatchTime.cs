using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>One mod's patch method as a deep capture timed it: the mod, which patch on which game method, its calls and its time.</summary>
    internal sealed class PatchTiming
    {
        public PatchTiming(string mod, string label, long calls, double ms)
        {
            Mod = mod;
            Label = label;
            Calls = calls;
            Ms = ms;
        }

        public string Mod { get; }
        public string Label { get; }
        public long Calls { get; }
        public double Ms { get; }
    }

    /// <summary>One mod's own patch code in a deep capture: its time and calls per frame, and its costliest patch.</summary>
    internal sealed class PatchTimeRow
    {
        public string Mod, Top;
        public double MsPerFrame, CallsPerFrame, TopMsPerFrame;
    }

    /// <summary>
    /// Each mod's own patch code in a deep capture: its prefixes, postfixes and finalizers on the game
    /// methods the capture splits (the costliest ones), each timed by itself, the timers' own cost per
    /// call taken off (<see cref="OwnCost.Net"/>); and the mods whose transpilers change the game's
    /// code, which can only be timed inside the methods they change.
    /// </summary>
    internal static class PatchTime
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static List<PatchTimeRow> ByMod(IEnumerable<PatchTiming> timed, double frames, double perCallMs)
        {
            double f = frames > 0 ? frames : 1;
            return timed.GroupBy(t => t.Mod)
                .Select(g =>
                {
                    var nets = g.Select(t => new { t.Label, t.Calls, Ms = OwnCost.Net(t.Ms, t.Calls, perCallMs) }).ToList();
                    var top = nets.OrderByDescending(n => n.Ms).ThenBy(n => n.Label, StringComparer.Ordinal).First();
                    return new PatchTimeRow
                    {
                        Mod = g.Key, MsPerFrame = nets.Sum(n => n.Ms) / f, CallsPerFrame = nets.Sum(n => n.Calls) / f, Top = top.Label, TopMsPerFrame = top.Ms / f,
                    };
                })
                .OrderByDescending(r => r.MsPerFrame).ThenBy(r => r.Mod, StringComparer.Ordinal).ToList();
        }

        /// <summary>"      0.780 ms  100.2 calls  Protective Wards   top: WearNTear.UpdateWear postfix 0.750 ms".</summary>
        public static string Line(PatchTimeRow r) =>
            string.Format(Ci, "   {0,8:0.000} ms {1,6:0.0} calls  {2}   top: {3} {4:0.000} ms", r.MsPerFrame, r.CallsPerFrame, r.Mod, r.Top, r.TopMsPerFrame);

        /// <summary>"ValheimCommunityPatch 14 methods, Get Out Of The Water 2, Jotunn 1", the most first; "none".</summary>
        public static string TranspilerLine(IDictionary<string, int> methodsByMod)
        {
            if (methodsByMod.Count == 0) return "none";
            var ordered = methodsByMod.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            return string.Join(", ", ordered.Select((kv, i) => kv.Key + " " + kv.Value.ToString(Ci) + (i == 0 ? " methods" : "")).ToArray());
        }
    }
}
