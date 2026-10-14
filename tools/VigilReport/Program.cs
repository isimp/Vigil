using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Vigil.Core;

namespace Vigil.Report
{
    /// <summary>
    /// Reads Vigil's recordings (the running or last session, plus earlier ones in
    /// Vigil-runs/). On a player's machine they are in the game's data folder
    /// (LocalLow\IronGate\Valheim\Vigil); a server keeps them in BepInEx/Vigil. Sessions NetProbe recorded before
    /// Vigil replaced it are read the same way.
    ///   sessions                     list sessions, newest first, with their headline numbers
    ///   worst [session] [n]          the n seconds with the longest frames, in world
    ///   incidents [session]          every incident report with what triggered it and its headline
    ///   diff &lt;older&gt; [newer]          mods, config files and Harmony patches that changed between two sessions
    /// A session is "current" (default), a folder name from Vigil-runs, or a 1-based number
    /// from the "sessions" list. --dir &lt;folder&gt; reads recordings kept elsewhere (a server's pulled copy).
    /// </summary>
    internal static class Program
    {
        private const string DefaultDir = @"%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\Vigil";

        private static int Main(string[] args)
        {
            var list = args.ToList();
            string dir = Environment.ExpandEnvironmentVariables(DefaultDir);
            int d = list.IndexOf("--dir");
            if (d >= 0 && d + 1 < list.Count) { dir = list[d + 1]; list.RemoveRange(d, 2); }
            if (list.Count == 0) { Console.WriteLine(Usage()); return 0; }
            try
            {
                switch (list[0])
                {
                    case "sessions": Sessions(dir); return 0;
                    case "worst": Worst(Resolve(dir, Arg(list, 1)), list.Count > 2 ? int.Parse(list[2]) : 15); return 0;
                    case "incidents": Incidents(Resolve(dir, Arg(list, 1))); return 0;
                    case "diff":
                        if (list.Count < 2) { Console.WriteLine(Usage()); return 1; }
                        Diff(Resolve(dir, list[1]), Resolve(dir, Arg(list, 2))); return 0;
                    default: Console.WriteLine(Usage()); return 1;
                }
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
        }

        private static string Arg(List<string> l, int i) => l.Count > i ? l[i] : "current";

        private static string Usage() =>
            "VigilReport sessions | worst [session] [n] | incidents [session] | diff <older> [newer]   [--dir <folder>]\n" +
            "session = current (default), a Vigil-runs folder name, or its number in the sessions list";

        private static List<string> AllSessions(string dir)
        {
            var result = new List<string> { dir };
            string runs = Path.Combine(dir, RunArchive.FolderName);
            if (Directory.Exists(runs))
                result.AddRange(Directory.GetDirectories(runs).OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal));
            return result;
        }

        private static string Resolve(string dir, string session)
        {
            if (session == "current") return dir;
            var all = AllSessions(dir);
            if (int.TryParse(session, out int n) && n >= 1 && n <= all.Count) return all[n - 1];
            string path = Path.Combine(dir, RunArchive.FolderName, session);
            if (Directory.Exists(path)) return path;
            throw new ArgumentException("no such session: " + session);
        }

        private static string Name(string session, string dir) => session == dir ? "current" : Path.GetFileName(session);

        // The game keeps its files open while it writes them, so read alongside it.
        private static string ReadOrEmpty(string path)
        {
            if (!File.Exists(path)) return "";
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        private static void Sessions(string dir)
        {
            int i = 1;
            foreach (var s in AllSessions(dir))
            {
                string summary = ReadOrEmpty(RunFiles.Find(s, "summary.txt"));
                string first = summary.Split('\n').FirstOrDefault()?.Trim() ?? "";
                string frames = summary.Split('\n').FirstOrDefault(l => l.StartsWith("Frames in world:"))?.Trim() ?? "";
                int incidents = RunFiles.All(s, "incident-*.txt").Count;
                Console.WriteLine($"{i,3}. {Name(s, dir)}   {incidents} incident reports");
                if (first.Length > 0) Console.WriteLine("      " + first);
                if (frames.Length > 0) Console.WriteLine("      " + frames);
                i++;
            }
        }

        private static readonly string[] WorstColumns =
            { "time", "dt_max_ms", "fps", "gc", "fixed_max", "render_ms", "top_method", "top_method_ms", "top_mod", "top_mod_ms",
              "ping_ms", "max_gap_ms", "send_queue_kb", "log_lines", "top_log", "exceptions", "top_exception", "alloc_top", "alloc_top_kb",
              "handler_top", "handler_top_ms", "file_writes", "top_file", "faults_s", "mem_load", "biome", "ui", "incident", "note" };

        private static void Worst(string session, int n)
        {
            string path = RunFiles.Find(session, "timeline.tsv");
            if (!File.Exists(path)) throw new FileNotFoundException("no timeline in " + session);
            var t = TimelineReader.Parse(ReadOrEmpty(path));
            Console.WriteLine($"{t.Rows.Count} seconds recorded. The {n} seconds in world with the longest frame:");
            var cols = WorstColumns.Where(c => t.Columns.Contains(c)).ToList();
            foreach (var r in t.Worst("dt_max_ms", n, ("situation", "in world")).Concat(t.Worst("dt_max_ms", n, ("situation", "server"))))
                Console.WriteLine("   " + string.Join("  ", cols.Select(c => c + "=" + (r[c] ?? "-"))));
            Console.WriteLine();
            Console.WriteLine("Column overview (seconds with a value, mean, max):");
            foreach (var c in new[] { "fps", "dt_max_ms", "gc", "render_ms", "gpu_ms", "mods_ms", "probe_ms", "ping_ms", "max_gap_ms", "send_queue_kb",
                                      "app_in_kb", "app_out_kb", "rpc_out", "rpc_in", "log_lines", "exceptions", "alloc_top_kb", "handler_top_ms", "file_writes",
                                      "ph_ui_rebuild", "proc_cpu", "sys_cpu", "ws_mb", "faults_s", "mem_load" })
            {
                var s = t.Stats(c);
                if (s.Count > 0) Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "   {0,-14} {1,6}  mean {2,9:0.0}  max {3,9:0.0}", c, s.Count, s.Mean, s.Max));
            }
        }

        private static void Incidents(string session)
        {
            var files = RunFiles.All(session, "incident-*.txt");
            Console.WriteLine($"{files.Count} incident reports in {session}");
            foreach (var f in files)
            {
                var lines = File.ReadAllLines(f);
                string trigger = lines.FirstOrDefault(l => l.StartsWith("What triggered it:")) ?? "";
                int h = Array.FindIndex(lines, l => l.StartsWith("== The longest frame"));
                Console.WriteLine("   " + Path.GetFileName(f));
                Console.WriteLine("      " + trigger);
                if (h >= 0 && h + 1 < lines.Length) Console.WriteLine("      " + lines[h + 1]);
            }
            foreach (var f in RunFiles.All(session, "deep-*.txt")) Console.WriteLine("   deep capture: " + Path.GetFileName(f));
        }

        private static void Diff(string older, string newer)
        {
            Console.WriteLine("Mods:");
            var mods = SessionDiff.Mods(ReadOrEmpty(RunFiles.Find(older, "mods.tsv")), ReadOrEmpty(RunFiles.Find(newer, "mods.tsv")));
            foreach (var l in mods.DefaultIfEmpty("   no changes")) Console.WriteLine("   " + l.Trim());
            Console.WriteLine("Config files:");
            var configs = SessionDiff.Configs(ReadOrEmpty(RunFiles.Find(older, "configs.tsv")), ReadOrEmpty(RunFiles.Find(newer, "configs.tsv")));
            foreach (var l in configs.DefaultIfEmpty("   no changes")) Console.WriteLine("   " + l.Trim());
            Console.WriteLine("Harmony patches on game methods:");
            string olderMap = RunFiles.Find(older, "patches.tsv"), newerMap = RunFiles.Find(newer, "patches.tsv");
            if (!File.Exists(olderMap) || !File.Exists(newerMap)) Console.WriteLine("   not recorded in both sessions");
            else
                foreach (var l in PatchMap.Diff(ReadOrEmpty(olderMap).Split('\n'), ReadOrEmpty(newerMap).Split('\n')).DefaultIfEmpty("no changes"))
                    Console.WriteLine("   " + l);
            Console.WriteLine("Frames in world:");
            foreach (var s in new[] { older, newer })
                Console.WriteLine("   " + Path.GetFileName(s) + ": " +
                    (ReadOrEmpty(RunFiles.Find(s, "summary.txt")).Split('\n').FirstOrDefault(l => l.StartsWith("Frames in world:"))?.Trim() ?? "(no summary)"));
        }
    }
}
