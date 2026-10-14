using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The HUD line: frame rate, frame time, ping, data waiting to go out, machine memory, the
    /// server's frame rate and whether the recorder records, each with a level. A value turns
    /// <see cref="Level.Warn"/> as it nears the limit that writes an incident report and
    /// <see cref="Level.Bad"/> at it; a value not measured (NaN) is left out.
    /// </summary>
    internal static class HudLine
    {
        internal enum Level { Fine, Warn, Bad }

        internal sealed class Values
        {
            public double Fps = double.NaN, FrameAvgMs = double.NaN, FrameMaxMs = double.NaN, PingMs = double.NaN, QueueKb = double.NaN,
                MemLoadPct = double.NaN, ServerFps = double.NaN;
            public bool Recording;

            /// <summary>An admin's new alerts and the worst of them; 0 leaves the count out.</summary>
            public int Alerts;
            public AlertLevel AlertWorst;
        }

        /// <summary>The recorder's incident limits the levels follow.</summary>
        internal sealed class Limits
        {
            public double SlowFps, PingMs, QueueKb, MemoryLoadPct;
        }

        internal readonly struct Segment
        {
            public Segment(string text, Level level)
            {
                Text = text;
                Level = level;
            }

            public string Text { get; }

            public Level Level { get; }
        }

        /// <summary>A dedicated server runs at 30 frames a second; below these it falls behind.</summary>
        private const double ServerWarnFps = 25, ServerBadFps = 15;

        /// <summary>Machine memory this many points below its report limit is amber.</summary>
        private const double MemoryMargin = 5;

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static List<Segment> Build(Values v, Limits limits)
        {
            var s = new List<Segment>();
            if (Known(v.Fps)) s.Add(new Segment(v.Fps.ToString("0", Ci) + " fps", Below(v.Fps, limits.SlowFps, limits.SlowFps * 1.5)));
            if (Known(v.FrameAvgMs)) s.Add(new Segment(v.FrameAvgMs.ToString("0", Ci) + " ms", Level.Fine));
            if (Known(v.PingMs)) s.Add(new Segment("ping " + v.PingMs.ToString("0", Ci), AtOrAbove(v.PingMs, limits.PingMs, limits.PingMs / 2)));
            if (Known(v.QueueKb)) s.Add(new Segment("queue " + v.QueueKb.ToString("0.0", Ci) + " kB", AtOrAbove(v.QueueKb, limits.QueueKb, limits.QueueKb / 2)));
            if (Known(v.MemLoadPct))
                s.Add(new Segment("mem " + v.MemLoadPct.ToString("0", Ci) + "%", AtOrAbove(v.MemLoadPct, limits.MemoryLoadPct, limits.MemoryLoadPct - MemoryMargin)));
            if (Known(v.ServerFps)) s.Add(new Segment("server " + v.ServerFps.ToString("0", Ci) + " fps", Below(v.ServerFps, ServerBadFps, ServerWarnFps)));
            if (v.Alerts > 0)
                s.Add(new Segment(v.Alerts.ToString(Ci) + (v.Alerts == 1 ? " alert" : " alerts"),
                    v.AlertWorst == AlertLevel.Bad ? Level.Bad : v.AlertWorst == AlertLevel.Warn ? Level.Warn : Level.Fine));
            s.Add(v.Recording ? new Segment("REC", Level.Fine) : new Segment("paused", Level.Warn));
            return s;
        }

        private static bool Known(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

        private static Level Below(double value, double bad, double warn) => value < bad ? Level.Bad : value < warn ? Level.Warn : Level.Fine;

        internal static Level AtOrAbove(double value, double bad, double warn) => value >= bad ? Level.Bad : value >= warn ? Level.Warn : Level.Fine;
    }
}
