using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// "What could spawn here" in the panel's World tab and "vigil spawnable": every spawn entry for
    /// the spot that was under the crosshair when the panel opened, judged by the game's rules
    /// (<see cref="SpawnSpotReader"/>, <see cref="SpawnRules"/>), read again every 2 s while the card
    /// shows, since the time of day, the weather and the count about change. Only reads.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private static readonly Ui.Column[] SpawnableColumns =
        {
            new Ui.Column("Entry", 170, false), new Ui.Column("Now", 60, false), new Ui.Column("Why not", 0, false),
        };

        private readonly SpawnSpotReader _spot = new SpawnSpotReader();
        private SpawnSpotReader.Reading _spotReading;
        private double _spotReadAt = double.NegativeInfinity;

        private void PickSpotNow()
        {
            _spot.Pick();
            _spotReading = null;
            _spotReadAt = double.NegativeInfinity;
        }

        private SpawnSpotReader.Reading ReadSpot()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { return _spot.Read(); }
            catch (Exception) { return null; }
            finally { _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds); }
        }

        private PanelBadge SpawnableBadge()
        {
            var r = _spotReading;
            if (r == null || r.Spot.Biome == 0) return null;
            int could = r.Answers.Count(a => a.Verdict == SpawnVerdict.Could);
            if (could > 0 && r.Spot.InsideBase) return new PanelBadge(could + " could, in a base", Skin.Tone.Accent);
            return new PanelBadge(could + " could", Skin.Tone.Quiet);
        }

        private void DrawSpawnable()
        {
            if (Player.m_localPlayer == null)
            {
                Ui.Note("Join a world to check a spot.");
                return;
            }

            // Read on a layout pass only, so the pass that draws the card draws what was laid out.
            if (Event.current.type == EventType.Layout && _spot.Picked && Now - _spotReadAt >= 2)
            {
                _spotReadAt = Now;
                _spotReading = ReadSpot();
            }

            var r = _spotReading;
            string what = !_spot.Picked ? "Nothing picked" : r == null ? "Not read" : r.Spot.Biome == 0 ? "Not loaded" : r.Spot.BiomeName;
            string about = !_spot.Picked
                ? string.Format(CultureInfo.InvariantCulture, "No ground under your crosshair within {0:0} m when the panel opened. Aim at a spot and open the panel again.", SpawnSpotReader.Reach)
                : string.Format(CultureInfo.InvariantCulture, "The spot under your crosshair when the panel opened, {0:0} m away", r != null ? r.Distance : 0f);
            if (Ui.Tool(what, about, "Again")) Ui.Later(PickSpotNow);
            if (r == null || r.Spot.Biome == 0) return;

            Ui.Row("Spot", SpawnRules.SpotLine(r.Spot));
            Ui.Row("Spawns run by", RunByWords(r));
            if (r.Answers.Count == 0) Ui.Note("No spawn list has an entry for " + r.Spot.BiomeName + ".");
            else Ui.WrapTable(SpawnableColumns, r.Answers.Select(a => new[] { a.Label, NowWords(a), a.Verdict == SpawnVerdict.Could ? "<color=#A8A9B2>" + a.Why + "</color>" : a.Why }));
            if (r.SwitchedOff > 0) Ui.Note(SwitchedOffWords(r));
            Ui.Note(CaveatWords());
        }

        private static string NowWords(SpawnAnswer a) =>
            a.Verdict == SpawnVerdict.Could ? "<color=#9BD67C>could</color>" : a.Verdict == SpawnVerdict.NotNow ? "<color=#E8B04A>not now</color>" : "<color=#A8A9B2>no</color>";

        private string RunByWords(SpawnSpotReader.Reading r) =>
            r.System != null && r.System.IsValid() ? OwnershipLook.OwnerLine(r.System, _ownership, PingOf, Limits.PingMs) : "no spawn system is loaded there";

        private static string SwitchedOffWords(SpawnSpotReader.Reading r) =>
            r.SwitchedOff + (r.SwitchedOff == 1 ? " entry" : " entries") + " for " + r.Spot.BiomeName + " switched off by the game or a mod, left out.";

        private static string CaveatWords()
        {
            string mods = SpawnSpotReader.Mods;
            string head = "By the game's rules, for the spot's own zone; a raid's entries show while it spawns near you.";
            return mods == null ? head : head + " " + mods + " patch the spawn checks and may add checks of their own that Vigil cannot read, so \"could\" is the most that may happen.";
        }

        /// <summary>"vigil spawnable": the spot under the crosshair now, in lines; the World tab shows it from then on too.</summary>
        private string SpawnableCommand()
        {
            PickSpotNow();
            if (!_spot.Picked) return string.Format(CultureInfo.InvariantCulture, "no ground under your crosshair (looked {0:0} m)", SpawnSpotReader.Reach);
            var r = _spot.Read();
            _spotReading = r;
            _spotReadAt = Now;
            if (r == null || r.Spot.Biome == 0) return "the ground there is not loaded";
            var lines = new List<string>
            {
                string.Format(CultureInfo.InvariantCulture, "the spot under your crosshair, {0:0} m away: {1}", r.Distance, SpawnRules.SpotLine(r.Spot)),
                "  spawns there run by: " + OwnershipLook.Plain(RunByWords(r)),
            };
            if (r.Answers.Count == 0) lines.Add("  no spawn list has an entry for " + r.Spot.BiomeName);
            lines.AddRange(r.Answers.Select(a => "  " + a.Now.PadRight(9) + a.Label + "   " + a.Why));
            if (r.SwitchedOff > 0) lines.Add("  " + SwitchedOffWords(r));
            lines.Add("  " + CaveatWords());
            return string.Join("\n", lines);
        }

        private IEnumerator SpawnableOnce(List<Check> checks)
        {
            const string what = "what could spawn at the spot under the crosshair";
            if (Player.m_localPlayer == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not in a world"));
                yield break;
            }
            var probe = new SpawnSpotReader();
            probe.Pick();
            if (!probe.Picked)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "no ground under the crosshair; look at the ground and run it again"));
                yield break;
            }
            var r = probe.Read();
            if (r == null || r.Spot.Biome == 0) checks.Add(new Check(Here, what, Outcome.Warn, "the ground there is not loaded"));
            else if (r.Answers.Count == 0) checks.Add(new Check(Here, what, Outcome.Warn, "no spawn entry for " + r.Spot.BiomeName + "; the spawn lists may not be readable"));
            else
            {
                int could = r.Answers.Count(a => a.Verdict == SpawnVerdict.Could), notNow = r.Answers.Count(a => a.Verdict == SpawnVerdict.NotNow);
                checks.Add(new Check(Here, what, Outcome.Pass, r.Spot.BiomeName + ": " + r.Answers.Count + " entries, " + could + " could, " + notNow + " not now, " + (r.Answers.Count - could - notNow) + " no"));
            }
        }
    }
}
