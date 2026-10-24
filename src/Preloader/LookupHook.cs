using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// Puts a <see cref="LookupMemory"/> in front of BepInEx's search for an assembly's DLL
    /// (<c>Utility.TryResolveDllAssembly</c>, which the preloader's resolve handler runs for BepInEx's
    /// folder, the patchers and the plugins, listing every folder under each): a search that found
    /// nothing is not run again for the same assembly and folder until the start is over. The handler's
    /// look among the assemblies already loaded runs as before, so an assembly loaded later is found.
    /// Goes in from the patcher, before the chainloader starts.
    /// </summary>
    internal static class LookupHook
    {
        private const string HarmonyId = "isimp.Vigil.lookups";

        // Kept for the session: its patches stay in.
        private static Harmony s_harmony;

        public static LookupMemory Memory { get; private set; }

        /// <summary>Why the memory is not on, when it was asked for and could not be put in; null otherwise.</summary>
        public static string Problem { get; private set; }

        public static void Install()
        {
            var memory = new LookupMemory();
            try
            {
                var search = AccessTools.Method(typeof(Utility), nameof(Utility.TryResolveDllAssembly),
                    new[] { typeof(AssemblyName), typeof(string), typeof(Assembly).MakeByRefType() });
                if (search == null)
                {
                    Problem = "BepInEx's assembly search has another shape";
                    return;
                }

                s_harmony = new Harmony(HarmonyId);
                s_harmony.Patch(search,
                    prefix: new HarmonyMethod(typeof(LookupHook), nameof(SearchPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(LookupHook), nameof(SearchPostfix)));
            }
            catch (Exception e)
            {
                Problem = e.GetType().Name + ": " + e.Message;
                return;
            }

            Memory = memory;
            memory.Begin();
        }

        /// <summary>Ends the memory for the session; null when there was none. Later calls give the first result.</summary>
        public static LookupResult End(string why) => Memory?.End(why);

        // Called by Harmony: false skips the folder search, answered as nothing found.
        private static bool SearchPrefix(AssemblyName assemblyName, string directory, ref Assembly assembly, ref bool __result)
        {
            var memory = Memory;
            if (memory == null || assemblyName == null || !memory.KnownMissing(assemblyName.Name, directory)) return true;
            assembly = null;
            __result = false;
            return false;
        }

        private static void SearchPostfix(AssemblyName assemblyName, string directory, bool __result)
        {
            if (!__result && assemblyName != null) Memory?.Missing(assemblyName.Name, directory);
        }
    }
}
