using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "The build menu sets up its navigation a frame later": the build menu links
    /// its buttons for the arrow keys and a controller after every open and every change of the
    /// search text, and before linking it forces every canvas to update twice and lays the whole
    /// button grid out again. With the patch on, the linking waits for the frame after the request,
    /// when the screen layout has placed the buttons anyway, and follows the game's rules without
    /// forcing anything: the columns are the shown buttons on the first row, each button links to
    /// its neighbours, and the grid's edge links take over at the sides.
    /// </summary>
    internal static class GridLinks
    {
        /// <summary>A button's neighbours by index among the shown buttons; -1 where the grid's edge link takes over.</summary>
        public struct Links : IEquatable<Links>
        {
            public readonly int Left, Right, Up, Down;
            public Links(int left, int right, int up, int down) { Left = left; Right = right; Up = up; Down = down; }
            public bool Equals(Links o) => Left == o.Left && Right == o.Right && Up == o.Up && Down == o.Down;
            public override bool Equals(object obj) => obj is Links l && Equals(l);
            public override int GetHashCode() => ((Left * 31 + Right) * 31 + Up) * 31 + Down;
            public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2}, {3})", Left, Right, Up, Down);
        }

        /// <summary>Rows within this distance count as one, as in the game.</summary>
        private const float SameRow = 0.01f;

        /// <summary>The grid's columns: the shown buttons, in order, that sit on the first one's row (at least 1; 0 without buttons).</summary>
        public static int Columns(IList<float> rowYs)
        {
            if (rowYs == null || rowYs.Count == 0) return 0;
            int n = 0;
            foreach (float y in rowYs)
            {
                if (Math.Abs(y - rowYs[0]) > SameRow) break;
                n++;
            }
            return Math.Max(1, n);
        }

        public static Links Neighbours(int i, int count, int columns)
        {
            if (columns <= 0) columns = 1;
            int row = i / columns, col = i % columns;
            return new Links(
                col > 0 ? i - 1 : -1,
                col < columns - 1 && i + 1 < count ? i + 1 : -1,
                row > 0 ? i - columns : -1,
                i + columns < count ? i + columns : -1);
        }

        /// <summary>A request made in <paramref name="requestFrame"/> (-1 for none) is linked once a later frame has come.</summary>
        public static bool Due(int requestFrame, int now) => requestFrame >= 0 && now > requestFrame;

        public static string StateLine(bool on, string problem, long putOff, double lastMs, int lastButtons)
        {
            if (!on) return "off: this game's switch (Patches.BuildMenuNavigationLater)";
            if (problem != null) return "stepped aside: " + problem;
            if (putOff == 0) return "working · nothing put off yet";
            return string.Format(CultureInfo.InvariantCulture, "working · {0:N0} navigation setups put off to the next frame · the last linked {1:N0} buttons in {2:0.0} ms",
                putOff, lastButtons, lastMs);
        }
    }
}
