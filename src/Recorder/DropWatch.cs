using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Steamworks;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Says how a connection ended when it ends without a goodbye (a player's on the server, the
    /// server's on a player's game), from the last minute of that connection's seconds and how it
    /// was closed: the other side's goodbye (ZNet.RPC_Disconnect), Steam closing it with its reason
    /// (ZSteamSocket.OnStatusChanged, which then closes the socket and forgets the connection's
    /// handle, so the reason is kept by socket), the game's own 30 s timeout (ZRpc's time since
    /// the last answer), or this side closing it. Leaving the world or shutting the server down
    /// goes through ZNet.StopAll, never ZNet.Disconnect, so it is no drop. Only prefixes that read.
    /// </summary>
    internal static class DropWatch
    {
        /// <summary>The seconds kept per connection.</summary>
        public const int SecondsKept = 60;

        /// <summary>A drop: the other side's name, the reading and the connection's last seconds.</summary>
        public static Action<string, DropAnalysis, IReadOnlyList<DropAnalysis.Sample>> Dropped;

        private static readonly Dictionary<long, RingBuffer<DropAnalysis.Sample>> s_samples = new Dictionary<long, RingBuffer<DropAnalysis.Sample>>();
        private static readonly HashSet<ZNetPeer> s_goodbye = new HashSet<ZNetPeer>();
        private static readonly Dictionary<ZSteamSocket, KeyValuePair<DropAnalysis.Ending, string>> s_steam = new Dictionary<ZSteamSocket, KeyValuePair<DropAnalysis.Ending, string>>();
        private static readonly AccessTools.FieldRef<ZRpc, float> s_sinceAnswer = AccessTools.FieldRefAccess<ZRpc, float>("m_timeSinceLastPing");
        private static readonly AccessTools.FieldRef<ZSteamSocket, HSteamNetConnection> s_con = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>("m_con");
        private static readonly System.Reflection.FieldInfo s_sockets = AccessTools.Field(typeof(ZSteamSocket), "m_sockets");

        public static void Start(Harmony harmony)
        {
            var me = typeof(DropWatch);
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), "RPC_Disconnect"), "drops: goodbyes", prefix: Patching.M(me, nameof(GoodbyePrefix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZSteamSocket), "OnStatusChanged"), "drops: Steam's reasons", prefix: Patching.M(me, nameof(StatusPrefix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) }), "drops: how connections end",
                prefix: Patching.M(me, nameof(DisconnectPrefix)));
        }

        /// <summary>A second of one connection, from the recorder's row for it (a player's on the server, the timeline's on a player's game).</summary>
        public static void Add(long peer, TimelineTable.Row row, double at)
        {
            if (!s_samples.TryGetValue(peer, out var ring)) s_samples[peer] = ring = new RingBuffer<DropAnalysis.Sample>(SecondsKept);
            ring.Add(new DropAnalysis.Sample
            {
                At = at, InKbs = RecorderModule.Num(row, "sock_in_kbs"), OutKbs = RecorderModule.Num(row, "sock_out_kbs"), QueueKb = RecorderModule.Num(row, "send_queue_kb"),
                UnackedKb = RecorderModule.Num(row, "unacked_kb"), PingMs = RecorderModule.Num(row, "ping_ms"), MaxGapMs = RecorderModule.Num(row, "max_gap_ms"),
                RateKbs = RecorderModule.Num(row, "steam_rate_kbs"), QualityLocal = RecorderModule.Num(row, "quality_local"), QualityRemote = RecorderModule.Num(row, "quality_remote"),
            });
        }

        /// <summary>A connection's last seconds, oldest first; none when it has none yet.</summary>
        public static DropAnalysis.Sample[] Recent(long peer) => s_samples.TryGetValue(peer, out var ring) ? ring.ToArray() : new DropAnalysis.Sample[0];

        private static void GoodbyePrefix(ZNet __instance, ZRpc rpc)
        {
            try
            {
                var peer = __instance.GetPeers().FirstOrDefault(p => p.m_rpc == rpc);
                if (peer != null) s_goodbye.Add(peer);
            }
            catch (Exception) { }
        }

        private static void StatusPrefix(SteamNetConnectionStatusChangedCallback_t data)
        {
            try
            {
                var state = data.m_info.m_eState;
                if (state != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                    && state != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally) return;
                if (!(s_sockets?.GetValue(null) is List<ZSteamSocket> sockets)) return;
                var socket = sockets.FirstOrDefault(s => s != null && s_con(s) == data.m_hConn);
                if (socket == null) return;
                if (s_steam.Count > 64) s_steam.Clear();
                var ending = state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ? DropAnalysis.Ending.ClosedByPeer : DropAnalysis.Ending.SteamProblem;
                s_steam[socket] = new KeyValuePair<DropAnalysis.Ending, string>(ending, data.m_info.m_eEndReason + ": " + data.m_info.m_szEndDebug);
            }
            catch (Exception) { }
        }

        private static void DisconnectPrefix(ZNet __instance, ZNetPeer peer)
        {
            try
            {
                if (peer == null || Bots.IsBot(peer)) return;
                s_samples.TryGetValue(peer.m_uid, out var ring);
                s_samples.Remove(peer.m_uid);
                if (s_goodbye.Remove(peer)) return; // a goodbye is no drop; the event log says it left

                double silent = peer.m_rpc != null ? s_sinceAnswer(peer.m_rpc) : 0;
                string steam = null;
                DropAnalysis.Ending ending;
                var found = SocketChain.Find(peer.m_socket, typeof(ISocket), s => s is ZSteamSocket).Found as ZSteamSocket;
                if (found != null && s_steam.TryGetValue(found, out var reason))
                {
                    s_steam.Remove(found);
                    ending = reason.Key;
                    steam = reason.Value;
                    // Steam calls a line it gave up on a problem; its silence is what decides.
                    if (ending == DropAnalysis.Ending.SteamProblem && silent < 5) ending = DropAnalysis.Ending.ClosedByPeer;
                }
                else if (silent >= 29) ending = DropAnalysis.Ending.Timeout;
                else if (peer.m_rpc == null || !peer.m_rpc.IsConnected()) ending = DropAnalysis.Ending.ClosedByPeer;
                else ending = DropAnalysis.Ending.ClosedHere;

                bool server = __instance.IsServer();
                string who = server ? (string.IsNullOrEmpty(peer.m_playerName) ? peer.m_socket?.GetHostName() ?? "a player" : peer.m_playerName) : "the server";
                var samples = ring != null ? ring.ToArray() : new DropAnalysis.Sample[0];
                Dropped?.Invoke(who, DropAnalysis.Read(who, !server, samples, ending, silent, steam), samples);
            }
            catch (Exception) { }
        }
    }
}
