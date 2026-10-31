using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "OdinOnDemand fades the game's music by where it plays" (<see cref="OdinMusicFade"/>).
    /// OdinOnDemand's <c>AudioFader</c> sets the game's music source every frame to its own volume
    /// times <c>FadeGain</c>, which measures from the local player to the nearest playing radio's own
    /// place. A prefix on <c>FadeGain</c> gives the same curve measured to the nearest place each
    /// playing radio's sound comes from: its speaker outputs while it plays from each speaker, else
    /// its sound source, which OdinOnDemand puts in the middle of its speakers. A radio counts as
    /// playing by OdinOnDemand's own test. OdinOnDemand's code is compared with 1.3.0's, and any
    /// change makes the patch step aside. The switch is this game's own.
    /// </summary>
    internal static class OdinMusicFadePatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.odinmusicfade";
        private const string OodGuid = "com.valmedia.odinondemand";

        /// <summary>OdinOnDemand 1.3.0's AudioFader.FadeGain and GetDistanceFromMediaplayers.</summary>
        private const ulong KnownCode = 0xD2D87138EF4BC8F7UL;

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        public static string OodVersion { get; private set; }

        /// <summary>Why the patch stepped aside; null while it is in (or OdinOnDemand is absent).</summary>
        public static string Problem { get; private set; }

        public static string State => OdinMusicFade.StateLine(On, Found, OodVersion, Problem, FadeOff);

        private static Action<string> s_log;
        private static IDictionary s_players;
        private static PropertyInfo s_audio, s_settings, s_paused, s_playing, s_lowest, s_fadeType;
        private static FieldInfo s_emitters;

        // Kept from frame to frame: the fade runs every frame.
        private static readonly List<FadeSource> s_sources = new List<FadeSource>();
        private static readonly List<List<SoundPlace>> s_places = new List<List<SoundPlace>>();

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var ood = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == OodGuid);
            Found = ood?.Instance != null;
            OodVersion = ood?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var asm = ood.Instance.GetType().Assembly;
                var fader = asm.GetType("OdinOnDemand.Utils.AudioFader");
                var player = asm.GetType("OdinOnDemand.MPlayer.BasePlayer");
                var settings = asm.GetType("OdinOnDemand.MPlayer.PlayerSettings");
                var config = asm.GetType("OdinOnDemand.Utils.Config.OODConfig");
                var lists = asm.GetType("OdinOnDemand.Utils.Net.ComponentLists");
                var gain = fader != null ? AccessTools.DeclaredMethod(fader, "FadeGain") : null;
                var distance = fader != null ? AccessTools.DeclaredMethod(fader, "GetDistanceFromMediaplayers") : null;
                if (gain == null || distance == null || gain.ReturnType != typeof(float) || CodeShape.Hash(new MethodBase[] { gain, distance }) != KnownCode)
                {
                    Problem = "OdinOnDemand's music fade code changed (an update)";
                    return;
                }
                s_players = lists != null ? AccessTools.DeclaredField(lists, "MediaComponentLists")?.GetValue(null) as IDictionary : null;
                s_audio = player != null ? AccessTools.DeclaredProperty(player, "mAudio") : null;
                s_settings = player != null ? AccessTools.DeclaredProperty(player, "PlayerSettings") : null;
                s_emitters = player != null ? AccessTools.DeclaredField(player, "speakerEmitters") : null;
                s_paused = settings != null ? AccessTools.DeclaredProperty(settings, "IsPaused") : null;
                s_playing = settings != null ? AccessTools.DeclaredProperty(settings, "IsPlaying") : null;
                s_lowest = config != null ? AccessTools.DeclaredProperty(config, "LowestVolumeDB") : null;
                s_fadeType = config != null ? AccessTools.DeclaredProperty(config, "AudioFadeType") : null;
                if (s_players == null || s_audio == null || s_settings == null || s_emitters == null || s_paused == null || s_playing == null || s_lowest == null || s_fadeType == null)
                {
                    Problem = "OdinOnDemand's players were not found (an update changed them)";
                    return;
                }
                new Harmony(HarmonyId).Patch(gain, prefix: new HarmonyMethod(typeof(OdinMusicFadePatch), nameof(BeforeGain)));
            }
            catch (Exception e) { Problem = "OdinOnDemand could not be hooked: " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>Whether OdinOnDemand's own Music Fade Type leaves the game's music alone, so the fade never runs.</summary>
        private static bool FadeOff
        {
            get
            {
                try { return Found && Problem == null && (s_fadeType?.GetValue(null, null) as ConfigEntryBase)?.BoxedValue?.ToString() == "None"; }
                catch (Exception) { return false; }
            }
        }

        private static bool BeforeGain(ref float __result)
        {
            if (!On || Problem != null) return true;
            try
            {
                var me = Player.m_localPlayer;
                if (me == null)
                {
                    __result = 1f;
                    return false;
                }
                Collect();
                float lowest = s_lowest.GetValue(null, null) is ConfigEntry<float> entry ? entry.Value : 0f;
                var at = me.transform.position;
                __result = OdinMusicFade.Gain(at.x, at.y, at.z, s_sources, lowest);
                return false;
            }
            catch (Exception e)
            {
                Problem = "its fade failed: " + e.GetType().Name + " " + e.Message;
                s_log?.Invoke("Vigil: the patch for OdinOnDemand's music fade stepped aside: " + Problem);
                return true;
            }
        }

        /// <summary>Every playing radio by OdinOnDemand's own test, with the places its sound comes from.</summary>
        private static void Collect()
        {
            s_sources.Clear();
            int used = 0;
            foreach (IList list in s_players.Values)
            {
                foreach (var player in list)
                {
                    if (!(player is Component c) || c == null) continue;
                    var audio = s_audio.GetValue(player, null) as AudioSource;
                    if (audio == null || !(audio.isPlaying || (audio.clip != null && audio.time != 0f) || audio.loop)) continue;
                    var settings = s_settings.GetValue(player, null);
                    if (settings == null || (bool)s_paused.GetValue(settings, null) || !(bool)s_playing.GetValue(settings, null)) continue;
                    if (used == s_places.Count) s_places.Add(new List<SoundPlace>());
                    var places = s_places[used++];
                    places.Clear();
                    // Speaker outputs exist only while the radio plays from each speaker; the sound source is then a silent carrier.
                    if (s_emitters.GetValue(player) is IList emitters)
                        foreach (var e in emitters)
                            if (e is Component ec && ec != null) Add(places, ec.transform.position);
                    if (places.Count == 0) Add(places, audio.transform.position);
                    s_sources.Add(new FadeSource(audio.maxDistance, places));
                }
            }
        }

        private static void Add(List<SoundPlace> places, Vector3 p) => places.Add(new SoundPlace(p.x, p.y, p.z));
    }
}
