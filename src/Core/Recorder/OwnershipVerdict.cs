using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// When a line of "Who runs what" is worth a look: a game on a slow connection that runs moving
    /// things (creatures, ships, carts), which everyone else then sees late. Slow is the HUD line's
    /// amber from half the ping report limit, red at the limit. The server and objects no game runs
    /// get no verdict, and neither does a game whose ping is not known (it comes from the server's
    /// status, which only admins get).
    /// </summary>
    internal static class OwnershipVerdict
    {
        public sealed class Verdict
        {
            public Verdict(ResultTone tone, string text)
            {
                Tone = tone;
                Text = text;
            }

            public ResultTone Tone { get; }

            public string Text { get; }
        }

        public static Verdict Of(OwnershipCount.Row row, double pingMs, double pingLimitMs)
        {
            if (row == null || row.Role == OwnerRole.Nobody || row.Role == OwnerRole.Server || row.Moving == 0 || double.IsNaN(pingMs) || pingMs < pingLimitMs / 2)
            {
                return null;
            }

            string who = row.Role == OwnerRole.You ? "Your game" : row.Name + "'s game";
            string text = string.Format(CultureInfo.InvariantCulture,
                "{0} runs {1} moving {2} here on a slow connection (ping {3:0} ms), so everyone else sees them late.",
                who, row.Moving, row.Moving == 1 ? "thing" : "things", pingMs);
            return new Verdict(pingMs >= pingLimitMs ? ResultTone.Bad : ResultTone.Caution, text);
        }

        /// <summary>
        /// The card's badge: the first game worth a look, else the game that runs the most moving
        /// things (not nobody); none when nothing around moves.
        /// </summary>
        public static Verdict Badge(IList<OwnershipCount.Row> rows, Func<OwnershipCount.Row, Verdict> verdictOf)
        {
            foreach (var row in rows)
            {
                var v = verdictOf(row);
                if (v != null)
                {
                    return new Verdict(v.Tone, row.Name + ": " + row.Moving.ToString(CultureInfo.InvariantCulture) + " moving");
                }
            }

            var busiest = rows.Where(r => r.Owner != 0 && r.Moving > 0).OrderByDescending(r => r.Moving).FirstOrDefault();
            return busiest == null ? null : new Verdict(ResultTone.Quiet, "most: " + busiest.Name);
        }
    }
}
