using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// What the screen layout rebuilds in one frame, for the build menu's close line: while armed,
    /// a prefix on each of the two rebuild passes that run when the canvases update (Unity UI's
    /// <c>CanvasUpdateRegistry.PerformUpdate</c> and TextMeshPro's <c>TMP_UpdateManager.DoRebuilds</c>)
    /// counts the parts waiting in its queues by where they sit, and its postfix times the pass.
    /// Unarmed, each pass costs one flag check.
    /// </summary>
    internal static class UiRebuildProbe
    {
        private const int PlaceDepth = 9;

        // Frames an armed count may stay open, in case the frame that takes it never comes (recording paused).
        private const int ArmedFrames = 2;

        private static bool s_armed, s_unityRead, s_textRead;
        private static int s_armedAt;
        private static UiRebuild s_current;
        // Unity's queues are an internal set type that offers only the generic list interface.
        private static IList<ICanvasElement> s_unityLayout, s_unityGraphic;
        private static IList<TMP_Text> s_textLayout, s_textGraphic, s_textInternal;
        private static readonly Stopwatch s_watch = new Stopwatch();
        private static readonly List<string> s_names = new List<string>();

        /// <summary>The frame in which the build menu last picked a piece, to tell whether the click ran before Vigil's frame start.</summary>
        public static int PickFrame { get; private set; } = -1;

        public static void Hook(Harmony harmony)
        {
            var me = typeof(UiRebuildProbe);
            Patching.Apply(harmony, AccessTools.Method(typeof(CanvasUpdateRegistry), "PerformUpdate"), "what the screen layout rebuilds after the build menu closes (Unity UI)",
                prefix: Patching.M(me, nameof(UnityPrefix)), postfix: Patching.M(me, nameof(UnityPostfix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(TMP_UpdateManager), "DoRebuilds"), "what the screen layout rebuilds after the build menu closes (TextMeshPro)",
                prefix: Patching.M(me, nameof(TextPrefix)), postfix: Patching.M(me, nameof(TextPostfix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(BuildUi), nameof(BuildUi.OnSelectPiece)), "the frame a build menu pick runs in",
                postfix: Patching.M(me, nameof(PickPostfix)));
        }

        private static void PickPostfix() => PickFrame = Time.frameCount;

        /// <summary>Starts counting the rebuilds of the frame now under way.</summary>
        public static void Arm()
        {
            s_current = new UiRebuild();
            s_armedAt = Time.frameCount;
            s_armed = true;
        }

        /// <summary>Stops counting and gives what was counted, or null when nothing was armed.</summary>
        public static UiRebuild Take()
        {
            var r = s_armed ? s_current : null;
            s_armed = false;
            s_current = null;
            return r;
        }

        private static bool Counting()
        {
            if (!s_armed) return false;
            if (Time.frameCount - s_armedAt <= ArmedFrames) return true;
            s_armed = false;
            s_current = null;
            return false;
        }

        private static void UnityPrefix(CanvasUpdateRegistry __instance)
        {
            if (!Counting()) return;
            try
            {
                if (!s_unityRead)
                {
                    s_unityRead = true;
                    s_unityLayout = Traverse.Create(__instance).Field("m_LayoutRebuildQueue").GetValue() as IList<ICanvasElement>;
                    s_unityGraphic = Traverse.Create(__instance).Field("m_GraphicRebuildQueue").GetValue() as IList<ICanvasElement>;
                    if (s_unityLayout == null || s_unityGraphic == null) Unreadable("Unity UI");
                }
                CountQueue(s_unityLayout);
                CountQueue(s_unityGraphic);
                s_current.Passes++;
            }
            catch (Exception) { }
            s_watch.Reset();
            s_watch.Start();
        }

        private static void UnityPostfix()
        {
            if (!s_armed) return;
            s_watch.Stop();
            s_current.UnityMs += s_watch.Elapsed.TotalMilliseconds;
        }

        private static void TextPrefix(TMP_UpdateManager __instance)
        {
            if (!Counting()) return;
            try
            {
                if (!s_textRead)
                {
                    s_textRead = true;
                    s_textLayout = Traverse.Create(__instance).Field("m_LayoutRebuildQueue").GetValue() as IList<TMP_Text>;
                    s_textGraphic = Traverse.Create(__instance).Field("m_GraphicRebuildQueue").GetValue() as IList<TMP_Text>;
                    s_textInternal = Traverse.Create(__instance).Field("m_InternalUpdateQueue").GetValue() as IList<TMP_Text>;
                    if (s_textLayout == null || s_textGraphic == null || s_textInternal == null) Unreadable("TextMeshPro");
                }
                CountQueue(s_textLayout);
                CountQueue(s_textGraphic);
                CountQueue(s_textInternal);
            }
            catch (Exception) { }
            s_watch.Reset();
            s_watch.Start();
        }

        private static void TextPostfix()
        {
            if (!s_armed) return;
            s_watch.Stop();
            s_current.TextMs += s_watch.Elapsed.TotalMilliseconds;
        }

        private static void Unreadable(string pass) =>
            Patching.Problems.Add("what the screen layout rebuilds after the build menu closes (" + pass + "): its queues could not be read, so the close line leaves them out");

        private static void CountQueue(IList<ICanvasElement> queue)
        {
            if (queue == null) return;
            for (int i = 0; i < queue.Count; i++)
            {
                var e = queue[i];
                Transform t = null;
                try { t = e?.transform; } catch (Exception) { }
                bool hidden = t && !t.gameObject.activeInHierarchy;
                s_current.Add(UiRebuild.Place(Names(t), PlaceDepth), e is TMP_Text || e is Text, e?.GetType().Name ?? "null", hidden);
            }
        }

        private static void CountQueue(IList<TMP_Text> queue)
        {
            if (queue == null) return;
            for (int i = 0; i < queue.Count; i++)
            {
                var c = queue[i];
                s_current.Add(UiRebuild.Place(Names(c ? c.transform : null), PlaceDepth), true, c ? c.GetType().Name : "null", c && !c.gameObject.activeInHierarchy);
            }
        }

        private static List<string> Names(Transform t)
        {
            s_names.Clear();
            for (var p = t; p; p = p.parent) s_names.Add(p.name);
            s_names.Reverse();
            return s_names;
        }
    }
}
