using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// A timed piece of work in one frame. <see cref="Parent"/> names the timed work it ran inside,
    /// if any; <see cref="Calls"/> is how often it ran in that frame.
    /// </summary>
    public sealed class Timed
    {
        public Timed(string name, float ms, string parent = null, int calls = 1)
        {
            Name = name; Ms = ms; Parent = parent; Calls = Math.Max(1, calls);
        }
        public string Name { get; }
        public float Ms { get; }
        public string Parent { get; }
        public int Calls { get; }
    }

    /// <summary>What is known about one frame: its length, the timed work in it, and conditions around it.</summary>
    public sealed class FrameBreakdown
    {
        public float DtMs;
        public int GcCollections;
        public int FixedSteps;
        public float RenderMs;
        public float PageFaultsPerSec;
        /// <summary>For example "teleporting" or "loading"; null in normal play.</summary>
        public string Situation;
        /// <summary>Time Vigil itself spent in this frame (writing a report, or one of its tools such as the census), outside every timer.</summary>
        public float OwnMs;
        public readonly List<Timed> Phases = new List<Timed>();
        public readonly List<Timed> Mods = new List<Timed>();
    }

    public sealed class HitchExplanation
    {
        public string Headline;
        public List<string> Causes = new List<string>();
        public float UnexplainedMs;
    }

    /// <summary>
    /// Explains a long frame in words: the timed work that took a large part of it (biggest
    /// first), the part no timer covered, and conditions that make frames long (garbage
    /// collection, physics catching up, paging).
    /// </summary>
    public static class HitchAnalysis
    {
        private const float NamedShare = 0.10f;
        private const float NamedMs = 100f;
        private const float UntimedShare = 0.30f;
        // Windows counts soft faults too, a few thousand a second is normal for the game.
        private const float PagingPerSec = 30000f;
        private const int CatchUpSteps = 3;

        public static HitchExplanation Explain(FrameBreakdown f)
        {
            var ci = CultureInfo.InvariantCulture;
            float dt = Math.Max(f.DtMs, 0.001f);
            var items = new List<KeyValuePair<float, string>>();

            void Consider(Timed t, string kind)
            {
                if (t.Ms < NamedMs && t.Ms < dt * NamedShare) return;
                string where = t.Parent != null ? ", inside " + t.Parent : "";
                string calls = t.Calls > 1 ? string.Format(ci, " in {0} calls", t.Calls) : "";
                items.Add(new KeyValuePair<float, string>(t.Ms, string.Format(ci, "{0}{1}: {2:0} ms{3} ({4:0}% of the frame{5})",
                    t.Name, kind, t.Ms, calls, t.Ms / dt * 100, where)));
            }

            foreach (var p in f.Phases) Consider(p, "");
            foreach (var m in f.Mods) Consider(m, " (mod code)");
            if (f.RenderMs > 0) Consider(new Timed("rendering", f.RenderMs), " (CPU side of drawing the frame)");
            if (f.OwnMs > 0) Consider(new Timed("Vigil itself", f.OwnMs), " (a report or one of its tools, not the game)");

            float timed = f.Phases.Where(p => p.Parent == null).Sum(p => p.Ms)
                        + f.Mods.Where(m => m.Parent == null).Sum(m => m.Ms)
                        + f.RenderMs + f.OwnMs;
            float untimed = Math.Max(0, f.DtMs - timed);
            if (untimed >= dt * UntimedShare)
                items.Add(new KeyValuePair<float, string>(untimed, string.Format(ci,
                    "{0:0} ms ({1:0}% of the frame) went outside the timed code: the Unity engine itself, physics, waiting for the graphics card, untimed scripts, or the operating system",
                    untimed, untimed / dt * 100)));

            var result = new HitchExplanation { UnexplainedMs = untimed };
            result.Causes.AddRange(items.OrderByDescending(i => i.Key).Select(i => i.Value));

            if (f.PageFaultsPerSec >= PagingPerSec)
                result.Causes.Add(string.Format(ci,
                    "heavy paging around this frame ({0:0} page faults/s): memory was being read back from disk, which stalls everything",
                    f.PageFaultsPerSec));
            if (f.GcCollections > 0)
                result.Causes.Add(string.Format(ci, "a garbage collection ran in this frame ({0}x), which pauses all script code", f.GcCollections));
            if (f.FixedSteps >= CatchUpSteps)
                result.Causes.Add(string.Format(ci, "{0} physics steps ran in this frame: the game was catching up after slow frames", f.FixedSteps));

            string top = items.Count > 0 ? items.OrderByDescending(i => i.Key).First().Value : "no single cause stands out";
            result.Headline = string.Format(ci, "{0:0} ms frame{1}: {2}", f.DtMs,
                string.IsNullOrEmpty(f.Situation) ? "" : " while " + f.Situation, top);
            return result;
        }
    }
}
