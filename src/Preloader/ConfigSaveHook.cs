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
    /// settings object. All go in from the patcher, before the chainloader starts, so
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
