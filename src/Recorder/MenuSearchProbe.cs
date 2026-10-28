using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The build menu's search, for its line in Vigil-events.tsv: a prefix and postfix on
    /// <c>BuildUi.UpdateSearch</c> time its outermost call (a first letter typed on a tag switches
    /// to all pieces and rebuilds the buttons, which calls the search again inside), and after it
    /// count the piece buttons left shown. Several changes of the text in one frame add up.
    /// </summary>
    internal static class MenuSearchProbe
    {
        private static int s_depth;
        private static long s_start;
        private static MenuSearch s_current;
        private static AccessTools.FieldRef<BuildUi, List<BuildUiPieceButton>> s_buttons;

        /// <summary>The frame of the last search, to tell whether it ran before Vigil's frame start.</summary>
        public static int Frame { get; private set; } = -1;

        public static void Hook(Harmony harmony)
        {
            if (AccessTools.Field(typeof(BuildUi), "m_pieceButtons")?.FieldType != typeof(List<BuildUiPieceButton>)) return;
            s_buttons = AccessTools.FieldRefAccess<BuildUi, List<BuildUiPieceButton>>("m_pieceButtons");
            var me = typeof(MenuSearchProbe);
            Patching.Apply(harmony, AccessTools.DeclaredMethod(typeof(BuildUi), "UpdateSearch", new[] { typeof(string) }), "the build menu's search timed",
                prefix: Patching.M(me, nameof(Prefix)), finalizer: Patching.M(me, nameof(Finalizer)));
        }

        /// <summary>The searches since the last call, or null when there was none.</summary>
        public static MenuSearch Take()
        {
            var s = s_current;
            s_current = null;
            return s;
        }

        private static void Prefix()
        {
            if (s_depth++ == 0) s_start = Stopwatch.GetTimestamp();
        }

        private static void Finalizer(BuildUi __instance, string __0)
        {
            if (--s_depth > 0) return;
            s_depth = 0;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - s_start) * 1000.0 / Stopwatch.Frequency;
                var s = s_current ?? (s_current = new MenuSearch());
                s.SearchMs += ms;
                s.Letters = (__0 ?? "").Replace(" ", "").Length;
                var buttons = s_buttons(__instance);
                int shown = 0;
                foreach (var b in buttons) if (b != null && b.gameObject.activeSelf) shown++;
                s.Shown = shown;
                s.Buttons = buttons.Count;
                Frame = Time.frameCount;
            }
            catch (Exception) { }
        }
    }
}
