using System;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "The wheel scrolls a chest grid with a bar" (<see cref="ChestScroll"/>): a prefix
    /// on CycleChests' own prefix on <c>Player.Update</c> skips it for the frame while a chest is open,
    /// the pointer is over that chest's grid or its scroll bar, and the grid's slots are taller than
    /// its view. The wheel then reaches only the grid's scroll view, which scrolls it the game's way;
    /// Vigil moves nothing itself. The switch is this game's own.
    /// </summary>
    internal static class ChestScrollPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.chestscroll";

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On = true;

        public static bool Found { get; private set; }

        public static string Version { get; private set; }

        /// <summary>Why the patch stepped aside; null while it is in (or CycleChests is absent).</summary>
        public static string Problem { get; private set; }

        /// <summary>Wheel turns over a chest grid with a bar that CycleChests did not get.</summary>
        public static long Kept { get; private set; }

        public static string State => ChestScroll.StateLine(On, Found, Version, Problem, Kept);

        public static void Hook()
        {
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == ChestScroll.Guid);
            Found = info != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var step = ChestScroll.FindInputStep(TypeLookup.Loaded.Find);
                if (step == null)
                {
                    Problem = "its wheel handling changed (an update)";
                    return;
                }
                new Harmony(HarmonyId).Patch(step, prefix: new HarmonyMethod(typeof(ChestScrollPatch), nameof(BeforeInputStep)));
            }
            catch (Exception e)
            {
                Problem = "CycleChests could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        /// <summary>Writes a line to the game's log for each burst of wheel turns while a chest is open (Patches.ChestGridScrollTrace).</summary>
        public static bool Trace = true;

        private static double s_lastTurnAt = double.NegativeInfinity;
        private static bool s_afterDue;
        private static Container s_tracedChest;
        private static float s_beforeScroll, s_lastScroll;
        private static readonly AccessTools.FieldRef<InventoryGui, Container> s_currentContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");

        private static bool BeforeInputStep(Player __instance)
        {
            if (!On || __instance != Player.m_localPlayer) return true;
            var gui = InventoryGui.instance;
            if (gui == null || !gui.IsContainerOpen()) return true;
            var grid = gui.ContainerGrid;
            if (grid == null || grid.m_gridRoot == null || !(grid.transform is RectTransform view)) return true;
            var scroll = Trace ? grid.GetComponentInParent<UnityEngine.UI.ScrollRect>() ?? grid.GetComponentInChildren<UnityEngine.UI.ScrollRect>() : null;
            float scrolled = scroll != null ? scroll.verticalNormalizedPosition : 0f;
            if (s_afterDue)
            {
                s_afterDue = false;
                Debug.Log("[Vigil] chest scroll: " + ChestScroll.AfterLine(s_currentContainer(gui) == s_tracedChest, s_beforeScroll, scrolled));
            }
            float before = s_lastScroll;
            s_lastScroll = scrolled;

            bool turned = Input.mouseScrollDelta.y != 0f;
            if (!turned) return true;
            Vector3 pointer = ZInput.pointerPosition;
            bool inGrid = Over(view, pointer);
            bool onBar = grid.m_scrollbar != null && grid.m_scrollbar.gameObject.activeInHierarchy && Over((RectTransform)grid.m_scrollbar.transform, pointer);
            float slots = grid.m_gridRoot.rect.height, height = view.rect.height;
            bool kept = ChestScroll.KeepsWheel(On, turned, true, inGrid || onBar, ChestScroll.HasBar(slots, height));
            if (kept) Kept++;

            double now = Time.realtimeSinceStartupAsDouble;
            if (Trace && ChestScroll.StartsBurst(s_lastTurnAt, now))
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                string chest = s_currentContainer(gui) != null ? s_currentContainer(gui).name : "none";
                string view2 = scroll == null ? "none" : (scroll.enabled ? "on" : "off") + ", sensitivity " + scroll.scrollSensitivity.ToString("0.#", ci) + ", content " + (scroll.content == grid.m_gridRoot ? "the slots" : scroll.content != null ? scroll.content.name : "none");
                Debug.Log("[Vigil] chest scroll: " + ChestScroll.TraceLine(pointer.x, pointer.y, inGrid, onBar, slots, height, kept, chest) + " · scroll view " + view2);
                s_tracedChest = s_currentContainer(gui);
                s_beforeScroll = before;
                s_afterDue = true;
            }
            s_lastTurnAt = now;
            return !kept;
        }

        /// <summary>Whether the pointer is inside the rect, as the game tests its slots (<c>InventoryGrid.GetHoveredElement</c>).</summary>
        private static bool Over(RectTransform rect, Vector3 pointer) => rect.rect.Contains(rect.InverseTransformPoint(pointer));
    }
}
