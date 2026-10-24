using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The session's Harmony patch map (Vigil-patches.tsv: every mod patch on a game method,
    /// its kind, priority and whether it can stop the original), and every patch change made
    /// while the game is already running. Harmony rebuilds the whole method each time any patch
    /// on it is added or removed and re-runs every transpiler on it, which can freeze a frame; a
    /// mod that patches and unpatches during play is named here with the time it took.
    /// </summary>
    internal static class PatchWatch
    {
        private sealed class Change { public long Count; public double Ms; public double MaxMs; }

        private static readonly Dictionary<string, Change> s_changes = new Dictionary<string, Change>();
        private static Dictionary<Assembly, string> s_owners;
        private static Assembly s_self;
        private static int s_mainThread;
        private static bool s_armed;
        [ThreadStatic] private static long t_start;
        [ThreadStatic] private static int t_depth;

        public static bool Watching { get; private set; }

        /// <summary>Hooks Harmony's own method rebuild. Changes are only counted once <see cref="Arm"/> is called after loading.</summary>
        public static void Start(Harmony harmony, InstrumentJournal journal, List<string> notes)
        {
            s_self = typeof(PatchWatch).Assembly;
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            const string id = "Harmony method rebuilds";
            // Harmony's own assembly is asked for the type: Harmony's search by name lists every type of every assembly.
            var functions = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchFunctions", false);
            var m = functions != null ? AccessTools.Method(functions, "UpdateWrapper") : null;
            if (m == null) { notes.Add("Patch changes during play are not watched: Harmony's rebuild method was not found."); return; }
            if (!journal.Allowed(id)) { notes.Add("Patch changes during play are not watched (that hook crashed the game once)."); return; }
            journal.Begin(id);
            try
            {
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(PatchWatch), nameof(Pre)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PatchWatch), nameof(Fin)) { priority = Priority.Last });
                Watching = true;
            }
            catch (Exception e) { notes.Add("Patch changes during play are not watched: " + e.GetType().Name); }
            finally { journal.Done(id); }
        }

        /// <summary>From now on every rebuild is a change made during play.</summary>
        public static void Arm() { s_owners = PhaseTimer.ModAssemblies(); s_armed = true; }

        private static void Pre()
        {
            if (!s_armed || Thread.CurrentThread.ManagedThreadId != s_mainThread) return;
            if (t_depth++ == 0) t_start = Stopwatch.GetTimestamp();
        }

        private static void Fin(MethodBase original)
        {
            if (!s_armed || Thread.CurrentThread.ManagedThreadId != s_mainThread || t_depth == 0) return;
            if (--t_depth > 0) return;
            double ms = PhaseTimer.TicksToMs(Stopwatch.GetTimestamp() - t_start);
            string who = Culprit();
            if (who == null) return; // Vigil's own timers
            string what = Describe(original);
            string key = who + " rebuilt " + what;
            if (!s_changes.TryGetValue(key, out var c)) s_changes[key] = c = new Change();
            c.Count++; c.Ms += ms; if (ms > c.MaxMs) c.MaxMs = ms;
            EventLog.Write("patch-change", who, what + " rebuilt in " + ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
        }

        /// <summary>The first mod method on the call stack, or null when Vigil itself is patching.</summary>
        private static string Culprit()
        {
            try
            {
                var frames = new StackTrace(2, false).GetFrames() ?? new StackFrame[0];
                foreach (var f in frames)
                {
                    var asm = f.GetMethod()?.DeclaringType?.Assembly;
                    if (asm == null) continue;
                    if (asm == s_self) return null;
                    if (s_owners != null && s_owners.TryGetValue(asm, out var mod)) return mod;
                }
            }
            catch (Exception) { }
            return "unknown";
        }

        private static string Describe(MethodBase m)
        {
            if (m == null) return "?";
            string args;
            try { args = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()); } catch (Exception) { args = "?"; }
            return (m.DeclaringType?.Name ?? "?") + "." + m.Name + "(" + args + ")";
        }

        public static string Summary()
        {
            if (!Watching) return "   not watched\r\n";
            if (s_changes.Count == 0) return "   none\r\n";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in s_changes.OrderByDescending(k => k.Value.Ms).Take(25))
                sb.AppendFormat(ci, "   {0,8:0.0} ms in {1} rebuilds (worst {2:0.0} ms)  {3}\r\n", kv.Value.Ms, kv.Value.Count, kv.Value.MaxMs, kv.Key);
            return sb.ToString();
        }

        /// <summary>Writes Vigil-patches.tsv: every mod patch on a game method, Vigil's own timers left out.</summary>
        public static string WriteMap(string dir)
        {
            var rows = Rows(null);
            string path = Path.Combine(dir, "Vigil-patches.tsv");
            string text = PatchMap.Format(rows);
            ThreadPool.QueueUserWorkItem(_ => OwnThreads.Timed(() => { try { File.WriteAllText(path, text); } catch (Exception) { } }));
            if (ResultStore.Shared.Shown)
            {
                var result = InspectResult.Patches(DateTime.Now, rows);
                result.File = Path.GetFileName(path);
                result.Path = path;
                ResultStore.Shared.Put(ResultStore.Patches, result);
            }
            return rows.Count + " patches on " + rows.Select(r => r.Method).Distinct().Count() + " methods written to Vigil-patches.tsv";
        }

        /// <summary>Every mod patch on the game methods <paramref name="include"/> takes (all when null), Vigil's own timers left out.</summary>
        public static List<PatchRow> Rows(Func<MethodBase, bool> include)
        {
            var owners = PhaseTimer.ModAssemblies();
            var rows = new List<PatchRow>();
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                if (include != null && !include(original)) continue;
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch (Exception) { continue; }
                if (info == null) continue;
                string method = Describe(original);
                Add(rows, method, "prefix", info.Prefixes, owners);
                Add(rows, method, "postfix", info.Postfixes, owners);
                Add(rows, method, "transpiler", info.Transpilers, owners);
                Add(rows, method, "finalizer", info.Finalizers, owners);
                Add(rows, method, "ilmanipulator", info.ILManipulators, owners);
            }
            return rows;
        }

        private static void Add(List<PatchRow> rows, string method, string kind, IEnumerable<Patch> patches, Dictionary<Assembly, string> owners)
        {
            if (patches == null) return;
            foreach (var p in patches)
            {
                if (p?.owner != null && p.owner.StartsWith("isimp.Vigil", StringComparison.Ordinal)) continue;
                var pm = p?.PatchMethod;
                var asm = pm?.DeclaringType?.Assembly;
                string owner = asm != null && owners.TryGetValue(asm, out var mod) ? mod : p?.owner ?? "?";
                rows.Add(new PatchRow
                {
                    Method = method, Kind = kind, Owner = owner, Priority = p?.priority ?? 0,
                    Patch = (pm?.DeclaringType?.Name ?? "?") + "." + (pm?.Name ?? "?"),
                    CanSkip = kind == "prefix" && pm is MethodInfo mi && mi.ReturnType == typeof(bool),
                });
            }
        }
    }
}
