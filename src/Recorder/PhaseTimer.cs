using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Bootstrap;
using HarmonyLib;
using Vigil.Core;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// Times methods on the main thread with a Harmony prefix and finalizer and adds the time up
    /// per frame, per second and per session. Three groups:
    /// the game's own per-frame work (always on), every mod's Unity callbacks (Update,
    /// FixedUpdate, LateUpdate, OnGUI; always on) and, on request for a limited time, every
    /// game method that mods have patched ("deep" mode). Every hook goes through the
    /// <see cref="InstrumentJournal"/>, so a hook that crashes the game is skipped next launch.
    /// Timing only measures; it never skips or changes the original.
    /// </summary>
    internal sealed class PhaseTimer
    {
        internal const string Nested = "other timed work";

        internal sealed class Slot
        {
            public int Id;
            public string Label;
            public string Mod;      // null for the game's own methods
            public string Parent;   // null when nothing else timed runs around it
            public string Column;   // timeline column, game methods only
            public bool Deep;
            public string PatchedBy; // mods patching this game method, filled by a deep capture
            public MethodBase Method; // deep hooks only, so they can be taken off again
            public bool Dropped;     // deep hook taken off early: called too often to time cheaply
            public bool Coroutine;   // one coroutine type, timed through Unity's coroutine runner
            public string PatchKind; // a mod's prefix, postfix or finalizer on a split method, timed by itself in a deep capture
            public long FrameTicks; public int FrameCalls;
            public long SecondTicks; public int SecondCalls;
            public long RunTicks; public long RunCalls;
            public long DeepTicks; public long DeepCalls;
            public long DeepMaxFrameTicks;   // the longest single frame during the deep capture
            public bool SpikeQueued;        // split queued because one frame took long
            // bytes allocated on the main thread inside the method, when the runtime can tell
            public long FrameAlloc, SecondAlloc, RunAlloc;
            // deep capture, the methods that took the most time: the stretch between the last prefix
            // and the first postfix, and the whole time of the same calls
            public bool Split;
            public long SplitCalls, OrigRuns, OrigTicks, SplitWholeTicks;
        }

        // type, method, timeline column, parent (null = top level)
        internal static readonly string[][] GamePhases =
        {
            new[] { "MonoUpdaters", "FixedUpdate", "ph_updaters_fixed", null },
            new[] { "MonoUpdaters", "Update", "ph_updaters", null },
            new[] { "MonoUpdaters", "LateUpdate", "ph_updaters_late", null },
            new[] { "ZNet", "Update", "ph_znet", null },
            new[] { "ZNet", "FixedUpdate", "ph_znet_fixed", null },
            new[] { "ZNet", "LateUpdate", "ph_znet_late", null },
            new[] { "ZSteamSocket", "UpdateAllSockets", "ph_sockets", "ZNet.Update" },
            new[] { "ZNet", "UpdatePeers", "ph_peers", "ZNet.Update" },
            new[] { "ZDOMan", "Update", "ph_zdoman", "ZNet.Update" },
            // A player joining (and, on a client, joining a server): every step, mod hooks included.
            new[] { "ZNet", "OnNewConnection", "ph_join_connect", Nested },
            new[] { "ZNet", "RPC_ServerHandshake", "ph_join_handshake", Nested },
            new[] { "ZNet", "RPC_PeerInfo", "ph_join_peerinfo", Nested },
            new[] { "ZNet", "RPC_CharacterID", "ph_join_character", Nested },
            new[] { "ZNet", "SendPlayerList", "ph_playerlist", Nested },
            new[] { "ZDOMan", "AddPeer", "ph_join_zdopeer", Nested },
            new[] { "ZoneSystem", "OnNewPeer", "ph_join_zones", Nested },
            new[] { "ZDOMan", "CreateSyncList", "ph_zdo_synclist", Nested },
            new[] { "ZRpc", "Update", "ph_rpc_update", Nested },
            new[] { "ZDOMan", "RPC_ZDOData", "ph_zdo_apply", Nested },
            new[] { "ZRoutedRpc", "HandleRoutedRPC", "ph_rpc_handle", Nested },
            // What grows with every player: world updates sent to each, objects handed over, messages passed on to all.
            new[] { "ZDOMan", "SendZDOs", "ph_zdo_send", Nested },
            new[] { "ZDOMan", "ReleaseZDOS", "ph_zdo_release", Nested },
            new[] { "ZRoutedRpc", "RouteRPC", "ph_route", Nested },
            new[] { "ZNetScene", "Update", "ph_scene", null },
            new[] { "ZNetScene", "CreateObjects", "ph_scene_create", "ZNetScene.Update" },
            new[] { "ZNetScene", "RemoveObjects", "ph_scene_remove", "ZNetScene.Update" },
            new[] { "ZoneSystem", "Update", "ph_zones", null },
            new[] { "ZoneSystem", "CreateLocalZones", "ph_zones_local", "ZoneSystem.Update" },
            new[] { "ZoneSystem", "SpawnZone", "ph_zones_spawn", Nested },
            new[] { "Heightmap", "Regenerate", "ph_terrain", Nested },
            new[] { "Game", "Update", "ph_game", null },
            new[] { "Game", "FixedUpdate", "ph_game_fixed", null },
            new[] { "Game", "UpdateSaving", "ph_saving", "Game.Update" },
            new[] { "Game", "SavePlayerProfile", "ph_save_profile", Nested },
            new[] { "ZNet", "Save", "ph_save_world", Nested },
            new[] { "EnvMan", "Update", "ph_env", null },
            new[] { "EnvMan", "FixedUpdate", "ph_env_fixed", null },
            new[] { "Player", "Update", "ph_player", null },
            new[] { "Player", "FixedUpdate", "ph_player_fixed", null },
            new[] { "Player", "LateUpdate", "ph_player_late", null },
            // The player's own actions. They run rarely, but one can freeze a frame: building and
            // planting, every inventory change (the game then re-checks all recipes and build
            // pieces and rebuilds the placement ghost), using a map table, opening a container.
            new[] { "Player", "UpdatePlacement", "ph_placement", "Player.Update" },
            new[] { "Player", "TryPlacePiece", "ph_place", Nested },
            new[] { "Player", "PlacePiece", "ph_place_piece", Nested },
            new[] { "Player", "RemovePiece", "ph_remove_piece", Nested },
            new[] { "Player", "OnInventoryChanged", "ph_inv_changed", Nested },
            new[] { "Player", "UpdateKnownRecipesList", "ph_known_recipes", Nested },
            new[] { "Player", "UpdateAvailablePiecesList", "ph_available_pieces", Nested },
            new[] { "PieceTable", "UpdateAvailable", "ph_piece_table", Nested },
            new[] { "Player", "SetupPlacementGhost", "ph_place_ghost", Nested },
            new[] { "Player", "SetPlaceMode", "ph_place_mode", Nested },
            new[] { "Player", "Interact", "ph_interact", Nested },
            new[] { "Humanoid", "EquipItem", "ph_equip", Nested },
            new[] { "MapTable", "OnWrite", "ph_maptable_write", Nested },
            new[] { "Minimap", "GetSharedMapData", "ph_map_share", Nested },
            new[] { "Minimap", "AddSharedMapData", "ph_map_merge", Nested },
            new[] { "Container", "RPC_OpenResponse", "ph_container_open", Nested },
            new[] { "InventoryGui", "Show", "ph_inventory_open", Nested },
            new[] { "Hud", "TogglePieceSelection", "ph_build_menu", Nested },
            new[] { "BuildUi", "UpdatePieceButtons", "ph_build_buttons", Nested },
            new[] { "Character", "CustomFixedUpdate", "ph_characters", Nested },
            new[] { "Humanoid", "CustomFixedUpdate", "ph_humanoids", Nested },
            new[] { "BaseAI", "UpdateAI", "ph_ai", Nested },
            new[] { "MonsterAI", "UpdateAI", "ph_ai_monster", Nested },
            new[] { "AnimalAI", "UpdateAI", "ph_ai_animal", Nested },
            new[] { "Ship", "CustomFixedUpdate", "ph_ships", Nested },
            new[] { "ZSyncTransform", "CustomFixedUpdate", "ph_sync_transform", Nested },
            new[] { "Minimap", "Update", "ph_minimap", null },
            new[] { "Hud", "Update", "ph_hud", null },
            new[] { "Hud", "LateUpdate", "ph_hud_late", null },
            new[] { "InventoryGui", "Update", "ph_inventory", null },
            new[] { "SpawnSystem", "UpdateSpawning", "ph_spawning", null },
            new[] { "RandEventSystem", "FixedUpdate", "ph_events", null },
            new[] { "ClutterSystem", "LateUpdate", "ph_grass", null },
            new[] { "AudioMan", "Update", "ph_audio", null },
            new[] { "AudioMan", "FixedUpdate", "ph_audio_fixed", null },
            new[] { "WearNTearUpdater", "Update", "ph_wear", null },
            new[] { "SteamManager", "Update", "ph_steam", null },
            new[] { "Smelter", "UpdateSmelter", "ph_smelters", null },
            // Unity rebuilds changed UI layouts and texts here, once per frame before drawing.
            new[] { "UnityEngine.Canvas", "SendWillRenderCanvases", "ph_ui_rebuild", null },
        };

        private static readonly Dictionary<MethodBase, Slot> s_byMethod = new Dictionary<MethodBase, Slot>();
        private static readonly List<Slot> s_slots = new List<Slot>();
        private static readonly List<Slot> s_touched = new List<Slot>();
        private static int s_mainThread;
        [ThreadStatic] private static long[] t_starts;
        [ThreadStatic] private static int[] t_ids;
        [ThreadStatic] private static int t_depth;
        [ThreadStatic] private static long[] t_allocs;
        [ThreadStatic] private static long[] t_oStarts;
        [ThreadStatic] private static int[] t_oIds;
        [ThreadStatic] private static int t_oDepth;
        // GC.GetAllocatedBytesForCurrentThread is in this game's runtime but not in the
        // reference assemblies Vigil builds against, so it is bound at run time.
        private static Func<long> s_allocNow;

        /// <summary>Allocations per method are measured (the runtime reports bytes allocated per thread).</summary>
        public static bool AllocationsMeasured => s_allocNow != null;

        private readonly Harmony _harmony = new Harmony("isimp.Vigil.timing");
        private readonly Harmony _deepHarmony = new Harmony("isimp.Vigil.deep");
        private readonly InstrumentJournal _journal;
        private readonly List<string> _missing = new List<string>();
        private readonly List<string> _failed = new List<string>();
        private readonly List<MethodBase> _deepMethods = new List<MethodBase>();
        private int _modHooks;

        public PhaseTimer(InstrumentJournal journal, bool measureAllocations)
        {
            _journal = journal;
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            if (measureAllocations) s_allocNow = AllocationCounter();
        }

        /// <summary>The runtime's per-thread allocation counter, or null when it is missing or does not count.</summary>
        private static Func<long> AllocationCounter()
        {
            try
            {
                var m = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) return null;
                var f = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), m);
                long before = f();
                var probe = new byte[1 << 20];
                GC.KeepAlive(probe);
                return f() - before >= 1 << 20 ? f : null;
            }
            catch (Exception) { return null; }
        }

        public IReadOnlyList<Slot> Slots => s_slots;
        public IReadOnlyList<string> Missing => _missing;
        public IReadOnlyList<string> Failed => _failed;
        public int ModHooks => _modHooks;
        private static bool s_deepRunning;

        public void HookGame()
        {
            foreach (var p in GamePhases)
            {
                string label = p[0] + "." + p[1];
                try
                {
                    var type = AccessTools.TypeByName(p[0]);
                    var m = type != null ? AccessTools.DeclaredMethod(type, p[1]) : null;
                    if (m == null) { _missing.Add(label); continue; }
                    Hook(_harmony, m, new Slot { Label = label, Parent = p[3], Column = p[2] });
                }
                catch (Exception e) { _failed.Add(label + " (" + e.GetType().Name + ")"); }
            }
        }

        // ---- coroutines: Unity runs every coroutine step through SetupCoroutine.InvokeMoveNext, an
        // engine method, so one hook there times them all without touching any mod's code.
        private static readonly Dictionary<Type, Slot> s_coroutineSlots = new Dictionary<Type, Slot>();
        private static Dictionary<Assembly, string> s_owners;

        public bool HookCoroutines()
        {
            const string id = "Unity coroutines";
            if (!_journal.Allowed(id)) { _failed.Add(id + " (skipped: it crashed the game once)"); return false; }
            var m = AccessTools.Method(AccessTools.TypeByName("UnityEngine.SetupCoroutine"), "InvokeMoveNext");
            if (m == null) { _missing.Add("UnityEngine.SetupCoroutine.InvokeMoveNext"); return false; }
            s_owners = ModAssemblies();
            _journal.Begin(id);
            try
            {
                _harmony.Patch(m, prefix: new HarmonyMethod(typeof(PhaseTimer), nameof(CoPre)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PhaseTimer), nameof(CoFin)) { priority = Priority.Last });
                return true;
            }
            catch (Exception e) { _failed.Add(id + " (" + e.GetType().Name + ")"); return false; }
            finally { _journal.Done(id); }
        }

        private static Slot CoroutineSlot(Type t)
        {
            if (s_coroutineSlots.TryGetValue(t, out var slot)) return slot;
            var asm = t.Assembly;
            string owner = s_owners != null && s_owners.TryGetValue(asm, out var mod) ? mod
                : asm.GetName().Name.StartsWith("assembly_", StringComparison.Ordinal) ? "Valheim" : asm.GetName().Name;
            slot = new Slot
            {
                Label = owner + " :: coroutine " + CoroutineName.Describe(t.Name, t.DeclaringType?.Name),
                Mod = owner == "Valheim" || owner.StartsWith("UnityEngine", StringComparison.Ordinal) ? null : owner,
                Coroutine = true,
            };
            slot.Id = s_slots.Count;
            s_slots.Add(slot);
            s_coroutineSlots[t] = slot;
            return slot;
        }

        private static void CoPre(IEnumerator enumerator)
        {
            if (!ProbeState.Active) return;
            if (enumerator == null || Thread.CurrentThread.ManagedThreadId != s_mainThread) return;
            Stacks();
            // A first step run from inside timed work (StartCoroutine in a timed method) is already in that time.
            if (t_depth > 0 || t_depth >= t_starts.Length) return;
            var slot = CoroutineSlot(enumerator.GetType());
            t_ids[t_depth] = slot.Id;
            if (s_allocNow != null) t_allocs[t_depth] = s_allocNow();
            t_starts[t_depth] = Stopwatch.GetTimestamp();
            t_depth++;
        }

        private static void Stacks()
        {
            if (t_starts != null) return;
            t_starts = new long[256]; t_ids = new int[256]; t_allocs = new long[256];
            t_oStarts = new long[256]; t_oIds = new int[256];
        }

        private static void CoFin(IEnumerator enumerator)
        {
            if (!ProbeState.Active) return;
            if (enumerator == null || Thread.CurrentThread.ManagedThreadId != s_mainThread || t_depth != 1) return;
            if (!s_coroutineSlots.TryGetValue(enumerator.GetType(), out var slot) || t_ids[0] != slot.Id) return;
            t_depth = 0;
            long dt = Stopwatch.GetTimestamp() - t_starts[0];
            if (slot.FrameCalls == 0) s_touched.Add(slot);
            slot.FrameTicks += dt; slot.FrameCalls++;
            if (s_allocNow != null) slot.FrameAlloc += s_allocNow() - t_allocs[0];
        }

        /// <summary>Unity callbacks of every MonoBehaviour in every loaded mod except Vigil.</summary>
        public void HookModCallbacks()
        {
            foreach (var kv in ModAssemblies())
            {
                Type[] types;
                try { types = kv.Key.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    foreach (var name in new[] { "Update", "FixedUpdate", "LateUpdate", "OnGUI" })
                    {
                        MethodInfo m;
                        try
                        {
                            m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        }
                        catch (Exception) { continue; }
                        if (m == null || m.IsAbstract) continue;
                        if (Hook(_harmony, m, new Slot { Label = kv.Value + " :: " + t.Name + "." + name, Mod = kv.Value })) _modHooks++;
                    }
                }
            }
        }

        /// <summary>
        /// Times every game method that mods have patched, until <see cref="StopDeep"/>. Each one
        /// is timed whole (game code plus every mod patch on it) and labelled with the mods that
        /// patch it. Mod methods themselves are never hooked here: asking the Mono runtime for the
        /// code of some mod patch methods aborts the whole game (a native abort no try/catch can
        /// stop), whereas game methods already patched by mods have been through that safely.
        /// </summary>
        // Installing one Harmony hook takes a few milliseconds, so a deep capture puts its hooks in
        // and takes them out a few per frame (BudgetedQueue) instead of freezing the game.
        private enum DeepState { Idle, Installing, Running, Removing }
        private DeepState _deep = DeepState.Idle;
        private readonly BudgetedQueue<KeyValuePair<MethodBase, string>> _toHook = new BudgetedQueue<KeyValuePair<MethodBase, string>>();
        private readonly BudgetedQueue<MethodBase> _toUnhook = new BudgetedQueue<MethodBase>();
        private readonly BudgetedQueue<MethodBase> _toSplit = new BudgetedQueue<MethodBase>();
        private readonly BudgetedQueue<KeyValuePair<MethodBase, Slot>> _toTimePatches = new BudgetedQueue<KeyValuePair<MethodBase, Slot>>();
        private static readonly string[] TimerOwners = { "isimp.Vigil.timing", "isimp.Vigil.deep", "isimp.Vigil.selftime" };

        /// <summary>One timer pair's own cost per call in ms (calibrated by <see cref="OwnHooks"/>), taken off the mods' patch times; 0 until known.</summary>
        public static double PairMs;
        private static readonly Dictionary<Slot, MethodBase> s_methodOf = new Dictionary<Slot, MethodBase>();
        private int _spikeSplits;

        /// <summary>
        /// The frame's time for one slot during a deep capture: keeps its longest frame, and queues
        /// the split for a method mods patch that just stalled a frame, so the next time the same
        /// action runs the report can say how much of the stall is the game's code.
        /// </summary>
        private void Spike(Slot s)
        {
            if (s.FrameTicks > s.DeepMaxFrameTicks) s.DeepMaxFrameTicks = s.FrameTicks;
            if (!DeepSplit.SplitOnSpike(TicksToMs(s.FrameTicks), s.PatchedBy != null, s.Split || s.SpikeQueued, s.Dropped, _spikeSplits)) return;
            if (!s_methodOf.TryGetValue(s, out var m)) return;
            s.SpikeQueued = true;
            _spikeSplits++;
            _toSplit.Add(new[] { m });
        }

        public int SpikeSplits => _spikeSplits;
        private readonly List<string> _deepDropped = new List<string>();

        public bool DeepActive => _deep != DeepState.Idle;
        public bool DeepRunning => _deep == DeepState.Running;
        public int DeepPending => _toHook.Count + _toUnhook.Count + _toSplit.Count;
        public IReadOnlyList<string> DeepDropped => _deepDropped;

        /// <summary>
        /// Queues every game method that mods have patched for timing, except work that only runs
        /// at load or save time. <see cref="StepDeep"/> installs them over the following frames.
        /// </summary>
        public int BeginDeep()
        {
            if (DeepActive) return 0;
            var mods = ModAssemblies();
            var targets = new List<KeyValuePair<MethodBase, string>>();
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                if (original?.DeclaringType == null || !DeepPlan.WorthTiming(original.Name)) continue;
                string asm = original.DeclaringType.Assembly.GetName().Name;
                if (!asm.StartsWith("assembly_", StringComparison.Ordinal)) continue;
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch (Exception) { continue; }
                if (info == null) continue;
                var by = info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.Transpilers)
                    .Select(p => p.PatchMethod?.DeclaringType != null && mods.TryGetValue(p.PatchMethod.DeclaringType.Assembly, out string m) ? m : null)
                    .Where(m => m != null).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
                if (by.Count == 0) continue;
                string patchedBy = string.Join(", ", by.ToArray());
                // Already timed all the time: it needs no second timer, only the mods' names.
                if (s_byMethod.TryGetValue(original, out var timed)) { timed.PatchedBy = patchedBy; continue; }
                targets.Add(new KeyValuePair<MethodBase, string>(original, patchedBy));
            }
            _deepDropped.Clear();
            _toHook.Add(targets);
            _deep = DeepState.Installing;
            return targets.Count;
        }

        /// <summary>
        /// One frame's share of installing or removing deep hooks, within <paramref name="budgetMs"/>.
        /// True on the frame the last hook went in, which is when the capture itself starts.
        /// </summary>
        public bool StepDeep(double budgetMs)
        {
            var sw = Stopwatch.StartNew();
            Func<double> elapsed = () => sw.Elapsed.TotalMilliseconds;
            switch (_deep)
            {
                case DeepState.Installing:
                    _toHook.Step(kv =>
                    {
                        if (s_byMethod.ContainsKey(kv.Key)) return;
                        var slot = new Slot
                        {
                            Label = kv.Key.DeclaringType.Name + "." + kv.Key.Name, Parent = Nested, Deep = true, PatchedBy = kv.Value, Method = kv.Key,
                        };
                        if (Hook(_deepHarmony, kv.Key, slot)) _deepMethods.Add(kv.Key);
                    }, elapsed, budgetMs);
                    if (_toHook.Count > 0) return false;
                    foreach (var s in s_slots) { s.DeepTicks = 0; s.DeepCalls = 0; s.SplitCalls = 0; s.OrigRuns = 0; s.OrigTicks = 0; s.SplitWholeTicks = 0; s.DeepMaxFrameTicks = 0; s.SpikeQueued = false; }
                    _spikeSplits = 0;
                    _deep = DeepState.Running;
                    s_deepRunning = true;
                    return true;
                case DeepState.Running:
                    // Hot hooks come off first, then the split goes on, within one budget per frame.
                    if (_toUnhook.Count > 0) _toUnhook.Step(Unhook, elapsed, budgetMs);
                    else if (_toSplit.Count > 0) _toSplit.Step(HookSplit, elapsed, budgetMs);
                    else _toTimePatches.Step(TimePatch, elapsed, budgetMs);
                    return false;
                case DeepState.Removing:
                    _toUnhook.Step(Unhook, elapsed, budgetMs);
                    if (_toUnhook.Count == 0) _deep = DeepState.Idle;
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Takes the hooks off methods called so often, and so cheap per call, that the timer costs
        /// more than the method; they are listed in the report instead of timed.
        /// </summary>
        public void DropHotDeepHooks(long frames)
        {
            if (frames <= 0) return;
            foreach (var s in s_slots)
            {
                if (!s.Deep || s.Method == null || s.Dropped || s.DeepCalls == 0) continue;
                double calls = (double)s.DeepCalls / frames;
                double micros = TicksToMs(s.DeepTicks) * 1000.0 / s.DeepCalls;
                if (!DeepPlan.TooHot(calls, micros)) continue;
                s.Dropped = true;
                _deepDropped.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}   {1:0} calls/frame at {2:0.00} microseconds each   patched by {3}", s.Label, calls, micros, s.PatchedBy));
                _deepMethods.Remove(s.Method);
                _toUnhook.Add(new[] { s.Method });
            }
        }

        /// <summary>
        /// Splits the <paramref name="count"/> patched methods that took the most time so far into
        /// the game's code and what the mods' prefixes and postfixes add; the hooks go on over the
        /// next frames. Returns how many were picked.
        /// </summary>
        public int SplitTop(int count)
        {
            var picked = s_byMethod
                .Where(kv => kv.Value.PatchedBy != null && !kv.Value.Dropped && !kv.Value.Split && kv.Value.DeepCalls > 0)
                .OrderByDescending(kv => kv.Value.DeepTicks)
                .Take(count)
                .Select(kv => kv.Key)
                .ToList();
            _toSplit.Add(picked);
            return picked.Count;
        }

        public void StopDeep()
        {
            s_deepRunning = false;
            _toSplit.Clear();
            _toTimePatches.Clear();
            _toUnhook.Add(_deepMethods);
            _deepMethods.Clear();
            _deep = _toUnhook.Count > 0 ? DeepState.Removing : DeepState.Idle;
        }

        private void Unhook(MethodBase m)
        {
            try { _deepHarmony.Unpatch(m, HarmonyPatchType.All, _deepHarmony.Id); } catch (Exception) { }
            if (!s_byMethod.TryGetValue(m, out var slot)) return;
            slot.Split = false; // the split pair came off with the deep hooks
            if (slot.Deep) { s_byMethod.Remove(m); s_methodOf.Remove(slot); }
        }

        private bool Hook(Harmony harmony, MethodBase m, Slot slot)
        {
            string id = slot.Label;
            if (m.ContainsGenericParameters || (m.DeclaringType != null && m.DeclaringType.ContainsGenericParameters)) return false;
            if (s_byMethod.ContainsKey(m)) return false;
            if (!_journal.Allowed(id)) { _failed.Add(id + " (skipped: it crashed the game once)"); return false; }
            slot.Id = s_slots.Count;
            s_slots.Add(slot);
            s_byMethod[m] = slot;
            s_methodOf[slot] = m;
            _journal.Begin(id);
            try
            {
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(PhaseTimer), nameof(Pre)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PhaseTimer), nameof(Fin)) { priority = Priority.Last });
                _journal.Done(id);
                return true;
            }
            catch (Exception e)
            {
                _journal.Done(id);
                s_byMethod.Remove(m);
                s_methodOf.Remove(slot);
                _failed.Add(id + " (" + e.GetType().Name + ")");
                return false;
            }
        }

        // The split is a second prefix and a postfix, never a transpiler: in the HarmonyX 2.9 that
        // BepInEx ships, rebuilding a method through any transpiler turns a "break" out of a try
        // block inside a loop into another round of that loop (Terminal.AddString then printed the
        // same line forever and the game froze). The prefix with the lowest possible priority runs
        // after every other prefix, where the game's code starts; the postfix with the highest runs
        // before every other postfix and reads Harmony's __runOriginal. Both go through the deep
        // Harmony instance, so taking the deep hooks off removes them too.
        private void HookSplit(MethodBase m)
        {
            if (!s_byMethod.TryGetValue(m, out var slot) || slot.Split) return;
            string id = slot.Label + " (split)";
            if (!_journal.Allowed(id)) { _failed.Add(id + " (skipped: it crashed the game once)"); return; }
            _journal.Begin(id);
            try
            {
                _deepHarmony.Patch(m, prefix: new HarmonyMethod(typeof(PhaseTimer), nameof(MarkPrefix)) { priority = int.MinValue },
                    postfix: new HarmonyMethod(typeof(PhaseTimer), nameof(OrigPost)) { priority = int.MaxValue });
                slot.Split = true;
                if (!_deepMethods.Contains(m)) _deepMethods.Add(m);
                QueuePatchesOf(m, slot.Label);
            }
            catch (Exception e) { _failed.Add(id + " (" + e.GetType().Name + ")"); }
            finally { _journal.Done(id); }
        }

        // A split method's mods' prefixes, postfixes and finalizers, each to be timed by itself (Vigil's
        // timers left out, Vigil's own hooks in as the mod Vigil).
        private void QueuePatchesOf(MethodBase m, string label)
        {
            Patches info;
            try { info = Harmony.GetPatchInfo(m); } catch (Exception) { return; }
            if (info == null) return;
            var mods = ModAssemblies();
            var queue = new List<KeyValuePair<MethodBase, Slot>>();
            void Each(IEnumerable<Patch> patches, string kind)
            {
                foreach (var p in patches)
                {
                    var pm = p.PatchMethod;
                    if (pm?.DeclaringType == null || TimerOwners.Contains(p.owner) || s_byMethod.ContainsKey(pm)) continue;
                    var asm = pm.DeclaringType.Assembly;
                    string mod = asm == typeof(PhaseTimer).Assembly ? OwnTime.Mod : mods.TryGetValue(asm, out string name) ? name : null;
                    if (mod == null) continue;
                    queue.Add(new KeyValuePair<MethodBase, Slot>(pm, new Slot { Label = label + " " + kind, Parent = Nested, Deep = true, Mod = mod, PatchKind = kind, Method = pm }));
                }
            }
            Each(info.Prefixes, "prefix");
            Each(info.Postfixes, "postfix");
            Each(info.Finalizers, "finalizer");
            _toTimePatches.Add(queue);
        }

        private void TimePatch(KeyValuePair<MethodBase, Slot> kv)
        {
            if (s_byMethod.ContainsKey(kv.Key)) return;
            if (Hook(_deepHarmony, kv.Key, kv.Value)) _deepMethods.Add(kv.Key);
        }

        /// <summary>The mods' patch methods timed by themselves in this deep capture.</summary>
        public List<PatchTiming> PatchTimings() =>
            s_slots.Where(s => s.PatchKind != null && s.DeepCalls > 0).Select(s => new PatchTiming(s.Mod, s.Label, s.DeepCalls, TicksToMs(s.DeepTicks))).ToList();

        /// <summary>How many game methods each mod changes with transpilers.</summary>
        public static Dictionary<string, int> TranspilersByMod()
        {
            var mods = ModAssemblies();
            var counts = new Dictionary<string, int>();
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch (Exception) { continue; }
                if (info == null) continue;
                foreach (string mod in info.Transpilers.Select(t => t.PatchMethod?.DeclaringType != null && mods.TryGetValue(t.PatchMethod.DeclaringType.Assembly, out string m) ? m : null)
                    .Where(m => m != null).Distinct())
                    counts[mod] = (counts.TryGetValue(mod, out int n) ? n : 0) + 1;
            }
            return counts;
        }

        /// <summary>Every loaded mod's assembly with its plugin name, Vigil left out.</summary>
        private static readonly Dictionary<string, Slot> s_own = new Dictionary<string, Slot>(StringComparer.Ordinal);

        /// <summary>
        /// One call of Vigil's own work, timed by the plugin around it (<see cref="OwnTime"/>): added to the
        /// frame like a mod callback's, under the mod name Vigil. Nothing while the recorder does not run.
        /// </summary>
        public static void OwnCall(string label, long ticks, long alloc)
        {
            if (!ProbeState.Active || s_mainThread == 0 || Thread.CurrentThread.ManagedThreadId != s_mainThread) return;
            if (!s_own.TryGetValue(label, out var slot))
            {
                slot = new Slot { Label = label, Mod = OwnTime.Mod };
                slot.Id = s_slots.Count;
                s_slots.Add(slot);
                s_own[label] = slot;
            }
            if (slot.FrameCalls == 0) s_touched.Add(slot);
            slot.FrameTicks += ticks;
            slot.FrameCalls++;
            slot.FrameAlloc += alloc;
        }

        /// <summary>Bytes allocated on this thread so far, when allocations are measured; 0 otherwise.</summary>
        public static long AllocNow() => s_allocNow != null ? s_allocNow() : 0;

        internal static Dictionary<Assembly, string> ModAssemblies()
        {
            var d = new Dictionary<Assembly, string>();
            foreach (var p in Chainloader.PluginInfos.Values)
            {
                if (p.Instance == null) continue;
                var a = p.Instance.GetType().Assembly;
                if (a == typeof(PhaseTimer).Assembly || d.ContainsKey(a)) continue;
                d[a] = p.Metadata.Name;
            }
            return d;
        }

        private static void Pre(MethodBase __originalMethod)
        {
            if (!ProbeState.Active) return;
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread) return;
            if (!s_byMethod.TryGetValue(__originalMethod, out var slot)) return;
            Stacks();
            if (t_depth >= t_starts.Length) return;
            t_ids[t_depth] = slot.Id;
            if (s_allocNow != null) t_allocs[t_depth] = s_allocNow();
            t_starts[t_depth] = Stopwatch.GetTimestamp();
            t_depth++;
        }

        private static void Fin(MethodBase __originalMethod)
        {
            if (!ProbeState.Active) return;
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread || t_depth == 0) return;
            if (!s_byMethod.TryGetValue(__originalMethod, out var slot)) return;
            // The original threw: its inner timer never reached the postfix.
            while (t_oDepth > 0 && t_oIds[t_oDepth - 1] == slot.Id) t_oDepth--;
            if (t_ids[t_depth - 1] != slot.Id) return; // our prefix was skipped for this call
            t_depth--;
            long dt = Stopwatch.GetTimestamp() - t_starts[t_depth];
            if (slot.FrameCalls == 0) s_touched.Add(slot);
            slot.FrameTicks += dt; slot.FrameCalls++;
            if (s_allocNow != null) slot.FrameAlloc += s_allocNow() - t_allocs[t_depth];
            if (slot.Split && s_deepRunning) slot.SplitWholeTicks += dt;
        }

        /// <summary>The last prefix on a split method: the game's code starts now (unless a prefix stopped it).</summary>
        private static void MarkPrefix(MethodBase __originalMethod)
        {
            if (!ProbeState.Active) return;
            if (!s_deepRunning || Thread.CurrentThread.ManagedThreadId != s_mainThread) return;
            if (!s_byMethod.TryGetValue(__originalMethod, out var slot) || !slot.Split) return;
            Stacks();
            if (t_oDepth >= t_oStarts.Length) return;
            t_oIds[t_oDepth] = slot.Id;
            t_oStarts[t_oDepth] = Stopwatch.GetTimestamp();
            t_oDepth++;
        }

        /// <summary>The first postfix on a split method: the game's code, if it ran, has ended.</summary>
        private static void OrigPost(MethodBase __originalMethod, bool __runOriginal)
        {
            if (!ProbeState.Active) return;
            long now = Stopwatch.GetTimestamp();
            if (!s_deepRunning || Thread.CurrentThread.ManagedThreadId != s_mainThread) return;
            if (!s_byMethod.TryGetValue(__originalMethod, out var slot)) return;
            bool marked = t_oDepth > 0 && t_oIds[t_oDepth - 1] == slot.Id;
            if (marked) t_oDepth--;
            // A prefix stopped the game's code: counted as a call without it.
            if (!__runOriginal) { slot.SplitCalls++; return; }
            // The game's code ran; without its start mark (the split went on mid-call) the call is left out.
            if (!marked) return;
            slot.SplitCalls++;
            slot.OrigRuns++;
            slot.OrigTicks += now - t_oStarts[t_oDepth];
        }

        public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Closes the frame: hands every slot used in it to <paramref name="sink"/>, folds the
        /// frame into the second and the session, and starts the next frame.
        /// </summary>
        public void EndFrame(Action<Slot, long> sink)
        {
            foreach (var s in s_touched)
            {
                sink?.Invoke(s, s.FrameTicks);
                s.SecondTicks += s.FrameTicks; s.SecondCalls += s.FrameCalls;
                s.RunTicks += s.FrameTicks; s.RunCalls += s.FrameCalls;
                s.SecondAlloc += s.FrameAlloc; s.RunAlloc += s.FrameAlloc;
                if (s_deepRunning) { s.DeepTicks += s.FrameTicks; s.DeepCalls += s.FrameCalls; Spike(s); }
                s.FrameTicks = 0; s.FrameCalls = 0; s.FrameAlloc = 0;
            }
            s_touched.Clear();
            t_depth = 0;
            t_oDepth = 0;
        }

        /// <summary>After a pause in recording: drops open timers and whatever the frame and second hold.</summary>
        public void DiscardPending()
        {
            t_depth = 0;
            t_oDepth = 0;
            foreach (var s in s_touched) { s.FrameTicks = 0; s.FrameCalls = 0; s.FrameAlloc = 0; }
            s_touched.Clear();
            EndSecond();
        }

        public void EndSecond()
        {
            foreach (var s in s_slots) { s.SecondTicks = 0; s.SecondCalls = 0; s.SecondAlloc = 0; }
        }
    }
}
