using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;

namespace Vigil.Recorder
{
    /// <summary>
    /// Vigil-events.tsv: things that happen once rather than every second. Players joining and
    /// leaving (on a client: connecting to and losing the server, and leaving on your own), a
    /// server shutting down, world saves with how long preparing them and the save thread took,
    /// and every incident report written.
    /// Saves run partly on their own thread, hence the lock.
    /// </summary>
    internal static class EventLog
    {
        private static readonly object s_lock = new object();
        private static string s_path;
        private static readonly Stopwatch s_saveMain = new Stopwatch();
        [ThreadStatic] private static Stopwatch t_saveThread;

        public static void Start(string dir, Harmony harmony)
        {
            s_path = Path.Combine(dir, "Vigil-events.tsv");
            try { File.WriteAllText(s_path, "time\tkind\twho\tdetail\r\n"); } catch (Exception) { s_path = null; }
            var me = typeof(EventLog);
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), "RPC_PeerInfo"), "players joining", postfix: Patching.M(me, nameof(PeerInfoPostfix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) }), "players leaving",
                prefix: Patching.M(me, nameof(DisconnectPrefix)));
            // Leaving on your own, or a server shutting down, closes the connections without
            // ZNet.Disconnect; StopAll is the one place both go through.
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), "StopAll"), "leaving the world",
                prefix: Patching.M(me, nameof(StopAllPrefix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), "SaveWorldThread"), "world save thread",
                prefix: Patching.M(me, nameof(SaveThreadPrefix)), finalizer: Patching.M(me, nameof(SaveThreadFinalizer)));
            // Each step of a join, timed with every mod hook on it, as one line per step.
            foreach (var step in new[] { "OnNewConnection", "RPC_ServerHandshake", "RPC_PeerInfo", "RPC_CharacterID" })
                Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), step), "join step " + step,
                    prefix: Patching.M(me, nameof(StepPrefix)), finalizer: Patching.M(me, nameof(StepFinalizer)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.AddPeer)), "join step AddPeer",
                prefix: Patching.M(me, nameof(StepPrefix)), finalizer: Patching.M(me, nameof(StepFinalizer)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZoneSystem), "OnNewPeer"), "join step OnNewPeer",
                prefix: Patching.M(me, nameof(StepPrefix)), finalizer: Patching.M(me, nameof(StepFinalizer)));
            ZNet.WorldSaveStarted += () => s_saveMain.Restart();
            ZNet.WorldSaveFinished += () =>
            {
                s_saveMain.Stop();
                // With a mod that spreads the save over frames (AsyncSave) this spans many frames; the
                // timeline's frame times show whether the game actually paused.
                Write("save", "world", "save prepared: " + s_saveMain.ElapsedMilliseconds + " ms from start to finish");
            };
        }

        public static void Write(string kind, string who, string detail)
        {
            if (s_path == null) return;
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "\t" + kind + "\t" + Clean(who) + "\t" + Clean(detail) + "\r\n";
            lock (s_lock)
            {
                try { File.AppendAllText(s_path, line); } catch (Exception) { }
            }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        private static string Name(ZNetPeer p) =>
            p == null ? "?" : p.m_server ? "server" : string.IsNullOrEmpty(p.m_playerName) ? "peer " + p.m_uid : p.m_playerName;

        private static void PeerInfoPostfix(ZRpc rpc)
        {
            try
            {
                var peer = ZNet.instance?.GetPeers().Find(p => p.m_rpc == rpc);
                if (Bots.IsBot(peer)) return; // the player simulation reports its own players
                bool server = ZNet.instance != null && ZNet.instance.IsServer();
                Write(server ? "joined" : "connected", Name(peer), peer?.m_socket != null ? peer.m_socket.GetEndPointString() : "");
            }
            catch (Exception) { }
        }

        private static void DisconnectPrefix(ZNetPeer peer)
        {
            try
            {
                if (Bots.IsBot(peer)) return; // the player simulation reports its own players
                bool server = ZNet.instance != null && ZNet.instance.IsServer();
                Write(server ? "left" : "disconnected", Name(peer), "");
            }
            catch (Exception) { }
        }

        private static readonly System.Reflection.FieldInfo s_haveStopped = AccessTools.Field(typeof(ZNet), "m_haveStoped");

        private static void StopAllPrefix(ZNet __instance)
        {
            try
            {
                if (s_haveStopped != null && (bool)s_haveStopped.GetValue(__instance)) return; // already stopped once
                var peers = __instance.GetPeers();
                if (__instance.IsServer())
                {
                    foreach (var p in peers) Write("left", Name(p), "server shut down");
                    Write("stopped", "world", "server shut down with " + peers.Count + " player(s) on");
                }
                else
                {
                    foreach (var p in peers) Write("disconnected", Name(p), "you left the world");
                }
            }
            catch (Exception) { }
        }

        // Steps run inside each other (RPC_PeerInfo adds the peer to ZDOMan, which tells ZoneSystem), hence a stack.
        [ThreadStatic] private static System.Collections.Generic.Stack<Stopwatch> t_steps;

        private static void StepPrefix()
        {
            if (t_steps == null) t_steps = new System.Collections.Generic.Stack<Stopwatch>();
            t_steps.Push(Stopwatch.StartNew());
        }

        private static void StepFinalizer(System.Reflection.MethodBase __originalMethod, Exception __exception)
        {
            long ms = t_steps != null && t_steps.Count > 0 ? t_steps.Pop().ElapsedMilliseconds : -1;
            Write("join-step", __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name,
                ms + " ms (game code and every mod hook on it)" + (__exception != null ? ", failed: " + __exception.Message : ""));
        }

        private static void SaveThreadPrefix()
        {
            t_saveThread = Stopwatch.StartNew();
        }

        private static void SaveThreadFinalizer(Exception __exception)
        {
            long ms = t_saveThread != null ? t_saveThread.ElapsedMilliseconds : -1;
            Write("save", "world", "save thread took " + ms + " ms" + (__exception != null ? ", failed: " + __exception.Message : ""));
        }
    }
}
