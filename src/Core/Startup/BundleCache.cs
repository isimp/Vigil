using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>Which way one bundle load goes.</summary>
    internal enum BundleChoice
    {
        /// <summary>Loaded as the mod asked, untouched.</summary>
        Usual,

        /// <summary>Loaded as the mod asked, and a fast copy is made for the next load.</summary>
        UsualAndConvert,

        /// <summary>Loaded from the fast copy.</summary>
        Copy,
    }

    /// <summary>How a bundle load ended, as the startup report counts it.</summary>
    internal static class BundleOutcome
    {
        public const string Copy = "copy", Usual = "usual", Plain = "plain", Failed = "failed", Blocked = "blocked";
    }

    /// <summary>What the cache does at a start before the first load: files to delete and the entries it keeps.</summary>
    internal sealed class BundleHousekeeping
    {
        public bool Reset { get; set; }

        public List<string> Delete { get; } = new List<string>();

        public List<BundleEntry> Keep { get; } = new List<BundleEntry>();
    }

    /// <summary>
    /// The bundle cache's rules. A mod's LZMA bundle has to be unpacked whole every time it loads; an
    /// LZ4 copy of it loads by reading only what is used. The first load of such a bundle goes as usual
    /// and has Unity write the copy; later loads read the copy. Anything the cache cannot vouch for
    /// (a checksum asked for, a stream it cannot read twice, a copy that failed once) loads as usual.
    /// </summary>
    internal static class BundleCache
    {
        public const string CopyExtension = ".bundle", TempExtension = ".tmp", SourceExtension = ".src", ResetMarker = "reset";
        public const int DefaultKeepDays = 30;

        /// <summary>Bundles below this are cheap to unpack and are left alone.</summary>
        public const long MinBytes = 1_000_000;

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static BundleChoice Choose(BundleShape shape, long length, uint crc, bool seekable, bool copyReady, bool blocked, bool convertingNow, bool on)
        {
            if (!on || crc != 0 || !seekable || length < MinBytes) return BundleChoice.Usual;
            if (shape == null || !shape.WorthACopy || blocked) return BundleChoice.Usual;
            if (copyReady) return BundleChoice.Copy;
            return convertingNow ? BundleChoice.Usual : BundleChoice.UsualAndConvert;
        }

        /// <summary>On in a player's game by its own switch; on a dedicated server only by the server's switch.</summary>
        public static bool On(bool server, bool playerSwitch, bool serverSwitch) => server ? serverSwitch : playerSwitch;

        /// <summary>The dedicated server's executable is valheim_server (valheim_server.x86_64 on Linux).</summary>
        public static bool IsDedicatedServer(string processName) =>
            processName != null && processName.StartsWith("valheim_server", StringComparison.OrdinalIgnoreCase);

        /// <param name="entries">The index as read at this start.</param>
        /// <param name="files">The names of the files in the cache folder.</param>
        /// <param name="now">This start's time.</param>
        /// <param name="keepDays">Days a copy may go unused before it is deleted.</param>
        /// <param name="reset">A reset was asked for: everything goes.</param>
        public static BundleHousekeeping Housekeep(List<BundleEntry> entries, List<string> files, DateTime now, int keepDays, bool reset)
        {
            var h = new BundleHousekeeping { Reset = reset };
            entries = entries ?? new List<BundleEntry>();
            files = files ?? new List<string>();
            if (reset)
            {
                h.Delete.AddRange(files);
                return h;
            }

            var cutoff = now.AddDays(-Math.Max(1, keepDays));
            var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var keptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                bool recent = e.LastUsed >= cutoff;
                bool kept = e.IsBlocked ? recent : recent && present.Contains(e.Key + CopyExtension);
                if (!kept) continue;
                h.Keep.Add(e);
                if (e.IsReady) keptKeys.Add(e.Key);
            }

            foreach (var f in files)
            {
                if (f.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase) || f.EndsWith(SourceExtension, StringComparison.OrdinalIgnoreCase)) h.Delete.Add(f);
                else if (f.EndsWith(CopyExtension, StringComparison.OrdinalIgnoreCase) && !keptKeys.Contains(f.Substring(0, f.Length - CopyExtension.Length))) h.Delete.Add(f);
            }
            return h;
        }

        /// <summary>The asset names of a bundle as one value, whatever order Unity lists them in: "count:hash".</summary>
        public static string AssetsSignature(IEnumerable<string> names)
        {
            var sorted = (names ?? Enumerable.Empty<string>()).OrderBy(n => n, StringComparer.Ordinal).ToList();
            ulong hash = BundleKey.Fnv(Encoding.UTF8.GetBytes(string.Join("\n", sorted)));
            return sorted.Count.ToString(Ci) + ":" + hash.ToString("x16", Ci);
        }

        /// <summary>The cache row: ready copies and their size on disk, and bundles that keep loading the usual way.</summary>
        public static string DiskLine(IEnumerable<BundleEntry> entries)
        {
            var list = (entries ?? Enumerable.Empty<BundleEntry>()).ToList();
            int ready = list.Count(e => e.IsReady), blocked = list.Count(e => e.IsBlocked);
            long bytes = list.Where(e => e.IsReady).Sum(e => e.CopyBytes);
            string line = ready == 0 ? "no fast copies yet"
                : string.Format(Ci, "{0} fast cop{1}, {2:0} MB on disk", ready, ready == 1 ? "y" : "ies", bytes / 1e6);
            if (blocked > 0) line += string.Format(Ci, ", {0} bundle{1} that load{2} the usual way", blocked, blocked == 1 ? "" : "s", blocked == 1 ? "s" : "");
            return line;
        }
    }
}
