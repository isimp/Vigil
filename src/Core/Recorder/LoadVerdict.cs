using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>The measurements a load test files per second, with the same names on both machines.</summary>
    public static class LoadMetrics
    {
        // traffic of the test objects, per direction: planned, sent by the owner, received by the other side,
        // and how old the receiver's copy of the beacon was (seconds, including the reply that reported it)
        public const string UpTarget = "up_target_kbs", UpSent = "up_sent_kbs", UpRecv = "up_recv_kbs", UpStale = "up_stale_s";
        public const string DownTarget = "down_target_kbs", DownSent = "down_sent_kbs", DownRecv = "down_recv_kbs", DownStale = "down_stale_s";
        public const string Rtt = "rtt_ms";

        public const string ServerFps = "srv_fps", ServerFrame = "srv_frame_ms", ServerFrameMax = "srv_frame_max_ms",
            ServerOver50 = "srv_over_50", ServerOver100 = "srv_over_100", ServerCpu = "srv_cpu", ServerZdoMan = "srv_zdoman_ms",
            ServerZdoApply = "srv_zdo_apply_ms", ServerSyncList = "srv_synclist_ms", ServerRpc = "srv_rpc_ms", ServerZones = "srv_zones_ms",
            ServerGc = "srv_gc", ServerHeap = "srv_heap_mb", ServerMemory = "srv_ws_mb", ServerZdos = "srv_zdos", ServerQueue = "srv_queue_kb",
            ServerPeers = "srv_peers", ServerLoad = "srv_load_1m", ServerTestCost = "srv_test_ms";

        public const string ClientFps = "cli_fps", ClientFrame = "cli_frame_ms", ClientFrameMax = "cli_frame_max_ms",
            ClientOver50 = "cli_over_50", ClientOver100 = "cli_over_100", ClientCpu = "cli_cpu", ClientZdoMan = "cli_zdoman_ms",
            ClientZdoApply = "cli_zdo_apply_ms", ClientScene = "cli_scene_ms", ClientQueue = "cli_queue_kb",
            ClientGc = "cli_gc", ClientTestCost = "cli_test_ms", ClientMoved = "cli_moved_m";

        // Steam's own view of the connection on each machine, toward the other one: what it put on the
        // wire (kB/s), the send rate it allows itself now and its configured floor (kB/s), the data
        // waiting to go out and the data sent but not yet confirmed (kB), and its own ping (ms: the
        // line, without any queue). "queue" above is the game's count, both of those together. The
        // batch limit (kB) is how much the game lets wait or go unconfirmed before it holds back new
        // world updates (vanilla 10 kB; the Network mod's Batch Size).
        public const string ServerOut = "srv_out_kbs", ServerRate = "srv_rate_kbs", ServerFloor = "srv_floor_kbs", ServerPending = "srv_pending_kb",
            ServerUnacked = "srv_unacked_kb", ServerPing = "srv_ping_ms", ServerBatch = "srv_batch_kb";
        public const string ClientOut = "cli_out_kbs", ClientRate = "cli_rate_kbs", ClientFloor = "cli_floor_kbs", ClientPending = "cli_pending_kb",
            ClientUnacked = "cli_unacked_kb", ClientPing = "cli_ping_ms", ClientBatch = "cli_batch_kb";

        // The player's game: in front (1) or in the background (0), its frame cap (fps), and the whole
        // computer's CPU (%, the game's own share is cli_cpu) and memory in use (%).
        public const string ClientFocused = "cli_focused", ClientFrameCap = "cli_frame_cap", ClientMachineCpu = "cli_sys_cpu", ClientMemory = "cli_mem_load";

        // time per frame in the timed code (top-level timers added up): the work, without the
        // wait a frame-capped server spends sleeping
        public const string ServerWork = "srv_work_ms", ClientWork = "cli_work_ms";
        public const string ServerSend = "srv_zdo_send_ms", ServerRelease = "srv_zdo_release_ms", ServerRoute = "srv_route_ms", ServerLogErrors = "srv_log_err";

        // simulated players: how many, the world data the server sent them (kB/s, all together),
        // their queues (kB each), the objects each one knows, the updates they sent the server (per
        // second, all together), and per finished arrival the seconds until its area was in
        public const string Bots = "bots", BotDown = "bot_down_kbs", BotQueue = "bot_queue_kb", BotKnown = "bot_known",
            BotUpdates = "bot_updates_s", BotArrival = "bot_arrival_s";

        public static readonly string[] All =
        {
            UpTarget, UpSent, UpRecv, UpStale, DownTarget, DownSent, DownRecv, DownStale, Rtt,
            ServerFps, ServerFrame, ServerFrameMax, ServerOver50, ServerOver100, ServerCpu, ServerWork, ServerZdoMan, ServerZdoApply, ServerSyncList,
            ServerSend, ServerRelease, ServerRoute, ServerRpc, ServerZones, ServerGc, ServerHeap, ServerMemory, ServerZdos, ServerQueue,
            ServerPending, ServerUnacked, ServerOut, ServerRate, ServerFloor, ServerPing, ServerBatch,
            ServerPeers, ServerLoad, ServerLogErrors, ServerTestCost,
            ClientFps, ClientFrame, ClientFrameMax, ClientOver50, ClientOver100, ClientCpu, ClientWork, ClientZdoMan, ClientZdoApply, ClientScene,
            ClientQueue, ClientPending, ClientUnacked, ClientOut, ClientRate, ClientFloor, ClientPing, ClientBatch,
            ClientFocused, ClientFrameCap, ClientMachineCpu, ClientMemory, ClientGc, ClientTestCost, ClientMoved,
            Bots, BotDown, BotQueue, BotKnown, BotUpdates, BotArrival,
        };

        public static bool IsServer(string m) => m.StartsWith("srv_", StringComparison.Ordinal);
        public static bool IsClient(string m) => m.StartsWith("cli_", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a finished load test: how much traffic each direction carried before falling behind,
    /// what held it back, how long the catch-up took once the load stopped, and how much frame time
    /// each machine had left.
    /// </summary>
    public static class LoadVerdict
    {
        // The first seconds of a step, in which the traffic is still building up, are left out.
        public const int SettleSeconds = 3;
        // A step is kept up with when this share of its traffic got through and the copies stayed fresh.
        public const double KeptUpShare = 0.85;
        public const double ServerBudgetMs = 1000.0 / 30;

        private static string Label(string dir) => dir == "up" ? "Up (your game to the server)" : "Down (the server to your game)";

        public static List<string> Judge(LoadPlan plan, LoadLedger ledger, string abortReason)
        {
            var ci = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            if (abortReason != null) lines.Add("Stopped early: " + abortReason + ".");
            var baseline = plan.Stages.FirstOrDefault(s => s.Kind == LoadPlan.Kind.Baseline);
            int b = baseline?.Index ?? -1;

            foreach (var dir in new[] { "up", "down" })
            {
                var readings = Read(plan, ledger, dir);
                if (readings.Count == 0) continue;
                var steps = readings.Select(r => r.Stage).ToList();
                string stale = dir == "up" ? LoadMetrics.UpStale : LoadMetrics.DownStale;
                Func<LoadPlan.Stage, double> achieved = s => readings.First(r => r.Stage == s).Achieved;
                Func<LoadPlan.Stage, bool> ok = s => readings.First(r => r.Stage == s).KeptUp;
                var measured = readings.Where(r => r.Measured).Select(r => r.Stage).ToList();
                if (measured.Count == 0) { lines.Add(Label(dir) + ": no measurements."); continue; }
                double baseStale = b >= 0 ? ledger.Get(b, stale).P95 : double.NaN;
                if (double.IsNaN(baseStale)) baseStale = 0;

                var fail = measured.FirstOrDefault(s => !ok(s));
                var kept = measured.TakeWhile(s => ok(s)).ToList();
                if (fail == null)
                    lines.Add(string.Format(ci, "{0}: kept up through {1:0} kB/s{2}.", Label(dir), kept.Last().RateKbps,
                        kept.Last() == steps.Last() ? ", the highest step" : " (the test ended before the next step)"));
                else if (kept.Count == 0)
                    lines.Add(string.Format(ci, "{0}: did not keep up even at {1:0} kB/s.", Label(dir), fail.RateKbps));
                else
                    lines.Add(string.Format(ci, "{0}: kept up through {1:0} kB/s.", Label(dir), kept.Last().RateKbps));

                if (fail != null)
                {
                    double got = achieved(fail);
                    double st = ledger.Get(fail.Index, stale, SettleSeconds).P95;
                    lines.Add(string.Format(ci, "   At {0:0} kB/s: {1:0} kB/s got through ({2:0}%){3}.", fail.RateKbps, got, 100 * got / fail.RateKbps,
                        double.IsNaN(st) ? "" : string.Format(ci, ", world updates up to {0:0.0} s old", st)));
                    foreach (var reason in Bottleneck(ledger, b, fail, dir)) lines.Add("   " + reason);
                }
                lines.Add(string.Format(ci, "   The most that got through in one step: {0:0} kB/s.", measured.Max(s => achieved(s))));

                var rec = plan.RecoveryFor(dir);
                var series = rec != null ? ledger.Series(rec.Index, stale) : new double[0];
                if (series.Count > 0) lines.Add("   " + CatchUp(series, Math.Max(1.0, baseStale + 0.5)));
            }

            if (b >= 0) Headroom(plan, ledger, b, lines);
            return lines;
        }

        /// <summary>One load step of a direction as the verdict reads it: what got through, how old the copies got, and whether it kept up.</summary>
        public sealed class StepReading
        {
            public LoadPlan.Stage Stage;
            public double Achieved = double.NaN, StaleP95 = double.NaN;
            public bool Measured, KeptUp;
            public double Share => Stage.RateKbps > 0 ? Achieved / Stage.RateKbps : double.NaN;
        }

        /// <summary>
        /// The direction's load steps in order, each read by the verdict's rule: kept up when
        /// <see cref="KeptUpShare"/> of its traffic got through and the copies stayed fresh (no more
        /// than 1 s older than at the baseline, and at least 1.5 s allowed).
        /// </summary>
        public static List<StepReading> Read(LoadPlan plan, LoadLedger ledger, string dir)
        {
            string recv = dir == "up" ? LoadMetrics.UpRecv : LoadMetrics.DownRecv;
            string sent = dir == "up" ? LoadMetrics.UpSent : LoadMetrics.DownSent;
            string stale = dir == "up" ? LoadMetrics.UpStale : LoadMetrics.DownStale;
            int b = plan.Stages.FirstOrDefault(s => s.Kind == LoadPlan.Kind.Baseline)?.Index ?? -1;
            double baseStale = b >= 0 ? ledger.Get(b, stale).P95 : double.NaN;
            if (double.IsNaN(baseStale)) baseStale = 0;
            double staleLimit = Math.Max(1.5, baseStale + 1.0);
            var readings = new List<StepReading>();
            foreach (var s in plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load && s.Direction == dir))
            {
                double r = ledger.Get(s.Index, recv, SettleSeconds).Mean;
                if (double.IsNaN(r)) r = ledger.Get(s.Index, sent, SettleSeconds).Mean;
                double st = ledger.Get(s.Index, stale, SettleSeconds).P95;
                bool measured = !double.IsNaN(r);
                readings.Add(new StepReading
                {
                    Stage = s, Achieved = r, StaleP95 = st, Measured = measured,
                    KeptUp = measured && r >= KeptUpShare * s.RateKbps && (double.IsNaN(st) || st <= staleLimit),
                });
            }
            return readings;
        }

        /// <summary>What held the direction back at its first step that did not keep up, in the verdict's words; null when every step kept up.</summary>
        public static string HeldBack(LoadPlan plan, LoadLedger ledger, string dir)
        {
            var fail = Read(plan, ledger, dir).Where(r => r.Measured).FirstOrDefault(r => !r.KeptUp);
            if (fail == null) return null;
            int b = plan.Stages.FirstOrDefault(s => s.Kind == LoadPlan.Kind.Baseline)?.Index ?? -1;
            return string.Join(" ", Bottleneck(ledger, b, fail.Stage, dir).ToArray());
        }

        /// <summary>How long the direction took to catch up once its load stopped, in the verdict's words; null without a recovery measured.</summary>
        public static string CaughtUp(LoadPlan plan, LoadLedger ledger, string dir)
        {
            string stale = dir == "up" ? LoadMetrics.UpStale : LoadMetrics.DownStale;
            int b = plan.Stages.FirstOrDefault(s => s.Kind == LoadPlan.Kind.Baseline)?.Index ?? -1;
            double baseStale = b >= 0 ? ledger.Get(b, stale).P95 : double.NaN;
            if (double.IsNaN(baseStale)) baseStale = 0;
            var rec = plan.RecoveryFor(dir);
            var series = rec != null ? ledger.Series(rec.Index, stale) : new double[0];
            return series.Count > 0 ? CatchUp(series, Math.Max(1.0, baseStale + 0.5)) : null;
        }

        private static IEnumerable<string> Bottleneck(LoadLedger l, int b, LoadPlan.Stage s, string dir)
        {
            var ci = CultureInfo.InvariantCulture;
            var reasons = new List<string>();
            double srvBase = b >= 0 ? l.Get(b, LoadMetrics.ServerFrame).Mean : double.NaN;
            double srv = l.Get(s.Index, LoadMetrics.ServerFrame, SettleSeconds).Mean;
            double srvWorst = l.Get(s.Index, LoadMetrics.ServerFrameMax, SettleSeconds).P95;
            double srvWorstBase = b >= 0 ? l.Get(b, LoadMetrics.ServerFrameMax).P95 : double.NaN;
            bool serverBusy = (!double.IsNaN(srv) && !double.IsNaN(srvBase) && srv >= Math.Max(srvBase * 1.5, srvBase + 10))
                || (!double.IsNaN(srvWorst) && srvWorst >= 100 && (double.IsNaN(srvWorstBase) || srvWorst >= 2 * srvWorstBase));
            if (serverBusy)
                reasons.Add(string.Format(ci, "The server's main thread was the limit: its frame time went from {0:0.0} to {1:0.0} ms, worst frames {2:0} ms.",
                    srvBase, srv, srvWorst));

            double cliBase = b >= 0 ? l.Get(b, LoadMetrics.ClientFrame).Mean : double.NaN;
            double cli = l.Get(s.Index, LoadMetrics.ClientFrame, SettleSeconds).Mean;
            double fpsBase = b >= 0 ? l.Get(b, LoadMetrics.ClientFps).Mean : double.NaN;
            double fps = l.Get(s.Index, LoadMetrics.ClientFps, SettleSeconds).Mean;
            // A game sent to the background runs at another frame cap; its frame time then says
            // nothing about the load.
            bool clientBusy = SameView(l, b, s.Index) && ((!double.IsNaN(cli) && !double.IsNaN(cliBase) && cli >= Math.Max(cliBase * 1.5, cliBase + 10))
                || (!double.IsNaN(fps) && !double.IsNaN(fpsBase) && fps < fpsBase * 0.67));
            if (clientBusy)
                reasons.Add(string.Format(ci, "Your game was the limit: its frame time went from {0:0.0} to {1:0.0} ms ({2:0} to {3:0} fps).",
                    cliBase, cli, fpsBase, fps));

            if (!serverBusy && !clientBusy) reasons.Add(LinkLimit(l, b, s, dir));
            return reasons;
        }

        /// <summary>
        /// Which part of the connection was full, from Steam's own numbers on the sending machine:
        /// the send rate Steam allowed itself (and whether that was only its configured floor), the
        /// line (Steam's own ping climbs when data queues in a router on the way), or the game's
        /// batch limit (it hands Steam new world updates only while less than that waits or is
        /// unconfirmed, so with a calm line the flow is bounded by batch size per round trip).
        /// </summary>
        private static string LinkLimit(LoadLedger l, int b, LoadPlan.Stage s, string dir)
        {
            var ci = CultureInfo.InvariantCulture;
            bool up = dir == "up";
            string who = up ? "your game" : "the server";
            Func<string, string, double> at = (cli, srv) => l.Get(s.Index, up ? cli : srv, SettleSeconds).Mean;
            Func<string, string, double> before = (cli, srv) => b >= 0 ? l.Get(b, up ? cli : srv).Mean : double.NaN;
            double sent = at(LoadMetrics.ClientOut, LoadMetrics.ServerOut), rate = at(LoadMetrics.ClientRate, LoadMetrics.ServerRate);
            double floor = at(LoadMetrics.ClientFloor, LoadMetrics.ServerFloor), batch = at(LoadMetrics.ClientBatch, LoadMetrics.ServerBatch);
            double pending = at(LoadMetrics.ClientPending, LoadMetrics.ServerPending), unacked = at(LoadMetrics.ClientUnacked, LoadMetrics.ServerUnacked);
            double queue = at(LoadMetrics.ClientQueue, LoadMetrics.ServerQueue), ping = at(LoadMetrics.ClientPing, LoadMetrics.ServerPing);
            double pingBase = before(LoadMetrics.ClientPing, LoadMetrics.ServerPing), sentBase = before(LoadMetrics.ClientOut, LoadMetrics.ServerOut);

            if (double.IsNaN(sent) || double.IsNaN(rate))
                return "Neither machine was busy, but no more got through the connection" +
                    (queue >= 8 ? string.Format(ci, ", and {0:0.0} kB waited in the sender's queue all along", queue) : "") +
                    ". Steam's own numbers for it were not recorded, so this report cannot tell whether the send rate, the game's batch limit or the line was full.";

            // Steam reports the waiting data in the same read as its rate.
            if (rate > 0 && sent >= 0.9 * rate && pending >= 2)
            {
                var sb = new StringBuilder(string.Format(ci, "Steam's send rate on {0} was the limit: it sent {1:0} kB/s, all the rate it allowed itself ({2:0} kB/s), while {3:0.0} kB waited to go out.",
                    who, sent, rate, pending));
                if (!double.IsNaN(floor) && Math.Abs(rate - floor) <= Math.Max(1, 0.02 * floor))
                    sb.Append(" That rate is its configured floor (Send Rate Floor in the Network mod's config; the game on its own sets 150 kB/s): Steam did not raise it, so the floor is the rate it sends at.");
                if (!double.IsNaN(sentBase))
                    sb.AppendFormat(ci, " Before the test {0} already sent {1:0} kB/s of its own, which left about {2:0} kB/s for the test.", who, sentBase, Math.Max(0, rate - sentBase));
                return sb.ToString();
            }

            if (!double.IsNaN(ping) && !double.IsNaN(pingBase) && ping >= Math.Max(pingBase * 1.5, pingBase + 30))
                return string.Format(ci, "The line was full: Steam's own ping on {0} went from {1:0} to {2:0} ms under the load, so the data queued on the way (in a router or at the provider), not in the game.",
                    who, pingBase, ping);

            if (!double.IsNaN(batch) && batch > 0 && queue >= 0.8 * batch)
                return string.Format(ci, "The game's batch limit on {0} was the limit: it hands Steam new world updates only while less than {1:0} kB waits or is unconfirmed, and that stayed full ({2:0.0} kB{3}). Steam allowed {4:0} kB/s{5}, so the flow is bounded by the batch size per round trip (Batch Size in the Network mod's config; the game on its own uses 10 kB).",
                    who, batch, queue, double.IsNaN(unacked) ? "" : string.Format(ci, ", {0:0.0} kB of it sent and not yet confirmed", unacked),
                    rate, double.IsNaN(ping) ? "" : string.Format(ci, " and its ping stayed at {0:0} ms", ping));

            return string.Format(ci, "Neither machine was busy and no part of the connection was clearly full: Steam on {0} sent {1:0} kB/s of the {2:0} kB/s it allowed itself, {3:0.0} kB waited or was unconfirmed{4}{5}.",
                who, sent, rate, queue, double.IsNaN(batch) ? "" : string.Format(ci, " (batch limit {0:0} kB)", batch),
                double.IsNaN(ping) ? "" : string.Format(ci, ", ping {0:0} ms", ping));
        }

        // The player's game was in the same state as at the baseline: focused or not, and the same
        // frame cap. Unknown counts as the same.
        private static bool SameView(LoadLedger l, int b, int stage)
        {
            if (b < 0) return true;
            double fb = l.Get(b, LoadMetrics.ClientFocused).Mean, fs = l.Get(stage, LoadMetrics.ClientFocused, SettleSeconds).Mean;
            double cb = l.Get(b, LoadMetrics.ClientFrameCap).Mean, cs = l.Get(stage, LoadMetrics.ClientFrameCap, SettleSeconds).Mean;
            if (!double.IsNaN(fb) && !double.IsNaN(fs) && Math.Abs(fb - fs) >= 0.2) return false;
            if (!double.IsNaN(cb) && !double.IsNaN(cs) && Math.Abs(cb - cs) > 1) return false;
            return true;
        }

        private static string CatchUp(IReadOnlyList<double> series, double fresh)
        {
            var ci = CultureInfo.InvariantCulture;
            if (series[0] <= fresh) return "There was nothing to catch up: the copies were fresh as soon as the load stopped.";
            for (int i = 1; i < series.Count; i++)
            {
                bool settled = true;
                for (int j = i; j < Math.Min(series.Count, i + 3); j++) if (series[j] > fresh) { settled = false; break; }
                if (settled) return string.Format(ci, "It caught up {0} s after the load stopped (copies fresh again, under {1:0.0} s old).", i, fresh);
            }
            return string.Format(ci, "It had not caught up after {0} s: the copies were still {1:0.0} s old.", series.Count, series[series.Count - 1]);
        }

        private static void Headroom(LoadPlan plan, LoadLedger l, int b, List<string> lines)
        {
            var ci = CultureInfo.InvariantCulture;
            var loads = plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load).ToList();
            double srvBase = l.Get(b, LoadMetrics.ServerFrame).Mean;
            var heavy = loads.Where(s => !double.IsNaN(l.Get(s.Index, LoadMetrics.ServerFrame, SettleSeconds).Mean))
                .OrderByDescending(s => l.Get(s.Index, LoadMetrics.ServerFrame, SettleSeconds).Mean).FirstOrDefault();
            if (!double.IsNaN(srvBase) && heavy != null)
                lines.Add(string.Format(ci, "Server frame time: {0:0.0} ms at baseline, {1:0.0} ms at {2} (worst frame {3:0} ms); it aims at {4:0} ms per frame (30 fps).",
                    srvBase, l.Get(heavy.Index, LoadMetrics.ServerFrame, SettleSeconds).Mean, heavy.Name,
                    l.Get(heavy.Index, LoadMetrics.ServerFrameMax, SettleSeconds).Max, ServerBudgetMs));
            double workBase = l.Get(b, LoadMetrics.ServerWork).Mean;
            var busiest = loads.Where(s => !double.IsNaN(l.Get(s.Index, LoadMetrics.ServerWork, SettleSeconds).Mean))
                .OrderByDescending(s => l.Get(s.Index, LoadMetrics.ServerWork, SettleSeconds).Mean).FirstOrDefault();
            if (!double.IsNaN(workBase) && busiest != null)
                lines.Add(string.Format(ci, "Server work per frame (its timed code, without the wait between frames): {0:0.0} ms at baseline, {1:0.0} ms at {2}, of the {3:0} ms a frame has.",
                    workBase, l.Get(busiest.Index, LoadMetrics.ServerWork, SettleSeconds).Mean, busiest.Name, ServerBudgetMs));
            double fpsBase = l.Get(b, LoadMetrics.ClientFps).Mean;
            var withFps = loads.Where(s => !double.IsNaN(l.Get(s.Index, LoadMetrics.ClientFps, SettleSeconds).Mean)).ToList();
            var otherView = withFps.Where(s => !SameView(l, b, s.Index)).ToList();
            var comparable = withFps.Except(otherView).ToList();
            if (!double.IsNaN(fpsBase) && otherView.Count > 0)
            {
                double focusBase = l.Get(b, LoadMetrics.ClientFocused).Mean, focusOther = otherView.Average(s => l.Get(s.Index, LoadMetrics.ClientFocused, SettleSeconds).Mean);
                string state = !double.IsNaN(focusBase) && !double.IsNaN(focusOther) && focusBase >= 0.5 && focusOther < 0.5 ? "in the background"
                    : !double.IsNaN(focusBase) && !double.IsNaN(focusOther) && focusBase < 0.5 && focusOther >= 0.5 ? "in front, unlike at baseline"
                    : "at another frame cap";
                lines.Add(string.Format(ci, "Your game was {0} during {1} step{2} (frame cap {3:0} fps there, {4:0} at baseline), so {5} frame rate does not show the load's effect{6}",
                    state, otherView.Count, otherView.Count == 1 ? "" : "s", otherView.Average(s => l.Get(s.Index, LoadMetrics.ClientFrameCap, SettleSeconds).Mean),
                    l.Get(b, LoadMetrics.ClientFrameCap).Mean, otherView.Count == 1 ? "its" : "their", comparable.Count > 0 ? "; the comparison below leaves them out." : "."));
            }
            var slow = comparable.OrderBy(s => l.Get(s.Index, LoadMetrics.ClientFps, SettleSeconds).Mean).FirstOrDefault();
            if (!double.IsNaN(fpsBase) && slow != null)
            {
                double slowFps = l.Get(slow.Index, LoadMetrics.ClientFps, SettleSeconds).Mean;
                lines.Add(slowFps >= fpsBase * 0.95
                    ? string.Format(ci, "Your game kept its frame rate under the load: {0:0} fps at baseline, at least {1:0} fps at every step.", fpsBase, slowFps)
                    : string.Format(ci, "Your game: {0:0} fps at baseline, {1:0} fps at its slowest step ({2}){3}.", fpsBase, slowFps, slow.Name, MachineBusy(l, slow.Index)));
            }
        }

        // Another reason for a slow step than the load: other programs on the player's computer, or
        // its memory nearly full (the system then pages, which stalls every program).
        private static string MachineBusy(LoadLedger l, int stage)
        {
            var ci = CultureInfo.InvariantCulture;
            double game = l.Get(stage, LoadMetrics.ClientCpu, SettleSeconds).Mean, all = l.Get(stage, LoadMetrics.ClientMachineCpu, SettleSeconds).Mean;
            double mem = l.Get(stage, LoadMetrics.ClientMemory, SettleSeconds).Mean;
            var parts = new List<string>();
            if (!double.IsNaN(game) && !double.IsNaN(all) && all - game >= 40)
                parts.Add(string.Format(ci, "other programs kept the computer busy (CPU at {0:0}% in all, the game's share {1:0}%)", all, game));
            if (!double.IsNaN(mem) && mem >= 95)
                parts.Add(string.Format(ci, "its memory was {0:0}% full", mem));
            return parts.Count == 0 ? "" : "; at that step " + string.Join(" and ", parts.ToArray());
        }

        /// <summary>
        /// Reads a player simulation: how many simulated players the server kept up with, what gave
        /// first, how much server work each player added and where the frame would fill up, how long
        /// arriving players waited for their area, and the recovery once they left.
        /// </summary>
        public static List<string> JudgePlayers(LoadPlan plan, LoadLedger l, string abortReason, string pattern)
        {
            var ci = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            if (abortReason != null) lines.Add("Stopped early: " + abortReason + ".");
            int b = plan.Stages.FirstOrDefault(s => s.Kind == LoadPlan.Kind.Baseline)?.Index ?? -1;
            var steps = plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load && s.Direction == "players").ToList();
            var measured = steps.Where(s => l.Get(s.Index, LoadMetrics.ServerFrame).Count > 0 || l.Get(s.Index, LoadMetrics.DownStale).Count > 0).ToList();
            string label = "Players (" + pattern + ")";
            if (measured.Count == 0) { lines.Add(label + ": no measurements."); return lines; }

            double frameBase = b >= 0 ? l.Get(b, LoadMetrics.ServerFrame).Mean : double.NaN;
            double staleBase = b >= 0 ? l.Get(b, LoadMetrics.DownStale).P95 : double.NaN;
            if (double.IsNaN(staleBase)) staleBase = 0;
            var readings = ReadPlayers(plan, l);
            Func<LoadPlan.Stage, bool> overloaded = s => readings.First(r => r.Stage == s).Overloaded;
            Func<LoadPlan.Stage, bool> ok = s => readings.First(r => r.Stage == s).KeptUp;

            var fail = measured.FirstOrDefault(s => !ok(s));
            var kept = measured.TakeWhile(s => ok(s)).ToList();
            if (fail == null)
                lines.Add(string.Format(ci, "{0}: the server kept up with {1} simulated player{2}{3}.", label, kept.Last().Players, kept.Last().Players == 1 ? "" : "s",
                    kept.Last() == steps.Last() ? ", the most this test added" : " (the test ended before the next step)"));
            else if (kept.Count == 0)
                lines.Add(label + ": the server did not keep up even with 1 simulated player.");
            else
                lines.Add(string.Format(ci, "{0}: the server kept up with {1} simulated players.", label, kept.Last().Players));

            if (fail != null)
            {
                double f = l.Get(fail.Index, LoadMetrics.ServerFrame, SettleSeconds).Mean;
                double w = l.Get(fail.Index, LoadMetrics.ServerFrameMax, SettleSeconds).P95;
                double st = l.Get(fail.Index, LoadMetrics.DownStale, SettleSeconds).P95;
                double rtt = l.Get(fail.Index, LoadMetrics.Rtt, SettleSeconds).Mean;
                lines.Add(string.Format(ci, "   At {0} players: server frame {1:0.0} ms (baseline {2:0.0}), worst frames {3:0} ms, your world updates up to {4:0.0} s old, round trip {5:0} ms.",
                    fail.Players, f, frameBase, w, st, rtt));
                if (overloaded(fail))
                    lines.Add(string.Format(ci, "   The server's main thread gave first: its work per frame reached {0:0.0} ms.", l.Get(fail.Index, LoadMetrics.ServerWork, SettleSeconds).Mean));
                else
                    lines.Add(string.Format(ci, "   The server itself was not overloaded, but your updates waited: it serves one player per frame in turn, so with {0} players each waits about {1} frames ({2:0} ms at {3:0.0} ms a frame) for its next batch.",
                        fail.Players, fail.Players + 1, (fail.Players + 1) * (double.IsNaN(f) ? ServerBudgetMs : f), double.IsNaN(f) ? ServerBudgetMs : f));
            }

            var withWork = measured.Where(s => !double.IsNaN(l.Get(s.Index, LoadMetrics.ServerWork, SettleSeconds).Mean)).ToList();
            if (withWork.Count >= 2)
            {
                var xs = withWork.Select(s => (double)s.Players).ToList();
                var ys = withWork.Select(s => l.Get(s.Index, LoadMetrics.ServerWork, SettleSeconds).Mean).ToList();
                var (slope, intercept) = LinearFit.Fit(xs, ys);
                if (!double.IsNaN(slope) && slope >= 0.05)
                    lines.Add(string.Format(ci, "   Each simulated player added about {0:0.00} ms of server work per frame ({1} to {2} players); at that rate the server's {3:0} ms frame fills at about {4} players.",
                        slope, xs.Min(), xs.Max(), ServerBudgetMs, (int)Math.Floor((ServerBudgetMs - intercept) / slope)));
                else if (!double.IsNaN(slope))
                    lines.Add(string.Format(ci, "   Server work per frame barely grew with players ({0:0.00} ms each; {1:0.0} ms with {2}).", slope, ys.Last(), xs.Last()));
            }

            var arrivals = measured.SelectMany(s => l.Series(s.Index, LoadMetrics.BotArrival)).OrderBy(v => v).ToList();
            if (arrivals.Count > 0)
            {
                double median = arrivals.Count % 2 == 1 ? arrivals[arrivals.Count / 2] : (arrivals[arrivals.Count / 2 - 1] + arrivals[arrivals.Count / 2]) / 2;
                lines.Add(string.Format(ci, "   Arriving players got their area in typically {0:0} s, slowest {1:0} s ({2} arrivals).", median, arrivals.Last(), arrivals.Count));
            }

            double queue = l.Get(measured.Last().Index, LoadMetrics.BotQueue, SettleSeconds).Mean;
            if (queue >= 8)
                lines.Add(string.Format(ci, "   The simulated players' links were full ({0:0.0} kB waiting each): the server sent them as fast as their links take.", queue));

            var rec = plan.RecoveryFor("players");
            var series = rec != null ? l.Series(rec.Index, LoadMetrics.DownStale) : new double[0];
            if (series.Count > 0)
            {
                string after = CatchUp(series, Math.Max(1.0, staleBase + 0.5));
                lines.Add("   After the simulated players left, " + char.ToLowerInvariant(after[0]) + after.Substring(1));
            }

            if (b >= 0) Headroom(plan, l, b, lines);
            return lines;
        }

        /// <summary>One step of a player simulation as the verdict reads it.</summary>
        public sealed class PlayerReading
        {
            public LoadPlan.Stage Stage;
            public double FrameMs = double.NaN, WorstMs = double.NaN, WorkMs = double.NaN, StaleP95 = double.NaN;
            public bool Measured, Overloaded, KeptUp;
        }

        /// <summary>
        /// The simulation's steps in order, each read by the verdict's rule: overloaded when the
        /// server's frame grew by half (and 10 ms) over the baseline or its worst frames reached
        /// 100 ms and doubled; kept up when not overloaded and the real player's copies stayed fresh.
        /// </summary>
        public static List<PlayerReading> ReadPlayers(LoadPlan plan, LoadLedger l)
        {
            int b = plan.Stages.FirstOrDefault(s => s.Kind == LoadPlan.Kind.Baseline)?.Index ?? -1;
            double frameBase = b >= 0 ? l.Get(b, LoadMetrics.ServerFrame).Mean : double.NaN;
            double worstBase = b >= 0 ? l.Get(b, LoadMetrics.ServerFrameMax).P95 : double.NaN;
            double staleBase = b >= 0 ? l.Get(b, LoadMetrics.DownStale).P95 : double.NaN;
            if (double.IsNaN(staleBase)) staleBase = 0;
            double staleLimit = Math.Max(1.5, staleBase + 1.0);
            var readings = new List<PlayerReading>();
            foreach (var s in plan.Stages.Where(s => s.Kind == LoadPlan.Kind.Load && s.Direction == "players"))
            {
                double f = l.Get(s.Index, LoadMetrics.ServerFrame, SettleSeconds).Mean;
                double w = l.Get(s.Index, LoadMetrics.ServerFrameMax, SettleSeconds).P95;
                double st = l.Get(s.Index, LoadMetrics.DownStale, SettleSeconds).P95;
                bool overloaded = (!double.IsNaN(f) && !double.IsNaN(frameBase) && f >= Math.Max(frameBase * 1.5, frameBase + 10))
                    || (!double.IsNaN(w) && w >= 100 && (double.IsNaN(worstBase) || w >= 2 * worstBase));
                readings.Add(new PlayerReading
                {
                    Stage = s, FrameMs = f, WorstMs = w, StaleP95 = st, WorkMs = l.Get(s.Index, LoadMetrics.ServerWork, SettleSeconds).Mean,
                    Measured = l.Get(s.Index, LoadMetrics.ServerFrame).Count > 0 || l.Get(s.Index, LoadMetrics.DownStale).Count > 0,
                    Overloaded = overloaded, KeptUp = !overloaded && (double.IsNaN(st) || st <= staleLimit),
                });
            }
            return readings;
        }

        /// <summary>One line per step with the steady mean of each measurement (load steps leave out their first seconds).</summary>
        public static List<string> Table(LoadPlan plan, LoadLedger ledger, IEnumerable<string> metrics)
        {
            var ci = CultureInfo.InvariantCulture;
            var cols = metrics.ToList();
            var lines = new List<string>();
            var head = new StringBuilder();
            head.AppendFormat("   {0,-16}", "step");
            foreach (var c in cols) head.AppendFormat(" {0,14}", c);
            lines.Add(head.ToString());
            foreach (var s in plan.Stages)
            {
                if (!ledger.Has(s.Index)) continue;
                var sb = new StringBuilder();
                sb.AppendFormat("   {0,-16}", s.Name);
                foreach (var c in cols)
                {
                    double v = ledger.Get(s.Index, c, s.Kind == LoadPlan.Kind.Load ? SettleSeconds : 0).Mean;
                    sb.Append(' ').Append(double.IsNaN(v) ? new string(' ', 13) + "-" : v.ToString("0.0", ci).PadLeft(14));
                }
                lines.Add(sb.ToString());
            }
            return lines;
        }
    }
}
