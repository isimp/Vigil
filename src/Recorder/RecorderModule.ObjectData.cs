using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// "World-object data by mod" in the panel's Network tab and in the summary (<see cref="ObjectCapture"/>):
    /// a capture of what this game sends, run on request for 60 s and on its own for as long as a deep
    /// capture runs; each kind of object with its sends, its size and who takes how much of it, and the
    /// values whose changes made objects send again.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const string OnRequest = "on request", ForSelfTest = "for the self-test";

        private static readonly Ui.Column[] ObjectColumns =
        {
            new Ui.Column("Object", 120, false), new Ui.Column("sends/s", 60, true), new Ui.Column("bytes each", 70, true),
            new Ui.Column("the game", 70, true), new Ui.Column("mods", 0, false),
        };

        private static readonly Ui.Column[] ChangeColumns =
        {
            new Ui.Column("Value", 0, false), new Ui.Column("Whose", 120, false), new Ui.Column("On", 120, false), new Ui.Column("changes/s", 70, true),
        };

        /// <summary>Every frame: a capture whose time is up ends; one runs for as long as a deep capture runs.</summary>
        private void UpdateObjectData()
        {
            bool deep = _timer != null && _timer.DeepRunning;
            if (deep && !ObjectCapture.On) ObjectCapture.Start(ObjectCapture.DuringDeep, float.PositiveInfinity);
            if (!deep && ObjectCapture.On && ObjectCapture.Reason == ObjectCapture.DuringDeep) ObjectCapture.Stop();
            ObjectCapture.Tick();
        }

        private PanelBadge ObjectDataBadge() =>
            ObjectCapture.On ? new PanelBadge("capturing", Skin.Tone.Caution)
            : ObjectCapture.Last != null ? new PanelBadge("captured " + Ago(ObjectCapture.LastAt), Skin.Tone.Quiet) : null;

        private static string Ago(DateTime at)
        {
            double minutes = (DateTime.Now - at).TotalMinutes;
            return minutes < 1 ? "just now" : Math.Round(minutes).ToString("0", Wc) + " min ago";
        }

        private void DrawObjectData()
        {
            if (ObjectCapture.On) Ui.Row("Capture", "running " + ObjectCapture.Reason + (ObjectCapture.Reason == ObjectCapture.DuringDeep ? "" : ", " + Math.Ceiling(ObjectCapture.SecondsLeft).ToString("0", Wc) + " s to go"));
            else if (Ui.Tool("Capture", "60 s of what your game sends, split by who adds the data and what makes it send again", "Run"))
                Ui.Later(() => ObjectCapture.Start(OnRequest, ObjectCapture.CaptureSeconds));
            var v = ObjectCapture.Last;
            if (v == null)
            {
                Ui.Note("No capture yet: Run takes 60 s, and a deep capture takes one on its own.");
                return;
            }
            Ui.Row("Captured", Ago(ObjectCapture.LastAt) + " (" + ObjectCapture.LastReason + "), over " + Math.Round(v.Seconds).ToString("0", Wc) + " s: "
                + v.Sends.ToString("N0", Wc) + " objects sent, " + v.Kinds.ToString(Wc) + " kinds");
            if (v.Prefabs.Count == 0) Ui.Note("Your game sent no objects then: it runs none that changed.");
            else Ui.Table(ObjectColumns, v.Prefabs.Select(p => new[]
            {
                ObjectDataView.ObjectLabel(p), p.SendsPerSecond.ToString("0.0", Wc), Math.Round(p.BytesEach).ToString("0", Wc) + " B",
                Math.Round(p.GameEach).ToString("0", Wc) + " B", ObjectDataView.ModsLine(p),
            }));
            Ui.Heading("What makes them send again, changes per second");
            if (v.Changes.Count == 0) Ui.Note("No value changed on an object your game runs.");
            else Ui.Table(ChangeColumns, v.Changes.Select(c => new[] { c.Value, c.Whose, c.On, c.PerSecond.ToString("0.0", Wc) }));
            Ui.Note("Bytes each: the object's data as the game sends it; the position and owner it adds to every object are left out. A value counts for whoever's code holds its name, the game first; unnamed values are unknown. "
                + "A moving object sends again for its movement whatever the mods do. Only objects your game runs are sent from here.");
        }

        /// <summary>The summary's lines for the last capture.</summary>
        private IEnumerable<string> ObjectDataLines()
        {
            var v = ObjectCapture.Last;
            if (v == null)
            {
                yield return "World-object data by mod: no capture this session (the panel's Network tab runs one; a deep capture takes one on its own).";
                yield break;
            }
            yield return "World-object data by mod, captured " + ObjectCapture.LastAt.ToString("HH:mm:ss", Wc) + " (" + ObjectCapture.LastReason + ") over "
                + Math.Round(v.Seconds).ToString("0", Wc) + " s: " + v.Sends.ToString("N0", Wc) + " objects sent, " + v.Kinds.ToString(Wc) + " kinds";
            foreach (var p in v.Prefabs)
                yield return string.Format(Wc, "   {0,-28} {1,6:0.0}/s {2,6:0} B each   the game {3,4:0} B   mods: {4}",
                    ObjectDataView.ObjectLabel(p), p.SendsPerSecond, p.BytesEach, p.GameEach, ObjectDataView.ModsLine(p));
            yield return "   what makes them send again:";
            foreach (var c in v.Changes)
                yield return string.Format(Wc, "      {0,-28} {1,-20} {2,6:0.0}/s   on {3}", c.Value, c.Whose, c.PerSecond, c.On);
        }

        /// <summary>Self-test: a 3 s capture whose parts add up to no more than the objects sent, the size model checked against the real sizes.</summary>
        private IEnumerator ObjectDataOnce(List<Check> checks)
        {
            const string what = "world-object data by mod, captured 3 s";
            if (ObjectCapture.On)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "a capture runs (" + ObjectCapture.Reason + "); not started again"));
                yield break;
            }
            if (Player.m_localPlayer == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "join a world first"));
                yield break;
            }
            ObjectCapture.Start(ForSelfTest, 3f);
            double until = Now + 6;
            while (ObjectCapture.On && Now < until) yield return null;
            if (ObjectCapture.On) ObjectCapture.Stop();
            var v = ObjectCapture.Last;
            if (v == null || v.Sends == 0)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "your game sent no objects in 3 s (it runs none that changed)"));
                yield break;
            }
            var over = v.Prefabs.Where(p => p.GameEach < -0.5).ToList();
            var top = v.Prefabs[0];
            checks.Add(over.Count == 0
                ? new Check(Here, what, Outcome.Pass, v.Sends.ToString("N0", Wc) + " objects sent in " + v.Kinds.ToString(Wc) + " kinds; the most: " + ObjectDataView.ObjectLabel(top) + " "
                    + Math.Round(top.BytesEach).ToString("0", Wc) + " B each (the game " + Math.Round(top.GameEach).ToString("0", Wc) + " B, mods " + ObjectDataView.ModsLine(top) + "); "
                    + v.Changes.Count.ToString(Wc) + " kinds of change")
                : new Check(Here, what, Outcome.Fail, "the values counted add up to more than the object sent for " + string.Join(", ", over.Select(p => p.Prefab).ToArray()) + ", so the sizes are worked out wrong"));
        }
    }
}
