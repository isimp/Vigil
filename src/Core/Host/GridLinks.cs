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
    /// its neighbours, down from a row above a short last row reaches the last button, and the
    /// grid's edge links take over at the sides. Where an edge link is empty, a grid the game lets
    /// wrap (the piece buttons) leads past a row's end into the next row and back.
    /// </summary>
    internal static class GridLinks
    {
        /// <summary>
        /// A button's neighbours by index among the shown buttons; -1 where the grid's edge link
        /// takes over. <see cref="WrapLeft"/> and <see cref="WrapRight"/> are the buttons a
        /// wrapping grid leads to at a row's ends when the edge link there is empty, -1 for none.
        /// </summary>
        public struct Links : IEquatable<Links>
        {
            public readonly int Left, Right, Up, Down, WrapLeft, WrapRight;
            public Links(int left, int right, int up, int down) : this(left, right, up, down, -1, -1) { }
            public Links(int left, int right, int up, int down, int wrapLeft, int wrapRight)
            {
                Left = left; Right = right; Up = up; Down = down; WrapLeft = wrapLeft; WrapRight = wrapRight;
            }
            public bool Equals(Links o) => Left == o.Left && Right == o.Right && Up == o.Up && Down == o.Down && WrapLeft == o.WrapLeft && WrapRight == o.WrapRight;
            public override bool Equals(object obj) => obj is Links l && Equals(l);
            public override int GetHashCode() => ((((Left * 31 + Right) * 31 + Up) * 31 + Down) * 31 + WrapLeft) * 31 + WrapRight;
            public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2}, {3}, wrap {4}, {5})", Left, Right, Up, Down, WrapLeft, WrapRight);
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

        /// <summary>
        /// Button <paramref name="i"/>'s links among <paramref name="count"/> shown buttons in
        /// <paramref name="columns"/> columns; <paramref name="wrap"/> for a grid the game lets
        /// wrap. A wrap never leads back to the first button, as in the game.
        /// </summary>
        public static Links Neighbours(int i, int count, int columns, bool wrap)
        {
            if (columns <= 0) columns = 1;
            int row = i / columns, col = i % columns;
            int down = i + columns < count ? i + columns
                : row < (count - 1) / columns ? count - 1
                : -1;
            bool left = col > 0, right = col < columns - 1 && i + 1 < count;
            return new Links(
                left ? i - 1 : -1,
                right ? i + 1 : -1,
                row > 0 ? i - columns : -1,
                down,
                !left && wrap && i - 1 > 0 && i - 1 < count ? i - 1 : -1,
                !right && wrap && i + 1 < count ? i + 1 : -1);
        }

        /// <summary>
        /// One side's link: the neighbour, else the grid's edge link where <paramref name="edgeSet"/>
        /// (the caller judges that, as an engine object can read as gone without being a null
        /// reference), else the wrap, else none.
        /// </summary>
        public static T Side<T>(int neighbour, T edge, bool edgeSet, int wrap, Func<int, T> button) where T : class
        {
            if (neighbour >= 0) return button(neighbour);
            if (edgeSet) return edge;
            return wrap >= 0 ? button(wrap) : null;
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
