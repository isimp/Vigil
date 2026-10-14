using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// A connection's last minute as the panel shows it: its latest ping, connection quality both
    /// ways, Steam's send rate and the data waiting or unconfirmed, then a column a second for the
    /// ping, the waiting and unconfirmed data, the longest pause between packets and the incoming
    /// traffic.
    /// </summary>
    internal static class LinkResult
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static Result Build(string name, IReadOnlyList<DropAnalysis.Sample> samples, DateTime at)
        {
            var r = new Result(name, at);
            if (samples.Count == 0)
            {
                r.Add(new TextBlock(null, "Nothing measured yet: the last minute fills while Vigil records."));
                return r;
            }

            var now = samples[samples.Count - 1];
            r.Badge = "ping " + Num(now.PingMs, "0") + " ms";
            r.BadgeTone = !double.IsNaN(now.PingMs) && now.PingMs >= 300 ? ResultTone.Bad : !double.IsNaN(now.PingMs) && now.PingMs >= 150 ? ResultTone.Caution : ResultTone.Quiet;
            r.Add(new TilesBlock())
                .Add("Ping", Num(now.PingMs, "0") + " ms")
                .Add("Quality here / there", Num(now.QualityLocal, "0.00") + " / " + Num(now.QualityRemote, "0.00"))
                .Add("Steam's send rate", Num(now.RateKbs, "0") + " kB/s")
                .Add("Waiting", Num(now.QueueKb, "0.0") + " kB")
                .Add("Unconfirmed", Num(now.UnackedKb, "0.0") + " kB");
            string left = "-" + samples.Count.ToString(Ci) + " s";
            r.Add(new StripBlock("Ping, each second", samples.Select(s => s.PingMs).ToList(), 150, 300, "ms", left, "now"));
            r.Add(new StripBlock("Waiting to go out, each second", samples.Select(s => s.QueueKb).ToList(), 16, 32, "kB", left, "now"));
            r.Add(new StripBlock("Sent, not confirmed, each second", samples.Select(s => s.UnackedKb).ToList(), 16, 32, "kB", left, "now"));
            r.Add(new StripBlock("Longest pause between packets, each second", samples.Select(s => s.MaxGapMs).ToList(), 300, 1000, "ms", left, "now"));
            r.Add(new StripBlock("Coming in, each second", samples.Select(s => s.InKbs).ToList(), double.PositiveInfinity, double.PositiveInfinity, "kB/s", left, "now"));
            return r;
        }

        private static string Num(double v, string format) => double.IsNaN(v) ? "-" : v.ToString(format, Ci);
    }
}
