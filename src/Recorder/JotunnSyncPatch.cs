using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Jotunn's config sync with one copy of the keys" (<see cref="KeySnapshot{T}"/>):
    /// Jotunn's <c>SynchronizationManager.ApplyConfigZPackage</c> checks each synced setting with
    /// <c>config.Keys.Contains(...)</c>, and BepInEx's <c>ConfigFile.Keys</c> copies every key of the file
    /// each time. A prefix and a finalizer on Jotunn's method mark its run on this thread; inside that
    /// run a prefix on <c>ConfigFile.Keys</c> hands out one snapshot of the file's keys per run (made again
    /// when the file's count of settings changes), whose <c>Contains</c> asks a set. Its first answers are
    /// asked both ways, and at a disagreement Jotunn's own way comes back for good. Outside the run
    /// <c>Keys</c> is untouched. Prefixes and finalizers only, no transpiler (see HookShapeTests).
    /// The switch is this game's own: the answers are the same, only quicker.
    /// </summary>
    internal static class JotunnSyncPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.jotunnsync";
        private const string Guid = "com.jotunn.jotunn";
        private const string TypeName = "Jotunn.Managers.SynchronizationManager", MethodName = "ApplyConfigZPackage";

        private static readonly KeyCheckAgreement Agreement = new KeyCheckAgreement(3);
        private static readonly KeySnapshots<ConfigFile, ConfigDefinition> Snapshots = new KeySnapshots<ConfigFile, ConfigDefinition>(Agreement);

        [ThreadStatic]
        private static int t_inSync;

        [ThreadStatic]
        private static bool t_copying;

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }

        public static string Version { get; private set; }

        /// <summary>Why the patch stepped aside; null while it is in (or Jotunn is absent).</summary>
        public static string Problem { get; private set; }

        public static long Answered => Agreement.Answered;

        public static string State => KeyCheckAgreement.StateLine(On, Found, Version, Problem ?? Agreement.Problem, Agreement.Answered, Agreement.Agreed);

        public static void Hook()
        {
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == Guid);
            Found = info != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            var type = TypeLookup.Loaded.Find(TypeName);
            Problem = Install(type != null ? AccessTools.Method(type, MethodName) : null);
        }

        /// <summary>Puts the hooks in on Jotunn's method; why not, or null when they are in.</summary>
        internal static string Install(System.Reflection.MethodBase apply)
        {
            var keys = AccessTools.PropertyGetter(typeof(ConfigFile), nameof(ConfigFile.Keys));
            if (apply == null || keys == null || keys.ReturnType != typeof(ICollection<ConfigDefinition>)) return "Jotunn's config sync was not found (an update changed it)";
            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(apply, prefix: new HarmonyMethod(typeof(JotunnSyncPatch), nameof(SyncPrefix)), finalizer: new HarmonyMethod(typeof(JotunnSyncPatch), nameof(SyncFinalizer)));
                harmony.Patch(keys, prefix: new HarmonyMethod(typeof(JotunnSyncPatch), nameof(KeysPrefix)) { priority = Priority.First });
                return null;
            }
            catch (Exception e)
            {
                return "Jotunn could not be patched: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static void SyncPrefix() => t_inSync++;

        // A finalizer, so the mark comes off even when Jotunn's method throws; the exception goes on as before.
        private static void SyncFinalizer()
        {
            if (t_inSync > 0) t_inSync--;
            if (t_inSync == 0) Snapshots.Clear();
        }

        // Called by Harmony on every ConfigFile.Keys: outside Jotunn's sync it lets BepInEx answer.
        private static bool KeysPrefix(ConfigFile __instance, ref ICollection<ConfigDefinition> __result)
        {
            if (t_inSync == 0 || t_copying || !On || !Agreement.UseKey) return true;
            try
            {
                __result = Snapshots.Get(__instance, __instance.Count, () => Copy(__instance));
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static ConfigDefinition[] Copy(ConfigFile file)
        {
            t_copying = true;
            try { return file.Keys.ToArray(); }
            finally { t_copying = false; }
        }
    }
}
