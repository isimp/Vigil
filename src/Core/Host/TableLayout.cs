using System;
using System.Collections.Generic;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// How the panel's tables share their width: every column first gets the width of its widest
    /// cell or heading; room to spare goes to the column meant to take the rest (else the last). When
    /// the room is short, numbers keep their width and the text columns give way, the widest first,
    /// down to a floor (their own width when that is less); a text cut short ends in "…".
    /// </summary>
    internal static class TableLayout
    {
        public const string Ellipsis = "…";

        /// <summary>A column's widest content, whether it takes the room to spare, and whether it holds numbers.</summary>
        public readonly struct Col
        {
            public Col(float needed, bool flexible, bool number)
            {
                Needed = needed;
                Flexible = flexible;
                Number = number;
            }

            public float Needed { get; }
            public bool Flexible { get; }
            public bool Number { get; }
        }

        /// <summary>The widths for <paramref name="available"/> (the row less the gaps between columns).</summary>
        public static float[] Widths(float available, IList<Col> cols, float minText)
        {
            var widths = cols.Select(c => Math.Max(0f, c.Needed)).ToArray();
            float total = widths.Sum();
            if (total <= available)
            {
                int take = Enumerable.Range(0, cols.Count).Where(i => cols[i].Flexible).DefaultIfEmpty(cols.Count - 1).First();
                if (take >= 0) widths[take] += available - total;
                return widths;
            }

            var texts = Enumerable.Range(0, cols.Count).Where(i => !cols[i].Number).ToList();
            float numbers = Enumerable.Range(0, cols.Count).Where(i => cols[i].Number).Sum(i => widths[i]);
            float room = available - numbers;
            float Floor(int i) => Math.Min(widths[i], minText);
            float At(float cap) => texts.Sum(i => Math.Min(widths[i], Math.Max(cap, Floor(i))));
            if (At(0f) >= room)
            {
                foreach (int i in texts) widths[i] = Floor(i);
                return widths;
            }

            // The widest text columns give way first: find the cap that leaves exactly the room there is.
            float low = 0f, high = texts.Max(i => widths[i]);
            for (int step = 0; step < 40; step++)
            {
                float mid = (low + high) / 2f;
                if (At(mid) > room) high = mid;
                else low = mid;
            }
            float cap = (float)Math.Round(low, 2);
            foreach (int i in texts) widths[i] = Math.Min(widths[i], Math.Max(cap, Floor(i)));
            return widths;
        }

        /// <summary>The text as it fits <paramref name="width"/>: whole, or cut and ended with "…"; rich text is left whole.</summary>
        public static string Fit(string text, float width, Func<string, float> measure)
        {
            if (string.IsNullOrEmpty(text) || measure(text) <= width || text.IndexOf('<') >= 0) return text;
            int low = 0, high = text.Length - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (measure(text.Substring(0, mid) + Ellipsis) <= width) low = mid;
                else high = mid - 1;
            }
            return text.Substring(0, low).TrimEnd() + Ellipsis;
        }
    }
}
