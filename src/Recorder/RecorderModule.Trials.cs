using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// Self-test routines that hold Vigil's readings and changes against the game itself, on this game
    /// only, and put everything back: the object readings against the game's own verdicts nearby
    /// (<see cref="RuleAgreement"/>); the server's weather tried for a few seconds, set, frozen and back
    /// (<see cref="WeatherTrial"/>); OdinOnDemand's level against the game's volume, with a music volume
    /// tried and put back unsaved; a map pin and the clipboard, each made and taken back. A trial ends on
    /// its own when its seconds run out, so a routine cut short leaves nothing changed.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const float AgreeRadius = 30f;
        private static readonly FieldInfo s_envBiome = AccessTools.Field(typeof(EnvMan), "m_currentBiome");
        private static readonly FieldInfo s_envPeriod = AccessTools.Field(typeof(EnvMan), "m_environmentPeriod");
        private static readonly MethodInfo s_hiveSpace = AccessTools.Method(typeof(Beehive), "HaveFreeSpace");
        private static readonly FieldInfo s_mapPins = AccessTools.Field(typeof(Minimap), "m_pins");
        private static readonly HashSet<string> ComparedCrops = new HashSet<string> { "Healthy", "NoSun", "NoSpace" };

        private IEnumerable<SelfTestRoutine> TrialRoutines()
        {
            yield return new SelfTestRoutine(Here, "Vigil's object readings against the game's, within 30 m", 10f, RulesAgree);
            yield return new SelfTestRoutine(Here, "the server's weather, tried on this game", 15f, ServerWeatherTried);
            yield return new SelfTestRoutine(Here, "OdinOnDemand's level against the game's volume", 8f, OdinVolumeTried);
            yield return new SelfTestRoutine(Here, "a map pin and the clipboard, made and taken back", 2f, PinAndClipboard);
        }

        private IEnumerator RulesAgree(List<Check> checks)
        {
            const string what = "Vigil's object readings against the game's, within 30 m";
            var player = Player.m_localPlayer;
            if (player == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "join a world first"));
                yield break;
            }
            var at = player.transform.position;
            var findings = new List<RuleAgreement.Finding>();
            bool Close(Component c) => c != null && Vector3.Distance(c.transform.position, at) <= AgreeRadius;
            string Name(Component c) => OwnershipView.DisplayName(c.gameObject) + " " + Vector3.Distance(c.transform.position, at).ToString("0", Ic) + " m away";
            int CoverAt(Component c, Vector3 point)
            {
                int blocked = ObjectParts.CastCover(point, null, out _).Count(r => r.Blocked);
                Cover.GetCoverForPoint(point, out float share, out _);
                findings.Add(new RuleAgreement.Finding("cover", Name(c), RuleAgreement.Cover(blocked, share), hard: true));
                return blocked;
            }

            foreach (var f in UnityEngine.Object.FindObjectsByType<Fermenter>(FindObjectsSortMode.None).Where(Close))
                if (f.m_roofCheckPoint != null) CoverAt(f, f.m_roofCheckPoint.position);
            foreach (var b in UnityEngine.Object.FindObjectsByType<Beehive>(FindObjectsSortMode.None).Where(Close))
            {
                if (b.m_coverPoint == null) continue;
                int blocked = CoverAt(b, b.m_coverPoint.position);
                if (s_hiveSpace != null) findings.Add(new RuleAgreement.Finding("beehive", Name(b), RuleAgreement.Bees(blocked, b.m_maxCover, (bool)s_hiveSpace.Invoke(b, null)), hard: true));
            }
            foreach (var fire in UnityEngine.Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None).Where(Close))
                CoverAt(fire, fire.transform.position + Vector3.up * fire.m_coverCheckOffset);
            foreach (var plant in UnityEngine.Object.FindObjectsByType<Plant>(FindObjectsSortMode.None).Where(Close))
            {
                string status = plant.GetStatus().ToString();
                if (!ComparedCrops.Contains(status)) continue;
                string sky = ObjectParts.CropSurroundings(plant, out var inWay);
                findings.Add(new RuleAgreement.Finding("crop", Name(plant), RuleAgreement.Crop(status, sky != null, inWay.Count > 0), hard: false));
            }
            foreach (var s in UnityEngine.Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None).Where(Close))
            {
                bool? smoke = ObjectParts.SmokeBlocked(s);
                if (smoke == null || s.m_smokeSpawner == null) continue;
                findings.Add(new RuleAgreement.Finding("smelter", Name(s), RuleAgreement.Smoke(smoke.Value, smoke.Value ? ObjectParts.VentBlocker(s.m_smokeSpawner) : null), hard: false));
            }
            var (result, detail) = RuleAgreement.Verdict(findings, AgreeRadius);
            checks.Add(new Check(Here, what, result, detail));

            var rested = ReadRested(player);
            int game = player.GetComfortLevel();
            string differ = RuleAgreement.Comfort(rested.Number, game);
            checks.Add(differ == null
                ? new Check(Here, "the comfort where you stand against the game's count", Outcome.Pass, game.ToString(Ic) + ", as the game counts it")
                : new Check(Here, "the comfort where you stand against the game's count", Outcome.Warn, differ + ": a mod changes comfort or shelter, or a piece counts in a way the card does not show"));
        }

        private static string WeatherNow(EnvMan env) => env.GetCurrentEnvironment()?.m_name;

        private IEnumerator ServerWeatherTried(List<Check> checks)
        {
            const string what = "the server's weather, tried on this game";
            var env = EnvMan.instance;
            var player = Player.m_localPlayer;
            if (player == null || env == null || ZNet.instance == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "join a world first"));
                yield break;
            }
            if (ServerWeatherPatch.Problem != null)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, "stepped aside: " + ServerWeatherPatch.Problem));
                yield break;
            }

            object biome = s_envBiome?.GetValue(env);
            string own = WeatherWatch.GameOverride(env);
            const string roll = "the game's roll as Vigil works it out";
            if (own == null && ServerWeatherPatch.Current.IsEmpty)
            {
                string worked = ServerWeatherPatch.RollNow(env, biome), shown = WeatherNow(env);
                checks.Add(worked == null ? new Check(Here, roll, Outcome.Info, "could not be worked out here")
                    : worked == shown ? new Check(Here, roll, Outcome.Pass, "the same as the game's: " + shown)
                    : new Check(Here, roll, Outcome.Warn, "Vigil works out " + worked + ", the game shows " + shown + " (a mod may pick the weather another way)"));
            }
            else checks.Add(new Check(Here, roll, Outcome.Info, "not compared now: " + (own != null ? "the game's own override gives " + own : "the server's weather is in charge")));

            long dayLength = env.m_dayLengthSec;
            if (!ServerWeatherPatch.State.IsEmpty)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not tried: the server holds " + ServerWeatherPatch.State.NowLine(dayLength) + ", left as it is"));
                yield break;
            }
            string pick = WeatherTrial.Pick(WeatherWatch.Known(), WeatherNow(env));
            if (pick == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not tried: the game knows no other weather"));
                yield break;
            }

            bool ownTime = env.m_debugTimeOfDay;
            var set = ServerWeather.None.Set(pick, "the self-test", ZNet.instance.GetTimeSeconds());
            string expected = set.WeatherFor(own);
            ServerWeatherPatch.Try(set, WeatherTrial.Seconds);
            float until = Time.realtimeSinceStartup + 3f;
            while (WeatherNow(env) != expected && Time.realtimeSinceStartup < until) yield return null;
            string got = WeatherNow(env);
            checks.Add(got == expected
                ? new Check(Here, "set: one weather for every biome", Outcome.Pass, "the game moved to " + got + (own != null ? ", its own override, which wins as it should" : ""))
                : new Check(Here, "set: one weather for every biome", Outcome.Fail, "set " + pick + ", the game shows " + got + " instead of " + expected));

            double held = WeatherTrial.HeldSecond(ZNet.instance.GetTimeSeconds(), dayLength);
            ServerWeatherPatch.Try(ServerWeather.None.Freeze("the self-test", held), WeatherTrial.Seconds);
            until = Time.realtimeSinceStartup + 0.3f;
            while (Time.realtimeSinceStartup < until) yield return null;
            var wrong = new List<string>();
            float heldHour = DayClock.Fraction(held, dayLength);
            if (!ownTime && !DayClock.Near(env.GetDayFraction(), heldHour, 0.002f))
                wrong.Add("the hour drawn is " + DayClock.Clock(env.GetDayFraction()) + ", not the frozen " + DayClock.Clock(heldHour));
            if (env.m_debugTimeOfDay != ownTime) wrong.Add("the game's time-of-day switch was left " + (env.m_debugTimeOfDay ? "on" : "off"));
            double t = ZNet.instance.GetTimeSeconds();
            bool beds = DayClock.SleepTime(DayClock.Fraction(t, dayLength)) && t > player.m_wakeupTime + env.m_sleepCooldownSeconds;
            if (EnvMan.CanSleep() != beds) wrong.Add("beds say " + (EnvMan.CanSleep() ? "sleep" : "no sleep") + ", the real clock " + (beds ? "sleep" : "no sleep"));
            if (WeatherWatch.GameOverride(env) == null && s_envPeriod != null && (long)s_envPeriod.GetValue(env) != WeatherRoll.Period((long)held, env.m_environmentDuration))
                wrong.Add("the weather is not rolled for the frozen second");
            checks.Add(wrong.Count == 0
                ? new Check(Here, "freeze: each biome's weather and the hour", Outcome.Pass, (ownTime ? "the hour you drew stays yours" : "the hour drawn held at " + DayClock.Clock(heldHour)) + ", beds by the real clock, the roll held")
                : new Check(Here, "freeze: each biome's weather and the hour", Outcome.Fail, string.Join("; ", wrong.ToArray())));

            ServerWeatherPatch.EndTrial();
            string back = null, expectedBack = null;
            until = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                back = WeatherNow(env);
                expectedBack = WeatherWatch.GameOverride(env) ?? ServerWeatherPatch.RollNow(env, s_envBiome?.GetValue(env));
                if (back == expectedBack) break;
            }
            checks.Add(back == expectedBack && env.m_debugTimeOfDay == ownTime
                ? new Check(Here, "back: the game's own", Outcome.Pass, back + " again, and the world's hour")
                : new Check(Here, "back: the game's own", Outcome.Warn, "the game shows " + back + ", its own would be " + expectedBack + (env.m_debugTimeOfDay != ownTime ? "; the time-of-day switch was left changed" : "")));
        }

        private IEnumerator OdinVolumeTried(List<Check> checks)
        {
            const string what = "OdinOnDemand's level against the game's volume";
            if (!OdinVolumePatch.OodFound || !OdinVolumePatch.On || OdinVolumePatch.Problem != null)
            {
                checks.Add(new Check(Here, what, OdinVolumePatch.Problem != null ? Outcome.Fail : Outcome.Info, OdinVolumePatch.State));
                yield break;
            }
            float master = OdinVolumePatch.Master, music = OdinVolumePatch.Music;
            var levels = OdinVolumePatch.MixerLevels();
            int agree = levels.Count(l => OdinVolume.MixerAgrees(l, master, music));
            checks.Add(levels.Count == 0 ? new Check(Here, what, Outcome.Info, "no radio, screen or girdle loaded near you")
                : agree == levels.Count ? new Check(Here, what, Outcome.Pass, "all " + levels.Count.ToString(Ic) + " at " + OdinVolume.Db(master, music).ToString("0.0", Ic) + " dB: " + OdinVolume.NowLine(master, music))
                : new Check(Here, what, Outcome.Fail, (levels.Count - agree).ToString(Ic) + " of " + levels.Count.ToString(Ic) + " not at " + OdinVolume.Db(master, music).ToString("0.0", Ic) + " dB: "
                    + string.Join(", ", levels.Where(l => !OdinVolume.MixerAgrees(l, master, music)).Select(l => l.ToString("0.0", Ic) + " dB").ToArray())));

            // Its own check, so a failure reading the panels does not keep the trial below from running.
            const string slider = "OdinOnDemand's master slider as the game's music slider";
            try
            {
                var sliders = OdinVolumePatch.Sliders();
                string label = OdinVolume.SliderLabel(music);
                int right = sliders.Count(s => Mathf.Approximately(s.Min, OdinVolume.SliderMin) && Mathf.Approximately(s.Max, OdinVolume.SliderMax) && Mathf.Abs(s.Value - music) < 0.01f && s.Label == label);
                checks.Add(sliders.Count == 0 ? new Check(Here, slider, Outcome.Info, "open a radio's cog panel once to check its slider")
                    : right == sliders.Count ? new Check(Here, slider, Outcome.Pass, sliders.Count.ToString(Ic) + " panel(s): 0 to 100 %, at the music volume, labelled \"" + label + "\"")
                    : new Check(Here, slider, Outcome.Fail, string.Join("; ", sliders.Select(s => s.Min.ToString("0.##", Ic) + " to " + s.Max.ToString("0.##", Ic) + ", at " + s.Value.ToString("0.##", Ic) + ", \"" + s.Label + "\"").ToArray())));
            }
            catch (Exception e) { checks.Add(new Check(Here, slider, Outcome.Fail, e.GetType().Name + " " + e.Message)); }

            const string tried = "a music volume set from the slider, tried and put back";
            float trial = OdinVolume.TrialMusic(music);
            OdinVolumePatch.TryMusic(trial, 5f);
            bool followed = Mathf.Abs(MusicMan.m_masterMusicVolume - master * trial) < 0.001f && Mathf.Abs(OdinVolumePatch.Music - trial) < 0.001f;
            if (levels.Count > 0)
            {
                float until = Time.realtimeSinceStartup + 2.5f;
                while (Time.realtimeSinceStartup < until && !OdinVolumePatch.MixerLevels().Any(l => OdinVolume.MixerAgrees(l, master, trial))) yield return null;
                followed = followed && OdinVolumePatch.MixerLevels().Any(l => OdinVolume.MixerAgrees(l, master, trial));
            }
            OdinVolumePatch.EndMusicTrial();
            bool restored = Mathf.Abs(OdinVolumePatch.Music - music) < 0.001f && Mathf.Abs(MusicMan.m_masterMusicVolume - master * music) < 0.001f;
            checks.Add(followed && restored
                ? new Check(Here, tried, Outcome.Pass, Math.Round(trial * 100).ToString("0", Ic) + " %: the game's music" + (levels.Count > 0 ? " and OdinOnDemand's level" : "") + " followed; yours (" + Math.Round(music * 100).ToString("0", Ic) + " %) is back, nothing saved")
                : new Check(Here, tried, Outcome.Fail, (followed ? "" : "did not follow " + Math.Round(trial * 100).ToString("0", Ic) + " %; ") + (restored ? "yours is back" : "yours is not back yet; it comes back within 5 s")));
        }

        private IEnumerator PinAndClipboard(List<Check> checks)
        {
            const string pin = "a map pin, made and taken back";
            var map = Minimap.instance;
            var player = Player.m_localPlayer;
            if (map == null || player == null || s_mapPins == null) checks.Add(new Check(Here, pin, Outcome.Info, "join a world first"));
            else
            {
                const string key = "self-test";
                var p = player.transform.position;
                TogglePinAt(key, p.x, p.z, "Vigil self-test");
                bool made = _spawnPins.TryGetValue(key, out var pinData) && ((System.Collections.IList)s_mapPins.GetValue(map)).Contains(pinData);
                TogglePinAt(key, p.x, p.z, "Vigil self-test");
                bool gone = !_spawnPins.ContainsKey(key) && (pinData == null || !((System.Collections.IList)s_mapPins.GetValue(map)).Contains(pinData));
                checks.Add(made && gone ? new Check(Here, pin, Outcome.Pass, "the pin came onto your map and went again")
                    : new Check(Here, pin, Outcome.Fail, (made ? "" : "no pin came onto the map; ") + (gone ? "" : "the pin stayed")));
            }

            const string clip = "a path copied to the clipboard and read back";
            string before = GUIUtility.systemCopyBuffer;
            PathClipboard.Copy(_dir);
            string got = GUIUtility.systemCopyBuffer;
            string label = PathClipboard.Label(_dir);
            GUIUtility.systemCopyBuffer = before;
            PathClipboard.Forget();
            string wanted;
            try { wanted = Path.GetFullPath(_dir); } catch (Exception) { wanted = _dir; }
            checks.Add(got == wanted && label == PathCopy.Done
                ? new Check(Here, clip, Outcome.Pass, "the recordings folder's path, and the button said \"" + PathCopy.Done + "\"; your clipboard is as it was")
                : new Check(Here, clip, Outcome.Fail, "read back \"" + got + "\", the button said \"" + label + "\"; your clipboard is as it was"));
            yield break;
        }
    }
}
