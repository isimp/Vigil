using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Mono.Cecil;

namespace Vigil.Startup
{
    /// <summary>
    /// Vigil's BepInEx patcher. BepInEx calls <see cref="Initialize"/> before any plugin loads, then
    /// reads <see cref="TargetDLLs"/> and hands each named assembly to <see cref="Patch"/>. Initialize
    /// starts the startup timeline (a log listener that times the lines marking each plugin) and tidies
    /// the bundle cache; Patch edits Unity's AssetBundleModule so mods' bundle loads ask the cache first.
    /// Nothing here touches a Unity type: an assembly loaded this early could no longer be edited.
    /// </summary>
    public static class Preloader
    {
        private static readonly string[] BundleModule = { "UnityEngine.AssetBundleModule.dll" };

        internal static ManualLogSource Log { get; private set; }

        /// <summary>Unity's bundle module when the cache is on in this game, nothing otherwise.</summary>
        public static IEnumerable<string> TargetDLLs => StartupState.CacheOn ? BundleModule : Array.Empty<string>();

        public static void Initialize()
        {
            Log = Logger.CreateLogSource("Vigil.Preloader");
            try
            {
                StartupState.Begin();
                Log.LogInfo(StartupState.Status());
            }
            catch (Exception e)
            {
                Log.LogError("Vigil's patcher could not start: " + e);
            }
        }

        public static void Patch(AssemblyDefinition assembly)
        {
            if (assembly == null) return;
            try
            {
                var module = assembly.MainModule;
                var name = typeof(Preloader).Assembly.GetName();
                string problem = BundleHookEdit.Apply(module, BundleHookEdit.HookType(module, name.Name, name.Version));
                StartupState.EditResult = problem ?? StartupState.Edited;
                if (problem == null) Log.LogInfo("Bundle cache: Unity's bundle loading asks the cache first (" + assembly.Name.Name + " edited in memory).");
                else Log.LogWarning("Bundle cache: Unity's bundle loading was left as it is, so bundles load the usual way: " + problem);
            }
            catch (Exception e)
            {
                StartupState.EditResult = e.GetType().Name + ": " + e.Message;
                Log.LogError("Bundle cache: the edit of Unity's bundle loading failed, bundles load the usual way: " + e);
            }
        }

        public static void Finish()
        {
        }
    }
}
