using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "The build menu opens faster" (<see cref="MenuReuse"/>). Two prefixes on the
    /// game's build menu. On <c>BuildUi.UpdatePieceButtons</c>, only while the menu is hidden (the
    /// refresh an open does before it shows the menu): the pieces the game would show are fetched the
    /// game's way, and when the buttons already show exactly those, the rebuild is skipped and each
    /// button's material colour and favourite star are refreshed. Mods' postfixes on the method still
    /// run; ComfortMe redraws its badges on every frame the menu is open anyway, and DragNBuild's mark
    /// on a button depends only on its piece. On <c>BuildUi.ConfigureButtonNavigation</c>, while the
    /// menu is hidden: the game's grid setup links only shown buttons, so it links none and only
    /// forces every canvas to update; the repair button's links, set whether shown or not, are kept.
    /// The open sets up the navigation again once the menu is shown. The switch is this game's own.
    /// </summary>
    internal static class BuildMenuPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.buildmenu";

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        /// <summary>Opens that kept their buttons, and opens that rebuilt them, since the start.</summary>
        public static long Kept { get; private set; }
        public static long Rebuilt { get; private set; }

        /// <summary>Buttons kept in the frame now under way and the frame it was, for the open's line.</summary>
        public static int KeptButtons { get; private set; }
        public static int KeptFrame { get; private set; } = -1;

        public static string State => MenuReuse.StateLine(On, Problem, Kept, Rebuilt);

        private static AccessTools.FieldRef<BuildUi, PieceTable> s_tool;
        private static AccessTools.FieldRef<BuildUi, List<IPieceList>> s_lists;
        private static AccessTools.FieldRef<BuildUi, int> s_list, s_tag, s_count;
        private static AccessTools.FieldRef<BuildUi, List<BuildUiPieceButton>> s_buttons;
        private static AccessTools.FieldRef<BuildUi, BuildUiPieceButton> s_special;
        private static Func<BuildUi, Navigation> s_specialNavigation;
        private static readonly List<Piece> s_wanted = new List<Piece>();
        private static readonly List<Piece> s_shown = new List<Piece>();

        public static void Hook()
        {
            try
            {
                var update = AccessTools.DeclaredMethod(typeof(BuildUi), "UpdatePieceButtons");
                var navigation = AccessTools.DeclaredMethod(typeof(BuildUi), "ConfigureButtonNavigation");
                var specialNavigation = AccessTools.DeclaredMethod(typeof(BuildUi), "GetSpecialButtonNavigation");
                string[] fields = { "m_currentBuildTool", "m_pieceLists", "m_currentPieceList", "m_currentTagId", "m_availablePieceCount", "m_pieceButtons", "m_specialPieceButton" };
                if (update == null || navigation == null || specialNavigation == null || specialNavigation.ReturnType != typeof(Navigation) ||
                    Array.Exists(fields, f => AccessTools.Field(typeof(BuildUi), f) == null))
                {
                    Problem = "the build menu changed (an update)";
                    return;
                }
                s_tool = AccessTools.FieldRefAccess<BuildUi, PieceTable>("m_currentBuildTool");
                s_lists = AccessTools.FieldRefAccess<BuildUi, List<IPieceList>>("m_pieceLists");
                s_list = AccessTools.FieldRefAccess<BuildUi, int>("m_currentPieceList");
                s_tag = AccessTools.FieldRefAccess<BuildUi, int>("m_currentTagId");
                s_count = AccessTools.FieldRefAccess<BuildUi, int>("m_availablePieceCount");
                s_buttons = AccessTools.FieldRefAccess<BuildUi, List<BuildUiPieceButton>>("m_pieceButtons");
                s_special = AccessTools.FieldRefAccess<BuildUi, BuildUiPieceButton>("m_specialPieceButton");
                s_specialNavigation = AccessTools.MethodDelegate<Func<BuildUi, Navigation>>(specialNavigation);
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(BuildMenuPatch), nameof(BeforeUpdatePieceButtons)));
                harmony.Patch(navigation, prefix: new HarmonyMethod(typeof(BuildMenuPatch), nameof(BeforeNavigation)));
            }
            catch (Exception e)
            {
                Problem = "the build menu could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static bool BeforeUpdatePieceButtons(BuildUi __instance)
        {
            if (!On || Problem != null || __instance.gameObject.activeSelf) return true;
            try
            {
                var tool = s_tool(__instance);
                var lists = s_lists(__instance);
                int index = s_list(__instance);
                if (tool == null || lists == null || index < 0 || index >= lists.Count || lists[index] == null) return true;
                var list = lists[index];
                var buttons = s_buttons(__instance);
                var special = s_special(__instance);
                s_wanted.Clear();
                list.GetAvailablePiecesWithTag(s_tag(__instance), tool, s_wanted);
                s_shown.Clear();
                bool allShown = true;
                foreach (var b in buttons)
                {
                    if (b == null || !b.gameObject.activeSelf) { allShown = false; break; }
                    s_shown.Add(b.Piece);
                }
                bool keep = MenuReuse.Reuse(s_wanted, s_shown, p => p == null || p.m_repairPiece || p.m_removePiece, list.ShowTags, special != null ? special.Piece : null, allShown);
                s_wanted.Clear();
                s_shown.Clear();
                if (!keep)
                {
                    Rebuilt++;
                    return true;
                }
                s_count(__instance) = tool.m_availablePieces.Count;
                foreach (var b in buttons)
                {
                    b.RefreshFavorite();
                    b.UpdateRequirements();
                }
                if (special != null)
                {
                    special.RefreshFavorite();
                    special.UpdateRequirements();
                }
                Kept++;
                KeptButtons = buttons.Count;
                KeptFrame = Time.frameCount;
                return false;
            }
            catch (Exception e)
            {
                // Left to the game from here on; it rebuilds the buttons as it always did.
                Problem = "the build menu refresh failed: " + e.GetType().Name + " " + e.Message;
                return true;
            }
        }

        private static bool BeforeNavigation(BuildUi __instance)
        {
            if (Problem != null || !MenuReuse.SkipNavigation(On, __instance.gameObject.activeInHierarchy)) return true;
            try
            {
                var special = s_special(__instance);
                if (special != null) special.GetComponent<Button>().navigation = s_specialNavigation(__instance);
                return false;
            }
            catch (Exception e)
            {
                Problem = "the build menu's navigation failed: " + e.GetType().Name + " " + e.Message;
                return true;
            }
        }
    }
}
