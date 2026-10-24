using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// On a server: the config packages Drop That and Spawn That send each joining player, built
    /// once and handed to every later join (<see cref="JoinPackages"/>). Both mods register an RPC
    /// the joining player calls (<c>RPC_RequestConfigsDropThat</c>, <c>RPC_RequestConfigsSpawnThat</c>);
    /// their handler builds every package from scratch, on the main loop, and queues them for the
    /// mod's own dispatcher. A prefix on each handler queues the kept bytes instead, in the order and
    /// form the mod would: for Drop That one entry per split message in its outgoing queue, for Spawn
    /// That one package through its transfer service. Drop That's reload (<c>SyncManager.SyncConfigs</c>)
    /// counts as a change. Every part used is found by name at start; when one is missing, or a build
    /// throws, that mod builds its own packages as before and the reason is kept.
    /// </summary>
    internal static class JoinPackagesPatch
    {
        private const string HarmonyId = "isimp.Vigil.joinpackages";

        public static readonly JoinPackages DropThat = new JoinPackages("Drop That");
        public static readonly JoinPackages SpawnThat = new JoinPackages("Spawn That");

        /// <summary>The config switch (Server fixes > JoinPackages).</summary>
        public static bool Enabled = true;

        /// <summary>Null while the mod's packages are kept, else why not ("not installed", a missing part, a failed build).</summary>
        public static string DropThatProblem { get; private set; } = "not put in yet";
        public static string SpawnThatProblem { get; private set; } = "not put in yet";

        private static int s_dropThatReloads;
        private static float s_worldUpAt = -1f, s_lastAheadAt = float.NegativeInfinity;

        // Drop That's parts
        private static PropertyInfo s_dtHandlers, s_dtName, s_dtPacker, s_dtQueues, s_dtFormatter, s_dtOutPackage, s_dtOutRpc, s_dtOutTarget;
        private static MethodInfo s_dtInitialize, s_dtPack, s_dtEnqueue;
        private static Type s_dtOutgoing, s_dtQueueType;

        // Spawn That's parts
        private static PropertyInfo s_stHandlers, s_stName, s_stPacker, s_stService;
        private static MethodInfo s_stAddToQueue;

        public static void Hook()
        {
            var harmony = new Harmony(HarmonyId);
            DropThatProblem = Guarded(() => HookDropThat(harmony));
            SpawnThatProblem = Guarded(() => HookSpawnThat(harmony));
        }

        private static string Guarded(Func<string> hook)
        {
            try { return hook(); }
            catch (Exception e) { return "not put in: " + e.GetType().Name + " " + e.Message; }
        }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static string HookDropThat(Harmony harmony)
        {
            var sync = TypeLookup.Loaded.Find("DropThat.Core.SyncManager");
            if (sync == null) return "not installed";
            var asm = sync.Assembly;
            var missing = new List<string>();
            s_dtHandlers = sync.GetProperty("PackageHandlers", Any);
            if (s_dtHandlers == null || !typeof(IDictionary).IsAssignableFrom(s_dtHandlers.PropertyType)) missing.Add("SyncManager.PackageHandlers");
            var pair = sync.GetNestedType("HandlerPair", Any);
            s_dtName = pair?.GetProperty("UnpackerRpcName", Any);
            if (s_dtName == null || s_dtName.PropertyType != typeof(string)) missing.Add("HandlerPair.UnpackerRpcName");
            s_dtPacker = pair?.GetProperty("Packer", Any);
            if (!NoArgumentFunc(s_dtPacker)) missing.Add("HandlerPair.Packer");
            var request = sync.GetMethod("RPC_RequestConfigsDropThat", Any, null, new[] { typeof(ZRpc) }, null);
            if (request == null) missing.Add("SyncManager.RPC_RequestConfigsDropThat");
            var reload = sync.GetMethod("SyncConfigs", Any, null, Type.EmptyTypes, null);
            if (reload == null) missing.Add("SyncManager.SyncConfigs");
            var message = asm.GetType("ThatCore.Network.IMessage");
            s_dtInitialize = message?.GetMethod("Initialize", Type.EmptyTypes);
            if (s_dtInitialize == null) missing.Add("IMessage.Initialize");
            s_dtPack = message == null ? null : asm.GetType("ThatCore.Network.SplitMessageManager")?.GetMethod("Pack", Any, null, new[] { message }, null);
            if (s_dtPack == null || !typeof(IList).IsAssignableFrom(s_dtPack.ReturnType)) missing.Add("SplitMessageManager.Pack");
            var service = asm.GetType("ThatCore.Valheim.Network.OutgoingMessageService");
            s_dtFormatter = service?.GetProperty("BinaryFormatter", Any);
            if (s_dtFormatter == null || s_dtFormatter.PropertyType != typeof(BinaryFormatter)) missing.Add("OutgoingMessageService.BinaryFormatter");
            s_dtOutgoing = asm.GetType("ThatCore.Valheim.Network.OutgoingMessage");
            s_dtOutPackage = s_dtOutgoing?.GetProperty("Package", Any);
            if (s_dtOutPackage?.PropertyType != typeof(ZPackage) || s_dtOutPackage.GetSetMethod(true) == null) missing.Add("OutgoingMessage.Package");
            s_dtOutRpc = s_dtOutgoing?.GetProperty("ZRpc", Any);
            if (s_dtOutRpc?.PropertyType != typeof(ZRpc) || s_dtOutRpc.GetSetMethod(true) == null) missing.Add("OutgoingMessage.ZRpc");
            s_dtOutTarget = s_dtOutgoing?.GetProperty("Target", Any);
            if (s_dtOutTarget?.PropertyType != typeof(string) || s_dtOutTarget.GetSetMethod(true) == null) missing.Add("OutgoingMessage.Target");
            if (s_dtOutgoing != null)
            {
                s_dtQueueType = typeof(Queue<>).MakeGenericType(s_dtOutgoing);
                s_dtEnqueue = s_dtQueueType.GetMethod("Enqueue");
            }
            s_dtQueues = service?.GetProperty("SocketQueues", Any);
            if (s_dtQueues == null || s_dtQueueType == null || s_dtQueues.PropertyType != typeof(Dictionary<,>).MakeGenericType(typeof(string), s_dtQueueType))
                missing.Add("OutgoingMessageService.SocketQueues");
            string left = JoinPackages.LeftAlone("Drop That", missing);
            if (left != null) return left;
            harmony.Patch(request, prefix: new HarmonyMethod(typeof(JoinPackagesPatch), nameof(DropThatRequestPrefix)) { priority = Priority.First });
            harmony.Patch(reload, prefix: new HarmonyMethod(typeof(JoinPackagesPatch), nameof(DropThatReloadPrefix)));
            return null;
        }

        private static string HookSpawnThat(Harmony harmony)
        {
            var sync = TypeLookup.Loaded.Find("SpawnThat.Lifecycle.SyncManager");
            if (sync == null) return "not installed";
            var asm = sync.Assembly;
            var missing = new List<string>();
            s_stHandlers = sync.GetProperty("PackageHandlers", Any);
            if (s_stHandlers == null || !typeof(IDictionary).IsAssignableFrom(s_stHandlers.PropertyType)) missing.Add("SyncManager.PackageHandlers");
            var pair = sync.GetNestedType("HandlerPair", Any);
            s_stName = pair?.GetProperty("UnpackerRpcName", Any);
            if (s_stName == null || s_stName.PropertyType != typeof(string)) missing.Add("HandlerPair.UnpackerRpcName");
            s_stPacker = pair?.GetProperty("Packer", Any);
            if (s_stPacker == null || s_stPacker.PropertyType != typeof(Func<ZPackage>)) missing.Add("HandlerPair.Packer");
            var request = sync.GetMethod("RPC_RequestConfigsSpawnThat", Any, null, new[] { typeof(ZRpc) }, null);
            if (request == null) missing.Add("SyncManager.RPC_RequestConfigsSpawnThat");
            var service = asm.GetType("SpawnThat.Core.Network.DataTransferService");
            s_stService = service?.GetProperty("Service", Any);
            if (s_stService == null || s_stService.PropertyType != service) missing.Add("DataTransferService.Service");
            s_stAddToQueue = service?.GetMethod("AddToQueue", Any, null, new[] { typeof(ZPackage), typeof(string), typeof(ZRpc) }, null);
            if (s_stAddToQueue == null) missing.Add("DataTransferService.AddToQueue");
            string left = JoinPackages.LeftAlone("Spawn That", missing);
            if (left != null) return left;
            harmony.Patch(request, prefix: new HarmonyMethod(typeof(JoinPackagesPatch), nameof(SpawnThatRequestPrefix)) { priority = Priority.First });
            return null;
        }

        private static bool NoArgumentFunc(PropertyInfo p) =>
            p != null && p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(Func<>) && !p.PropertyType.GetGenericArguments()[0].IsValueType;

        // ------------------------------------------------------------------ what the packages were built from

        private static JoinStamp Stamp(int reloads)
        {
            int locations = ZoneSystem.instance != null && ZoneSystem.instance.m_locationInstances != null ? ZoneSystem.instance.m_locationInstances.Count : -1;
            return new JoinStamp(ZNet.instance != null ? ZNet.instance.GetInstanceID() : 0, locations, reloads);
        }

        private static string Joining(ZRpc rpc)
        {
            try { return "joining " + rpc.GetSocket().GetEndPointString(); }
            catch (Exception) { return "joining"; }
        }

        // ------------------------------------------------------------------ Drop That

        private static void DropThatReloadPrefix()
        {
            s_dropThatReloads++;
            DropThat.Clear();
        }

        private static bool DropThatRequestPrefix(ZRpc rpc)
        {
            if (!Enabled || DropThatProblem != null || rpc == null || ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            IList<JoinPackage> packages;
            var stamp = Stamp(s_dropThatReloads);
            try
            {
                if (DropThat.Has(stamp))
                {
                    packages = DropThat.Reuse();
                    EventLog.Write("join-packages", Joining(rpc), DropThat.ReusedLine());
                }
                else
                {
                    var watch = Stopwatch.StartNew();
                    var built = BuildDropThat();
                    DropThat.Keep(stamp, built, watch.Elapsed.TotalMilliseconds);
                    packages = DropThat.Kept;
                    EventLog.Write("join-packages", Joining(rpc), DropThat.BuiltLine("for this join"));
                }
            }
            catch (Exception e)
            {
                // Nothing is queued yet: the mod's own handler runs, now and for every later join.
                DropThatProblem = "a build failed, the mod builds its own: " + Inner(e);
                DropThat.Clear();
                return true;
            }
            try { QueueDropThat(rpc, packages); }
            catch (Exception e)
            {
                // The mod's own handler queues a full set, with transfer ids of its own; a part of the
                // kept set already queued only arrives twice, which the joining game sets aside.
                DropThatProblem = "queueing failed, the mod builds its own: " + Inner(e);
                DropThat.Clear();
                return true;
            }
            return false;
        }

        // Each split message serialized by the mod's own formatter into a package, as its outgoing queue holds them.
        private static List<JoinPackage> BuildDropThat()
        {
            var list = new List<JoinPackage>();
            var formatter = (BinaryFormatter)s_dtFormatter.GetValue(null, null);
            foreach (var pair in ((IDictionary)s_dtHandlers.GetValue(null, null)).Values)
            {
                string target = (string)s_dtName.GetValue(pair, null);
                object message = ((Func<object>)s_dtPacker.GetValue(pair, null))();
                s_dtInitialize.Invoke(message, null);
                foreach (object split in (IList)s_dtPack.Invoke(null, new[] { message }))
                {
                    using (var stream = new MemoryStream())
                    {
                        formatter.Serialize(stream, split);
                        var package = new ZPackage();
                        package.Write(stream.ToArray());
                        list.Add(new JoinPackage(target, package.GetArray()));
                    }
                }
            }
            return list;
        }

        private static void QueueDropThat(ZRpc rpc, IList<JoinPackage> packages)
        {
            string endpoint = rpc.GetSocket().GetEndPointString();
            var queues = (IDictionary)s_dtQueues.GetValue(null, null);
            lock (queues)
            {
                object queue = queues.Contains(endpoint) ? queues[endpoint] : null;
                if (queue == null) queues[endpoint] = queue = Activator.CreateInstance(s_dtQueueType);
                foreach (var p in packages)
                {
                    object entry = Activator.CreateInstance(s_dtOutgoing, true);
                    s_dtOutPackage.SetValue(entry, new ZPackage(p.Bytes), null);
                    s_dtOutRpc.SetValue(entry, rpc, null);
                    s_dtOutTarget.SetValue(entry, p.Target, null);
                    s_dtEnqueue.Invoke(queue, new[] { entry });
                }
            }
        }

        // ------------------------------------------------------------------ Spawn That

        private static bool SpawnThatRequestPrefix(ZRpc rpc)
        {
            if (!Enabled || SpawnThatProblem != null || rpc == null || ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            IList<JoinPackage> packages;
            var stamp = Stamp(0);
            try
            {
                if (SpawnThat.Has(stamp))
                {
                    packages = SpawnThat.Reuse();
                    EventLog.Write("join-packages", Joining(rpc), SpawnThat.ReusedLine());
                }
                else
                {
                    var watch = Stopwatch.StartNew();
                    var built = BuildSpawnThat();
                    SpawnThat.Keep(stamp, built, watch.Elapsed.TotalMilliseconds);
                    packages = SpawnThat.Kept;
                    EventLog.Write("join-packages", Joining(rpc), SpawnThat.BuiltLine("for this join"));
                }
            }
            catch (Exception e)
            {
                SpawnThatProblem = "a build failed, the mod builds its own: " + Inner(e);
                SpawnThat.Clear();
                return true;
            }
            try
            {
                object service = s_stService.GetValue(null, null);
                foreach (var p in packages) s_stAddToQueue.Invoke(service, new object[] { new ZPackage(p.Bytes), p.Target, rpc });
            }
            catch (Exception e)
            {
                // The mod's own handler queues the full set; a package already queued arrives twice, with the same content.
                SpawnThatProblem = "queueing failed, the mod builds its own: " + Inner(e);
                SpawnThat.Clear();
                return true;
            }
            return false;
        }

        private static List<JoinPackage> BuildSpawnThat()
        {
            var list = new List<JoinPackage>();
            foreach (var pair in ((IDictionary)s_stHandlers.GetValue(null, null)).Values)
                list.Add(new JoinPackage((string)s_stName.GetValue(pair, null), ((Func<ZPackage>)s_stPacker.GetValue(pair, null))().GetArray()));
            return list;
        }

        private static string Inner(Exception e)
        {
            var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            return inner.GetType().Name + " " + inner.Message;
        }

        // ------------------------------------------------------------------ built ahead

        /// <summary>
        /// Once a frame on a server: builds what is not kept a minute after the world is up, while
        /// nobody is connected (<see cref="JoinWarmUp"/>). Returns the milliseconds it spent, which
        /// are Vigil's own work for that frame.
        /// </summary>
        public static double Tick()
        {
            var net = ZNet.instance;
            if (net == null || ZoneSystem.instance == null || !net.IsServer() || !ZoneSystem.instance.LocationsGenerated)
            {
                s_worldUpAt = -1f;
                if (net == null) { DropThat.Clear(); SpawnThat.Clear(); }
                return 0;
            }
            float now = Time.realtimeSinceStartup;
            if (s_worldUpAt < 0f) s_worldUpAt = now;
            if (!Enabled) return 0;
            bool dropThatDue = DropThatProblem == null && !DropThat.Has(Stamp(s_dropThatReloads));
            bool spawnThatDue = SpawnThatProblem == null && !SpawnThat.Has(Stamp(0));
            if (!JoinWarmUp.Due(net.IsDedicated(), now - s_worldUpAt, net.GetPeers().Count, !dropThatDue && !spawnThatDue, now - s_lastAheadAt)) return 0;
            s_lastAheadAt = now;
            var all = Stopwatch.StartNew();
            if (dropThatDue) Ahead(DropThat, BuildDropThat, Stamp(s_dropThatReloads), e => DropThatProblem = e);
            if (spawnThatDue) Ahead(SpawnThat, BuildSpawnThat, Stamp(0), e => SpawnThatProblem = e);
            return all.Elapsed.TotalMilliseconds;
        }

        private static void Ahead(JoinPackages cache, Func<List<JoinPackage>> build, JoinStamp stamp, Action<string> failed)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                var built = build();
                cache.Keep(stamp, built, watch.Elapsed.TotalMilliseconds);
                EventLog.Write("join-packages", "server", cache.BuiltLine("ahead of the next join"));
            }
            catch (Exception e)
            {
                failed("a build failed, the mod builds its own: " + Inner(e));
                cache.Clear();
            }
        }

        /// <summary>The summary's and the self-test's line per mod: kept and sent, or why the mod builds its own.</summary>
        public static IEnumerable<string> Lines()
        {
            if (!Enabled)
            {
                yield return "switched off (Patches.JoinPackages): Drop That and Spawn That build their own";
                yield break;
            }
            yield return DropThatProblem == null ? DropThat.SummaryLine() : "Drop That: " + DropThatProblem;
            yield return SpawnThatProblem == null ? SpawnThat.SummaryLine() : "Spawn That: " + SpawnThatProblem;
        }

        /// <summary>For the self-test: builds a mod's packages twice and says whether the same bytes came out (Drop That's transfer id aside, a new one each build).</summary>
        public static string SameTwice(bool dropThat, out double ms)
        {
            ms = 0;
            string problem = dropThat ? DropThatProblem : SpawnThatProblem;
            if (problem != null) return problem;
            var watch = Stopwatch.StartNew();
            var a = dropThat ? DropThatPayloads() : BuildSpawnThat();
            var b = dropThat ? DropThatPayloads() : BuildSpawnThat();
            ms = watch.Elapsed.TotalMilliseconds / 2;
            if (a.Count != b.Count) return "the two builds made " + a.Count + " and " + b.Count + " packages";
            for (int i = 0; i < a.Count; i++)
                if (a[i].Target != b[i].Target || !BytesEqual(a[i].Bytes, b[i].Bytes)) return "package " + (i + 1) + " (" + a[i].Target + ") differed between two builds";
            return null;
        }

        // Drop That's split contents, the compressed configs without the split message around them (whose transfer id is new each build).
        private static List<JoinPackage> DropThatPayloads()
        {
            var list = new List<JoinPackage>();
            foreach (var pair in ((IDictionary)s_dtHandlers.GetValue(null, null)).Values)
            {
                string target = (string)s_dtName.GetValue(pair, null);
                object message = ((Func<object>)s_dtPacker.GetValue(pair, null))();
                s_dtInitialize.Invoke(message, null);
                foreach (object split in (IList)s_dtPack.Invoke(null, new[] { message }))
                {
                    var content = split.GetType().GetProperty("Content", Any)?.GetValue(split, null) as byte[];
                    list.Add(new JoinPackage(target, content ?? new byte[0]));
                }
            }
            return list;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
