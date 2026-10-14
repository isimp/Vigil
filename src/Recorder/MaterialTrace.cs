using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Vigil.Core;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// Console "vigil trace materials": names the code that makes material copies. Asking a
    /// renderer for <c>material</c>, <c>materials</c> or <c>GetMaterials</c> gives it copies of its
    /// own, and objects with copies can no longer be drawn together with others of their kind. While
    /// the trace runs, a prefix on those three records the first caller outside Unity and Vigil,
    /// its mod and the object, for every call that makes a copy now; it then takes itself off and
    /// writes Vigil-material-copies-*.txt. Only prefixes; nothing is changed.
    /// The getters are tiny, so code compiled before the trace started may have them inlined and
    /// is not seen; started from the main menu, the trace sees everything a world load runs.
    /// </summary>
    internal static class MaterialTrace
    {
        private const string Suffix = " (Instance)";
        private static Harmony s_harmony;
        private static CopyTrace s_trace;
        private static Dictionary<Assembly, string> s_owners;
        private static bool s_on, s_busy;
        private static float s_until;
        private static string s_dir, s_started;
        private static readonly Assembly s_self = typeof(MaterialTrace).Assembly;

        public static bool Running => s_on;

        public static string Start(string dir, float seconds)
        {
            if (s_on) return "the material trace is already running (" + Math.Max(0, s_until - Time.realtimeSinceStartup).ToString("0", CultureInfo.InvariantCulture) + " s left)";
            s_dir = dir;
            s_owners = PhaseTimer.ModAssemblies();
            s_trace = new CopyTrace();
            s_harmony = s_harmony ?? new Harmony("isimp.Vigil.materials");
            var me = typeof(MaterialTrace);
            int hooked = 0;
            if (Patching.Apply(s_harmony, AccessTools.PropertyGetter(typeof(Renderer), "material"), "material copies (Renderer.material)", prefix: Patching.M(me, nameof(Prefix)))) hooked++;
            if (Patching.Apply(s_harmony, AccessTools.PropertyGetter(typeof(Renderer), "materials"), "material copies (Renderer.materials)", prefix: Patching.M(me, nameof(Prefix)))) hooked++;
            if (Patching.Apply(s_harmony, AccessTools.Method(typeof(Renderer), "GetMaterials", new[] { typeof(List<Material>) }), "material copies (Renderer.GetMaterials)", prefix: Patching.M(me, nameof(Prefix)))) hooked++;
            if (hooked == 0) return "the material trace could not hook Unity's material getters";
            s_started = DateTime.Now.ToString("HH:mm:ss");
            s_until = Time.realtimeSinceStartup + seconds;
            s_on = true;
            bool inWorld = Player.m_localPlayer != null;
            return string.Format(CultureInfo.InvariantCulture, "tracing material copies for {0:0} s{1}", seconds,
                inWorld ? "; code that already ran may be missed, so start it from the main menu next time, or walk far away and back so the area loads again"
                        : "; join the world now and go to the place you want to check");
        }

        /// <summary>Ends the trace when its time is up (called every frame).</summary>
        public static void Update()
        {
            if (s_on && Time.realtimeSinceStartup >= s_until) Finish("time is up");
        }

        public static string Stop() => s_on ? Finish("stopped from the console") : "no material trace is running";

        private static string Finish(string why)
        {
            s_on = false;
            try { s_harmony?.UnpatchSelf(); } catch (Exception) { }
            string file = "Vigil-material-copies-" + DateTime.Now.ToString("HHmmss") + ".txt";
            try
            {
                var lines = new List<string>
                {
                    "Vigil material copies   from " + s_started + " to " + DateTime.Now.ToString("HH:mm:ss") + " (" + why + ")",
                    "Each line is code that asked a renderer for its material(s) while the renderer still shared them, which made",
                    "copies for that renderer alone: objects with copies cannot be drawn together with others of their kind.",
                    "Callers compiled before the trace started may be missing (the getters can be inlined into them).",
                    s_trace.Total + " copies made.",
                    "",
                };
                lines.AddRange(s_trace.Report(40));
                File.WriteAllLines(Path.Combine(s_dir, file), lines.ToArray());
            }
            catch (Exception e) { return "material trace ended but its report failed: " + e.GetType().Name + " " + e.Message; }
            var result = InspectResult.Trace(DateTime.Now, s_trace, why);
            result.File = file;
            result.Path = Path.Combine(s_dir, file);
            ResultStore.Shared.Put(ResultStore.Trace, result);
            string msg = "material trace ended (" + why + "): " + s_trace.Total + " copies, report " + file;
            try { MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, "Vigil: " + msg); } catch (Exception) { }
            return msg;
        }

        private static void Prefix(Renderer __instance)
        {
            if (!s_on || s_busy) return;
            s_busy = true;
            try
            {
                // Only a call that makes a copy now: the renderer still draws with a shared material.
                var shared = __instance.sharedMaterial;
                if (shared == null || shared.name.EndsWith(Suffix, StringComparison.Ordinal)) return;
                Caller(out string caller, out string owner);
                s_trace.Add(caller, owner, ObjectName(__instance));
            }
            catch (Exception) { }
            finally { s_busy = false; }
        }

        // Frames that only pass the call on: Unity, the runtime and reflection (a getter reached
        // through PropertyInfo.GetValue), Harmony and MonoMod, and Vigil itself.
        private static bool PassesOn(Assembly asm)
        {
            if (asm == s_self) return true;
            string n = asm.GetName().Name;
            return n.StartsWith("UnityEngine", StringComparison.Ordinal) || n == "mscorlib" || n == "netstandard" || n == "System"
                || n.StartsWith("System.", StringComparison.Ordinal) || n == "0Harmony" || n.StartsWith("MonoMod", StringComparison.Ordinal)
                || n.StartsWith("Mono.", StringComparison.Ordinal);
        }

        /// <summary>The first caller that is not Unity, the runtime or a patching library, with the two that called it.</summary>
        private static void Caller(out string caller, out string owner)
        {
            caller = "?"; owner = "?";
            var frames = new StackTrace(1, false).GetFrames();
            if (frames == null) return;
            var chain = new List<string>();
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                var t = m?.DeclaringType;
                if (t == null) continue;                       // Harmony's replacement method
                var asm = t.Assembly;
                if (chain.Count == 0 && PassesOn(asm)) continue;
                if (chain.Count == 0)
                    owner = s_owners != null && s_owners.TryGetValue(asm, out var mod) ? mod
                        : asm.GetName().Name.StartsWith("assembly_", StringComparison.Ordinal) ? "Valheim" : asm.GetName().Name;
                chain.Add(t.FullName + "." + m.Name);
                if (chain.Count == 3) break;
            }
            if (chain.Count > 0) caller = string.Join("  <-  ", chain.ToArray());
        }

        private static string ObjectName(Renderer r)
        {
            var view = r.GetComponentInParent<ZNetView>(true);
            return view != null ? Utils.GetPrefabName(view.gameObject) : Utils.GetPrefabName(r.gameObject) + " (not a world object)";
        }
    }
}
