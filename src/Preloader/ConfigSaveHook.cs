using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// Puts a <see cref="SaveHold"/> in front of BepInEx's config saves: a prefix on
    /// <c>ConfigFile.Save</c> hands each save to the hold, and prefixes on <c>ConfigFile.Reload</c> and
    /// the <c>ConfigFile</c> constructor write a held file before it is read back or opened by another
    /// settings object; while it holds, a guard does the same for a mod opening, checking for or
    /// copying a held file itself. All go in from the patcher, before the chainloader starts, so
    /// StartupAccelerator's holding back of patches (from the chainloader on) does not delay them. A
    /// timer ends the hold at its cap should Vigil.dll never end it.
    /// </summary>
    internal static class ConfigSaveHook
    {
        private const string HarmonyId = "isimp.Vigil.configsaves";
        private const int MaxDifferences = 5;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly FieldInfo Owner = AccessTools.Field(typeof(ConfigFile), "_ownerMetadata");

        [ThreadStatic]
        private static bool t_writing;

        private static System.Threading.Timer s_cap;

        // Kept for the session: its patches stay in.
        private static Harmony s_harmony;

        public static SaveHold Hold { get; private set; }

        /// <summary>Why the hold is not on, when it was asked for and could not be put in; null otherwise.</summary>
        public static string Problem { get; private set; }

        public static void Install()
        {
            var hold = new SaveHold(Stamp, WriteNow, () => DateTime.UtcNow);
            try
            {
                s_harmony = new Harmony(HarmonyId);
                s_harmony.Patch(AccessTools.Constructor(typeof(ConfigFile), new[] { typeof(string), typeof(bool), typeof(BepInPlugin) }),
                    prefix: new HarmonyMethod(typeof(ConfigSaveHook), nameof(OpenPrefix)) { priority = Priority.First });
                s_harmony.Patch(AccessTools.Method(typeof(ConfigFile), nameof(ConfigFile.Reload)),
                    prefix: new HarmonyMethod(typeof(ConfigSaveHook), nameof(ReloadPrefix)) { priority = Priority.First });
                s_harmony.Patch(AccessTools.Method(typeof(ConfigFile), nameof(ConfigFile.Save)),
                    prefix: new HarmonyMethod(typeof(ConfigSaveHook), nameof(SavePrefix)) { priority = Priority.First });
            }
            catch (Exception e)
            {
                Problem = e.GetType().Name + ": " + e.Message;
                return;
            }

            Hold = hold;
            hold.Begin();
            s_cap = new System.Threading.Timer(_ => StartupState.ReleaseSaves(SaveHold.CapWhy), null, SaveHold.Cap + TimeSpan.FromSeconds(5), System.Threading.Timeout.InfiniteTimeSpan);
            InstallGuard();
        }

        /// <summary>Why mods' own reads of held files are not watched; null when they are.</summary>
        public static string GuardProblem { get; private set; }

        private static readonly List<MethodBase> Guarded = new List<MethodBase>();

        // A mod reading, checking or copying a settings file itself goes past BepInEx. In Mono every
        // read of a file's contents by path ends in one FileStream constructor, File.Exists in
        // FileSystem.FileExists, and File.Copy and FileInfo.CopyTo in FileSystem.CopyFile; a held file
        // about to be read there is written first. Without these the hold still works, unguarded.
        private static void InstallGuard()
        {
            try
            {
                var fileSystem = typeof(File).Assembly.GetType("System.IO.FileSystem", false);
                var open = AccessTools.Constructor(typeof(FileStream), new[] { typeof(string), typeof(FileMode), typeof(FileAccess), typeof(FileShare), typeof(int), typeof(bool), typeof(FileOptions) });
                var exists = fileSystem == null ? null : AccessTools.Method(fileSystem, "FileExists", new[] { typeof(string) });
                var copy = fileSystem == null ? null : AccessTools.Method(fileSystem, "CopyFile", new[] { typeof(string), typeof(string), typeof(bool) });
                if (open == null || exists == null || copy == null)
                {
                    GuardProblem = "this runtime's file opening has another shape";
                    return;
                }

                s_harmony.Patch(open, prefix: new HarmonyMethod(typeof(ConfigSaveHook), nameof(OpenStreamPrefix)) { priority = Priority.First });
                Guarded.Add(open);
                s_harmony.Patch(exists, prefix: new HarmonyMethod(typeof(ConfigSaveHook), nameof(ExistsPrefix)) { priority = Priority.First });
                Guarded.Add(exists);
                s_harmony.Patch(copy, prefix: new HarmonyMethod(typeof(ConfigSaveHook), nameof(CopyPrefix)) { priority = Priority.First });
                Guarded.Add(copy);
            }
            catch (Exception e)
            {
                GuardProblem = e.GetType().Name + ": " + e.Message;
                RemoveGuard();
            }
        }

        // The guard has nothing to do once the hold is over: its prefixes come off.
        private static void RemoveGuard()
        {
            foreach (var m in Guarded)
            {
                try { s_harmony.Unpatch(m, HarmonyPatchType.Prefix, HarmonyId); }
                catch (Exception) { }
            }

            Guarded.Clear();
        }

        /// <summary>Ends the hold, writing each held file once; null when there was no hold. Later calls give the first result.</summary>
        public static SaveHoldResult End(string why)
        {
            var hold = Hold;
            if (hold == null) return null;
            var result = hold.Release(why);
            lock (Gate)
            {
                if (result != null && !s_checked)
                {
                    s_checked = true;
                    s_cap?.Dispose();
                    RemoveGuard();
                    Check = CheckWritten(hold.Written);
                }
            }

            return result;
        }

        private static readonly object Gate = new object();
        private static bool s_checked;

        /// <summary>Right after the hold ended: files read back and compared, settings compared, and the first settings whose file value differs.</summary>
        public static string[] Check { get; private set; } = Array.Empty<string>();

        // Called by Harmony: false skips BepInEx's save, which the hold then writes later.
        private static bool SavePrefix(ConfigFile __instance)
        {
            if (t_writing || Hold == null) return true;
            try
            {
                return !Hold.Save(__instance, __instance.ConfigFilePath, (Owner?.GetValue(__instance) as BepInPlugin)?.Name);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void ReloadPrefix(ConfigFile __instance)
        {
            if (t_writing || Hold == null) return;
            try { Hold.BeforeRead(__instance.ConfigFilePath); }
            catch (Exception) { }
        }

        // A new settings object checks whether its file exists and reads it: a held save of the same
        // path by another object is written first, as it would be there already.
        private static void OpenPrefix(string configPath)
        {
            if (t_writing || Hold == null || configPath == null) return;
            try { Hold.BeforeRead(Path.GetFullPath(configPath)); }
            catch (Exception) { }
        }

        // Called by Harmony on every file opened by path while the guard is in: a read of an existing file only.
        private static void OpenStreamPrefix(string path, FileMode mode, FileAccess access)
        {
            if ((access & FileAccess.Read) == 0 || (mode != FileMode.Open && mode != FileMode.OpenOrCreate)) return;
            Guard(path);
        }

        private static void ExistsPrefix(string fullPath) => Guard(fullPath);

        private static void CopyPrefix(string sourceFullPath) => Guard(sourceFullPath);

        private static void Guard(string path)
        {
            var hold = Hold;
            if (hold == null || !hold.Holding || path == null || t_writing || t_guarding || t_watcher) return;
            t_guarding = true;
            try
            {
                if (!hold.Holds(Path.GetFileName(path))) return;
                string full = Path.GetFullPath(path);
                if (hold.IsWaiting(full)) hold.ReadPast(full, Reader());
            }
            catch (Exception)
            {
                // a path the runtime would refuse too
            }
            finally
            {
                t_guarding = false;
            }
        }

        [ThreadStatic]
        private static bool t_guarding;

        // Mono's file watcher (System.IO.DefaultWatcher) looks for each watched file every 750 ms on a
        // thread of its own; once a look is seen to come from it, that thread is passed over at once.
        [ThreadStatic]
        private static bool t_watcher;

        private const string WatcherType = "System.IO.DefaultWatcher";

        private static readonly HashSet<string> Frameworks = new HashSet<string>(StringComparer.Ordinal) { "mscorlib", "System", "BepInEx", "BepInEx.Preloader", "0Harmony", "Vigil.Preloader" };
        private static readonly string[] FrameworkFamilies = { "System.", "MonoMod.", "Mono." };

        // The plugin whose code asked for the file: the first frame outside the runtime, BepInEx, Harmony
        // and this patcher. Null when no mod's code is on the stack, or when Mono's watcher is looking for
        // the file: on its own thread, or on a mod's while the mod switches the watcher on (its first look).
        private static string Reader()
        {
            bool watcher = false;
            Assembly mod = null;
            foreach (var frame in new System.Diagnostics.StackTrace(2, false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
            {
                var type = frame.GetMethod()?.DeclaringType;
                if (type == null) continue;
                if (type.FullName == WatcherType) watcher = true;
                string name = type.Assembly.GetName().Name;
                if (mod == null && !Frameworks.Contains(name) && !FrameworkFamilies.Any(f => name.StartsWith(f, StringComparison.Ordinal))) mod = type.Assembly;
            }

            if (watcher)
            {
                if (mod == null) t_watcher = true;
                return null;
            }

            return mod == null ? null : PluginOf(mod) ?? mod.GetName().Name;
        }

        private static string PluginOf(Assembly assembly)
        {
            try
            {
                string location = assembly.Location;
                if (string.IsNullOrEmpty(location)) return null;
                return BepInEx.Bootstrap.Chainloader.PluginInfos.Values
                    .FirstOrDefault(p => string.Equals(p.Location, location, StringComparison.OrdinalIgnoreCase))?.Metadata.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteNow(object file)
        {
            bool was = t_writing;
            t_writing = true;
            try { ((ConfigFile)file).Save(); }
            finally { t_writing = was; }
        }

        private static string Stamp(string path)
        {
            try
            {
                var f = new FileInfo(path);
                return f.Exists ? f.Length.ToString(Ci) + ":" + f.LastWriteTimeUtc.Ticks.ToString(Ci) : "";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        // Each file written at the end, read back as BepInEx reads it, against the values its settings hold now.
        private static string[] CheckWritten(List<object> written)
        {
            int files = 0, settings = 0;
            var differences = new List<string>();
            foreach (var file in written.OfType<ConfigFile>())
            {
                try
                {
                    var disk = CfgValues.Parse(File.ReadAllLines(file.ConfigFilePath));
                    var live = file.Select(kv => new KeyValuePair<string, string>(CfgValues.Key(kv.Key.Section, kv.Key.Key), kv.Value.GetSerializedValue())).ToList();
                    files++;
                    settings += live.Count;
                    foreach (var d in CfgValues.Differences(disk, live, MaxDifferences - differences.Count)) differences.Add(Path.GetFileName(file.ConfigFilePath) + " " + d);
                }
                catch (Exception e)
                {
                    if (differences.Count < MaxDifferences) differences.Add(Path.GetFileName(file.ConfigFilePath) + " could not be read back: " + e.Message);
                }
            }

            return new[] { files.ToString(Ci), settings.ToString(Ci) }.Concat(differences).ToArray();
        }
    }
}
