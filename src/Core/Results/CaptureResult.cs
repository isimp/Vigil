using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>The results of a deep capture and of an incident (a lag report or one Vigil wrote on its own).</summary>
    internal static class CaptureResult
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly string[] ListSeparator = { ", " };
        private static readonly string[] StallHeaders = { "Method", "Frame, ms", "Patched by" };
        private static readonly bool[] StallRight = { false, true, false };

        /// <summary>One patched game method as the deep capture timed it.</summary>
        internal sealed class DeepRow
        {
            /// <param name="label">The method.</param>
            /// <param name="patchedBy">The mods that patch it, separated by ", ".</param>
            /// <param name="msPerFrame">Its time per frame, the mods' patches included.</param>
            /// <param name="callsPerFrame">How often it ran per frame.</param>
            /// <param name="worstMs">Its longest single frame.</param>
            /// <param name="gameShare">The share of its time in the game's own code, where it was split; NaN otherwise.</param>
            public DeepRow(string label, string patchedBy, double msPerFrame, double callsPerFrame, double worstMs, double gameShare)
            {
                Label = label;
                PatchedBy = patchedBy;
                MsPerFrame = msPerFrame;
                CallsPerFrame = callsPerFrame;
                WorstMs = worstMs;
                GameShare = gameShare;
            }

            public string Label { get; }

            public string PatchedBy { get; }

            public double MsPerFrame { get; }

            public double CallsPerFrame { get; }

            public double WorstMs { get; }

            public double GameShare { get; }
        }

        public static Result Deep(DateTime at, long frames, string reason, IReadOnlyList<DeepRow> rows)
        {
            double worst = rows.Count > 0 ? rows.Max(x => x.WorstMs) : 0;
            var r = new Result("Deep capture", at)
            {
                Badge = rows.Count > 0 ? "worst frame " + worst.ToString("0", Ci) + " ms" : "nothing timed",
                BadgeTone = worst >= DeepSplit.SpikeMs ? ResultTone.Caution : ResultTone.Quiet,
            };
            r.Add(new TilesBlock())
                .Add("Frames", frames.ToString(Ci))
                .Add("Methods timed", rows.Count.ToString(Ci))
                .Add("Their time", rows.Sum(x => x.MsPerFrame).ToString("0.00", Ci) + " ms/frame")
                .Add("Worst frame", worst.ToString("0", Ci) + " ms");
            if (!string.IsNullOrEmpty(reason))
            {
                r.Add(new TextBlock(null, "Started: " + reason + "."));
            }

            var methods = r.Add(new BarsBlock("Most time, per patched game method", "the game's code", "the mods' patches"));
            foreach (var x in rows.OrderByDescending(x => x.MsPerFrame).Take(10))
            {
                methods.Add(x.Label, x.MsPerFrame, x.MsPerFrame.ToString("0.000", Ci) + " ms/frame", double.IsNaN(x.GameShare) ? double.NaN : x.GameShare * x.MsPerFrame);
            }

            var perMod = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var x in rows)
            {
                foreach (string mod in (x.PatchedBy ?? "").Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    perMod[mod] = (perMod.TryGetValue(mod, out double v) ? v : 0) + x.MsPerFrame;
                }
            }

            var mods = r.Add(new BarsBlock("Time of the methods each mod patches"));
            foreach (var kv in perMod.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(8))
            {
                mods.Add(kv.Key, kv.Value, kv.Value.ToString("0.000", Ci) + " ms/frame");
            }

            var stalls = r.Add(new TableBlock("Longest single frames", StallHeaders, StallRight));
            foreach (var x in rows.Where(x => x.WorstMs >= DeepSplit.SpikeMs).OrderByDescending(x => x.WorstMs).Take(20))
            {
                stalls.Rows.Add(new[] { x.Label, x.WorstMs.ToString("0.0", Ci), x.PatchedBy ?? "" });
            }

            return r;
        }

        /// <summary>A connection that ended other than with a goodbye: how, the reasons, and its last seconds.</summary>
        public static Result Drop(DateTime at, DropAnalysis d, IReadOnlyList<DropAnalysis.Sample> samples)
        {
            var r = new Result("Drop", at)
            {
                Badge = d.Kind switch
                {
                    DropAnalysis.Verdict.LineDied => "line died",
                    DropAnalysis.Verdict.GameFroze => "game froze",
                    DropAnalysis.Verdict.ClosedWithoutGoodbye => "closed without goodbye",
                    DropAnalysis.Verdict.ClosedHere => "closed here",
                    DropAnalysis.Verdict.Left => "left",
                    _ => "timed out",
                },
                BadgeTone = d.Kind == DropAnalysis.Verdict.Left ? ResultTone.Quiet : ResultTone.Caution,
                Summary = d.Headline,
            };
            r.Add(new VerdictBlock(r.BadgeTone, d.Headline));
            if (d.Lines.Count > 0)
            {
                r.Add(new TextBlock(null, string.Join("\n", d.Lines)));
            }

            if (samples.Count > 0)
            {
                string left = "-" + samples.Count.ToString(Ci) + " s";
                r.Add(new StripBlock("Longest pause between packets, each second", samples.Select(s => s.MaxGapMs).ToList(), 300, 1000, "ms", left, "the end"));
                r.Add(new StripBlock("Data waiting to reach the other side, each second", samples.Select(s => s.QueueKb).ToList(), 16, 32, "kB", left, "the end"));
            }

            return r;
        }

        /// <param name="at">When it was written.</param>
        /// <param name="kind">"hitch", "slow", "manual" and so on.</param>
        /// <param name="detail">What triggered it.</param>
        /// <param name="worst">The longest recent frame, or null when none was kept.</param>
        /// <param name="perSecondWorstMs">Each recent second's longest frame, oldest first.</param>
        public static Result Incident(DateTime at, string kind, string detail, FrameBreakdown worst, IReadOnlyList<double> perSecondWorstMs)
        {
            var r = new Result(kind == "manual" ? "Lag report" : "Incident", at)
            {
                Badge = kind,
                BadgeTone = kind == "manual" ? ResultTone.Quiet : ResultTone.Caution,
                Summary = detail,
            };
            string named = kind == "manual" ? "Lag report" : char.ToUpperInvariant(kind[0]) + kind.Substring(1);
            r.Add(new VerdictBlock(kind == "manual" ? ResultTone.Quiet : ResultTone.Caution, named + ": " + detail));
            if (worst != null)
            {
                var a = HitchAnalysis.Explain(worst);
                r.Add(new TextBlock("The longest recent frame", a.Headline + (a.Causes.Count > 0 ? "\n" + string.Join("\n", a.Causes) : "")));
                var bars = r.Add(new BarsBlock("Timed work in that frame"));
                foreach (var t in worst.Phases.Concat(worst.Mods).OrderByDescending(t => t.Ms).Take(8))
                {
                    bars.Add(t.Name, t.Ms, t.Ms.ToString("0.0", Ci) + " ms" + (t.Calls > 1 ? ", " + t.Calls.ToString(Ci) + " calls" : "") + (t.Parent != null ? ", inside " + t.Parent : ""));
                }
            }

            if (perSecondWorstMs.Count > 0)
            {
                r.Add(new StripBlock("Longest frame, each second", perSecondWorstMs, 50, 250, "ms", "-" + perSecondWorstMs.Count.ToString(Ci) + " s", "now"));
            }

            return r;
        }
    }
}
