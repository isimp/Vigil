using System.Linq;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// What Vigil's plugin reads from the patcher and asks of it, in plain values only. The plugin finds
    /// this class by name at run time rather than referencing the patcher's assembly, so Vigil.dll still
    /// loads when the patcher is not installed.
    /// </summary>
    public static class Bridge
    {
        /// <summary>The steps recorded so far, one <see cref="StartupEvent.Line"/> each.</summary>
        public static string[] Events() => StartupState.EventLines();

        /// <summary>Adds a step Vigil saw itself (the menu, a join), on the patcher's clock.</summary>
        public static void Mark(string kind, string text) => StartupState.Add(kind, text);

        /// <summary>Stops listening to the log: the start is over.</summary>
        public static void StopTimeline() => StartupState.StopTimeline();

        public static bool TimelineOn() => StartupState.TimelineOn;

        public static bool CacheOn() => StartupState.CacheOn;

        public static bool Server() => StartupState.Server;

        /// <summary>"edited", "off", "not run", or why Unity's bundle loading was left as it is.</summary>
        public static string EditResult() => StartupState.EditResult;

        public static string HousekeepingResult() => StartupState.HousekeepingResult;

        public static string CacheDir() => StartupState.CacheDir;

        public static string Status() => StartupState.Status();

        /// <summary>Loads that asked the cache, copies made, failed and running, copies loaded and refused: seven counts in that order.</summary>
        public static int[] Counts() => new[]
        {
            StartupState.Asked, StartupState.ConversionsStarted, StartupState.ConversionsDone, StartupState.ConversionsFailed,
            StartupState.ConversionsStarted - StartupState.ConversionsDone - StartupState.ConversionsFailed, StartupState.CopiesLoaded, StartupState.CopiesRefused,
        };

        /// <summary>The cache's index as its file holds it (<see cref="BundleCacheIndex.Format"/>).</summary>
        public static string IndexText() => BundleCacheIndex.Format(StartupState.Entries().OrderBy(e => e.Key, System.StringComparer.Ordinal));

        public static void SaveIndex() => StartupState.SaveIndex();

        public static string RequestReset() => StartupState.RequestReset();

        public static bool ResetRequested() => StartupState.ResetRequested();

        /// <summary>Whether this start emptied the cache because a reset was asked for.</summary>
        public static bool ResetDone() => StartupState.ResetDone;
    }
}
