using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Vigil's own start, timed step by step: each <see cref="Lap"/> names the work done since the
    /// previous one. The steps go into the startup report as "vigil-step" lines, so Vigil's share of the
    /// start is split into the parts that took it.
    /// </summary>
    internal static class StartSteps
    {
        private static readonly Stopwatch Clock = new Stopwatch();
        private static readonly List<KeyValuePair<string, double>> Steps = new List<KeyValuePair<string, double>>();
        private static double s_last;

        /// <summary>Starts (or starts again, for the first frame's work) the clock the next laps are read from.</summary>
        public static void Begin()
        {
            Clock.Reset();
            Clock.Start();
            s_last = 0;
        }

        public static void Lap(string name)
        {
            if (!Clock.IsRunning) return;
            double now = Clock.Elapsed.TotalMilliseconds;
            Steps.Add(new KeyValuePair<string, double>(name, now - s_last));
            s_last = now;
        }

        public static void End() => Clock.Stop();

        public static List<StartupEvent> Events() =>
            Steps.Select(s => new StartupEvent(0, StartupLines.VigilStep, s.Key + "|" + s.Value.ToString("0.0", CultureInfo.InvariantCulture))).ToList();
    }
}
