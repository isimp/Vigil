using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>What a mod's join packages were built from: the world session, the world's location count and the mod's config reloads.</summary>
    internal readonly struct JoinStamp : IEquatable<JoinStamp>
    {
        public readonly int World, Locations, Reloads;

        public JoinStamp(int world, int locations, int reloads) { World = world; Locations = locations; Reloads = reloads; }

        public bool Equals(JoinStamp o) => World == o.World && Locations == o.Locations && Reloads == o.Reloads;

        public override bool Equals(object o) => o is JoinStamp s && Equals(s);

        public override int GetHashCode() => (World * 397 ^ Locations) * 397 ^ Reloads;
    }

    /// <summary>One package as the mod sends it to a joining player: the message it goes to and its bytes.</summary>
    internal sealed class JoinPackage
    {
        public string Target { get; }
        public byte[] Bytes { get; }

        public JoinPackage(string target, byte[] bytes) { Target = target; Bytes = bytes; }
    }

    /// <summary>
    /// The config packages one mod (Drop That, Spawn That) sends each joining player. The mod builds
    /// them from scratch for every join, on the server's main loop, and the same bytes come out each
    /// time; so the first build is kept and handed to every later join, until what they were built
    /// from changes (<see cref="JoinStamp"/>).
    /// </summary>
    internal sealed class JoinPackages
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private IList<JoinPackage> _kept;
        private JoinStamp _stamp;

        public JoinPackages(string mod) { Mod = mod; }

        public string Mod { get; }
        public int Builds { get; private set; }
        public int Reuses { get; private set; }
        public double LastBuildMs { get; private set; }
        public double SavedMs { get; private set; }

        /// <summary>Whether packages built from what the world holds now are kept.</summary>
        public bool Has(JoinStamp now) => _kept != null && _stamp.Equals(now);

        /// <summary>The kept packages, for the join they were just built for (not counted as sent from the cache).</summary>
        public IList<JoinPackage> Kept => _kept;

        public void Keep(JoinStamp stamp, IList<JoinPackage> packages, double buildMs)
        {
            _kept = packages;
            _stamp = stamp;
            LastBuildMs = buildMs;
            Builds++;
        }

        /// <summary>The kept packages, for one more join.</summary>
        public IList<JoinPackage> Reuse()
        {
            Reuses++;
            SavedMs += LastBuildMs;
            return _kept;
        }

        public void Clear() => _kept = null;

        /// <summary>Null when every part of the mod the cache uses is there, else why the mod builds its own packages.</summary>
        public static string LeftAlone(string mod, IList<string> missing) =>
            missing == null || missing.Count == 0 ? null : mod + " builds its own join packages: not found in this version: " + string.Join(", ", missing.ToArray());

        public string BuiltLine(string why)
        {
            int n = _kept?.Count ?? 0;
            long bytes = _kept?.Sum(p => (long)p.Bytes.Length) ?? 0;
            return Mod + ": join packages built in " + Math.Round(LastBuildMs).ToString("0", Ci) + " ms " + why + " (" + n.ToString(Ci) + (n == 1 ? " package, " : " packages, ") +
                (bytes / 1048576.0).ToString("0.0", Ci) + " MB); later joins get the same";
        }

        public string ReusedLine()
        {
            int n = _kept?.Count ?? 0;
            return Mod + ": " + n.ToString(Ci) + (n == 1 ? " join package" : " join packages") + " kept from the first build (" + Math.Round(LastBuildMs).ToString("0", Ci) + " ms) sent";
        }

        public string SummaryLine()
        {
            if (Builds == 0) return Mod + ": no join packages built yet";
            string built = Builds == 1 ? "built once in " + Math.Round(LastBuildMs).ToString("0", Ci) + " ms"
                : "built " + Builds.ToString(Ci) + " times (last " + Math.Round(LastBuildMs).ToString("0", Ci) + " ms)";
            return Mod + ": " + built + ", sent from the cache to " + Reuses.ToString(Ci) + (Reuses == 1 ? " join" : " joins") +
                " (about " + (SavedMs / 1000.0).ToString("0.0", Ci) + " s of standing still saved)";
        }
    }

    /// <summary>
    /// When a server builds the join packages before anyone asks: on a dedicated server, a minute
    /// after its world is up (its locations placed), with nobody connected or joining, only when
    /// none are kept for what the world holds now, and at most every five minutes.
    /// </summary>
    internal static class JoinWarmUp
    {
        public const double AfterSeconds = 60;
        public const double BetweenSeconds = 300;

        public static bool Due(bool dedicated, double secondsSinceWorldUp, int peers, bool kept, double secondsSinceLastAhead = double.PositiveInfinity) =>
            dedicated && secondsSinceWorldUp >= AfterSeconds && peers == 0 && !kept && secondsSinceLastAhead >= BetweenSeconds;
    }
}
