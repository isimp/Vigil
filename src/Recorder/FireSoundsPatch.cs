using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Fire sounds on a player's game (<see cref="FireSounds"/>). A postfix after <c>Fireplace.Awake</c>
    /// lists the fire's looping sources. One without the game's sound component (ZSFX) gets one while
    /// the switch is on, with the game's hearth's values and the fire's kind as its hash, so the game
    /// caps loops of that kind like its own; a source on no mixer group goes on the group the game's
    /// hearth plays through, where the SFX volume applies. Every loop then gets the player's level:
    /// under the rules through the component's modifier, which no fire code writes, on a plain loop as
    /// its own volume times the level. A placement ghost has no world object yet and is left alone.
    /// The level and the switch reach the loaded loops at once; a component given is taken back with
    /// its play-on-awake off first, so its removal does not stop the loop it leaves behind.
    /// </summary>
    internal static class FireSoundsPatch
    {
        private sealed class Loop
        {
            public AudioSource Source;
            public ZSFX Sfx;
            public bool HadRules;
            public bool Given;
            public float BaseVolume;
            public string Kind;
        }

        /// <summary>One loop's level against the one the setting calls for, for the self-test.</summary>
        public sealed class Level
        {
            public string Kind;
            public float Actual;
            public float Expected;
        }

        /// <summary>What a muted stand-in source got when given the rules.</summary>
        public sealed class StandIn
        {
            public bool Values;
            public bool Hash;
            public bool OnMixer;
            public string Error;
        }

        private static readonly List<Loop> Loops = new List<Loop>();

        /// <summary>The player's level, scope and switch, kept current by the config entries.</summary>
        public static int Percent = FireSounds.DefaultPercent;
        public static FireSounds.Scope Scope;
        public static bool RulesOn;

        /// <summary>Why the patch stepped aside; null while it works.</summary>
        public static string Problem { get; private set; }

        /// <summary>Loops given the game's rules this session and still holding them.</summary>
        public static int Given { get; private set; }

        public static string State => FireSounds.RulesLine(RulesOn, Given, Problem);

        public static void Hook(Harmony harmony)
        {
            var awake = AccessTools.Method(typeof(Fireplace), "Awake");
            if (!Patching.Apply(harmony, awake, "fire sounds", postfix: Patching.M(typeof(FireSoundsPatch), nameof(AfterAwake))))
            {
                Problem = "the fire's loading could not be hooked";
            }
        }

        private static void AfterAwake(Fireplace __instance)
        {
            try
            {
                if (__instance == null) return;
                var view = __instance.GetComponent<ZNetView>();
                if (view == null || view.GetZDO() == null) return;
                string kind = Utils.GetPrefabName(__instance.gameObject);
                foreach (var source in __instance.GetComponentsInChildren<AudioSource>(true))
                {
                    if (source == null || !source.loop) continue;
                    var loop = new Loop { Source = source, Sfx = source.GetComponent<ZSFX>(), BaseVolume = source.volume, Kind = kind };
                    loop.HadRules = loop.Sfx != null;
                    if (!loop.HadRules && RulesOn) GiveRules(loop);
                    Apply(loop);
                    Loops.Add(loop);
                }

                if (Loops.Count > 256) Prune();
            }
            catch (Exception e) { Problem = "a fire's loops could not be read: " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>The level and scope changed: every loaded loop follows.</summary>
        public static void ApplyAll()
        {
            Prune();
            foreach (var loop in Loops) Apply(loop);
        }

        /// <summary>The switch changed: loops that came without the rules get them, or lose the ones given.</summary>
        public static void SetRules(bool on)
        {
            RulesOn = on;
            Prune();
            foreach (var loop in Loops.Where(l => !l.HadRules))
            {
                if (on) GiveRules(loop);
                else TakeRules(loop);
                Apply(loop);
            }
        }

        /// <summary>The loops within <see cref="FireSounds.NearMetres"/> of a point, for the panel's line.</summary>
        public static List<FireSounds.LoopFacts> Near(Vector3 at)
        {
            Prune();
            return Loops.Where(l => Vector3.Distance(l.Source.transform.position, at) <= FireSounds.NearMetres)
                .Select(l => new FireSounds.LoopFacts(l.Kind, l.HadRules, l.Source.outputAudioMixerGroup != null)).ToList();
        }

        /// <summary>Each near loop's level against the one the setting calls for.</summary>
        public static List<Level> LevelsNear(Vector3 at)
        {
            Prune();
            var levels = new List<Level>();
            foreach (var loop in Loops.Where(l => Vector3.Distance(l.Source.transform.position, at) <= FireSounds.NearMetres))
            {
                float mod = FireSounds.Modifier(Percent, Scope, loop.HadRules);
                levels.Add(loop.Sfx != null
                    ? new Level { Kind = loop.Kind, Actual = loop.Sfx.GetVolumeModifier(), Expected = mod }
                    : new Level { Kind = loop.Kind, Actual = loop.Source.volume, Expected = loop.BaseVolume * mod });
            }

            return levels;
        }

        /// <summary>
        /// Gives the rules to a muted stand-in source on an object of its own, reads what it got, and
        /// takes the object away: the giving checked without a fire or a sound.
        /// </summary>
        public static StandIn TryStandIn()
        {
            var result = new StandIn();
            GameObject go = null;
            try
            {
                go = new GameObject("Vigil fire stand-in");
                go.SetActive(false);
                var source = go.AddComponent<AudioSource>();
                source.loop = true;
                source.playOnAwake = false;
                source.mute = true;
                source.volume = 0.57f;
                source.clip = AudioClip.Create("Vigil fire stand-in", 441, 1, 44100, false);
                var loop = new Loop { Source = source, BaseVolume = source.volume, Kind = "stand-in" };
                GiveRules(loop);
                var sfx = loop.Sfx;
                result.Values = sfx != null && Mathf.Approximately(sfx.m_minVol, FireSounds.HearthRules.MinVol) && Mathf.Approximately(sfx.m_maxVol, FireSounds.HearthRules.MaxVol)
                    && Mathf.Approximately(sfx.m_fadeInDuration, FireSounds.HearthRules.FadeInSeconds) && sfx.m_maxConcurrentSources == FireSounds.HearthRules.NearestKept
                    && sfx.m_distanceReverb == FireSounds.HearthRules.DistanceReverb && sfx.m_audioClips.Length == 1;
                result.Hash = sfx != null && sfx.m_hash == FireSounds.KindHash("stand-in");
                result.OnMixer = source.outputAudioMixerGroup != null;
                if (loop.Given) Given--;
            }
            catch (Exception e) { result.Error = e.GetType().Name + " " + e.Message; }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }

            return result;
        }

        private static void Apply(Loop loop)
        {
            if (loop.Source == null) return;
            float mod = FireSounds.Modifier(Percent, Scope, loop.HadRules);
            if (loop.Sfx != null) loop.Sfx.SetVolumeModifier(mod);
            else loop.Source.volume = loop.BaseVolume * mod;
        }

        private static void GiveRules(Loop loop)
        {
            if (loop.Sfx != null || loop.Source == null || loop.Source.clip == null) return;
            var sfx = loop.Source.gameObject.AddComponent<ZSFX>();
            sfx.m_audioClips = new[] { loop.Source.clip };
            sfx.m_minVol = FireSounds.HearthRules.MinVol;
            sfx.m_maxVol = FireSounds.HearthRules.MaxVol;
            sfx.m_fadeInDuration = FireSounds.HearthRules.FadeInSeconds;
            sfx.m_maxConcurrentSources = FireSounds.HearthRules.NearestKept;
            sfx.m_distanceReverb = FireSounds.HearthRules.DistanceReverb;
            sfx.m_playOnAwake = true;
            sfx.m_hash = FireSounds.KindHash(loop.Kind);
            if (loop.Source.outputAudioMixerGroup == null)
            {
                var group = GameSfxGroup();
                if (group != null) loop.Source.outputAudioMixerGroup = group;
            }

            loop.Sfx = sfx;
            loop.Given = true;
            Given++;
        }

        private static void TakeRules(Loop loop)
        {
            if (!loop.Given || loop.Sfx == null) return;
            var sfx = loop.Sfx;
            loop.Sfx = null;
            loop.Given = false;
            Given--;
            sfx.m_playOnAwake = false;
            UnityEngine.Object.Destroy(sfx);
            if (loop.Source == null) return;
            loop.Source.volume = loop.BaseVolume;
            if (!loop.Source.isPlaying && loop.Source.gameObject.activeInHierarchy) loop.Source.Play();
        }

        /// <summary>The mixer group the game's hearth plays through, or the game mixer's SFX group.</summary>
        private static AudioMixerGroup GameSfxGroup()
        {
            var hearth = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("hearth") : null;
            var source = hearth != null ? hearth.GetComponentInChildren<AudioSource>(true) : null;
            if (source != null && source.outputAudioMixerGroup != null) return source.outputAudioMixerGroup;
            var mixer = AudioMan.instance != null ? AudioMan.instance.m_masterMixer : null;
            var groups = mixer != null ? mixer.FindMatchingGroups("SFX") : null;
            return groups != null && groups.Length > 0 ? groups[0] : null;
        }

        private static void Prune()
        {
            int before = Loops.Count;
            Loops.RemoveAll(l => l.Source == null);
            if (Loops.Count != before) Given = Loops.Count(l => l.Given);
        }
    }
}
