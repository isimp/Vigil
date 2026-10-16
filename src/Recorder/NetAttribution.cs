using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Which mod the bytes on the wire belong to. Every package names its message by hash in its
    /// first four bytes; the name is learned from ZRpc.Invoke when sent, and the receiving
    /// handler (ZRpc's own table) tells which assembly handles it. Routed messages (one package
    /// kind, "RoutedRPC") are split further by <see cref="RpcProbe"/>, which hands each one in
    /// with its own owner. World-object sync (ZDOData) counts for the game. A message this game
    /// never sent is named from <see cref="Book"/>: a postfix on the game's hash function
    /// (<c>StringExtensionMethods.GetStableHashCode</c>) learns every name hashed while the game runs.
    /// Only reads.
    /// </summary>
    internal static class NetAttribution
    {
        private const string Routed = "RoutedRPC";

        private static readonly MessageTally s_outByMod = new MessageTally(rankByBytes: true);
        private static readonly MessageTally s_inByMod = new MessageTally(rankByBytes: true);
        private static readonly MessageTally s_outByKind = new MessageTally(rankByBytes: true);
        private static readonly MessageTally s_inByKind = new MessageTally(rankByBytes: true);
        private static readonly Dictionary<string, long> s_secOut = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> s_secIn = new Dictionary<string, long>();
        private static readonly Dictionary<int, string> s_names = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> s_owners = new Dictionary<int, string>();
        private static readonly FieldInfo s_zrpcFunctions = AccessTools.Field(typeof(ZRpc), "m_functions");
        private static Dictionary<string, string> s_guidToMod, s_assemblyToMod;
        [ThreadStatic] private static string t_sending;

        /// <summary>The names of the messages, for every number on the wire (<see cref="MessageNames"/>).</summary>
        public static readonly MessageNames Book = new MessageNames();

        public static void Patch(Harmony harmony)
        {
            Patching.Apply(harmony, AccessTools.Method(typeof(ZRpc), nameof(ZRpc.Invoke), new[] { typeof(string), typeof(object[]) }), "package names",
                prefix: Patching.M(typeof(NetAttribution), nameof(InvokePrefix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(StringExtensionMethods), nameof(StringExtensionMethods.GetStableHashCode), new[] { typeof(string) }), "message names learned",
                postfix: Patching.M(typeof(NetAttribution), nameof(HashPostfix)));
        }

        // Every name the game or a mod hashes, on any thread; the book keeps the first of each number.
        private static void HashPostfix(string str, int __result) => Book.Learned(__result, str);

        /// <summary>Once every plugin has loaded: each mod's config sync message, "id ConfigSync".</summary>
        public static void LearnSyncNames()
        {
            Maps();
            Book.Sync(s_guidToMod);
        }

        private static void InvokePrefix(string method)
        {
            if (!ProbeState.Active) return;
            t_sending = method;
            if (method == null) return;
            int hash = method.GetStableHashCode();
            if (!s_names.ContainsKey(hash)) s_names[hash] = method;
        }

        private static void Maps()
        {
            if (s_guidToMod != null) return;
            s_guidToMod = new Dictionary<string, string>(StringComparer.Ordinal);
            s_assemblyToMod = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Chainloader.PluginInfos.Values)
            {
                s_guidToMod[p.Metadata.GUID] = p.Metadata.Name;
                if (p.Instance != null) s_assemblyToMod[p.Instance.GetType().Assembly.GetName().Name] = p.Metadata.Name;
            }
        }

        /// <summary>The mod a message belongs to, by its name and the assembly of its handler.</summary>
        public static string OwnerOf(string name, Delegate handler, string namedIn = null)
        {
            Maps();
            string asm = handler?.Method?.DeclaringType?.Assembly.GetName().Name;
            return NetOwner.Resolve(name, asm, s_guidToMod, s_assemblyToMod, namedIn);
        }

        /// <summary>The first delegate stored in a handler object (the game's RpcMethod / RoutedMethod wrappers).</summary>
        public static Delegate HandlerDelegate(object handler)
        {
            if (handler == null) return null;
            foreach (var f in handler.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(handler) is Delegate d) return d;
            return null;
        }

        /// <summary>A package went out on <paramref name="rpc"/>.</summary>
        public static void Sent(ZRpc rpc, int bytes)
        {
            string name = t_sending ?? "?";
            t_sending = null;
            if (name == Routed) return; // counted per message by RpcProbe
            Count(s_outByMod, s_outByKind, s_secOut, PackageOwner(rpc, name.GetStableHashCode(), name), name, bytes);
        }

        /// <summary>A package came in on <paramref name="rpc"/>; its first four bytes name the message.</summary>
        public static void Received(ZRpc rpc, ZPackage package)
        {
            int pos = package.GetPos();
            int hash;
            try { package.SetPos(0); hash = package.ReadInt(); }
            catch (Exception) { return; }
            finally { package.SetPos(pos); }
            string name = hash == 0 ? "ping" : s_names.TryGetValue(hash, out var n) ? n : Book.NameOf(hash);
            if (name == Routed) return;
            string owner = PackageOwner(rpc, hash, name);
            if (name == null) name = "message " + hash;
            Count(s_inByMod, s_inByKind, s_secIn, owner, name, package.Size());
        }

        private static readonly int s_routedHash = Routed.GetStableHashCode();

        /// <summary>
        /// "mod: message" for a package about to be handled, or null for the package kind that
        /// carries routed messages (those are timed one by one where they are handled).
        /// </summary>
        public static string HandlerLabel(ZRpc rpc, ZPackage package)
        {
            int pos = package.GetPos();
            int hash;
            try { package.SetPos(0); hash = package.ReadInt(); }
            catch (Exception) { return null; }
            finally { package.SetPos(pos); }
            if (hash == s_routedHash) return null;
            string name = hash == 0 ? "ping" : s_names.TryGetValue(hash, out var n) ? n : Book.NameOf(hash);
            return PackageOwner(rpc, hash, name) + ": " + (name ?? "message " + hash);
        }

        /// <summary>A routed message, counted by <see cref="RpcProbe"/> with its owner already known.</summary>
        public static void RoutedSent(string owner, string label, int bytes) => Count(s_outByMod, s_outByKind, s_secOut, owner, label, bytes);
        public static void RoutedReceived(string owner, string label, int bytes) => Count(s_inByMod, s_inByKind, s_secIn, owner, label, bytes);

        private static string PackageOwner(ZRpc rpc, int hash, string name)
        {
            if (hash == 0 || name == "ZDOData" || name == "ping") return "Valheim";
            if (s_owners.TryGetValue(hash, out var o)) return o;
            Delegate handler = null;
            try
            {
                if (s_zrpcFunctions?.GetValue(rpc) is IDictionary functions && functions.Contains(hash)) handler = HandlerDelegate(functions[hash]);
            }
            catch (Exception) { }
            o = OwnerOf(name, handler, Book.ModOf(hash));
            if (handler != null || name != null) s_owners[hash] = o;
            return o;
        }

        private static void Count(MessageTally byMod, MessageTally byKind, Dictionary<string, long> second, string owner, string label, int bytes)
        {
            byMod.Add(owner, bytes);
            byKind.Add(owner + ": " + label, bytes);
            second.TryGetValue(owner, out long b);
            second[owner] = b + bytes;
        }

        /// <summary>The mod with the most bytes each way in the second that just ended.</summary>
        public static void EndSecond(out string topOut, out double topOutKb, out string topIn, out double topInKb)
        {
            Top(s_secOut, out topOut, out topOutKb);
            Top(s_secIn, out topIn, out topInKb);
            s_secOut.Clear(); s_secIn.Clear();
        }

        private static void Top(Dictionary<string, long> d, out string mod, out double kb)
        {
            mod = ""; long best = 0;
            foreach (var kv in d) if (kv.Value > best) { best = kv.Value; mod = kv.Key; }
            kb = best / 1024.0;
        }

        public static string Window(float span) =>
            " sent, by mod: " + s_outByMod.EndWindow(span, 15) + " received, by mod: " + s_inByMod.EndWindow(span, 15) +
            " sent, by mod and message: " + s_outByKind.EndWindow(span, 15) + " received, by mod and message: " + s_inByKind.EndWindow(span, 15);

        public static string Peek(float span) =>
            " sent, by mod: " + s_outByMod.Peek(span, 15) + " received, by mod: " + s_inByMod.Peek(span, 15) +
            " sent, by mod and message: " + s_outByKind.Peek(span, 15) + " received, by mod and message: " + s_inByKind.Peek(span, 15);

        public static string Summary(float seconds) =>
            " sent, by mod: " + s_outByMod.Summary(seconds, 25) + " received, by mod: " + s_inByMod.Summary(seconds, 25) +
            " sent, by mod and message: " + s_outByKind.Summary(seconds, 25) + " received, by mod and message: " + s_inByKind.Summary(seconds, 25);
    }
}
