using System;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "Wide containers fit the frame": the game lays a container's slots out centred
    /// in the chest frame and scrolls it only up and down, so a grid wider than the frame (a tombstone
    /// ExtraSlots widened to hold its equipped items where they were) loses both sides, out of sight
    /// and out of reach. Such a grid is shrunk evenly until every column fits and centred again; a grid
    /// that fits keeps its size. Only the drawing changes: the inventory and its slot positions stay.
    /// </summary>
    internal static class ContainerFit
    {
        /// <summary>The scale that makes the slots fit the frame's width: 1 when they fit already.</summary>
        public static float Scale(float slotsWidth, float viewWidth)
        {
            if (viewWidth <= 0f || slotsWidth <= viewWidth) return 1f;
            return viewWidth / slotsWidth;
        }

        /// <summary>How far the slots move sideways to sit centred in the frame; 0 within half a pixel.</summary>
        public static float Shift(float viewCentre, float slotsLeft, float slotsRight)
        {
            float shift = viewCentre - (slotsLeft + slotsRight) / 2f;
            return Math.Abs(shift) > 0.5f ? shift : 0f;
        }

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, string problem, long fitted)
        {
            if (!on) return "off: this game's switch (Patches.WideContainerFit)";
            if (problem != null) return "stepped aside: " + problem;
            string line = "working · a container wider than its frame is shrunk until every column fits";
            return fitted > 0 ? line + " · " + fitted.ToString(System.Globalization.CultureInfo.InvariantCulture) + " shown shrunk" : line;
        }
    }
}
