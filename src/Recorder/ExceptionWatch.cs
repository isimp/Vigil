using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Counts every exception thrown in the game process the moment it is thrown, caught or not,
    /// by type and by the mod (or game) method that threw it. Mods that catch their own errors and
    /// carry on leave no log line, yet each exception costs time; a storm of them shows up here.
    /// Only counts: the exception itself is untouched.
    /// </summary>
    internal static class ExceptionWatch
    {
        private sealed class ProbeException : Exception { }

        private static readonly object s_lock = new object();
        private static readonly ValueTally s_tally = new ValueTally(null);
        private static Dictionary<Assembly, string> s_owners;
        [ThreadStatic] private static bool t_inside;

        /// <summary>The runtime raises first-chance notifications (checked with one exception at start).</summary>
        public static bool Works { get; private set; }

        /// <summary>The runtime also says which method threw.</summary>
        public static bool KnowsThrower { get; private set; }

        public static void Start()
        {
            s_owners = PhaseTimer.ModAssemblies();
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            try { throw new ProbeException(); }
            catch (ProbeException) { }
        }

        private static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
        {
            if (!ProbeState.Active) return;
            // A failure in here would raise this handler again on the same thread.
            if (t_inside) return;
            t_inside = true;
            try
            {
                var ex = e.Exception;
                if (ex is ProbeException)
                {
                    Works = true;
                    KnowsThrower = Thrower(ex) != null;
                    return;
                }
                string label = ex.GetType().Name + " in " + Where(ex);
                lock (s_lock) s_tally.Add(label, 0);
            }
            catch (Exception) { }
            finally { t_inside = false; }
        }

        private static MethodBase Thrower(Exception ex)
        {
            try { return ex.TargetSite; } catch (Exception) { return null; }
        }

        private static string Where(Exception ex)
        {
            var site = Thrower(ex);
            var type = site?.DeclaringType;
            if (type == null) return "(thrower unknown)";
            var asm = type.Assembly;
            string owner = s_owners != null && s_owners.TryGetValue(asm, out var mod) ? mod
                : asm.GetName().Name.StartsWith("assembly_", StringComparison.Ordinal) ? "Valheim" : asm.GetName().Name;
            return owner + " (" + type.Name + "." + site.Name + ")";
        }

        public static void EndSecond(out string top, out long count)
        {
            lock (s_lock) s_tally.EndSecond(out top, out _, out count);
        }

        public static string Peek(float seconds) { lock (s_lock) return s_tally.Peek(seconds, 15); }
        public static void EndWindow(float seconds) { lock (s_lock) s_tally.EndWindow(seconds, 0); }
        public static string Summary(float seconds) { lock (s_lock) return s_tally.Summary(seconds, 25); }
    }
}
