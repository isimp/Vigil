using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// A finished load test or player simulation as the panel shows it, read by the verdict's own
    /// rules (<see cref="LoadVerdict.Read"/>, <see cref="LoadVerdict.ReadPlayers"/>), so the panel and
    /// the report agree on every step.
    /// </summary>
    internal static class LoadResult
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly string[] Directions = { "up", "down" };
        private static readonly string[] StepHeaders = { "Step", "Got through, kB/s", "Copies' age, s", "Server frame, ms", "Your game, fps" };
        private static readonly string[] PlayerHeaders = { "Step", "Server work, ms", "Server frame, ms", "Worst frame, ms", "Copies' age, s" };
        private static readonly bool[] NumbersAfterFirst = { false, true, true, true, true };

        public static Result Build(LoadPlan plan, LoadLedger ledger, string abortReason, DateTime at)
        {
            var r = new Result("Load test", at);
            if (abortReason != null)
            {
                r.Add(new VerdictBlock(ResultTone.Caution, "Stopped early: " + abortReason + "."));
            }

            var directions = Directions.Where(d => LoadVerdict.Read(plan, ledger, d).Count > 0).ToList();
            var fell = new List<string>();
            bool anyMeasured = false;
            foreach (string dir in directions)
            {
                var readings = LoadVerdict.Read(plan, ledger, dir);
                var measured = readings.Where(x => x.Measured).ToList();
                if (measured.Count == 0)
                {
                    r.Add(new VerdictBlock(ResultTone.Quiet, Name(dir) + ": no measurements."));
                    continue;
                }

                anyMeasured = true;
                var fail = measured.FirstOrDefault(x => !x.KeptUp);
                var kept = measured.TakeWhile(x => x.KeptUp).ToList();
                if (fail == null)
                {
                    r.Add(new VerdictBlock(ResultTone.Good, string.Format(Ci, "{0} kept up through {1:0} kB/s{2}", Name(dir), kept[kept.Count - 1].Stage.RateKbps,
                        kept[kept.Count - 1] == readings[readings.Count - 1] ? ", the highest step" : "; the test ended before the next step")));
                }
                else
                {
                    fell.Add(Lower(dir));
                    r.Add(kept.Count == 0
                        ? new VerdictBlock(ResultTone.Bad, string.Format(Ci, "{0} did not keep up even at {1:0} kB/s: {2:0}% got through", Name(dir), fail.Stage.RateKbps, 100 * fail.Share))
                        : new VerdictBlock(ResultTone.Caution, string.Format(Ci, "{0} kept up through {1:0} kB/s; at {2:0} kB/s only {3:0}% got through",
                            Name(dir), kept[kept.Count - 1].Stage.RateKbps, fail.Stage.RateKbps, 100 * fail.Share)));
                }
            }

            r.Badge = abortReason != null ? "stopped early" : fell.Count == 2 ? "both directions fell behind" : fell.Count == 1 ? fell[0] + " fell behind" : anyMeasured ? "kept up" : null;
            r.BadgeTone = abortReason != null || fell.Count > 0 ? ResultTone.Caution : anyMeasured ? ResultTone.Good : ResultTone.Quiet;

            foreach (string dir in directions)
            {
                var readings = LoadVerdict.Read(plan, ledger, dir);
                if (!readings.Any(x => x.Measured))
                {
                    continue;
                }

                double max = Math.Min(1.25, Math.Max(1.0, readings.Where(x => x.Measured).Max(x => x.Share)));
                var chart = r.Add(new ColumnsBlock(Name(dir) + ": share that got through, per step", LoadVerdict.KeptUpShare, "85% kept up", max) { Unit = "kB/s" });
                bool failed = false;
                foreach (var x in readings)
                {
                    var tone = !x.Measured || failed ? ResultTone.Quiet : x.KeptUp ? ResultTone.Good : ResultTone.Caution;
                    failed |= x.Measured && !x.KeptUp;
                    string rate = x.Stage.RateKbps.ToString("0", Ci);
                    chart.Add(rate, x.Measured ? x.Share : 0, tone, x.Measured
                        ? string.Format(Ci, "{0} kB/s: {1:0} kB/s got through ({2:0}%)", rate, x.Achieved, 100 * x.Share)
                        : rate + " kB/s: not measured");
                }

                string held = LoadVerdict.HeldBack(plan, ledger, dir);
                if (held != null)
                {
                    r.Add(new TextBlock("What held the " + Lower(dir) + " back", held));
                }
            }

            AddFrames(r, plan, ledger, LoadMetrics.ServerFrame, "Server", double.NaN, null);
            var after = directions.Select(d => LoadVerdict.CaughtUp(plan, ledger, d) is string t ? Name(d) + ": " + t : null).Where(t => t != null).ToList();
            if (after.Count > 0)
            {
                r.Add(new TextBlock("After the load", string.Join("\n", after)));
            }

            var table = r.Add(new TableBlock("Every step", StepHeaders, NumbersAfterFirst));
            foreach (var s in plan.Stages.Where(s => ledger.Has(s.Index)))
            {
                int skip = s.Kind == LoadPlan.Kind.Load ? LoadVerdict.SettleSeconds : 0;
                bool down = s.Direction == "down";
                table.Rows.Add(new[]
                {
                    s.Name,
                    s.Kind == LoadPlan.Kind.Baseline ? "-" : Num(ledger.Get(s.Index, down ? LoadMetrics.DownRecv : LoadMetrics.UpRecv, skip).Mean, "0"),
                    Num(ledger.Get(s.Index, down ? LoadMetrics.DownStale : LoadMetrics.UpStale, skip).P95, "0.0"),
                    Num(ledger.Get(s.Index, LoadMetrics.ServerFrame, skip).Mean, "0.0"),
                    Num(ledger.Get(s.Index, LoadMetrics.ClientFps, skip).Mean, "0"),
                });
            }

            return r;
        }

        public static Result BuildPlayers(LoadPlan plan, LoadLedger ledger, string abortReason, string pattern, DateTime at)
        {
            var r = new Result("Player simulation", at);
            if (abortReason != null)
            {
                r.Add(new VerdictBlock(ResultTone.Caution, "Stopped early: " + abortReason + "."));
            }

            var readings = LoadVerdict.ReadPlayers(plan, ledger);
            var measured = readings.Where(x => x.Measured).ToList();
            if (measured.Count == 0)
            {
                r.Add(new VerdictBlock(ResultTone.Quiet, "No measurements."));
                r.Badge = abortReason != null ? "stopped early" : null;
                r.BadgeTone = ResultTone.Caution;
                return r;
            }

            var fail = measured.FirstOrDefault(x => !x.KeptUp);
            var kept = measured.TakeWhile(x => x.KeptUp).ToList();
            if (fail == null)
            {
                int n = kept[kept.Count - 1].Stage.Players;
                r.Add(new VerdictBlock(ResultTone.Good, string.Format(Ci, "The server kept up with {0} simulated player{1} ({2}){3}", n, n == 1 ? "" : "s", pattern,
                    kept[kept.Count - 1] == readings[readings.Count - 1] ? ", the most this test added" : "; the test ended before the next step")));
                r.Badge = "kept up with " + n.ToString(Ci);
                r.BadgeTone = abortReason != null ? ResultTone.Caution : ResultTone.Good;
            }
            else
            {
                string gave = fail.Overloaded ? "its main thread gave" : "your updates waited too long";
                r.Add(kept.Count == 0
                    ? new VerdictBlock(ResultTone.Bad, "The server did not keep up even with 1 simulated player: " + gave)
                    : new VerdictBlock(ResultTone.Caution, string.Format(Ci, "The server kept up with {0} simulated players ({1}); at {2} {3}",
                        kept[kept.Count - 1].Stage.Players, pattern, fail.Stage.Players, gave)));
                r.Badge = "gave at " + fail.Stage.Players.ToString(Ci) + (fail.Stage.Players == 1 ? " player" : " players");
                r.BadgeTone = kept.Count == 0 ? ResultTone.Bad : ResultTone.Caution;
            }

            double top = measured.Max(x => Work(x));
            var chart = r.Add(new ColumnsBlock("Server work per frame, per step", LoadVerdict.ServerBudgetMs, "a frame's 33 ms",
                Math.Max(LoadVerdict.ServerBudgetMs * 1.2, double.IsNaN(top) ? 0 : top * 1.1)) { Unit = "players" });
            bool failed = false;
            foreach (var x in readings)
            {
                var tone = !x.Measured || failed ? ResultTone.Quiet : x.KeptUp ? ResultTone.Good : ResultTone.Caution;
                failed |= x.Measured && !x.KeptUp;
                string n = x.Stage.Players.ToString(Ci);
                chart.Add(n, x.Measured && !double.IsNaN(Work(x)) ? Work(x) : 0, tone, x.Measured
                    ? string.Format(Ci, "{0} players: {1:0.0} ms of work, frame {2:0.0} ms, worst {3:0} ms", n, Work(x), x.FrameMs, x.WorstMs)
                    : n + " players: not measured");
            }

            var lines = LoadVerdict.JudgePlayers(plan, ledger, abortReason, pattern);
            var details = lines.Skip(abortReason != null ? 2 : 1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (details.Count > 0)
            {
                r.Add(new TextBlock("What the simulation showed", string.Join("\n", details)));
            }

            var table = r.Add(new TableBlock("Every step", PlayerHeaders, NumbersAfterFirst));
            foreach (var s in plan.Stages.Where(s => ledger.Has(s.Index)))
            {
                int skip = s.Kind == LoadPlan.Kind.Load ? LoadVerdict.SettleSeconds : 0;
                table.Rows.Add(new[]
                {
                    s.Name,
                    Num(ledger.Get(s.Index, LoadMetrics.ServerWork, skip).Mean, "0.0"),
                    Num(ledger.Get(s.Index, LoadMetrics.ServerFrame, skip).Mean, "0.0"),
                    Num(ledger.Get(s.Index, LoadMetrics.ServerFrameMax, skip).P95, "0"),
                    Num(ledger.Get(s.Index, LoadMetrics.DownStale, skip).P95, "0.0"),
                });
            }

            return r;
        }

        /// <summary>The server's work per frame where measured, else its whole frame.</summary>
        private static double Work(LoadVerdict.PlayerReading x) => double.IsNaN(x.WorkMs) ? x.FrameMs : x.WorkMs;

        /// <summary>Frame time on the server and on the player's game, quiet (the baseline) against the busiest load step.</summary>
        private static void AddFrames(Result r, LoadPlan plan, LoadLedger ledger, string serverMetric, string serverLabel, double mark, string markLabel)
        {
            int b = plan.Stages.FirstOrDefault(s => s.Kind == LoadPlan.Kind.Baseline)?.Index ?? -1;
            if (b < 0)
            {
                return;
            }

            var loads = plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load).ToList();
            var rows = new List<(string Label, double From, double To)>();
            foreach (var (metric, label) in new[] { (serverMetric, serverLabel), (LoadMetrics.ClientFrame, "Your game") })
            {
                double quiet = ledger.Get(b, metric).Mean;
                var busy = loads.Select(s => ledger.Get(s.Index, metric, LoadVerdict.SettleSeconds).Mean).Where(v => !double.IsNaN(v)).ToList();
                if (!double.IsNaN(quiet) && busy.Count > 0)
                {
                    rows.Add((label, quiet, busy.Max()));
                }
            }

            if (rows.Count == 0)
            {
                return;
            }

            var block = r.Add(new RangesBlock("Frame time, quiet against the busiest step", Math.Max(rows.Max(x => x.To) * 1.15, 1), mark, markLabel));
            foreach (var (label, from, to) in rows)
            {
                block.Add(label, from, to, string.Format(Ci, "{0:0.0} to {1:0.0} ms", from, to));
            }
        }

        private static string Name(string dir) => dir == "up" ? "Upload" : "Download";

        private static string Lower(string dir) => dir == "up" ? "upload" : "download";

        private static string Num(double v, string format) => double.IsNaN(v) ? "-" : v.ToString(format, Ci);
    }
}
