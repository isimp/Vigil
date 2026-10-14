using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// Where the panel was left, which tab was open and which cards and results were folded, kept
    /// as one line ("x,y,width,height,tab,fold|fold") in Vigil's own folder on this machine.
    /// <see cref="Clamped"/> puts it fully on the screen at hand, shrinking it to fit a smaller
    /// screen and growing it to its smallest usable size; with nothing remembered, or a damaged
    /// line, it opens centred on the overview.
    /// </summary>
    internal sealed class PanelPlacement
    {
        public const int DefaultWidth = 760, DefaultHeight = 520, MinWidth = 480, MinHeight = 320;

        /// <summary>X and Y are this until known; <see cref="Clamped"/> then centres the panel.</summary>
        private const int Unknown = int.MinValue;

        public int X = Unknown, Y = Unknown, Width = DefaultWidth, Height = DefaultHeight;

        public string Tab = "Overview";

        /// <summary>The folded cards and results, each by its name ("card:Tools/Inspect", "result:load").</summary>
        public HashSet<string> Folds = new HashSet<string>(StringComparer.Ordinal);

        public static PanelPlacement Parse(string text)
        {
            var p = new PanelPlacement();
            var parts = (text ?? "").Split(',');
            if (parts.Length < 5 || !Int(parts[0], out int x) || !Int(parts[1], out int y) || !Int(parts[2], out int w) || !Int(parts[3], out int h)) return p;
            p.X = x;
            p.Y = y;
            p.Width = w;
            p.Height = h;
            if (parts[4].Trim().Length > 0) p.Tab = parts[4].Trim();
            if (parts.Length > 5)
            {
                foreach (string f in parts[5].Trim().Split('|'))
                    if (f.Length > 0) p.Folds.Add(Unescape(f));
            }
            return p;
        }

        public string Format() => string.Join(",", new[]
        {
            Text(X), Text(Y), Text(Width), Text(Height), Tab, string.Join("|", Folds.OrderBy(f => f, StringComparer.Ordinal).Select(Escape).ToArray()),
        });

        public PanelPlacement Clamped(int screenWidth, int screenHeight)
        {
            int w = Math.Min(Math.Max(Width, MinWidth), Math.Max(screenWidth, 1));
            int h = Math.Min(Math.Max(Height, MinHeight), Math.Max(screenHeight, 1));
            int x = X == Unknown ? (screenWidth - w) / 2 : Math.Min(Math.Max(X, 0), screenWidth - w);
            int y = Y == Unknown ? (screenHeight - h) / 2 : Math.Min(Math.Max(Y, 0), screenHeight - h);
            return new PanelPlacement { X = Math.Max(x, 0), Y = Math.Max(y, 0), Width = w, Height = h, Tab = Tab, Folds = Folds };
        }

        /// <summary>
        /// The panel at the size its corner was dragged to, its top left where it was: no smaller
        /// than its smallest usable size, and no further than the screen's right and bottom edges.
        /// </summary>
        public PanelPlacement Resized(int width, int height, int screenWidth, int screenHeight)
        {
            int w = Math.Max(Math.Min(width, screenWidth - X), MinWidth);
            int h = Math.Max(Math.Min(height, screenHeight - Y), MinHeight);
            return new PanelPlacement { X = X, Y = Y, Width = w, Height = h, Tab = Tab, Folds = Folds };
        }

        /// <summary>A fold's name can hold the line's own separators (a card's title); they are written as %2C and %7C.</summary>
        private static string Escape(string s) => s.Replace("%", "%25").Replace(",", "%2C").Replace("|", "%7C");

        private static string Unescape(string s) => s.Replace("%7C", "|").Replace("%2C", ",").Replace("%25", "%");

        private static bool Int(string s, out int v) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        private static string Text(int v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
