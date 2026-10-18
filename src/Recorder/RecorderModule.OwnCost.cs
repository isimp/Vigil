using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Vigil's own cost" in the panel's Health tab and at the top of the summary (<see cref="OwnCost"/>):
    /// its own loops and the recorder's frame work, the panel while drawn, its always-on timers, its
    /// hooks in the game's methods (measured by <see cref="OwnHooks"/> for a few seconds, once on its
    /// own two minutes into a session and whenever asked), its threads, the files it writes and its
    /// own network messages.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private OwnHooks _ownHooks;
        private float _ownAutoFrom = -1f;
        private bool _ownAutoDone;
        private double _filesKb, _filesAt = double.NegativeInfinity;

        /// <summary>Every frame: the hook measurement's next step, and its first run two minutes into a session (in a world on a player's game).</summary>
        private void UpdateOwnCost()
        {
            if (_ownHooks == null) return;
            float now = Time.realtimeSinceStartup;
            _ownHooks.Step(now, 2.0);
            if (_ownAutoDone) return;
            bool ready = _server ? ZNet.instance != null : Player.m_localPlayer != null;
            if (!ready) return;
            if (_ownAutoFrom < 0) _ownAutoFrom = now;
            if (now - _ownAutoFrom < OwnHooks.AutoAfterSeconds) return;
            _ownAutoDone = true;
            _ownHooks.Start();
        }

        private OwnCostFacts OwnFacts()
        {
            var f = new OwnCostFacts();
            if (_recorder == null) return f;
            long frames = Math.Max(1, _recorder.FramesAll);
            long loops = 0, panel = 0, selfTest = 0, timerCalls = 0;
            foreach (var s in _timer?.Slots ?? new List<PhaseTimer.Slot>())
            {
                if (s.Deep) continue;
                if (s.Mod == OwnTime.Mod)
                {
                    if (s.Label != null && s.Label.EndsWith("Panel.OnGUI", StringComparison.Ordinal)) panel += s.RunTicks;
                    else if (s.Label != null && s.Label.IndexOf("SelfTestRun", StringComparison.Ordinal) >= 0) selfTest += s.RunTicks;
                    else loops += s.RunTicks;
                }
                else timerCalls += s.RunCalls;
            }
            double minutes = _recorder.MinutesRunning;
            f.FrameMs = _recorder.MeanFrameMs;
            // The recorder's frame work holds the deep capture's stepping, the reports taken and the join packages built ahead: those are tools.
            double steady = PhaseTimer.TicksToMs(loops + _recorder.ProbeRunTicks - OwnTools.DeepTicks) - OwnTools.ReportMs - OwnTools.JoinAheadMs;
            f.LoopsMs = Math.Max(0, steady) / frames;
            if (OwnTools.SelfTestRuns > 0)
                f.Tools.Add(new ToolCost("self-test", PhaseTimer.TicksToMs(selfTest), OwnTools.SelfTestFrames, OwnTools.SelfTestSeconds, OwnTools.SelfTestRuns));
            if (OwnTools.DeepRuns > 0)
                f.Tools.Add(new ToolCost("deep capture", PhaseTimer.TicksToMs(OwnTools.DeepTicks), OwnTools.DeepFrames, OwnTools.DeepSeconds, OwnTools.DeepRuns));
            if (OwnTools.JoinAheadRuns > 0)
                f.Tools.Add(new ToolCost("join packages built ahead", OwnTools.JoinAheadMs, OwnTools.JoinAheadRuns, OwnTools.JoinAheadMs / 1000.0, OwnTools.JoinAheadRuns));
            f.ReportCount = OwnTools.Reports;
            f.ReportMs = OwnTools.ReportMs;
            f.FramesAll = _recorder.FramesAll;
            f.SessionMinutes = _recorder.MinutesRunning;
            f.PanelMs = PhaseTimer.TicksToMs(panel);
            f.PanelOpenFrames = Panel.OpenFrames;
            f.TimerCallsPerFrame = timerCalls / (double)frames;
            f.TimerPairMs = _ownHooks?.PerCallMs ?? double.NaN;
            f.Hooks = _ownHooks?.Last;
            f.ThreadSecondsPerMinute = OwnThreads.Seconds / minutes;
            f.FilesKbPerMinute = RecordingsKb() / minutes;
            f.NetOutKbs = NetAttribution.OwnBytesOut / 1024.0 / (minutes * 60);
            f.NetInKbs = NetAttribution.OwnBytesIn / 1024.0 / (minutes * 60);
            return f;
        }

        /// <summary>The size of this session's recordings so far (the files written since it started; earlier sessions' left out), read at most every 10 s.</summary>
        private double RecordingsKb()
        {
            if (Now - _filesAt < 10) return _filesKb;
            _filesAt = Now;
            long bytes = 0;
            try
            {
                var since = _recorder.Started;
                foreach (var file in new DirectoryInfo(_dir).GetFiles("*", SearchOption.AllDirectories))
                {
                    if (file.FullName.IndexOf("Vigil-runs", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (file.LastWriteTime >= since) bytes += file.Length;
                }
            }
            catch (Exception) { }
            _filesKb = bytes / 1024.0;
            return _filesKb;
        }

        /// <summary>The block at the top of the summary.</summary>
        private IEnumerable<string> OwnCostLines()
        {
            var f = OwnFacts();
            yield return "Vigil itself: " + OwnCost.FrameLine(f);
            yield return "   tools: " + OwnCost.ToolsLine(f);
            yield return "   session: " + OwnCost.SessionLine(f);
            yield return "   hooks: " + OwnCost.HooksLine(f.Hooks);
            yield return "   panel: " + OwnCost.PanelLine(f);
            yield return "   other threads: " + OwnCost.ThreadsLine(f);
            yield return "   files: " + OwnCost.FilesLine(f);
            yield return "   network: " + OwnCost.NetworkLine(f);
            yield return "   memory: " + OwnCost.MemoryLine;
        }

        private PanelBadge OwnCostBadge() => new PanelBadge(OwnCost.Badge(OwnFacts()), Skin.Tone.Quiet);

        // Game methods that run every frame or step, where some mod's patch is likely.
        private static readonly string[] OftenRun =
        {
            "Player.Update", "Player.FixedUpdate", "Humanoid.CustomFixedUpdate", "Character.CustomFixedUpdate", "Hud.Update", "Minimap.Update",
            "ZNet.Update", "EnvMan.FixedUpdate", "GameCamera.LateUpdate", "Player.LateUpdate",
        };

        /// <summary>Self-test: one other mod's prefix or postfix on a game method that runs often, timed by itself for 2 s, as a deep capture times them.</summary>
        private System.Collections.IEnumerator PatchTimedOnce(List<SelfTestReport.Check> checks)
        {
            const string what = "a mod's patch timed by itself";
            if (_ownHooks == null || _ownHooks.Running)
            {
                checks.Add(new SelfTestReport.Check("Recorder", what, SelfTestReport.Result.Info, "Vigil's own hooks are being measured; not tried now"));
                yield break;
            }
            System.Reflection.MethodBase target = null;
            string label = null;
            foreach (var original in HarmonyLib.Harmony.GetAllPatchedMethods().ToList())
            {
                string name = original.DeclaringType != null ? original.DeclaringType.Name + "." + original.Name : original.Name;
                if (!OftenRun.Contains(name)) continue;
                var info = HarmonyLib.Harmony.GetPatchInfo(original);
                var patch = info?.Prefixes.Concat(info.Postfixes).FirstOrDefault(p => p.PatchMethod != null && p.owner != null && !p.owner.StartsWith("isimp.Vigil", StringComparison.Ordinal)
                    && !p.PatchMethod.ContainsGenericParameters && (p.PatchMethod.DeclaringType == null || !p.PatchMethod.DeclaringType.ContainsGenericParameters));
                if (patch == null) continue;
                target = patch.PatchMethod;
                var asm = target.DeclaringType?.Assembly;
                string mod = asm != null && PhaseTimer.ModAssemblies().TryGetValue(asm, out string m) ? m : patch.owner;
                label = mod + "'s " + (info.Prefixes.Contains(patch) ? "prefix" : "postfix") + " on " + name;
                break;
            }
            if (target == null)
            {
                checks.Add(new SelfTestReport.Check("Recorder", what, SelfTestReport.Result.Info, "no mod patches a game method that runs every frame here"));
                yield break;
            }
            if (!_ownHooks.BeginOne(target, label))
            {
                checks.Add(new SelfTestReport.Check("Recorder", what, SelfTestReport.Result.Warn, "its timer could not be put in: " + label));
                yield break;
            }
            double until = Now + 2;
            while (Now < until) yield return null;
            var (calls, ms) = _ownHooks.EndOne();
            checks.Add(calls > 0
                ? new SelfTestReport.Check("Recorder", what, SelfTestReport.Result.Pass, label + ": " + calls.ToString("N0", Wc) + " calls, " + ms.ToString("0.000", Wc) + " ms in 2 s")
                : new SelfTestReport.Check("Recorder", what, SelfTestReport.Result.Info, label + ": not called in 2 s (the runtime may have folded it into the game's method, or the method did not run)"));
        }

        /// <summary>Self-test: the timers' own cost was calibrated to something sensible (above nothing, under 10 microseconds a call), and the cost so far.</summary>
        private System.Collections.IEnumerator OwnCostOnce(List<SelfTestReport.Check> checks)
        {
            const string what = "Vigil's own cost";
            double perCall = _ownHooks?.PerCallMs ?? double.NaN;
            var f = OwnFacts();
            bool sane = !double.IsNaN(perCall) && perCall > 0 && perCall < 0.01;
            checks.Add(sane
                ? new SelfTestReport.Check("Recorder", what, SelfTestReport.Result.Pass, OwnCost.FrameLine(f) + "; a timer pair costs " + (perCall * 1000).ToString("0.00", Wc) + " microseconds")
                : new SelfTestReport.Check("Recorder", what, SelfTestReport.Result.Warn, "the timers' own cost could not be calibrated (" + (_ownHooks?.Problem ?? perCall.ToString(Wc)) + "), so the hooks' times keep it in; " + OwnCost.FrameLine(f)));
            yield break;
        }

        private void DrawOwnCost()
        {
            var f = OwnFacts();
            Ui.Row("Frame time", OwnCost.FrameLine(f));
            Ui.Row("Panel open", OwnCost.PanelLine(f));
            Ui.Row("Tools", OwnCost.ToolsLine(f));
            Ui.Row("Session", OwnCost.SessionLine(f));
            Ui.Row("Hooks", _ownHooks != null && _ownHooks.Running ? "measuring for " + OwnHooks.Seconds.ToString("0") + " s ..." : OwnCost.HooksLine(f.Hooks));
            if (_ownHooks != null && !_ownHooks.Running && Ui.Tool("Measure its hooks", "Times Vigil's hooks in the game's methods for 10 s, the timers' own cost taken off", "Measure"))
                Ui.Later(() => _ownHooks.Start());
            Ui.Row("Other threads", OwnCost.ThreadsLine(f));
            Ui.Row("Files", OwnCost.FilesLine(f));
            Ui.Row("Network", OwnCost.NetworkLine(f));
            Ui.Row("Memory", Toned(OwnCost.MemoryLine, ResultTone.Quiet));
            if (_ownHooks?.Problem != null) Ui.Note("The last measurement could not run: " + _ownHooks.Problem + ".");
        }
    }
}
