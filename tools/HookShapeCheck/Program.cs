using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Vigil.Recorder.HookShapeCheck
{
    /// <summary>
    /// Methods shaped like the game methods a deep capture hooks. AddString has the shape of
    /// Terminal.AddString: a loop whose try block ends in "break".
    /// </summary>
    public sealed class Shapes
    {
        public List<string> Buffer = new List<string>();
        public int MaxVisible = 30;
        public int Updates;
        public int Count;

        public void AddString(string text)
        {
            while (MaxVisible > 1)
            {
                try
                {
                    Buffer.Add(text);
                    while (Buffer.Count > 300) Buffer.RemoveAt(0);
                    Update();
                    break;
                }
                catch (Exception)
                {
                    MaxVisible--;
                }
            }
        }

        private void Update()
        {
            Steps.Add("body");
            // Ends a runaway loop for the check: the loop condition then stops it.
            if (++Updates >= 1000) MaxVisible = 0;
        }

        // The first instruction is the target of the loop's jump back.
        public void Spin() { do { Count++; Steps.Add("body"); } while (Count < 5); }

        // The first instruction opens a try block.
        public void TryFirst() { try { Count++; Steps.Add("body"); } finally { Count += 10; } }

        public static readonly List<string> Steps = new List<string>();
    }

    /// <summary>Stand-ins for Vigil's hook methods (the timer pair and the split pair) and one mod prefix.</summary>
    public static class Hooks
    {
        public static bool ModSkipsOriginal;
        public static void Pre() => Shapes.Steps.Add("pre");
        public static void Fin() => Shapes.Steps.Add("fin");
        public static void MarkPrefix() => Shapes.Steps.Add("mark");
        public static void OrigPost(bool __runOriginal) => Shapes.Steps.Add(__runOriginal ? "post" : "post(skipped)");
        public static bool ModPrefix() { Shapes.Steps.Add("mod"); return !ModSkipsOriginal; }
        public static IEnumerable<CodeInstruction> Unchanged(IEnumerable<CodeInstruction> instructions) => instructions;
    }

    public static class Program
    {
        private static int s_failures;

        private static HarmonyMethod H(string name, int priority) => new HarmonyMethod(typeof(Hooks), name) { priority = priority };

        // The layout PhaseTimer uses: the timer pair in one patch call, the split pair in a second one.
        private static void VigilHooks(Harmony h, MethodBase m)
        {
            h.Patch(m, prefix: H(nameof(Hooks.Pre), Priority.First), finalizer: H(nameof(Hooks.Fin), Priority.Last));
            h.Patch(m, prefix: H(nameof(Hooks.MarkPrefix), int.MinValue), postfix: H(nameof(Hooks.OrigPost), int.MaxValue));
        }

        private static void Check(string label, string method, Action<Harmony, MethodBase> hooks, bool modSkips, string expectedSteps, int expectedCount = -1)
        {
            var m = AccessTools.Method(typeof(Shapes), method);
            var mod = new Harmony("check.mod");
            var probe = new Harmony("check.probe");
            mod.Patch(m, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.ModPrefix)));
            hooks?.Invoke(probe, m);
            Hooks.ModSkipsOriginal = modSkips;
            Shapes.Steps.Clear();
            var s = new Shapes();
            if (method == nameof(Shapes.AddString)) s.AddString("vigil census"); else m.Invoke(s, null);
            mod.UnpatchSelf();
            probe.UnpatchSelf();

            string steps = string.Join(" ", Shapes.Steps.Take(12)) + (Shapes.Steps.Count > 12 ? " ..." : "");
            bool ok = steps == expectedSteps && (expectedCount < 0 || s.Count == expectedCount);
            if (!ok) s_failures++;
            Console.WriteLine("{0}  {1,-46} {2}{3}", ok ? "ok  " : "FAIL", label, steps,
                ok ? "" : "   (expected: " + expectedSteps + (expectedCount >= 0 ? ", Count " + expectedCount + ", got " + s.Count : "") + ")");
        }

        public static int Main()
        {
            Console.WriteLine("Vigil hook layout on game-shaped methods, HarmonyX " + typeof(Harmony).Assembly.GetName().Version);
            Check("AddString shape", nameof(Shapes.AddString), VigilHooks, false, "pre mod mark body post fin");
            Check("AddString shape, a mod prefix skips it", nameof(Shapes.AddString), VigilHooks, true, "pre mod mark post(skipped) fin");
            Check("loop back to the first instruction", nameof(Shapes.Spin), VigilHooks, false, "pre mod mark body body body body body post fin", 5);
            Check("try block at the first instruction", nameof(Shapes.TryFirst), VigilHooks, false, "pre mod mark body post fin", 11);

            // Why Vigil never adds a transpiler: with this HarmonyX, a transpiler that changes
            // nothing still turns the "break" into another round of the loop.
            var m = AccessTools.Method(typeof(Shapes), nameof(Shapes.AddString));
            var h = new Harmony("check.transpiler");
            h.Patch(m, transpiler: H(nameof(Hooks.Unchanged), Priority.Normal));
            var t = new Shapes();
            t.AddString("x");
            h.UnpatchSelf();
            Console.WriteLine("info  {0,-46} body ran {1} times{2}", "AddString shape with a do-nothing transpiler", t.Updates,
                t.Updates > 1 ? " (this HarmonyX breaks the loop: transpilers stay out of Vigil)" : " (this HarmonyX keeps the loop intact)");

            Console.WriteLine(s_failures == 0 ? "All checks passed." : s_failures + " check(s) failed.");
            return s_failures == 0 ? 0 : 1;
        }
    }
}
