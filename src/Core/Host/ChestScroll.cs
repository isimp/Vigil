using System;
using System.Linq;
using System.Reflection;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "The wheel scrolls a chest grid with a bar": CycleChests switches to the next
    /// or previous chest whenever the mouse wheel turns while a chest is open, so a chest whose slots
    /// are taller than its view (the game's own scroll bar) cannot be scrolled. With the pointer over
    /// such a grid, CycleChests' input step is skipped and the wheel is left to the grid's scroll
    /// view; anywhere else the wheel cycles chests as before.
    /// </summary>
    internal static class ChestScroll
    {
        public const string Guid = "neobotics.valheim_mod.cyclechests";

        public const string TypeName = "neobotics.ValheimMods.CycleChests";

        private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>Whether CycleChests' input step is skipped this frame, so the wheel scrolls the grid.</summary>
        public static bool KeepsWheel(bool on, bool containerOpen, bool overGrid, bool hasBar) => on && containerOpen && overGrid && hasBar;

        /// <summary>The game's own rule for a grid that scrolls (<c>InventoryGrid.ResetView</c>): its slots are taller than its view.</summary>
        public static bool HasBar(float slotsHeight, float viewHeight) => slotsHeight > viewHeight;

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool found, string version, string problem, long kept)
        {
            if (!on) return "off: this game's switch (Patches.ChestGridScroll)";
            if (!found) return "not needed: CycleChests is not installed";
            if (problem != null) return "stepped aside: " + problem;
            string line = "working · CycleChests " + version + " · the wheel over a chest grid with a bar scrolls it";
            return kept > 0 ? line + " · " + kept.ToString(System.Globalization.CultureInfo.InvariantCulture) + " wheel turns kept in a chest" : line;
        }

        /// <summary>
        /// CycleChests' input step: <c>CycleChests.Player_Update_Patch.Prefix(Player)</c>, its prefix on
        /// <c>Player.Update</c> that reads the arrow keys and the wheel. Null when the shape differs.
        /// </summary>
        public static MethodInfo FindInputStep(Func<string, Type> type)
        {
            var outer = type(TypeName);
            var patch = outer?.GetNestedType("Player_Update_Patch", BindingFlags.Public | BindingFlags.NonPublic);
            return patch?.GetMethods(Static).FirstOrDefault(m => m.Name == "Prefix" && m.ReturnType == typeof(void)
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "Player");
        }
    }
}
