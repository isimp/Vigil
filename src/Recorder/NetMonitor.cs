using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// What goes over the wire, per connection: every package received (ZRpc.HandlePackage) and
    /// sent (ZRpc.SendPackage) with its size, the longest silence between received packages,
    /// world objects received by prefab with the size of their data (ZDO.Deserialize inside
    /// ZDOMan.RPC_ZDOData, counted like uploads), and the socket's own view (ping, quality, send
    /// queue, rates). Only reads.
    /// </summary>
    internal sealed class NetMonitor
    {
        internal sealed class Conn
        {
            public string Name = "?";
            public string Key;               // unique per connection, for the join quiet time
            public long PacketsIn, BytesIn, PacketsOut, BytesOut;
            public long LastReceive;          // Stopwatch ticks
            public double MaxGapMs;           // longest silence this second, including an open one
            public long RunBytesIn, RunBytesOut;
        }

        private static readonly Dictionary<ZRpc, Conn> s_conns = new Dictionary<ZRpc, Conn>();
        private static readonly MessageTally s_zdoDown = new MessageTally(rankByBytes: true);
        private static bool s_inZdoData;
        private static long s_zdoDownBytes, s_runZdoDownBytes;
        private static int s_zdoDownCount;

        public NetMonitor(Harmony harmony)
        {
            var me = typeof(NetMonitor);
            Patching.Apply(harmony, AccessTools.Method(typeof(ZRpc), "HandlePackage"), "packages received",
                prefix: Patching.M(me, nameof(ReceivePrefix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZRpc), "SendPackage"), "packages sent",
                prefix: Patching.M(me, nameof(SendPrefix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData"), "world updates received",
                prefix: Patching.M(me, nameof(ZdoDataPrefix)), finalizer: Patching.M(me, nameof(ZdoDataFinalizer)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZDO), "Deserialize"), "world objects received by prefab",
                postfix: Patching.M(me, nameof(DeserializePostfix)));
        }

        private static Conn ConnOf(ZRpc rpc)
        {
            if (!s_conns.TryGetValue(rpc, out var c))
            {
                s_conns[rpc] = c = new Conn { LastReceive = Stopwatch.GetTimestamp(), Key = "conn " + (++s_connCount) };
                s_joins.Started(c.Key, Now());
                c.Name = NameOf(rpc);
            }
            return c;
        }

        private static string NameOf(ZRpc rpc)
        {
            try
            {
                if (ZNet.instance == null) return "?";
                foreach (var p in ZNet.instance.GetPeers())
                    if (p.m_rpc == rpc) return p.m_server ? "server" : (string.IsNullOrEmpty(p.m_playerName) ? "peer " + p.m_uid : p.m_playerName);
            }
            catch (Exception) { }
            return "?";
        }

        private static void ReceivePrefix(ZRpc __instance, ZPackage package)
        {
            if (!ProbeState.Active) return;
            try
            {
                var c = ConnOf(__instance);
                long now = Stopwatch.GetTimestamp();
                double gap = (now - c.LastReceive) * 1000.0 / Stopwatch.Frequency;
                if (gap > c.MaxGapMs) c.MaxGapMs = gap;
                c.LastReceive = now;
                int size = package != null ? package.Size() : 0;
                c.PacketsIn++; c.BytesIn += size; c.RunBytesIn += size;
                if (package != null) NetAttribution.Received(__instance, package);
            }
            catch (Exception) { }
        }

        private static void SendPrefix(ZRpc __instance, ZPackage pkg)
        {
            if (!ProbeState.Active) return;
            try
            {
                var c = ConnOf(__instance);
                int size = pkg != null ? pkg.Size() : 0;
                c.PacketsOut++; c.BytesOut += size; c.RunBytesOut += size;
                NetAttribution.Sent(__instance, size);
            }
            catch (Exception) { }
        }

        private static void ZdoDataPrefix(ZPackage pkg)
        {
            if (!ProbeState.Active) return;
            s_inZdoData = true;
            if (pkg != null) { s_zdoDownBytes += pkg.Size(); s_runZdoDownBytes += pkg.Size(); }
        }

        private static void ZdoDataFinalizer() { s_inZdoData = false; }

        private static void DeserializePostfix(ZDO __instance, ZPackage pkg)
        {
            if (!ProbeState.Active) return;
            if (!s_inZdoData) return;
            try
            {
                s_zdoDownCount++;
                s_zdoDown.Add(RecorderModule.NameOf(__instance.GetPrefab()), pkg != null ? pkg.Size() : 0);
                LoadTest.CountReceived(__instance, pkg);
            }
            catch (Exception) { }
        }

        public IEnumerable<KeyValuePair<ZRpc, Conn>> Connections => s_conns;
        public long ZdoDownBytes => s_zdoDownBytes;
        public long RunZdoDownBytes => s_runZdoDownBytes;
        public int ZdoDownCount => s_zdoDownCount;

        /// <summary>Longest silence on any connection this second, counting one still open now.</summary>
        // A joining player is sent the area around them while their own loading screen keeps them
        // quiet; their connection is not judged until this has passed.
        private const double JoinQuietSeconds = 90;
        private static readonly Settling s_joins = new Settling(JoinQuietSeconds);
        private static int s_connCount;
        private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>True once the connection has been up for the join quiet time.</summary>
        public bool Settled(ZRpc rpc) => !s_conns.TryGetValue(rpc, out var c) || s_joins.Settled(c.Key, Now());

        public double MaxGapMs(out string from) => MaxGapMs(out from, settledOnly: false);

        public double MaxGapMs(out string from, bool settledOnly)
        {
            double worst = 0; from = "";
            long now = Stopwatch.GetTimestamp();
            foreach (var kv in s_conns)
            {
                if (settledOnly && !s_joins.Settled(kv.Value.Key, Now())) continue;
                double open = (now - kv.Value.LastReceive) * 1000.0 / Stopwatch.Frequency;
                double g = Math.Max(kv.Value.MaxGapMs, open);
                if (g > worst) { worst = g; from = kv.Value.Name; }
            }
            return worst;
        }

        public string ZdoDownReport(float span) => s_zdoDown.EndWindow(span, 15);
        public string ZdoDownSummary(float seconds) => s_zdoDown.Summary(seconds, 20);

        public void EndSecond()
        {
            foreach (var c in s_conns.Values) { c.PacketsIn = c.BytesIn = c.PacketsOut = c.BytesOut = 0; c.MaxGapMs = 0; }
            s_zdoDownBytes = 0; s_zdoDownCount = 0;
            // forget connections that are gone
            if (ZNet.instance == null) { s_conns.Clear(); return; }
            var live = new HashSet<ZRpc>();
            foreach (var p in ZNet.instance.GetPeers()) live.Add(p.m_rpc);
            var dead = new List<ZRpc>();
            foreach (var k in s_conns.Keys) if (!live.Contains(k)) dead.Add(k);
            foreach (var k in dead) s_conns.Remove(k);
            foreach (var kv in s_conns) if (kv.Value.Name == "?" || kv.Value.Name.StartsWith("peer ")) kv.Value.Name = NameOf(kv.Key);
        }
    }
}
