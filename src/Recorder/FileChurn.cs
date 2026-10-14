using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Counts file writes in the config folder and the game's world and character folders, per
    /// file, with the file's size at each write. A mod rewriting a file every second, or many mods
    /// reloading each other's configs, shows up by name. Uses the operating system's change
    /// notifications, so it costs nothing while nothing is written. One write can raise more than
    /// one notification, so the counts are events, not exact writes.
    /// </summary>
    internal static class FileChurn
    {
        private static readonly object s_lock = new object();
        private static readonly ValueTally s_tally = new ValueTally("kB");
        private static readonly List<FileSystemWatcher> s_watchers = new List<FileSystemWatcher>();
        private static string s_ignore;

        /// <summary>Watches each folder that exists; <paramref name="ignore"/> (Vigil's own output) is left out.</summary>
        public static int Start(IEnumerable<KeyValuePair<string, string>> folders, string ignore)
        {
            s_folders = folders.ToList();
            s_ignoreGiven = ignore;
            s_ignore = string.IsNullOrEmpty(ignore) ? null : Path.GetFullPath(ignore);
            foreach (var kv in s_folders.ToList())
            {
                try
                {
                    if (!Directory.Exists(kv.Value)) continue;
                    string label = kv.Key, root = Path.GetFullPath(kv.Value);
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        InternalBufferSize = 65536,
                    };
                    FileSystemEventHandler on = (s, e) => Count(label, root, e.FullPath);
                    w.Changed += on;
                    w.Created += on;
                    w.Renamed += (s, e) => Count(label, root, e.FullPath);
                    w.EnableRaisingEvents = true;
                    s_watchers.Add(w);
                }
                catch (Exception) { }
            }
            return s_watchers.Count;
        }

        private static void Count(string label, string root, string path)
        {
            try
            {
                if (s_ignore != null && path.StartsWith(s_ignore, StringComparison.OrdinalIgnoreCase)) return;
                if (Directory.Exists(path)) return;
                double kb = 0;
                try { kb = new FileInfo(path).Length / 1024.0; } catch (Exception) { }
                string rel = path.Length > root.Length ? path.Substring(root.Length).TrimStart('\\', '/') : path;
                lock (s_lock) s_tally.Add(label + "/" + rel.Replace('\\', '/'), kb);
            }
            catch (Exception) { }
        }

        public static void EndSecond(out string top, out long count)
        {
            lock (s_lock)
            {
                // Ranked by kB, so the busiest file by size; the count is every event in the second.
                s_tally.EndSecond(out top, out _, out count);
            }
        }

        public static string Peek(float seconds) { lock (s_lock) return s_tally.Peek(seconds, 15); }
        public static void EndWindow(float seconds) { lock (s_lock) s_tally.EndWindow(seconds, 0); }
        public static string Summary(float seconds) { lock (s_lock) return s_tally.Summary(seconds, 25); }

        public static void Stop()
        {
            foreach (var w in s_watchers) { try { w.Dispose(); } catch (Exception) { } }
            s_watchers.Clear();
        }

        private static List<KeyValuePair<string, string>> s_folders;
        private static string s_ignoreGiven;

        /// <summary>Releases the watchers (and the threads behind them) while Vigil is not recording.</summary>
        public static void Suspend()
        {
            if (s_watchers.Count == 0) return;
            Stop();
        }

        /// <summary>Watches the folders of the last <see cref="Start"/> again.</summary>
        public static void Restart()
        {
            if (s_folders == null || s_watchers.Count > 0) return;
            Start(s_folders, s_ignoreGiven);
        }
    }
}
