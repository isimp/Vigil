using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Counts the routed network messages (RPCs) this game sends and receives, by method name,
    /// the kind of object they are aimed at and, for status effects, which effect. Every routed
    /// message is packed by RoutedRPCData.Serialize just before it goes out and unpacked by
    /// RoutedRPCData.Deserialize when one arrives, so those two methods see all of them. It only
    /// reads; the message is never changed.
    /// </summary>
    internal sealed class RpcProbe
    {
        private const int Top = 15;
        private const int ResolveAttempts = 5;

        private static readonly MessageTally s_sent = new MessageTally();
        private static readonly MessageTally s_received = new MessageTally();
        private static readonly Dictionary<int, string> s_methodNames = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> s_failedResolves = new Dictionary<int, int>();
        private static readonly int s_addStatusEffect = "RPC_AddStatusEffect".GetStableHashCode();
        private static readonly FieldInfo s_viewFunctions = AccessTools.Field(typeof(ZNetView), "m_functions");
        private static readonly FieldInfo s_routedFunctions = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");

        private readonly string _path;

        public RpcProbe(Harmony harmony, string path)
        {
            _path = path;
            File.WriteAllText(path, "Vigil network messages " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " - routed RPCs this game sent and received, per 10 s window\r\n");
            var me = typeof(RpcProbe);
            Patching.Apply(harmony, AccessTools.Method(typeof(ZRoutedRpc), nameof(ZRoutedRpc.InvokeRoutedRPC),
                    new[] { typeof(long), typeof(ZDOID), typeof(string), typeof(object[]) }), "message names",
                prefix: Patching.M(me, nameof(InvokePrefix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZRoutedRpc.RoutedRPCData), nameof(ZRoutedRpc.RoutedRPCData.Serialize)), "messages sent",
                postfix: Patching.M(me, nameof(SerializePostfix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZRoutedRpc.RoutedRPCData), nameof(ZRoutedRpc.RoutedRPCData.Deserialize)), "messages received",
                postfix: Patching.M(me, nameof(DeserializePostfix)));
        }

        private static void InvokePrefix(string methodName)
        {
            if (!ProbeState.Active) return;
            if (methodName == null) return;
            int hash = methodName.GetStableHashCode();
            if (!s_methodNames.ContainsKey(hash)) s_methodNames[hash] = methodName;
        }

        private static void SerializePostfix(ZRoutedRpc.RoutedRPCData __instance, ZPackage pkg)
        {
            if (!ProbeState.Active) return;
            try
            {
                string label = Label(__instance);
                s_sent.Add(label, pkg.Size());
                Count(s_secondSent, label);
                NetAttribution.RoutedSent(Owner(__instance), label, pkg.Size());
            }
            catch (Exception) { }
        }

        private static void DeserializePostfix(ZRoutedRpc.RoutedRPCData __instance, ZPackage pkg)
        {
            if (!ProbeState.Active) return;
            try
            {
                string label = Label(__instance);
                // On the server every routed message passes through here; who sent it is what matters.
                if (ZNet.instance != null && ZNet.instance.IsServer()) label += "  from " + Sender(__instance.m_senderPeerID);
                s_received.Add(label, pkg.Size());
                Count(s_secondReceived, label);
                NetAttribution.RoutedReceived(Owner(__instance), label, pkg.Size());
            }
            catch (Exception) { }
        }

        private static readonly Dictionary<string, int> s_secondSent = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> s_secondReceived = new Dictionary<string, int>();

        private static void Count(Dictionary<string, int> d, string label)
        {
            d.TryGetValue(label, out int n);
            d[label] = n + 1;
        }

        private static string Sender(long uid)
        {
            var peer = ZNet.instance.GetPeer(uid);
            return peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : "peer " + uid;
        }

        /// <summary>Counts for the second that just ended and the most frequent kind each way.</summary>
        public void EndSecond(out int sent, out string topSent, out int received, out string topReceived)
        {
            TopOf(s_secondSent, out sent, out topSent);
            TopOf(s_secondReceived, out received, out topReceived);
            s_secondSent.Clear(); s_secondReceived.Clear();
        }

        private static void TopOf(Dictionary<string, int> d, out int total, out string top)
        {
            total = 0; top = ""; int best = 0;
            foreach (var kv in d)
            {
                total += kv.Value;
                if (kv.Value > best) { best = kv.Value; top = kv.Key + " x" + kv.Value; }
            }
        }

        public string Peek(float seconds) =>
            " sent: " + s_sent.Peek(seconds, Top) + " received: " + s_received.Peek(seconds, Top);

        private static string Label(ZRoutedRpc.RoutedRPCData data)
        {
            string label = MethodName(data);
            if (data.m_methodHash == s_addStatusEffect) label += "(" + EffectName(data.m_parameters) + ")";
            if (!data.m_targetZDO.IsNone())
            {
                ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(data.m_targetZDO) : null;
                label += " -> " + (zdo != null ? RecorderModule.NameOf(zdo.GetPrefab()) : "(object not loaded)");
            }
            return label;
        }

        private static string MethodName(ZRoutedRpc.RoutedRPCData data)
        {
            int hash = data.m_methodHash;
            if (s_methodNames.TryGetValue(hash, out var name)) return name;
            s_failedResolves.TryGetValue(hash, out int failed);
            if (failed < ResolveAttempts)
            {
                name = Resolve(data);
                if (name != null) { s_methodNames[hash] = name; return name; }
                s_failedResolves[hash] = failed + 1;
            }
            return "hash:" + hash;
        }

        // A received message names its method only by hash; the handler registered for that hash
        // carries the method, whose name is the RPC's name in almost every case.
        private static string Resolve(ZRoutedRpc.RoutedRPCData data) => HandlerOf(data)?.Method.Name;

        /// <summary>"mod: method" of a routed message about to be handled.</summary>
        public static string HandlerLabel(ZRoutedRpc.RoutedRPCData data) => Owner(data) + ": " + MethodName(data);

        private static readonly Dictionary<int, string> s_owners = new Dictionary<int, string>();

        /// <summary>The mod a routed message belongs to (see <see cref="NetAttribution.OwnerOf"/>), cached per method.</summary>
        private static string Owner(ZRoutedRpc.RoutedRPCData data)
        {
            if (s_owners.TryGetValue(data.m_methodHash, out var o)) return o;
            var handler = HandlerOf(data);
            s_methodNames.TryGetValue(data.m_methodHash, out var name);
            o = NetAttribution.OwnerOf(name, handler);
            if (handler != null || name != null) s_owners[data.m_methodHash] = o;
            return o;
        }

        private static Delegate HandlerOf(ZRoutedRpc.RoutedRPCData data)
        {
            IDictionary functions = null;
            if (data.m_targetZDO.IsNone())
            {
                if (ZRoutedRpc.instance != null) functions = s_routedFunctions?.GetValue(ZRoutedRpc.instance) as IDictionary;
            }
            else if (ZDOMan.instance != null && ZNetScene.instance != null)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(data.m_targetZDO);
                ZNetView view = zdo != null ? ZNetScene.instance.FindInstance(zdo) : null;
                if (view != null) functions = s_viewFunctions?.GetValue(view) as IDictionary;
            }
            if (functions == null || !functions.Contains(data.m_methodHash)) return null;
            return NetAttribution.HandlerDelegate(functions[data.m_methodHash]);
        }

        private static string EffectName(ZPackage parameters)
        {
            int pos = parameters.GetPos();
            try
            {
                parameters.SetPos(0);
                int effect = parameters.ReadInt();
                var se = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(effect) : null;
                return se != null ? se.name : "effect:" + effect;
            }
            catch (Exception) { return "?"; }
            finally { parameters.SetPos(pos); }
        }

        public void Report(float span)
        {
            string text = "\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + span.ToString("0.0") + " s\r\n" +
                " sent: " + s_sent.EndWindow(span, Top) +
                " received: " + s_received.EndWindow(span, Top) +
                " traffic (every package, bytes):\r\n" + NetAttribution.Window(span);
            try { File.AppendAllText(_path, text); } catch (Exception) { }
        }

        public string Summary(float seconds)
        {
            return " sent: " + s_sent.Summary(seconds, Top) + " received: " + s_received.Summary(seconds, Top);
        }
    }
}
