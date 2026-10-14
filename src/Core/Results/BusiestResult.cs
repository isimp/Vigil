using System;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// "Busiest objects" as the panel shows it: what the changes around the player take to send at
    /// most, the heaviest type and the game that runs most of it, tiles, bars by type and by game,
    /// and the busiest objects one by one.
    /// </summary>
    internal static class BusiestResult
    {
        private const int Listed = 40, Bars = 8;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly string[] Headers = { "Object", "Run by", "Changes/s", "Size", "kB/s at most", "Away", "Moved" };
        private static readonly bool[] Right = { false, false, true, true, true, true, false };

        public static Result Build(DateTime at, BusiestObjects.Report report, float radius)
        {
            string within = radius > 0 ? "within " + radius.ToString("0", Ci) + " m" : "loaded";
            var r = new Result("Busiest objects", at);
            if (report.Changed == 0)
            {
                r.Badge = "nothing changed";
                r.BadgeTone = ResultTone.Good;
                r.Add(new VerdictBlock(ResultTone.Good, string.Format(Ci, "Nothing of the {0} {1} {2} changed in {3:0} s.",
                    report.Watched, report.Watched == 1 ? "object" : "objects", within, report.Seconds)));
                return r;
            }

            r.Badge = "about " + Kb(report.TotalBytesPerSecond) + " kB/s";
            r.BadgeTone = ResultTone.Quiet;
            var top = report.Types[0];
            string game = report.Objects.Where(o => o.Type == top.Name).GroupBy(o => o.Owner)
                .OrderByDescending(g => g.Sum(o => o.BytesPerSecond)).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
            r.Add(new VerdictBlock(ResultTone.Quiet, string.Format(Ci,
                "In {0:0} s {1}, {2} of {3} objects changed: about {4} kB/s if every change went out. Most of it is {5} ({6} {7}, {8} kB/s), {9}.",
                report.Seconds, within, report.Changed, report.Watched, Kb(report.TotalBytesPerSecond),
                top.Name, top.Objects, top.Objects == 1 ? "object" : "objects", Kb(top.BytesPerSecond), RunBy(game))));
            r.Add(new TilesBlock())
                .Add("Watched", report.Watched.ToString(Ci))
                .Add("Changed", report.Changed.ToString(Ci))
                .Add("Came or went", report.CameOrWent.ToString(Ci))
                .Add("kB/s at most", Kb(report.TotalBytesPerSecond));
            var types = r.Add(new BarsBlock("By type"));
            foreach (var t in report.Types.Take(Bars)) types.Add(t.Name, t.BytesPerSecond, Group(t));
            var games = r.Add(new BarsBlock("By the game that runs them"));
            foreach (var g in report.Games.Take(Bars)) games.Add(g.Name, g.BytesPerSecond, Group(g));
            var table = r.Add(new TableBlock("Busiest objects", Headers, Right));
            foreach (var o in report.Objects.Take(Listed))
            {
                table.Rows.Add(new[]
                {
                    o.Type, o.Owner, o.PerSecond.ToString("0.0", Ci), o.SizeBytes.ToString(Ci) + " B", Kb(o.BytesPerSecond),
                    o.Distance.ToString("0", Ci) + " m", o.Moved ? "moved" : "",
                });
            }

            r.Add(new TextBlock(null, "A changed object is sent with all its values, at most 20 times a second; moving and turning count as changes. "
                + "The kB/s are what that takes at most, to one receiver."));
            return r;
        }

        private static string Group(BusiestObjects.Group g) =>
            string.Format(Ci, "{0} {1}, {2} kB/s", g.Objects, g.Objects == 1 ? "object" : "objects", Kb(g.BytesPerSecond));

        private static string RunBy(string game) => game switch
        {
            "You" => "run by your game",
            "The server" => "run by the server",
            "Nobody" => "run by no game",
            _ => "run by " + game + "'s game",
        };

        private static string Kb(double bytesPerSecond) => (bytesPerSecond / 1024.0).ToString("0.0", Ci);
    }
}
