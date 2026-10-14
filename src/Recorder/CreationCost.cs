using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Time to create each world object as it loads in, per object type (ZNetScene.CreateObject:
    /// instantiating the prefab and every Awake on it, a mod's components included). Object types
    /// from content mods that are slow to create show up as zone-loading hitches.
    /// </summary>
    internal static class CreationCost
    {
        private static readonly ValueTally s_tally = new ValueTally("ms");
        private static int s_mainThread;
        [ThreadStatic] private static long t_start;
        [ThreadStatic] private static int t_prefab;
        [ThreadStatic] private static int t_depth;

        public static void Patch(Harmony harmony)
        {
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }), "object creation time per type",
                prefix: new HarmonyMethod(typeof(CreationCost), nameof(Pre)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(CreationCost), nameof(Fin)) { priority = Priority.Last });
        }

        private static void Pre(ZDO zdo)
        {
            if (!ProbeState.Active) return;
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread || zdo == null) return;
            // An object whose Awake creates another is counted once, as the outer one.
            if (t_depth++ > 0) return;
            t_prefab = zdo.GetPrefab();
            t_start = Stopwatch.GetTimestamp();
        }

        private static void Fin(ZDO zdo)
        {
            if (!ProbeState.Active) return;
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread || zdo == null || t_depth == 0) return;
            if (--t_depth > 0) return;
            double ms = PhaseTimer.TicksToMs(Stopwatch.GetTimestamp() - t_start);
            // Objects without a type (a load test's) return at once and are not world objects being built.
            if (t_prefab != 0) s_tally.Add(RecorderModule.NameOf(t_prefab), ms);
        }

        public static string Peek(float seconds) => s_tally.Peek(seconds, 15);
        public static void EndWindow(float seconds) => s_tally.EndWindow(seconds, 0);
        public static string Summary(float seconds) => s_tally.Summary(seconds, 25);
        public static void EndSecond() => s_tally.EndSecond(out _, out _, out _);
    }
}
