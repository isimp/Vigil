using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Time spent handling each kind of incoming network message, labelled with the mod it
    /// belongs to: direct packages through ZRpc.HandlePackage (world updates, config sync at a
    /// join) and routed messages through ZRoutedRpc.HandleRoutedRPC. The handlers are mod code,
    /// but only the game's two dispatch methods are hooked.
    /// </summary>
    internal static class HandlerTime
    {
        private static readonly ValueTally s_tally = new ValueTally("ms");
        private static int s_mainThread;
        [ThreadStatic] private static long t_packageStart;
        [ThreadStatic] private static string t_packageLabel;
        [ThreadStatic] private static long t_routedStart;
        [ThreadStatic] private static string t_routedLabel;

        public static void Patch(Harmony harmony)
        {
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            Patching.Apply(harmony, AccessTools.Method(typeof(ZRpc), "HandlePackage"), "time per network message kind",
                prefix: new HarmonyMethod(typeof(HandlerTime), nameof(PackagePre)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(HandlerTime), nameof(PackageFin)) { priority = Priority.Last });
            Patching.Apply(harmony, AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC"), "time per routed message kind",
                prefix: new HarmonyMethod(typeof(HandlerTime), nameof(RoutedPre)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(HandlerTime), nameof(RoutedFin)) { priority = Priority.Last });
        }

        private static void PackagePre(ZRpc __instance, ZPackage package)
        {
            if (!ProbeState.Active) return;
            t_packageLabel = null;
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread) return;
            // Routed messages travel inside one package kind and are timed one by one below.
            t_packageLabel = NetAttribution.HandlerLabel(__instance, package);
            if (t_packageLabel != null) t_packageStart = Stopwatch.GetTimestamp();
        }

        private static void PackageFin()
        {
            if (!ProbeState.Active) return;
            if (t_packageLabel == null) return;
            s_tally.Add(t_packageLabel, PhaseTimer.TicksToMs(Stopwatch.GetTimestamp() - t_packageStart));
            t_packageLabel = null;
        }

        private static void RoutedPre(ZRoutedRpc.RoutedRPCData data)
        {
            if (!ProbeState.Active) return;
            t_routedLabel = null;
            if (Thread.CurrentThread.ManagedThreadId != s_mainThread || data == null) return;
            t_routedLabel = RpcProbe.HandlerLabel(data);
            t_routedStart = Stopwatch.GetTimestamp();
        }

        private static void RoutedFin()
        {
            if (!ProbeState.Active) return;
            if (t_routedLabel == null) return;
            s_tally.Add(t_routedLabel, PhaseTimer.TicksToMs(Stopwatch.GetTimestamp() - t_routedStart));
            t_routedLabel = null;
        }

        public static void EndSecond(out string top, out double ms) => s_tally.EndSecond(out top, out ms, out _);
        public static string Peek(float seconds) => s_tally.Peek(seconds, 15);
        public static void EndWindow(float seconds) => s_tally.EndWindow(seconds, 0);
        public static string Summary(float seconds) => s_tally.Summary(seconds, 25);
    }
}
