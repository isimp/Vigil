using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>The incident reports a player's sharing switch sends on its own.</summary>
    [Flags]
    public enum ShareKinds
    {
        None = 0,
        LagReports = 1,
        Drops = 2,
        OtherIncidents = 4,
        EnvironmentReports = 8,

        /// <summary>What this game spawned and what mods cancelled, sent once a minute; not a report file.</summary>
        SpawnLogs = 16,
    }

    /// <summary>
    /// The reports waiting to go to the server, oldest first, each once: kept in Vigil-state as one
    /// path a line, so what waited when the game quit (or the connection died) goes at the next join.
    /// Incident reports and environment reports can be shared.
    /// </summary>
    internal sealed class ShareQueue
    {
        private readonly List<string> _files = new List<string>();

        public IReadOnlyList<string> Files => _files;

        /// <summary>Whether a report file can be shared: an incident report or an environment report.</summary>
        public static bool Shareable(string file)
        {
            string name = Path.GetFileName(file ?? "");
            string kind = ReportCatalog.KindOf(name);
            return kind != null && (name.StartsWith("Vigil-incident-", StringComparison.Ordinal) || name.StartsWith("Vigil-environment-", StringComparison.Ordinal));
        }

        /// <summary>Whether the switch, set to these kinds, sends this report on its own as it is written.</summary>
        public static bool Wanted(ShareKinds kinds, string file)
        {
            if (!Shareable(file)) return false;
            string name = Path.GetFileName(file);
            if (name.StartsWith("Vigil-environment-", StringComparison.Ordinal)) return (kinds & ShareKinds.EnvironmentReports) != 0;
            string kind = ReportCatalog.KindOf(name);
            ShareKinds needs = kind == "lag report" ? ShareKinds.LagReports : kind == "drop" ? ShareKinds.Drops : ShareKinds.OtherIncidents;
            return (kinds & needs) != 0;
        }

        /// <summary>Adds a report to the end; false when it already waits.</summary>
        public bool Add(string file)
        {
            if (string.IsNullOrEmpty(file) || _files.Contains(file, StringComparer.OrdinalIgnoreCase)) return false;
            _files.Add(file);
            return true;
        }

        public void Remove(string file) => _files.RemoveAll(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));

        /// <summary>The first report still there; the ones gone meanwhile leave the queue.</summary>
        public string Next(Func<string, bool> exists)
        {
            while (_files.Count > 0)
            {
                if (exists(_files[0])) return _files[0];
                _files.RemoveAt(0);
            }
            return null;
        }

        public string Format() => string.Join("\n", _files.ToArray());

        public static ShareQueue Parse(string text)
        {
            var q = new ShareQueue();
            foreach (var line in (text ?? "").Split('\n')) q.Add(line.Trim());
            return q;
        }
    }

    /// <summary>
    /// The server's side of shared reports: each player's in a folder named after them, made safe
    /// for a file system; only incident reports, each at most <see cref="MaxFileBytes"/>; a player's
    /// oldest go first to keep their folder under the server's limit.
    /// </summary>
    internal static class SharedStore
    {
        public const long MaxFileBytes = 2L * 1024 * 1024;
        public const int MaxFolderLength = 32;

        /// <summary>A player's folder: their name with only letters, digits, spaces, "-", "_" and "'" kept, others as "_".</summary>
        public static string Folder(string playerName)
        {
            var chars = (playerName ?? "").Trim().Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '\'' ? c : '_').ToArray();
            string s = new string(chars).Trim();
            if (s.Length > MaxFolderLength) s = s.Substring(0, MaxFolderLength).Trim();
            return s.Length == 0 ? "player" : s;
        }

        /// <summary>Why the server will not take a report; null when it will.</summary>
        public static string Refuse(string fileName, long size, long limitBytes)
        {
            if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName) || fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0 || !ShareQueue.Shareable(fileName))
                return "only incident reports can be shared";
            if (size > MaxFileBytes) return "the report is larger than " + (MaxFileBytes / 1048576) + " MB";
            if (size > limitBytes) return "the report is larger than this server keeps per player";
            return null;
        }

        /// <summary>The kept reports to delete, oldest first, so they and the one arriving fit under the limit.</summary>
        public static List<string> Prune(IEnumerable<(string Name, long Size, DateTime Modified)> kept, long incoming, long limitBytes)
        {
            var list = kept.OrderBy(k => k.Modified).ToList();
            long total = list.Sum(k => k.Size) + incoming;
            var gone = new List<string>();
            foreach (var k in list)
            {
                if (total <= limitBytes) break;
                gone.Add(k.Name);
                total -= k.Size;
            }
            return gone;
        }
    }
}
