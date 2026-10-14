using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// How a connection ended, read from how it was closed and the last minute's traffic: the other
    /// side left (its game said goodbye), its line died (it went silent at once while it had been
    /// sending normally, and the data waiting for it piled up), its game froze (its traffic faded
    /// before it stopped), its game closed the connection without a goodbye, or this side closed it.
    /// The game's own timeout closes a connection after 30 s without an answer; Steam can give up
    /// on its own first, with its reason.
    /// </summary>
    internal sealed class DropAnalysis
    {
        /// <summary>One second of the connection, as the recorder keeps it.</summary>
        public struct Sample
        {
            public double At, InKbs, OutKbs, QueueKb, UnackedKb, PingMs, MaxGapMs, RateKbs, QualityLocal, QualityRemote;
        }

        public enum Ending { Goodbye, ClosedByPeer, SteamProblem, Timeout, ClosedHere }

        public enum Verdict { Left, LineDied, GameFroze, ClosedWithoutGoodbye, ClosedHere, Unclear }

        /// <summary>Incoming traffic below this counts as nothing.</summary>
        public const double SilentKbs = 0.5;

        /// <summary>The seconds before the silence compared with the ones before them.</summary>
        public const int RecentSeconds = 10, EarlierSeconds = 20;

        public Verdict Kind;
        public string Headline;
        public List<string> Lines = new List<string>();
        public double SilentSeconds = double.NaN;

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <param name="who">The other side's name ("the server" on a player's game).</param>
        /// <param name="whoIsServer">True on a player's game, where the other side is the server.</param>
        /// <param name="samples">The connection's seconds, oldest first, up to its end.</param>
        /// <param name="ending">How it was closed.</param>
        /// <param name="silentFor">How long nothing had arrived when it was closed, as the game measured it (0 when unknown).</param>
        /// <param name="steamReason">Steam's end reason and words, or null.</param>
        public static DropAnalysis Read(string who, bool whoIsServer, IReadOnlyList<Sample> samples, Ending ending, double silentFor, string steamReason)
        {
            var d = new DropAnalysis();
            string them = whoIsServer ? "the server" : who;
            string Who = whoIsServer ? "The server" : who;
            int last = -1;
            for (int i = samples.Count - 1; i >= 0; i--) if (samples[i].InKbs >= SilentKbs) { last = i; break; }
            double end = samples.Count > 0 ? samples[samples.Count - 1].At + 1 : 0;
            double silent = silentFor > 0 ? silentFor : last >= 0 ? end - (samples[last].At + 1) : double.NaN;
            d.SilentSeconds = silent;

            bool faded = false, known = last >= 0;
            if (known)
            {
                var recent = samples.Skip(Math.Max(0, last - RecentSeconds + 1)).Take(Math.Min(RecentSeconds, last + 1)).ToList();
                var earlier = samples.Take(Math.Max(0, last - RecentSeconds + 1)).Reverse().Take(EarlierSeconds).ToList();
                double recentIn = recent.Average(s => s.InKbs), recentGap = recent.Max(s => s.MaxGapMs);
                if (earlier.Count >= 5)
                {
                    double earlierIn = earlier.Average(s => s.InKbs), earlierGap = Median(earlier.Select(s => s.MaxGapMs));
                    faded = (earlierIn >= 2 && recentIn < 0.5 * earlierIn) || (recentGap >= 300 && recentGap >= 3 * earlierGap);
                    d.Lines.Add(string.Format(Ci, "In its last {0} s, {1} sent {2:0.0} kB/s (before that {3:0.0} kB/s), the longest pause between packets {4:0} ms (before that typically {5:0} ms).",
                        recent.Count, them, recentIn, earlierIn, recentGap, earlierGap));
                }
                else
                {
                    d.Lines.Add(string.Format(Ci, "In its last {0} s, {1} sent {2:0.0} kB/s, the longest pause between packets {3:0} ms.", recent.Count, them, recentIn, recentGap));
                }

                var lastSample = samples[samples.Count - 1];
                double grew = lastSample.QueueKb - samples[last].QueueKb;
                if (grew >= 5)
                    d.Lines.Add(string.Format(Ci, "Data waiting to reach {0} grew from {1:0} to {2:0} kB while nothing came back: nothing it was sent was confirmed.", them, samples[last].QueueKb, lastSample.QueueKb));
                if (!double.IsNaN(samples[last].PingMs)) d.Lines.Add(string.Format(Ci, "Ping before the silence: {0:0} ms.", samples[last].PingMs));
            }
            if (!string.IsNullOrEmpty(steamReason)) d.Lines.Add("Steam: " + steamReason + ".");

            string silence = double.IsNaN(silent) ? "" : string.Format(Ci, "{0:0} s", silent);
            switch (ending)
            {
                case Ending.Goodbye:
                    d.Kind = Verdict.Left;
                    d.Headline = whoIsServer ? "The server ended the session: it said goodbye (it shut down, restarted or let you go)" : who + " left: their game said goodbye";
                    break;
                case Ending.ClosedByPeer:
                    d.Kind = Verdict.ClosedWithoutGoodbye;
                    d.Headline = whoIsServer ? "The server closed the connection without a goodbye (it stopped hard or crashed)"
                        : who + "'s game closed the connection without a goodbye (closed hard or crashed)";
                    break;
                case Ending.ClosedHere:
                    d.Kind = Verdict.ClosedHere;
                    d.Headline = whoIsServer ? "Your game closed the connection to the server" : "This server closed " + who + "'s connection (a kick, a ban, a wrong password or version)";
                    break;
                default:
                    if (!known)
                    {
                        d.Kind = Verdict.Unclear;
                        d.Headline = Who + " went silent" + (silence.Length > 0 ? " for " + silence : "") + " and the connection timed out; too soon after joining to tell the line from the game";
                    }
                    else if (faded)
                    {
                        d.Kind = Verdict.GameFroze;
                        d.Headline = (whoIsServer ? "The server" : who + "'s game") + " slowed down and then went silent" + (silence.Length > 0 ? " for " + silence : "");
                        d.Lines.Add("Its traffic faded before it stopped, so " + (whoIsServer ? "the server" : "the game") + " slowed down first: a freeze, a crash or the machine itself, not the line.");
                    }
                    else
                    {
                        d.Kind = Verdict.LineDied;
                        d.Headline = Who + "'s connection died: nothing arrived for " + (silence.Length > 0 ? silence : "the last seconds") + ", all at once";
                        d.Lines.Add("A line that dies stops everything at once, and " + them + " was sending normally until that second. That points at the line (a router or the provider, such as a daily forced reconnect), not at the game.");
                    }
                    break;
            }
            return d;
        }

        /// <summary>The connection's last seconds as a table, one row a second, seconds counted back from the end.</summary>
        public static List<string> Table(IReadOnlyList<Sample> samples)
        {
            var lines = new List<string> { "   t (s)   in kB/s  out kB/s  queue kB  unconfirmed kB  ping ms  longest pause ms" };
            if (samples.Count == 0) return lines;
            double end = samples[samples.Count - 1].At + 1;
            foreach (var s in samples)
                lines.Add(string.Format(Ci, "   {0,5:0}  {1,8:0.0}  {2,8:0.0}  {3,8:0.0}  {4,14:0.0}  {5,7:0}  {6,16:0}", s.At - end, s.InKbs, s.OutKbs, s.QueueKb, s.UnackedKb, s.PingMs, s.MaxGapMs));
            return lines;
        }

        private static double Median(IEnumerable<double> values)
        {
            var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
            return v.Count == 0 ? double.NaN : v[v.Count / 2];
        }
    }
}
