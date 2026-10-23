using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// The Overview tab's <em>Fire sounds</em>: a player's level for the loops of fires, which fires it
    /// reaches, and the switch that gives a mod's fire the game's sound rules (<see cref="FireSounds"/>,
    /// <see cref="FireSoundsPatch"/>). The slider moves the loaded loops as it is dragged and writes
    /// the setting once let go, so the config is saved once a move, not once a frame.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private ConfigEntry<int> _fireLevel;
        private ConfigEntry<FireSounds.Scope> _fireScope;
        private ConfigEntry<bool> _fireRules;

        // The level under the slider while it is dragged; below 0 when it is not.
        private float _fireLevelDragged = -1f;

        private void BindFireSounds()
        {
            _fireLevel = Config.Bind("Sound", "FireLevel", FireSounds.DefaultPercent,
                new ConfigDescription("On a player's game: the level of fire loops, in percent of each fire's own; 0 is silent. FireLevelAppliesTo says which fires it reaches. " +
                    "The game's SFX and master volume apply on top.", new AcceptableValueRange<int>(0, 100)));
            _fireScope = Config.Bind("Sound", "FireLevelAppliesTo", FireSounds.Scope.WithoutRules,
                "On a player's game: WithoutRules reaches the fires whose loop came without the game's sound component (a mod's fire that plays its loop as a plain source), All reaches every fire's loop.");
            _fireRules = Config.Bind("Sound", "ModFiresFollowGameRules", true,
                "On a player's game: a fire whose loop came without the game's sound component gets one as the fire loads, with the game's hearth's values (40 to 60 % of its own level, a fade-in, " +
                "the nearest few of a kind audible, reverb with distance), and a loop on no mixer group goes on the game's SFX group, where the SFX volume applies. Off, such a fire plays its loop as its mod made it.");
            FireSoundsPatch.Percent = _fireLevel.Value;
            FireSoundsPatch.Scope = _fireScope.Value;
            FireSoundsPatch.RulesOn = _fireRules.Value;
            _fireLevel.SettingChanged += (_, __) => { FireSoundsPatch.Percent = _fireLevel.Value; FireSoundsPatch.ApplyAll(); };
            _fireScope.SettingChanged += (_, __) => { FireSoundsPatch.Scope = _fireScope.Value; FireSoundsPatch.ApplyAll(); };
            _fireRules.SettingChanged += (_, __) => FireSoundsPatch.SetRules(_fireRules.Value);
        }

        private PanelBadge FireSoundsBadge()
        {
            string text = FireSounds.Badge(_fireLevel.Value, _fireRules.Value);
            return text == null ? null : new PanelBadge(text, Skin.Tone.Quiet);
        }

        private void DrawFireSounds()
        {
            Ui.BeginFormLine("Level");
            float shown = _fireLevelDragged >= 0f ? _fireLevelDragged : _fireLevel.Value;
            float now = Ui.Slider(shown, 0f, 100f, 180f, out bool released);
            int percent = Mathf.RoundToInt(now);
            if (released)
            {
                _fireLevelDragged = -1f;
                if (percent != _fireLevel.Value) Ui.Later(() => _fireLevel.Value = percent);
            }
            else if (!Mathf.Approximately(now, shown))
            {
                _fireLevelDragged = now;
                FireSoundsPatch.Percent = percent;
                FireSoundsPatch.ApplyAll();
            }

            Ui.Text(FireSounds.PercentLabel(percent));
            if (percent != FireSounds.DefaultPercent && Ui.Quiet("Back to 100"))
            {
                Ui.Later(() => { _fireLevelDragged = -1f; _fireLevel.Value = FireSounds.DefaultPercent; });
            }

            Ui.EndLine();
            Ui.BeginFormLine("Applies to");
            int scope = Ui.Choice((int)_fireScope.Value, FireSounds.ScopeWords);
            if (scope >= 0 && scope != (int)_fireScope.Value) Ui.Later(() => _fireScope.Value = (FireSounds.Scope)scope);
            Ui.EndLine();
            Ui.BeginFormLine("Game's sound rules");
            int rules = Ui.Choice(_fireRules.Value ? 0 : 1, OnOff);
            if (rules >= 0 && (rules == 0) != _fireRules.Value) Ui.Later(() => _fireRules.Value = rules == 0);
            Ui.EndLine();
            Ui.Row("Rules", Toned(FireSoundsPatch.State, FireSoundsPatch.Problem != null ? ResultTone.Caution : _fireRules.Value && FireSoundsPatch.Given > 0 ? ResultTone.Good : ResultTone.Quiet));
            var player = Player.m_localPlayer;
            Ui.Row("Near you", player == null ? "join a world first" : FireSounds.NearLine(FireSoundsPatch.Near(player.transform.position), FireSounds.NearMetres));
            Ui.Note("The game's fires play their loop through a sound component that keeps a hearth at 40 to 60 % of its own level, fades it in and lets only the nearest few of a kind be heard. " +
                "A mod's fire that leaves it out plays its loop at full level, every one of them; the switch gives such a fire the game's hearth's rules as it loads. " +
                "The level multiplies each fire's own and never raises it; the game's SFX and master volume apply on top. Yours only, kept in Vigil's config.");
        }

        private IEnumerator FireSoundsTried(List<Check> checks)
        {
            const string what = "fire loops at the chosen level, within 20 m";
            if (_server)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "a player's game only"));
                yield break;
            }

            if (FireSoundsPatch.Problem != null)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, FireSoundsPatch.State));
                yield break;
            }

            var player = Player.m_localPlayer;
            if (player == null) checks.Add(new Check(Here, what, Outcome.Info, "join a world first"));
            else
            {
                var levels = FireSoundsPatch.LevelsNear(player.transform.position);
                var off = levels.Where(l => !FireSounds.LevelAgrees(l.Actual, l.Expected)).ToList();
                string near = FireSounds.NearLine(FireSoundsPatch.Near(player.transform.position), FireSounds.NearMetres);
                checks.Add(levels.Count == 0 ? new Check(Here, what, Outcome.Info, near)
                    : off.Count == 0 ? new Check(Here, what, Outcome.Pass, "all " + levels.Count.ToString(Ic) + " at " + FireSounds.PercentLabel(_fireLevel.Value) + " (" + FireSounds.ScopeWords[(int)_fireScope.Value] + "): " + near)
                    : new Check(Here, what, Outcome.Fail, off.Count.ToString(Ic) + " of " + levels.Count.ToString(Ic) + " not at their level: "
                        + string.Join(", ", off.Select(l => l.Kind + " " + l.Actual.ToString("0.00", Ic) + " for " + l.Expected.ToString("0.00", Ic)).ToArray())));
            }

            const string given = "a fire loop given the game's rules, on a muted stand-in";
            var standIn = FireSoundsPatch.TryStandIn();
            checks.Add(standIn.Error != null ? new Check(Here, given, Outcome.Fail, standIn.Error)
                : !standIn.Values || !standIn.Hash ? new Check(Here, given, Outcome.Fail, (standIn.Values ? "" : "not the hearth's values; ") + (standIn.Hash ? "" : "not the kind's hash"))
                : !standIn.OnMixer ? new Check(Here, given, Outcome.Warn, "the hearth's values and the kind's hash, but no SFX mixer group was found to put it on")
                : new Check(Here, given, Outcome.Pass, "the hearth's values, the kind's hash, on the game's SFX mixer group"));
        }
    }
}
