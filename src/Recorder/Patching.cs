using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Vigil.Recorder
{
    /// <summary>
    /// Installs one Harmony patch on its own: a method missing in this game version or a patch
    /// that fails only loses that one measurement, noted for the session summary.
    /// </summary>
    internal static class Patching
    {
        public static readonly List<string> Problems = new List<string>();

        /// <summary>Applies the patches; false (with the reason in <see cref="Problems"/>) when it could not.</summary>
        public static bool Apply(Harmony harmony, MethodBase original, string label,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
        {
            if (original == null) { Problems.Add(label + ": not found in this game version"); return false; }
            try { harmony.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer); return true; }
            catch (Exception e) { Problems.Add(label + ": " + e.GetType().Name + " " + e.Message); return false; }
        }

        public static HarmonyMethod M(Type t, string name) => new HarmonyMethod(t, name);
    }
}
