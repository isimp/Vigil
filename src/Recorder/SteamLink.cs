using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Vigil.Core;
using Steamworks;

namespace Vigil.Recorder
{
    /// <summary>
    /// Steam's own view of a connection (ZSteamSocket), read from Steam: what it puts on the wire,
    /// the send rate it allows itself, the data waiting to go out and the data sent but not yet
    /// confirmed, its own ping (the line, without the queue in front of it), and the configured
    /// send-rate floor and ceiling. The game's GetCurrentSendRate is none of these: it divides the
    /// queue by the ping. A dedicated server talks to Steam through the game-server interface, a
    /// player's game through the client one. Only reads; failures are thrown for the caller to name.
    /// On the server the player's socket can sit under wrappers other mods left on it (ServerSync's
    /// BufferingSocket); the Steam socket is looked for underneath them.
    /// </summary>
    internal static class SteamLink
    {
        internal struct Status
        {
            public double PingMs, QualityLocal, QualityRemote, OutKbs, InKbs, RateKbs, PendingKb, UnackedKb, WaitMs;
            /// <summary>The wrappers the Steam socket was found under, outermost first; empty when none.</summary>
            public List<Type> Wrappers;
        }

        private static readonly FieldInfo s_con = AccessTools.Field(typeof(ZSteamSocket), "m_con");

        private static bool Dedicated => ZNet.instance != null && ZNet.instance.IsDedicated();

        /// <summary>False for a connection that is not a Steam socket (simulated players, PlayFab).</summary>
        public static bool TryRead(ISocket socket, out Status st) => TryRead(socket, out st, out _);

        /// <param name="socket">The connection, as the game holds it.</param>
        /// <param name="st">Steam's numbers, when true.</param>
        /// <param name="why">When false: the reason, for the game log.</param>
        public static bool TryRead(ISocket socket, out Status st, out string why)
        {
            st = default(Status);
            why = null;
            var chain = SocketChain.Find(socket, typeof(ISocket), s => s is ZSteamSocket);
            if (chain.Found == null) { why = chain.WhyNot("a Steam socket"); return false; }
            st.Wrappers = chain.Wrappers;
            if (s_con == null) { why = "ZSteamSocket has no m_con field"; return false; }
            var con = (HSteamNetConnection)s_con.GetValue(chain.Found);
            if (con == HSteamNetConnection.Invalid) { why = "the socket's Steam connection handle is not set"; return false; }
            var s = default(SteamNetConnectionRealTimeStatus_t);
            var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
            bool dedicated = Dedicated;
            EResult r = dedicated
                ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(con, ref s, 0, ref lanes)
                : SteamNetworkingSockets.GetConnectionRealTimeStatus(con, ref s, 0, ref lanes);
            if (r != EResult.k_EResultOK)
            {
                why = "Steam answered " + r + " through its " + (dedicated ? "game-server" : "client") + " interface for connection " + con.m_HSteamNetConnection;
                return false;
            }
            st.PingMs = s.m_nPing;
            st.QualityLocal = s.m_flConnectionQualityLocal;
            st.QualityRemote = s.m_flConnectionQualityRemote;
            st.OutKbs = s.m_flOutBytesPerSec / 1024.0;
            st.InKbs = s.m_flInBytesPerSec / 1024.0;
            st.RateKbs = s.m_nSendRateBytesPerSecond / 1024.0;
            st.PendingKb = (s.m_cbPendingReliable + s.m_cbPendingUnreliable) / 1024.0;
            st.UnackedKb = s.m_cbSentUnackedReliable / 1024.0;
            // how long a message queued now would wait before Steam starts putting it on the wire
            st.WaitMs = s.m_usecQueueTime.m_SteamNetworkingMicroseconds / 1000.0;
            return true;
        }

        /// <summary>The send-rate floor and ceiling Steam gives new connections (kB/s), NaN where Steam does not say.</summary>
        public static void Rates(out double floorKbs, out double ceilingKbs)
        {
            floorKbs = ReadInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin) / 1024.0;
            ceilingKbs = ReadInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax) / 1024.0;
        }

        private static double ReadInt(ESteamNetworkingConfigValue v)
        {
            IntPtr buf = Marshal.AllocHGlobal(8);
            try
            {
                ulong size = 8;
                ESteamNetworkingConfigDataType type;
                var r = Dedicated
                    ? SteamGameServerNetworkingUtils.GetConfigValue(v, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, out type, buf, ref size)
                    : SteamNetworkingUtils.GetConfigValue(v, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, out type, buf, ref size);
                bool ok = r == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK
                    || r == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited;
                return ok && type == ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32 ? Marshal.ReadInt32(buf) : double.NaN;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// The world-update batch limit (kB): the game hands Steam new world updates only while less
        /// than this waits or is unconfirmed. The game's own value is 10 kB; the Network mod
        /// (org.bepinex.plugins.network) replaces it with its Batch Size when its master switch and
        /// "Increase Batch Size" are on. NaN when that mod's settings are not where they used to be.
        /// </summary>
        public static double BatchKb()
        {
            const double vanilla = 10240 / 1024.0;
            if (!Chainloader.PluginInfos.TryGetValue("org.bepinex.plugins.network", out var info) || info.Instance == null) return vanilla;
            var cfg = info.Instance.Config;
            string master = Setting(cfg, "1 - General", "Enable Networking Improvements");
            string on = Setting(cfg, "2 - ZDO Sending", "Increase Batch Size");
            string size = Setting(cfg, "2 - ZDO Sending", "Batch Size");
            if (master == null || on == null || size == null) return double.NaN;
            return master == "On" && on == "On" && int.TryParse(size, out int bytes) ? bytes / 1024.0 : vanilla;
        }

        private static string Setting(ConfigFile cfg, string section, string key)
        {
            foreach (var kv in cfg)
                if (kv.Key.Section == section && kv.Key.Key == key) return kv.Value.BoxedValue?.ToString();
            return null;
        }
    }
}
