using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>One sender of a message that keeps coming: whose message, what, from whom, how often and how much, and in how many of the windows looked at.</summary>
    internal sealed class SteadyRow
    {
        public string Mod, Message, From;
        public double PerSecond, KbPerMinute;
        public int Seen, Of;
    }

    /// <summary>
    /// The messages that keep coming at a fixed rate whatever the players do, per sender. Messages are
    /// counted in the same 10 s windows as the traffic report; a sender is steady once it has been
    /// looked at for <see cref="LeastWindows"/> windows (a minute) and its rate per window over the last
    /// <see cref="Windows"/> (12 minutes) swings by no more than <see cref="MostSpread"/> of its mean,
    /// the windows without it counting as nothing. With a rate the same whenever it comes, that means
    /// it comes in at least 4 of 5 windows; a rate that varies has to come more often. It cannot tell
    /// whether a mod needs to send so often.
    /// </summary>
    internal sealed class SteadyMessages
    {
        public const int Windows = 72, LeastWindows = 6;
        public const double MostSpread = 0.5;

        // The windows kept and the one in progress.
        private const int Ring = Windows + 1;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        private sealed class Track
        {
            public string Mod, Message, From;
            public int First;
            public readonly int[] Counts = new int[Ring];
            public readonly long[] Bytes = new long[Ring];
        }

        private readonly Dictionary<string, Track> _tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        private readonly float[] _seconds = new float[Ring];
        private int _window;

        /// <summary>How many senders are kept: those heard from in the windows kept.</summary>
        public int Tracked => _tracks.Count;

        /// <summary>One message in the window in progress.</summary>
        public void Add(string mod, string message, string from, int bytes)
        {
            string key = mod + "\n" + message + "\n" + from;
            if (!_tracks.TryGetValue(key, out var t)) _tracks[key] = t = new Track { Mod = mod, Message = message, From = from, First = _window };
            int slot = _window % Ring;
            t.Counts[slot]++;
            t.Bytes[slot] += bytes;
        }

        /// <summary>The window in progress ended after <paramref name="seconds"/>; senders not heard from in the windows kept are forgotten.</summary>
        public void EndWindow(float seconds)
        {
            _seconds[_window % Ring] = seconds;
            _window++;
            int slot = _window % Ring;
            var gone = new List<string>();
            foreach (var kv in _tracks)
            {
                kv.Value.Counts[slot] = 0;
                kv.Value.Bytes[slot] = 0;
                if (kv.Value.Counts.All(c => c == 0)) gone.Add(kv.Key);
            }
            foreach (var key in gone) _tracks.Remove(key);
        }

        /// <summary>The steady senders, the most frequent first.</summary>
        public List<SteadyRow> Steady()
        {
            var rows = new List<SteadyRow>();
            foreach (var t in _tracks.Values)
            {
                int of = Math.Min(Windows, _window - t.First);
                if (of < LeastWindows) continue;
                int seen = 0;
                long count = 0, bytes = 0;
                double seconds = 0;
                var rates = new List<double>();
                for (int w = _window - of; w < _window; w++)
                {
                    int slot = w % Ring;
                    int c = t.Counts[slot];
                    float s = _seconds[slot] > 0 ? _seconds[slot] : 10f;
                    if (c > 0) seen++;
                    count += c;
                    bytes += t.Bytes[slot];
                    seconds += s;
                    rates.Add(c / s);
                }
                double mean = rates.Average();
                double spread = Math.Sqrt(rates.Sum(r => (r - mean) * (r - mean)) / rates.Count);
                if (mean <= 0 || spread > MostSpread * mean) continue;
                rows.Add(new SteadyRow
                {
                    Mod = t.Mod, Message = t.Message, From = t.From, PerSecond = count / seconds, KbPerMinute = bytes / 1024.0 / (seconds / 60.0), Seen = seen, Of = of,
                });
            }
            return rows.OrderByDescending(r => r.PerSecond).ThenBy(r => r.Message, StringComparer.Ordinal).ToList();
        }

        /// <summary>"Shield Sledding · ShieldSledding_State   from Olebole   5.5/s   16.1 kB/min   72 of 72 windows".</summary>
        public static string Line(SteadyRow r) =>
            r.Mod + " · " + r.Message + "   from " + r.From + "   " + r.PerSecond.ToString("0.0", Ci) + "/s   " + r.KbPerMinute.ToString("0.0", Ci) + " kB/min   "
            + r.Seen.ToString(Ci) + " of " + r.Of.ToString(Ci) + " windows";

        public static Dictionary<string, object> EncodeRows(IEnumerable<SteadyRow> rows) => new Dictionary<string, object>
        {
            { "rows", string.Join("\n", rows.Select(r => string.Join("\t", new[]
                {
                    Clean(r.Mod), Clean(r.Message), Clean(r.From), r.PerSecond.ToString("0.###", Ci), r.KbPerMinute.ToString("0.###", Ci), r.Seen.ToString(Ci), r.Of.ToString(Ci),
                })).ToArray()) },
        };

        public static List<SteadyRow> DecodeRows(IDictionary<string, string> d)
        {
            var rows = new List<SteadyRow>();
            if (!d.TryGetValue("rows", out string text) || string.IsNullOrEmpty(text)) return rows;
            foreach (var line in text.Split('\n'))
            {
                var v = line.Split('\t');
                if (v.Length < 7) continue;
                rows.Add(new SteadyRow { Mod = v[0], Message = v[1], From = v[2], PerSecond = Number(v[3]), KbPerMinute = Number(v[4]), Seen = (int)Number(v[5]), Of = (int)Number(v[6]) });
            }
            return rows;
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ');

        private static double Number(string s) => double.TryParse(s, NumberStyles.Float, Ci, out double v) ? v : 0;
    }
}
