using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        public static MenuReuse.Diff LastDiff { get; private set; }

        /// <summary>Stray repair buttons removed since the start.</summary>
        public static long Strays { get; private set; }

        public static string State => MenuReuse.StateLine(On, Problem, Kept, Rebuilt, LastDiff, Strays);

        /// <summary>The switch of "The build menu sets up its navigation a frame later" (<see cref="GridLinks"/>), kept current by its config entry.</summary>
        public static bool NavigationLaterOn;

        /// <summary>Why the navigation patch stepped aside; null while it is in.</summary>
        public static string NavigationProblem { get; private set; }

        /// <summary>Requests put off, and the last linking: its time, its buttons and its frame (-1 before the first).</summary>
        public static long PutOff { get; private set; }
        public static double LastLinkMs { get; private set; }
        public static int LastLinkButtons { get; private set; }
        public static int LastLinkFrame { get; private set; } = -1;

        public static string NavigationState => GridLinks.StateLine(NavigationLaterOn, NavigationProblem, PutOff, LastLinkMs, LastLinkButtons);

        private static AccessTools.FieldRef<BuildUi, RectTransform> s_pieceGrid, s_tagGrid;
        private static Func<BuildUi, Navigation> s_pieceEdge, s_tagEdge;
        private static MethodInfo s_gameNavigation;
        private static BuildUi s_pendingUi;
        private static int s_pendingFrame = -1;
        private static readonly List<Selectable> s_selectables = new List<Selectable>();
        private static readonly List<float> s_rows = new List<float>();

        private static Action<string> s_log;
        private static readonly HashSet<MenuReuse.Diff> s_logged = new HashSet<MenuReuse.Diff>();

        private static AccessTools.FieldRef<BuildUi, PieceTable> s_tool;
        private static AccessTools.FieldRef<BuildUi, List<IPieceList>> s_lists;
        private static AccessTools.FieldRef<BuildUi, int> s_list, s_tag, s_count;
        private static AccessTools.FieldRef<BuildUi, List<BuildUiPieceButton>> s_buttons;
        private static AccessTools.FieldRef<BuildUi, BuildUiPieceButton> s_special;
        private static AccessTools.FieldRef<BuildUi, RectTransform> s_corner;
        private static readonly List<BuildUiPieceButton> s_cornerButtons = new List<BuildUiPieceButton>();
        private static BuildUiPieceButton s_gameRemoves;
        private static Func<BuildUi, Navigation> s_specialNavigation;
        private static readonly List<Piece> s_wanted = new List<Piece>();
        private static readonly List<Piece> s_shown = new List<Piece>();

        public static void Hook(Action<string> log)
        {
            s_log = log;
            try
            {
                var update = AccessTools.DeclaredMethod(typeof(BuildUi), "UpdatePieceButtons");
                var navigation = AccessTools.DeclaredMethod(typeof(BuildUi), "ConfigureButtonNavigation");
                var specialNavigation = AccessTools.DeclaredMethod(typeof(BuildUi), "GetSpecialButtonNavigation");
                string[] fields = { "m_currentBuildTool", "m_pieceLists", "m_currentPieceList", "m_currentTagId", "m_availablePieceCount", "m_pieceButtons", "m_specialPieceButton", "m_specialButtonsContainer" };
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
                s_corner = AccessTools.FieldRefAccess<BuildUi, RectTransform>("m_specialButtonsContainer");
                s_specialNavigation = AccessTools.MethodDelegate<Func<BuildUi, Navigation>>(specialNavigation);
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(BuildMenuPatch), nameof(BeforeUpdatePieceButtons)), postfix: new HarmonyMethod(typeof(BuildMenuPatch), nameof(AfterUpdatePieceButtons)));
                harmony.Patch(navigation, prefix: new HarmonyMethod(typeof(BuildMenuPatch), nameof(BeforeNavigation)));
                HookNavigationLater(harmony, navigation);
            }
            catch (Exception e)
            {
                Problem = "the build menu could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static bool BeforeUpdatePieceButtons(BuildUi __instance)
        {
            // The game removes this one itself when it rebuilds, at the frame's end.
            try { s_gameRemoves = s_special != null ? s_special(__instance) : null; } catch (Exception) { s_gameRemoves = null; }
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
                var diff = MenuReuse.Compare(s_wanted, s_shown, IsSpecial, list.ShowTags, special != null ? special.Piece : null, allShown, out int at, IsGameRepair);
                LastDiff = diff;
                if (diff != MenuReuse.Diff.Same)
                {
                    Rebuilt++;
                    // Each reason once a session in the game's log, with what differed.
                    if (s_logged.Add(diff)) s_log?.Invoke("[Vigil] build menu: buttons rebuilt, " + MenuReuse.Words(diff) + Detail(diff, at, special));
                    s_wanted.Clear();
                    s_shown.Clear();
                    return true;
                }
                s_wanted.Clear();
                s_shown.Clear();
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

        // After the buttons are rebuilt or kept: the repair buttons the game made for all but the last
        // repair piece and never removes. A removed one is hidden at once, as Destroy waits for the
        // frame's end and the buttons can be refreshed twice in a frame.
        private static void AfterUpdatePieceButtons(BuildUi __instance)
        {
            if (!On || Problem != null) return;
            try
            {
                var corner = s_corner(__instance);
                if (corner == null) return;
                s_cornerButtons.Clear();
                for (int i = 0; i < corner.childCount; i++)
                {
                    var b = corner.GetChild(i).GetComponent<BuildUiPieceButton>();
                    if (b != null) s_cornerButtons.Add(b);
                }
                Func<BuildUiPieceButton, bool> live = b => b.gameObject.activeSelf && !ReferenceEquals(b, s_gameRemoves);
                // The game's own repair piece stays the repair button when mods add repair tools of their own.
                var own = MenuReuse.PickRepair(s_cornerButtons.Where(live), b => IsGameRepair(b.Piece), s_special(__instance));
                if (own != null && !ReferenceEquals(own, s_special(__instance))) s_special(__instance) = own;
                foreach (var stray in MenuReuse.Strays(s_cornerButtons, own, live))
                {
                    stray.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(stray.gameObject);
                    Strays++;
                }
                s_cornerButtons.Clear();
            }
            catch (Exception e)
            {
                Problem = "the repair corner clean-up failed: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static bool IsSpecial(Piece p) => p == null || p.m_repairPiece || p.m_removePiece;

        private static bool IsGameRepair(Piece p) => p != null && p.m_repairPiece && p.gameObject.name == MenuReuse.GameRepairPiece;

        private static string Name(Piece p) => p != null ? p.gameObject.name : "none";

        // What differed, from the lists still filled: the pieces at the button, the counts, the repair pieces.
        private static string Detail(MenuReuse.Diff diff, int at, BuildUiPieceButton special)
        {
            int wanted = 0;
            var repairs = new List<string>();
            foreach (var p in s_wanted) { if (IsSpecial(p)) repairs.Add(Name(p)); else wanted++; }
            switch (diff)
            {
                case MenuReuse.Diff.OtherPiece:
                    Piece now = null;
                    int n = -1;
                    foreach (var p in s_wanted) { if (IsSpecial(p)) continue; if (++n == at) { now = p; break; } }
                    return " (button " + at + ": " + Name(now) + " instead of " + Name(at < s_shown.Count ? s_shown[at] : null) + ")";
                case MenuReuse.Diff.MorePieces:
                case MenuReuse.Diff.FewerPieces:
                    return " (" + wanted + " pieces, " + s_shown.Count + " buttons)";
                case MenuReuse.Diff.RepairChanged:
                    return " (repair pieces: " + (repairs.Count == 0 ? "none" : string.Join(", ", repairs.ToArray())) + "; repair button: " + Name(special != null ? special.Piece : null) + ")";
                default:
                    return "";
            }
        }

        // The navigation patch's own parts; missing ones leave "opens faster" working.
        private static void HookNavigationLater(Harmony harmony, MethodInfo navigation)
        {
            var update = AccessTools.DeclaredMethod(typeof(BuildUi), "Update");
            var pieceEdge = AccessTools.DeclaredMethod(typeof(BuildUi), "GetPieceListEdgeNavigation");
            var tagEdge = AccessTools.DeclaredMethod(typeof(BuildUi), "GetTagListEdgeNavigation");
            if (update == null || pieceEdge == null || tagEdge == null || pieceEdge.ReturnType != typeof(Navigation) || tagEdge.ReturnType != typeof(Navigation) ||
                AccessTools.Field(typeof(BuildUi), "m_pieceButtonsContainer")?.FieldType != typeof(RectTransform) ||
                AccessTools.Field(typeof(BuildUi), "m_tagListScrollContent")?.FieldType != typeof(RectTransform))
            {
                NavigationProblem = "the build menu changed (an update)";
                return;
            }
            s_pieceGrid = AccessTools.FieldRefAccess<BuildUi, RectTransform>("m_pieceButtonsContainer");
            s_tagGrid = AccessTools.FieldRefAccess<BuildUi, RectTransform>("m_tagListScrollContent");
            s_pieceEdge = AccessTools.MethodDelegate<Func<BuildUi, Navigation>>(pieceEdge);
            s_tagEdge = AccessTools.MethodDelegate<Func<BuildUi, Navigation>>(tagEdge);
            s_gameNavigation = navigation;
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(BuildMenuPatch), nameof(AfterBuildUiUpdate)));
        }

        private static bool BeforeNavigation(BuildUi __instance)
        {
            bool skipHidden = Problem == null && MenuReuse.SkipNavigation(On, __instance.gameObject.activeInHierarchy);
            bool later = NavigationProblem == null && NavigationLaterOn && s_gameNavigation != null;
            if (!skipHidden && !later) return true;
            try
            {
                // The repair button's links, which the game sets whether it is shown or not.
                var special = s_special(__instance);
                if (special != null) special.GetComponent<Button>().navigation = s_specialNavigation(__instance);
            }
            catch (Exception e)
            {
                Problem = "the build menu's navigation failed: " + e.GetType().Name + " " + e.Message;
                if (!later) return true;
            }
            if (later)
            {
                s_pendingUi = __instance;
                s_pendingFrame = Time.frameCount;
                PutOff++;
            }
            return false;
        }

        // Runs every frame the menu is open, after the frame of a request, when the screen layout
        // has placed the buttons.
        private static void AfterBuildUiUpdate(BuildUi __instance)
        {
            if (!GridLinks.Due(s_pendingFrame, Time.frameCount) || s_pendingUi != __instance) return;
            s_pendingFrame = -1;
            s_pendingUi = null;
            if (!__instance.gameObject.activeInHierarchy) return;
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int n = Link(s_pieceGrid(__instance), s_pieceEdge(__instance)) + Link(s_tagGrid(__instance), s_tagEdge(__instance));
                LastLinkMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                LastLinkButtons = n;
                LastLinkFrame = Time.frameCount;
            }
            catch (Exception e)
            {
                NavigationProblem = "the build menu's navigation failed: " + e.GetType().Name + " " + e.Message;
                try { s_gameNavigation.Invoke(__instance, null); } catch (Exception) { }
            }
        }

        // The shown, usable buttons of a grid in its order, linked to their neighbours, the grid's
        // edge links at the sides. Returns how many were linked.
        private static int Link(RectTransform grid, Navigation edge)
        {
            if (grid == null) return 0;
            s_selectables.Clear();
            s_rows.Clear();
            for (int i = 0; i < grid.childCount; i++)
            {
                var child = grid.GetChild(i);
                if (!child.gameObject.activeInHierarchy) continue;
                var sel = child.GetComponent<Selectable>();
                if (sel == null || !sel.IsInteractable()) continue;
                s_selectables.Add(sel);
                if (child is RectTransform rt) s_rows.Add(rt.anchoredPosition.y);
            }
            int count = s_selectables.Count;
            if (count == 0) return 0;
            int columns = s_rows.Count == count ? GridLinks.Columns(s_rows) : 1;
            for (int i = 0; i < count; i++)
            {
                var l = GridLinks.Neighbours(i, count, columns);
                var nav = s_selectables[i].navigation;
                nav.mode = Navigation.Mode.Explicit;
                nav.selectOnLeft = l.Left >= 0 ? s_selectables[l.Left] : edge.selectOnLeft;
                nav.selectOnRight = l.Right >= 0 ? s_selectables[l.Right] : edge.selectOnRight;
                nav.selectOnUp = l.Up >= 0 ? s_selectables[l.Up] : edge.selectOnUp;
                nav.selectOnDown = l.Down >= 0 ? s_selectables[l.Down] : edge.selectOnDown;
                s_selectables[i].navigation = nav;
            }
            s_selectables.Clear();
            return count;
        }
    }
}
