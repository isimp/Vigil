using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Wide containers fit the frame" (<see cref="ContainerFit"/>): a postfix on
    /// <c>InventoryGrid.UpdateInventory</c>, for the open container's grid only, shrinks the slot grid
    /// (<c>m_gridRoot</c>) evenly when its columns are wider than the grid's frame, then moves it
    /// sideways until its top row sits centred, measured from the first and last slot of that row.
    /// The game's pointer tests go through each slot's transform, so clicks, drags and tooltips follow
    /// the smaller slots; the scroll view measures its content through the same transforms. Nothing
    /// in the inventory changes. The switch is this game's own.
    /// </summary>
    internal static class ContainerFitPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.containerfit";

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        /// <summary>Containers opened shrunk since the start.</summary>
        public static long Fitted { get; private set; }

        public static string State => ContainerFit.StateLine(On, Problem, Fitted);

        private static AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> s_elements;
        private static Inventory s_lastFitted;
        private static readonly Vector3[] s_corners = new Vector3[4];

        public static void Hook()
        {
            try
            {
                var update = AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.UpdateInventory), new[] { typeof(Inventory), typeof(Player), typeof(ItemDrop.ItemData) });
                if (update == null || AccessTools.Field(typeof(InventoryGrid), "m_elements") == null)
                {
                    Problem = "the game's slot grid changed (an update)";
                    return;
                }
                s_elements = AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
                new Harmony(HarmonyId).Patch(update, postfix: new HarmonyMethod(typeof(ContainerFitPatch), nameof(AfterUpdateInventory)));
            }
            catch (Exception e)
            {
                Problem = "the game's slot grid could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static void AfterUpdateInventory(InventoryGrid __instance, Inventory inventory)
        {
            var gui = InventoryGui.instance;
            if (gui == null || inventory == null || __instance != gui.ContainerGrid) return;
            var root = __instance.m_gridRoot;
            if (root == null || !(__instance.transform is RectTransform view) || !(root.parent is RectTransform parent)) return;
            int width = inventory.GetWidth();
            float scale = On ? ContainerFit.Scale(width * __instance.m_elementSpace, view.rect.width) : 1f;
            if (scale >= 1f)
            {
                // Back to the game's own layout, which puts the slot grid at the frame's left edge.
                if (root.localScale.x != 1f)
                {
                    root.localScale = Vector3.one;
                    root.anchoredPosition = new Vector2(0f, root.anchoredPosition.y);
                }
                return;
            }
            if (root.localScale.x != scale) root.localScale = new Vector3(scale, scale, 1f);
            if (inventory != s_lastFitted)
            {
                s_lastFitted = inventory;
                Fitted++;
            }

            var elements = s_elements(__instance);
            if (elements == null || elements.Count < width || width <= 0) return;
            float left = Edge(elements[0], parent, right: false);
            float right = Edge(elements[width - 1], parent, right: true);
            float centre = parent.InverseTransformPoint(view.TransformPoint(view.rect.center)).x;
            float shift = ContainerFit.Shift(centre, left, right);
            if (shift != 0f) root.anchoredPosition = new Vector2(root.anchoredPosition.x + shift, root.anchoredPosition.y);
        }

        /// <summary>A slot's left or right edge in the slot grid's parent.</summary>
        private static float Edge(InventoryElement element, RectTransform parent, bool right)
        {
            element.GetElementRectTransform().GetWorldCorners(s_corners);
            // Corners run bottom-left, top-left, top-right, bottom-right.
            return parent.InverseTransformPoint(s_corners[right ? 2 : 0]).x;
        }
    }
}
