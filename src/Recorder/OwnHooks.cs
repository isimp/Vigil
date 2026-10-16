using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Times Vigil's own hooks in the game's methods for a few seconds (<see cref="OwnCost"/>). The hooks
    /// run on hot paths (every object sent, every package, every name hashed), so timing them all the
    /// time would cost about what they cost; instead each of Vigil's prefix, postfix and finalizer
    /// methods gets a timing prefix and finalizer of its own for <see cref="Seconds"/>, put in and taken
    /// off a few per frame like the deep capture's, and skipped from the next start if putting one in
    /// crashed the game (<see cref="InstrumentJournal"/>). The timers' own cost per call is calibrated
    /// once on an empty method and taken off. The always-on timers on the mods' callbacks are not
    /// hooks of this kind and are counted apart. A hook the runtime folded into the game's method
    /// cannot be timed this way; such hooks are the smallest.
    /// </summary>
    internal sealed class OwnHooks
    {
        public const float Seconds = 10f, AutoAfterSeconds = 120f;
        private const int CalibrationCalls = 20000;
        private static readonly string[] NotTimed = { "isimp.Vigil.timing", "isimp.Vigil.deep", "isimp.Vigil.selftime" };

        private sealed class Acc
        {
            public string Label;
            public long Ticks, Calls;
        }

        private static readonly ConcurrentDictionary<MethodBase, Acc> s_acc = new ConcurrentDictionary<MethodBase, Acc>();
        private readonly Harmony _harmony = new Harmony("isimp.Vigil.selftime");
        private readonly InstrumentJournal _journal;
        private readonly Queue<MethodBase> _toHook = new Queue<MethodBase>(), _toUnhook = new Queue<MethodBase>();
        private readonly List<MethodBase> _hooked = new List<MethodBase>();
        private State _state;
        private float _measureStart;
        private long _frames;
        private DateTime _measuredAt;
        private HookMeasure _last;

        private enum State { Idle, Installing, Measuring, Removing }

        public OwnHooks(InstrumentJournal journal)
        {
            _journal = journal;
        }

        /// <summary>The timers' own cost per call in ms, from <see cref="Calibrate"/>; NaN until then.</summary>
        public double PerCallMs { get; private set; } = double.NaN;

        /// <summary>Why the last measurement or calibration could not run; null when it ran.</summary>
        public string Problem { get; private set; }

        public bool Running => _state != State.Idle;

        /// <summary>The last measurement, its age brought up to now; null before the first.</summary>
        public HookMeasure Last
        {
            get
            {
                if (_last != null) _last.MinutesAgo = (DateTime.Now - _measuredAt).TotalMinutes;
                return _last;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Empty()
        {
        }

        /// <summary>Once, on the main thread: what the timers add to one call, on a method that does nothing.</summary>
        public void Calibrate()
        {
            var empty = AccessTools.Method(typeof(OwnHooks), nameof(Empty));
            try
            {
                var acc = s_acc[empty] = new Acc { Label = "calibration" };
                _harmony.Patch(empty, prefix: new HarmonyMethod(typeof(OwnHooks), nameof(Pre)), finalizer: new HarmonyMethod(typeof(OwnHooks), nameof(Fin)));
                for (int i = 0; i < CalibrationCalls; i++) Empty();
                PerCallMs = acc.Calls > 0 ? PhaseTimer.TicksToMs(acc.Ticks) / acc.Calls : double.NaN;
            }
            catch (Exception e) { Problem = "calibration: " + e.GetType().Name + " " + e.Message; }
            finally
            {
                try { _harmony.Unpatch(empty, HarmonyPatchType.All, _harmony.Id); } catch (Exception) { }
                s_acc.TryRemove(empty, out _);
            }
        }

        /// <summary>Queues every one of Vigil's hook methods for timing; false when a measurement already runs.</summary>
        public bool Start()
        {
            if (_state != State.Idle) return false;
            s_acc.Clear();
            var labels = new Dictionary<MethodBase, List<string>>();
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch (Exception) { continue; }
                if (info == null) continue;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    var m = patch.PatchMethod;
                    if (m == null || patch.owner == null || !patch.owner.StartsWith("isimp.Vigil", StringComparison.Ordinal) || NotTimed.Contains(patch.owner)) continue;
                    if (m.ContainsGenericParameters || (m.DeclaringType != null && m.DeclaringType.ContainsGenericParameters)) continue;
                    if (!labels.TryGetValue(m, out var list)) labels[m] = list = new List<string>();
                    string label = original.DeclaringType != null ? original.DeclaringType.Name + "." + original.Name : original.Name;
                    if (!list.Contains(label)) list.Add(label);
                }
            }
            foreach (var kv in labels)
            {
                s_acc[kv.Key] = new Acc { Label = kv.Value[0] + (kv.Value.Count > 1 ? " (+" + (kv.Value.Count - 1) + ")" : "") };
                _toHook.Enqueue(kv.Key);
            }
            Problem = null;
            _state = State.Installing;
            return true;
        }

        /// <summary>Every frame: installs or takes off a few timers within <paramref name="budgetMs"/>, and ends the measurement after <see cref="Seconds"/>.</summary>
        public void Step(float now, double budgetMs)
        {
            if (_state == State.Idle) return;
            var sw = Stopwatch.StartNew();
            switch (_state)
            {
                case State.Installing:
                    while (_toHook.Count > 0 && sw.Elapsed.TotalMilliseconds < budgetMs) Hook(_toHook.Dequeue());
                    if (_toHook.Count > 0) return;
                    foreach (var a in s_acc.Values) { Interlocked.Exchange(ref a.Ticks, 0); Interlocked.Exchange(ref a.Calls, 0); }
                    _frames = 0;
                    _measureStart = now;
                    _state = State.Measuring;
                    return;
                case State.Measuring:
                    _frames++;
                    if (now - _measureStart < Seconds) return;
                    Finish(now - _measureStart);
                    foreach (var m in _hooked) _toUnhook.Enqueue(m);
                    _hooked.Clear();
                    _state = State.Removing;
                    return;
                case State.Removing:
                    while (_toUnhook.Count > 0 && sw.Elapsed.TotalMilliseconds < budgetMs)
                    {
                        var m = _toUnhook.Dequeue();
                        try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); } catch (Exception) { }
                    }
                    if (_toUnhook.Count == 0) { _state = State.Idle; s_acc.Clear(); }
                    return;
            }
        }

        private MethodBase _one;

        /// <summary>For the self-test: times one patch method by itself until <see cref="EndOne"/>; false while a measurement runs or when it cannot be put in.</summary>
        public bool BeginOne(MethodBase m, string label)
        {
            if (_state != State.Idle || _one != null || m == null || s_acc.ContainsKey(m)) return false;
            s_acc[m] = new Acc { Label = label };
            try
            {
                _harmony.Patch(m, prefix: new HarmonyMethod(typeof(OwnHooks), nameof(Pre)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(OwnHooks), nameof(Fin)) { priority = Priority.Last });
                _one = m;
                return true;
            }
            catch (Exception)
            {
                s_acc.TryRemove(m, out _);
                return false;
            }
        }

        /// <summary>The one patch method's calls and time (the timers' cost taken off), its timer taken off again.</summary>
        public (long Calls, double Ms) EndOne()
        {
            var m = _one;
            _one = null;
            if (m == null || !s_acc.TryRemove(m, out var a)) return (0, 0);
            try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); } catch (Exception) { }
            return (a.Calls, OwnCost.Net(PhaseTimer.TicksToMs(a.Ticks), a.Calls, double.IsNaN(PerCallMs) ? 0 : PerCallMs));
        }

        private void Hook(MethodBase m)
        {
            string id = "own hook " + (m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name;
            if (!_journal.Allowed(id)) return;
            _journal.Begin(id);
            try
            {
                _harmony.Patch(m, prefix: new HarmonyMethod(typeof(OwnHooks), nameof(Pre)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(OwnHooks), nameof(Fin)) { priority = Priority.Last });
                _hooked.Add(m);
            }
            catch (Exception) { s_acc.TryRemove(m, out _); }
            finally { _journal.Done(id); }
        }

        private void Finish(float seconds)
        {
            double perCall = double.IsNaN(PerCallMs) ? 0 : PerCallMs;
            var hooks = s_acc.Values.Where(a => a.Label != "calibration").GroupBy(a => a.Label)
                .Select(g => new HookCost(g.Key, g.Sum(a => a.Calls), g.Sum(a => OwnCost.Net(PhaseTimer.TicksToMs(a.Ticks), a.Calls, perCall))))
                .ToList();
            _last = new HookMeasure { Seconds = seconds, Frames = _frames, Hooks = hooks };
            _measuredAt = DateTime.Now;
        }

        private static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Fin(MethodBase __originalMethod, long __state)
        {
            if (!s_acc.TryGetValue(__originalMethod, out var a)) return;
            Interlocked.Add(ref a.Ticks, Stopwatch.GetTimestamp() - __state);
            Interlocked.Increment(ref a.Calls);
        }
    }

    /// <summary>Vigil's work on threads of its own (reports and the summary put together and written, names and config hashes read), in elapsed time.</summary>
    internal static class OwnThreads
    {
        private static long s_ticks;

        public static double Seconds => PhaseTimer.TicksToMs(Interlocked.Read(ref s_ticks)) / 1000.0;

        /// <summary>Runs <paramref name="work"/> and counts its elapsed time; for a thread of Vigil's own.</summary>
        public static void Timed(Action work)
        {
            long t = Stopwatch.GetTimestamp();
            try { work(); }
            finally { Interlocked.Add(ref s_ticks, Stopwatch.GetTimestamp() - t); }
        }
    }
}
