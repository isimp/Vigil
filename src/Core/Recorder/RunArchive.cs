using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vigil.Core
{
    /// <summary>
    /// Moves the previous session's report files (Vigil*.txt and Vigil*.tsv, and those NetProbe left:
    /// NetProbe.txt, NetProbe-*.txt, NetProbe-*.tsv) into Vigil-runs/&lt;when it
    /// ended&gt;/ before a new session writes fresh ones, so sessions can be compared later. Keeps
    /// the newest few sessions and only ever deletes folders it named itself. Never throws:
    /// a report that cannot be moved stays where it is.
    /// </summary>
    public static class RunArchive
    {
        public const string FolderName = "Vigil-runs";
        private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";
        private static readonly Regex OwnFolder = new Regex(@"^\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d(-\d+)?$");

        /// <summary>Returns the folder the previous session went to, or null when there was none.</summary>
        /// <param name="dir">The recordings folder.</param>
        /// <param name="keep">How many earlier sessions are kept at most.</param>
        /// <param name="maxBytes">Kept sessions together stay under this size; the oldest go first, the newest always stays.</param>
        public static string KeepPreviousRun(string dir, int keep, long maxBytes = long.MaxValue)
        {
            string target = null;
            try
            {
                var reports = Directory.GetFiles(dir, "Vigil*.txt").Concat(Directory.GetFiles(dir, "Vigil*.tsv"))
                    .Concat(Directory.GetFiles(dir).Where(OutputLocation.IsLegacyReport)).ToArray();
                if (reports.Length > 0)
                {
                    DateTime ended = reports.Max(File.GetLastWriteTime);
                    string runs = Path.Combine(dir, FolderName);
                    string stamp = ended.ToString(StampFormat, CultureInfo.InvariantCulture);
                    target = Path.Combine(runs, stamp);
                    for (int n = 2; Directory.Exists(target); n++) target = Path.Combine(runs, stamp + "-" + n);
                    Directory.CreateDirectory(target);
                    foreach (var f in reports)
                    {
                        try { File.Move(f, Path.Combine(target, Path.GetFileName(f))); }
                        catch (Exception) { }
                    }
                }
                Prune(Path.Combine(dir, FolderName), keep, maxBytes);
            }
            catch (Exception) { }
            return target;
        }

        private static void Prune(string runs, int keep, long maxBytes)
        {
            if (!Directory.Exists(runs)) return;
            var own = Directory.GetDirectories(runs)
                .Where(d => OwnFolder.IsMatch(Path.GetFileName(d)))
                .OrderBy(d => SortKey(Path.GetFileName(d)), StringComparer.Ordinal)
                .ToList();
            foreach (var old in own.Take(Math.Max(0, own.Count - keep)).ToList())
            {
                try { Directory.Delete(old, true); } catch (Exception) { }
                own.Remove(old);
            }
            if (maxBytes == long.MaxValue) return;
            long total = own.Sum(Size);
            for (int i = 0; i < own.Count - 1 && total > maxBytes; i++)
            {
                long size = Size(own[i]);
                try { Directory.Delete(own[i], true); total -= size; } catch (Exception) { }
            }
        }

        private static long Size(string folder)
        {
            try { return Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
            catch (Exception) { return 0; }
        }

        // "2026-10-08_18-00-00-10" must sort after "...-9" and after the plain stamp.
        private static string SortKey(string name)
        {
            int n = 1;
            string stamp = name;
            if (name.Length > StampFormat.Length)
            {
                stamp = name.Substring(0, StampFormat.Length);
                int.TryParse(name.Substring(StampFormat.Length + 1), out n);
            }
            return stamp + n.ToString("D6", CultureInfo.InvariantCulture);
        }
    }
}
