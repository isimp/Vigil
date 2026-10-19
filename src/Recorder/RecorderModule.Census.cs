using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// The census ("vigil census [radius]", the Tools tab's *Count objects*): a <see cref="CensusRun"/>
    /// stepped once a frame within its budget, its time counted as one of Vigil's tools, the result put
    /// under the card and the answer given to the console when it was asked there.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private CensusRun _censusRun;
        private bool _censusToConsole;

        private string StartCensus(string rest, bool toConsole)
        {
            if (_censusRun != null) return "a census is under way: " + _censusRun.Progress;
            if (CensusRun.Running) return "the self-test is running a census; try again in a moment";
            if (ZNetScene.instance == null) return "no world loaded";
            float radius = ConsoleHelp.CensusRadius;
            if (!string.IsNullOrWhiteSpace(rest) && float.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) radius = Mathf.Max(0f, r);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _censusRun = new CensusRun(_dir, radius, CensusRun.TimingSeconds, _journal, _engine?.LastValues());
            _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds);
            OwnTools.CensusRuns++;
            _censusToConsole = toConsole;
            return "counting what is loaded " + (radius > 0 && Player.m_localPlayer != null ? "within " + radius.ToString("0", CultureInfo.InvariantCulture) + " m" : "anywhere")
                + ", then timing the characters for " + CensusRun.TimingSeconds.ToString("0", CultureInfo.InvariantCulture) + " s; the report follows";
        }

        /// <summary>Every frame: the census's next slice, and its result once it is done.</summary>
        private void UpdateCensus()
        {
            var run = _censusRun;
            if (run == null) return;
            if (Player.m_localPlayer == null && !_server) run.Stop("left the world");
            double ms = run.Step();
            if (ms > 0.05)
            {
                _recorder?.AddOwnTime(ms);
                OwnTools.CensusMs += ms;
                OwnTools.CensusFrames++;
            }
            if (!run.Done) return;
            _censusRun = null;
            OwnTools.CensusSeconds += run.Scene.WalkSeconds;
            if (run.Result != null) ResultStore.Shared.Put(ResultStore.Census, run.Result);
            if (_censusToConsole) _host.Reply("Vigil: " + run.Answer);
            else if (run.Result == null) _host.Reply("Vigil: " + run.Answer);
        }

        private void DrawCensus()
        {
            if (_censusRun != null || CensusRun.Running)
            {
                string progress = _censusRun?.Progress ?? "the self-test is running a census";
                Ui.Tool("Count objects", char.ToUpperInvariant(progress[0]) + progress.Substring(1), "Counting", enabled: false);
            }
            else if (Ui.Tool("Count objects", "What is loaded within the radius, what it asks of drawing and physics, by mod; 0 counts all", "Run", ref _censusRadius, 50, "m"))
            {
                // The row turns into its running form, so the census starts at the next layout pass.
                string line = "census " + _censusRadius;
                Ui.Later(() => _host.RunForResult(line, ResultStore.Census));
            }
            ShowResult(ResultStore.Census);
        }

        // ------------------------------------------------------------------ self-test

        private IEnumerator CensusOnce(List<Check> checks)
        {
            const string what = "the census within 16 m, characters timed 2 s";
            if (Player.m_localPlayer == null || ZNetScene.instance == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not in a world"));
                yield break;
            }
            if (_censusRun != null || CensusRun.Running)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "a census was under way already"));
                yield break;
            }
            var run = new CensusRun(_dir, 16f, 2f, _journal, _engine?.LastValues());
            double until = Now + 20;
            // Stepped by the self-test itself, whose time Vigil counts as the self-test's.
            while (!run.Done && Now < until)
            {
                run.Step();
                yield return null;
            }
            if (!run.Done) run.Stop("the self-test's time ran out");
            var s = run.Scene;
            bool timersOut = !CensusRun.AnyTimerIn();
            if (run.Result == null || !File.Exists(run.Result.Path))
            {
                checks.Add(new Check(Here, what, Outcome.Fail, run.Answer ?? "no report written"));
                yield break;
            }
            if (!timersOut)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, "a census timer was still in a game method after the run"));
                yield break;
            }
            // Every timed method runs inside the game's loop, so their sum cannot exceed it.
            if (s.TimingProblem == null && !double.IsNaN(s.FixedUpdateMs) && run.CharactersMs + run.MoversMs > s.FixedUpdateMs * 1.05 + 0.5)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, string.Format(Ic, "characters and moving objects {0:0.0} ms, more than the game's loop around them ({1:0.0} ms): counted twice",
                    run.CharactersMs + run.MoversMs, s.FixedUpdateMs)));
                yield break;
            }
            if (s.TimingProblem == null && run.Characters > 0 && run.CharactersTimed == 0)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, string.Format(Ic, "{0} characters found, none of them ran while timed", run.Characters)));
                yield break;
            }
            if (PhysicsStep.Installed && s.Steps > 0 && double.IsNaN(s.PhysicsEngineMs))
            {
                checks.Add(new Check(Here, what, Outcome.Fail, "physics steps ran but Unity's physics step was not timed: the markers are no longer in the player loop"));
                yield break;
            }
            string timing = s.TimingProblem != null ? "characters not timed: " + s.TimingProblem
                : string.Format(Ic, "{0} of {1} characters ran in {2} steps, {3:0.000} ms a step of {4:0.000} for the game's loop{5}",
                    run.CharactersTimed, run.Characters, s.Steps, s.Steps > 0 ? run.CharactersMs / s.Steps : 0, s.Steps > 0 ? s.FixedUpdateMs / s.Steps : 0,
                    double.IsNaN(s.PhysicsEngineMs) ? "" : string.Format(Ic, ", physics engine {0:0.000}", s.PhysicsEngineMs / s.Steps));
            checks.Add(new Check(Here, what, Outcome.Pass, string.Format(Ic, "{0}; walk {1:0.0} s, at most {2:0.0} ms a frame; {3}",
                timing, s.WalkSeconds, s.MaxFrameMs, Path.GetFileName(run.Result.Path))));
        }
    }
}
