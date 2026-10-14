using System;
using System.IO;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// Where Vigil writes. A mod manager's shared profile hands every text file in it to the
    /// players who subscribe, so on a player's machine the recordings go to the game's own data
    /// folder instead; a dedicated server keeps them in their own folder inside BepInEx, where the
    /// server tooling collects them. A configured folder wins on both.
    /// </summary>
    public static class OutputLocation
    {
        public const string FolderName = "Vigil";

        /// <summary>The folder, and the prefix of files and folders, NetProbe used before Vigil replaced it.</summary>
        public const string LegacyName = "NetProbe";

        public static string Choose(string configured, bool server, string bepinexRoot, string gameDataDir)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            return Path.Combine(server ? bepinexRoot : gameDataDir, FolderName);
        }

        /// <summary>
        /// Moves what NetProbe recorded in <paramref name="from"/> to <paramref name="to"/>: its
        /// earlier sessions (NetProbe-runs/) into Vigil's (Vigil-runs/), its skipped timers and
        /// crash journal (NetProbe-state/) into Vigil-state/, and its last session's files
        /// (NetProbe.txt, NetProbe-*.txt, NetProbe-*.tsv) as they are, for the run archive to keep
        /// as an earlier session. Files keep their names, so a session reads as recorded. Nothing
        /// else in <paramref name="from"/> is touched, a file already at its new place is never
        /// replaced (the old copy stays), and an emptied NetProbe folder is removed. Returns the
        /// number of files moved. Never throws.
        /// </summary>
        public static int TakeOver(string from, string to)
        {
            int moved = 0;
            try
            {
                if (!Directory.Exists(from)) return 0;
                bool same = Same(from, to);
                if (!same)
                {
                    foreach (var f in Directory.GetFiles(from).Where(IsLegacyReport))
                    {
                        Directory.CreateDirectory(to);
                        moved += MoveFile(f, Path.Combine(to, Path.GetFileName(f)));
                    }
                }
                moved += MoveTree(Path.Combine(from, LegacyName + "-runs"), Path.Combine(to, RunArchive.FolderName));
                moved += MoveTree(Path.Combine(from, LegacyName + "-state"), Path.Combine(to, FolderName + "-state"));
                if (!same && string.Equals(Path.GetFileName(Path.GetFullPath(from).TrimEnd('\\', '/')), LegacyName, StringComparison.OrdinalIgnoreCase))
                    RemoveIfEmpty(from);
            }
            catch (Exception) { }
            return moved;
        }

        /// <summary>A report file NetProbe wrote: NetProbe.txt, or NetProbe-*.txt / NetProbe-*.tsv.</summary>
        public static bool IsLegacyReport(string path)
        {
            string name = Path.GetFileName(path);
            if (!(name.EndsWith(".txt", StringComparison.Ordinal) || name.EndsWith(".tsv", StringComparison.Ordinal))) return false;
            return name.StartsWith(LegacyName + "-", StringComparison.Ordinal) || name.StartsWith(LegacyName + ".", StringComparison.Ordinal);
        }

        private static bool Same(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static int MoveFile(string source, string target)
        {
            if (File.Exists(target)) return 0;
            try { File.Move(source, target); return 1; }
            catch (Exception) { return 0; }
        }

        /// <summary>Moves every file under <paramref name="source"/>, then removes the folders it emptied.</summary>
        private static int MoveTree(string source, string target)
        {
            if (!Directory.Exists(source)) return 0;
            int moved = 0;
            foreach (var f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(source.Length).TrimStart('\\', '/');
                string dest = Path.Combine(target, rel);
                try { Directory.CreateDirectory(Path.GetDirectoryName(dest)); } catch (Exception) { continue; }
                moved += MoveFile(f, dest);
            }
            foreach (var d in Directory.GetDirectories(source, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(source))
                RemoveIfEmpty(d);
            return moved;
        }

        private static void RemoveIfEmpty(string folder)
        {
            try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); } catch (Exception) { }
        }
    }
}
