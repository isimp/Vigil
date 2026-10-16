using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>One of Vigil's hooks as one measurement found it: the game method it hooks, its calls, and its time with the timers' own cost taken off.</summary>
    internal sealed class HookCost
    {
        public HookCost(string label, long calls, double ms)
        {
            Label = label;
            Calls = calls;
            Ms = ms;
        }

        public string Label { get; }
        public long Calls { get; }
        public double Ms { get; }
    }

    /// <summary>One measurement of Vigil's hooks: over how long and how many frames, how long ago, and each hook.</summary>
    internal sealed class HookMeasure
    {
        public double Seconds, Frames, MinutesAgo;
        public List<HookCost> Hooks = new List<HookCost>();
    }

    /// <summary>One of Vigil's one-off tools over the session: its time on the game's thread, and the frames, seconds and runs it ran for.</summary>
    internal sealed class ToolCost
    {
        public ToolCost(string name, double ms, long frames, double seconds, int runs)
        {
            Name = name;
            Ms = ms;
            Frames = frames;
            Seconds = seconds;
            Runs = runs;
        }

        public string Name { get; }
        public double Ms { get; }
        public long Frames { get; }
        public double Seconds { get; }
        public int Runs { get; }
    }

    /// <summary>What goes into Vigil's own cost.</summary>
    internal sealed class OwnCostFacts
    {
        /// <summary>The game's mean frame time, for the share Vigil takes.</summary>
        public double FrameMs;

        /// <summary>Vigil's own loops per frame (its areas' calls and the recorder's frame work), the panel left out.</summary>
        public double LoopsMs;

        /// <summary>The panel's drawing time in all, and the frames it was drawn in.</summary>
        public double PanelMs;
        public long PanelOpenFrames;

        /// <summary>Calls of the always-on timers per frame, and the cost of one timer pair (NaN until known).</summary>
        public double TimerCallsPerFrame, TimerPairMs = double.NaN;

        /// <summary>The last measurement of its hooks; null until there is one.</summary>
        public HookMeasure Hooks;

        public double ThreadSecondsPerMinute, FilesKbPerMinute, NetOutKbs, NetInKbs;

        /// <summary>The one-off tools that ran (the self-test, deep captures), apart from the steady work in <see cref="LoopsMs"/>.</summary>
        public List<ToolCost> Tools = new List<ToolCost>();

        /// <summary>The reports taken: how many, and their time on the game's thread in all.</summary>
        public long ReportCount;
        public double ReportMs;

        /// <summary>Frames and minutes over the session, for the cost with the tools.</summary>
        public long FramesAll;
        public double SessionMinutes;
    }

    /// <summary>
    /// Vigil's own cost in words, for its card and the summary: its steady work (one-off tools apart:
    /// the self-test, deep captures and reports, each per frame while it ran); its loops every frame; its hooks in
    /// the game's methods, which run on hot paths and so are timed only now and then, for a few
    /// seconds, the timers' own cost per call taken off (<see cref="Net"/>); its always-on timers on
    /// the mods' callbacks, at their calls times one timer pair's cost; the panel's extra while drawn;
    /// its threads, files and network messages. Memory per mod is not something the runtime tells.
    /// </summary>
    internal static class OwnCost
    {
        private const int HooksListed = 3;
        public const string MemoryLine = "not measured: the game's runtime does not tell memory per mod";
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>A hook's measured time less <paramref name="calls"/> times the timers' cost per call, never below nothing.</summary>
        public static double Net(double measuredMs, long calls, double perCallMs) => Math.Max(0, measuredMs - calls * perCallMs);

        public static double HooksMsPerFrame(HookMeasure m) => m == null || m.Frames <= 0 ? 0 : m.Hooks.Sum(h => h.Ms) / m.Frames;

        public static double TimersMsPerFrame(OwnCostFacts f) => double.IsNaN(f.TimerPairMs) ? 0 : f.TimerCallsPerFrame * f.TimerPairMs;

        public static double TotalMs(OwnCostFacts f) => f.LoopsMs + HooksMsPerFrame(f.Hooks) + TimersMsPerFrame(f);

        public static string Badge(OwnCostFacts f) => Ms(TotalMs(f)) + " ms per frame";

        /// <summary>"while they ran: self-test 6.2 ms per frame over 41 s (2 runs) · deep capture 1.9 ms per frame over 95 s · reports 18 ms each, 7 written".</summary>
        public static string ToolsLine(OwnCostFacts f)
        {
            var parts = f.Tools.Where(t => t.Frames > 0).Select(t => t.Name + " " + (t.Ms / t.Frames).ToString("0.0", Ci) + " ms per frame over "
                + Math.Round(t.Seconds).ToString("0", Ci) + " s" + (t.Runs > 1 ? " (" + t.Runs.ToString(Ci) + " runs)" : "")).ToList();
            if (f.ReportCount > 0) parts.Add("reports " + Math.Round(f.ReportMs / f.ReportCount).ToString("0", Ci) + " ms each, " + f.ReportCount.ToString(Ci) + " written");
            return parts.Count == 0 ? "none ran this session" : "while they ran: " + string.Join(" · ", parts.ToArray());
        }

        /// <summary>The steady cost per frame with the tools' time spread over every frame of the session.</summary>
        public static double SessionMs(OwnCostFacts f) =>
            TotalMs(f) + (f.FramesAll > 0 ? (f.Tools.Sum(t => t.Ms) + f.ReportMs) / f.FramesAll : 0);

        /// <summary>"1.79 ms per frame with the tools, over 14 min".</summary>
        public static string SessionLine(OwnCostFacts f) =>
            Ms(SessionMs(f)) + " ms per frame with the tools, over " + Math.Round(f.SessionMinutes).ToString("0", Ci) + " min";

        public static string FrameLine(OwnCostFacts f)
        {
            double total = TotalMs(f);
            string share = f.FrameMs > 0 ? ", " + (total / f.FrameMs * 100).ToString("0.0", Ci) + " % of yours" : "";
            string loops = "its loops " + Ms(f.LoopsMs), timers = "its timers " + Ms(TimersMsPerFrame(f));
            if (f.Hooks == null) return Ms(total) + " ms per frame so far" + share + ": " + loops + " · " + timers + " · its hooks not measured yet";
            return Ms(total) + " ms per frame" + share + ": " + loops + " · its hooks in the game " + Ms(HooksMsPerFrame(f.Hooks)) + " · " + timers;
        }

        public static string HooksLine(HookMeasure m)
        {
            if (m == null) return "not measured yet: Measure times them for 10 s (it also runs once on its own, 2 minutes into a session)";
            long calls = m.Hooks.Sum(h => h.Calls);
            double perSecond = m.Seconds > 0 ? calls / m.Seconds : 0;
            var top = m.Hooks.OrderByDescending(h => h.Ms).Take(HooksListed).Select(h => h.Label + " " + Ms(m.Frames > 0 ? h.Ms / m.Frames : 0));
            return m.Hooks.Count.ToString(Ci) + " hooks, " + Math.Round(perSecond).ToString("N0", Ci) + " calls/s: " + string.Join(" · ", top.ToArray())
                + " ms per frame; measured " + Math.Round(m.MinutesAgo).ToString("0", Ci) + " min ago over " + Math.Round(m.Seconds).ToString("0", Ci) + " s";
        }

        public static string PanelLine(OwnCostFacts f) =>
            f.PanelOpenFrames <= 0 ? "the panel was not open" : "+" + Ms(f.PanelMs / f.PanelOpenFrames) + " ms per frame while the panel is drawn";

        public static string ThreadsLine(OwnCostFacts f) => f.ThreadSecondsPerMinute.ToString("0.0", Ci) + " s per minute (reports, names), not on the game's frame";

        public static string FilesLine(OwnCostFacts f) => Math.Round(f.FilesKbPerMinute).ToString("0", Ci) + " kB per minute into its recordings";

        public static string NetworkLine(OwnCostFacts f) =>
            f.NetOutKbs.ToString("0.0", Ci) + " kB/s out, " + f.NetInKbs.ToString("0.0", Ci) + " kB/s in (its hello, alerts, reports and shared files)";

        private static string Ms(double ms) => ms.ToString("0.00", Ci);
    }
}
