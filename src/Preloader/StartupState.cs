using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// What the patcher knows during a start: the settings it read, the timed steps, and the bundle
    /// cache's index. Kept free of Unity types, because it runs before Unity's assemblies may load;
    /// <see cref="BundleHook"/> is the part that uses Unity, once the game itself loads bundles.
    /// </summary>
    internal static class StartupState
    {
        public const string Edited = "edited", NotRun = "not run", Off = "off";
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly object Gate = new object();
        private static readonly List<string> Events = new List<string>();
        private static readonly Stopwatch Clock = new Stopwatch();
        private static readonly Dictionary<string, BundleEntry> Index = new Dictionary<string, BundleEntry>(StringComparer.Ordinal);
        private static StartupListener s_listener;
        private static bool s_indexDirty, s_savesNoted;
        private static int s_logLines;

        /// <summary>Plain log lines kept during a start at most, so a start that floods the log stays bounded.</summary>
        private const int MaxLogLines = 6000;

        public static bool TimelineOn { get; private set; }

        public static bool CacheOn { get; private set; }

        /// <summary>Whether this game's settings ask for config saves to be held during the start.</summary>
        public static bool HoldAsked { get; private set; }

        public static bool Server { get; private set; }

        public static int KeepDays { get; private set; } = BundleCache.DefaultKeepDays;

        public static string CacheDir { get; private set; } = "";

        public static string EditResult { get; set; } = NotRun;

        public static string HousekeepingResult { get; private set; } = "";

        public static bool ResetDone { get; private set; }

        /// <summary>The plugin BepInEx is loading right now, from its last "Loading [...]" line.</summary>
        public static string CurrentPlugin { get; private set; } = "";

        public static int Asked { get; set; }

        public static int ConversionsStarted { get; set; }

        public static int ConversionsDone { get; set; }

        public static int ConversionsFailed { get; set; }

        public static int CopiesLoaded { get; set; }

        public static int CopiesRefused { get; set; }

        public static double Ms => Clock.Elapsed.TotalMilliseconds;

        public static void Begin()
        {
            Clock.Start();
            double engine = 0;
            try { engine = Math.Max(0, (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds); }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
            catch (System.ComponentModel.Win32Exception) { }

            string process = "";
            try { process = Process.GetCurrentProcess().ProcessName; } catch (InvalidOperationException) { }
            Server = BundleCache.IsDedicatedServer(process);

            var config = new ConfigFile(Path.Combine(Paths.ConfigPath, StartupSettings.ConfigFileName), false) { SaveOnConfigSet = false };
            TimelineOn = config.Bind(StartupSettings.Section, StartupSettings.Timeline, true, StartupSettings.TimelineText).Value;
            bool player = config.Bind(StartupSettings.Section, StartupSettings.Cache, true, StartupSettings.CacheText).Value;
            bool server = config.Bind(StartupSettings.Section, StartupSettings.CacheOnServer, false, StartupSettings.CacheOnServerText).Value;
            KeepDays = Math.Max(1, config.Bind(StartupSettings.Section, StartupSettings.KeepDays, BundleCache.DefaultKeepDays, StartupSettings.KeepDaysText).Value);
            CacheOn = BundleCache.On(Server, player, server);
            if (!CacheOn) EditResult = Off;
            bool hold = config.Bind(StartupSettings.Section, StartupSettings.Hold, true, StartupSettings.HoldText).Value;
            bool holdServer = config.Bind(StartupSettings.Section, StartupSettings.HoldOnServer, false, StartupSettings.HoldOnServerText).Value;
            HoldAsked = Server ? holdServer : hold;
            if (HoldAsked) ConfigSaveHook.Install();

            Add(StartupLines.Started, engine.ToString("0", Ci));
            if (TimelineOn)
            {
                s_listener = new StartupListener();
                BepInEx.Logging.Logger.Listeners.Add(s_listener);
            }

            CacheDir = Path.Combine(Path.Combine(Paths.CachePath, StartupSettings.FolderName), StartupSettings.BundlesFolder);
            if (CacheOn) Housekeep();
        }

        // Before the first load: a reset asked for, copies unused too long, half-made copies of an earlier start.
        private static void Housekeep()
        {
            Directory.CreateDirectory(CacheDir);
            string indexPath = Path.Combine(CacheDir, BundleCacheIndex.FileName);
            var entries = File.Exists(indexPath) ? BundleCacheIndex.Parse(File.ReadAllText(indexPath)) : new List<BundleEntry>();
            var files = Directory.GetFiles(CacheDir).Select(Path.GetFileName).ToList();
            bool reset = files.Contains(BundleCache.ResetMarker, StringComparer.OrdinalIgnoreCase);
            var plan = BundleCache.Housekeep(entries, files, DateTime.UtcNow, KeepDays, reset);
            int deleted = 0;
            foreach (var f in plan.Delete)
            {
                try { File.Delete(Path.Combine(CacheDir, f)); deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            ResetDone = plan.Reset;
            lock (Gate)
            {
                Index.Clear();
                foreach (var e in plan.Keep) Index[e.Key] = e;
            }
            s_indexDirty = true;
            SaveIndex();
            HousekeepingResult = plan.Reset
                ? "reset: " + deleted.ToString(Ci) + " files deleted"
                : plan.Keep.Count(e => e.IsReady).ToString(Ci) + " fast copies kept" + (deleted > 0 ? ", " + deleted.ToString(Ci) + " files deleted" : "");
        }

        public static void Add(string kind, string text)
        {
            var line = new StartupEvent(Ms, kind, text).Line();
            lock (Gate)
            {
                Events.Add(line);
                if (kind == StartupLines.Plugin) CurrentPlugin = StartupLines.PluginName(text);
            }
        }

        public static void AddLog(string text)
        {
            if (s_logLines >= MaxLogLines) return;
            s_logLines++;
            Add(StartupLines.Log, text);
        }

        public static string[] EventLines()
        {
            lock (Gate) return Events.ToArray();
        }

        public static void StopTimeline()
        {
            if (s_listener == null) return;
            BepInEx.Logging.Logger.Listeners.Remove(s_listener);
            s_listener = null;
        }

        public static BundleEntry Entry(string key)
        {
            if (key == null) return null;
            lock (Gate) return Index.TryGetValue(key, out var e) ? e : null;
        }

        public static void SetEntry(BundleEntry entry)
        {
            if (entry == null) return;
            lock (Gate) { Index[entry.Key] = entry; s_indexDirty = true; }
        }

        public static void MarkUsed(string key)
        {
            lock (Gate)
            {
                if (key == null || !Index.TryGetValue(key, out var e)) return;
                e.LastUsed = DateTime.UtcNow;
                s_indexDirty = true;
            }
        }

        public static List<BundleEntry> Entries()
        {
            lock (Gate) return Index.Values.ToList();
        }

        /// <summary>Writes the index when it changed: to a temporary file first, then over the old one.</summary>
        public static void SaveIndex()
        {
            string text;
            lock (Gate)
            {
                if (!s_indexDirty || string.IsNullOrEmpty(CacheDir)) return;
                text = BundleCacheIndex.Format(Index.Values.OrderBy(e => e.Key, StringComparer.Ordinal));
                s_indexDirty = false;
            }
            try
            {
                Directory.CreateDirectory(CacheDir);
                string path = Path.Combine(CacheDir, BundleCacheIndex.FileName), temp = path + ".new";
                File.WriteAllText(temp, text);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (IOException e) { Preloader.Log?.LogWarning("Bundle cache: the index could not be written: " + e.Message); }
            catch (UnauthorizedAccessException e) { Preloader.Log?.LogWarning("Bundle cache: the index could not be written: " + e.Message); }
        }

        /// <summary>Asks for the whole cache to be deleted at the next start, before any bundle loads (the copies are open now).</summary>
        public static string RequestReset()
        {
            if (string.IsNullOrEmpty(CacheDir)) return "the bundle cache has no folder in this game";
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(Path.Combine(CacheDir, BundleCache.ResetMarker), "asked " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", Ci) + " UTC\n");
            return "the bundle cache is emptied at the next start, before any bundle loads";
        }

        public static bool ResetRequested() => !string.IsNullOrEmpty(CacheDir) && File.Exists(Path.Combine(CacheDir, BundleCache.ResetMarker));

        public static string Status() =>
            "Vigil's patcher: timeline " + (TimelineOn ? "on" : "off") + "; bundle cache " + (CacheOn ? "on" : Server ? "off on a dedicated server" : "off")
            + (CacheOn ? " (" + HousekeepingResult + ")" : "") + "; " + HoldStatus();

        /// <summary>"config saves held until the menu", "... off", or why the hold could not be put in.</summary>
        public static string HoldStatus()
        {
            if (!HoldAsked) return "config saves not held" + (Server ? " on a dedicated server" : "");
            if (ConfigSaveHook.Hold == null) return "config saves not held: " + ConfigSaveHook.Problem;
            var r = ConfigSaveHook.Hold.Result;
            string guard = ConfigSaveHook.GuardProblem == null ? "" : "; mods' own reads of held files not watched: " + ConfigSaveHook.GuardProblem;
            return (r == null ? "config saves held until " + (Server ? "the world is up" : "the menu shows") : "config saves held until " + r.Why) + guard;
        }

        /// <summary>Ends the hold of config saves (the menu shown, the world up, the game quitting) and adds its step; "" when there was no hold.</summary>
        public static string ReleaseSaves(string why)
        {
            var result = ConfigSaveHook.End(why);
            if (result == null) return "";
            bool note;
            lock (Gate)
            {
                note = !s_savesNoted;
                s_savesNoted = true;
            }

            if (note)
            {
                Add(StartupLines.ConfigSaves, result.EventText());
                foreach (var line in result.Lines()) Preloader.Log?.LogInfo(line);
            }

            return result.EventText();
        }
    }
}
