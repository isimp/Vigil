using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "OdinOnDemand follows the game's volume" (<see cref="OdinVolume"/>). A postfix on
    /// OdinOnDemand's once-a-second <c>BasePlayer.UpdateChecks</c>, which sets the player's mixer to its
    /// own master volume, sets the game's master and music volume instead; turning the patch off lets
    /// OdinOnDemand set its own again the next second. A postfix on the cog panel's
    /// <c>UIController.ToggleSettingsPanel</c>, which puts OdinOnDemand's master volume on its slider each
    /// time the panel opens, puts the music volume there instead (the game's 0 to 1 range, labelled
    /// "Music Volume N %"), or OdinOnDemand's own back once the patch is off; a prefix on the slider's
    /// <c>OnMasterVolumeChanged</c> sets the game's music volume in place of OdinOnDemand's master volume,
    /// and saves it once the slider rests. A postfix on <c>EventSystem.OnApplicationFocus</c> after every
    /// other mod's puts the listener back at 1 when AzuMiscPatches gave it back at the master volume. All
    /// find their targets by name and step aside together, with the reason, when an update changed one.
    /// On a player's game only.
    /// </summary>
    internal static class OdinVolumePatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.odinvolume";
        private const string OodGuid = "com.valmedia.odinondemand", AzuGuid = "Azumatt.AzuMiscPatches";

        private static PropertyInfo s_audio, s_settings, s_type, s_screens, s_radios, s_girdle;
        private static MethodInfo s_masterFor;
        private static Type s_playerType;
        private static PropertyInfo s_ui;
        private static FieldInfo s_slider, s_panel, s_uiPlayer, s_azuMute;

        // Unity's slider reports a value its new range clamps as a change; Vigil's own range and value
        // changes are not the player's.
        private static bool s_settingSlider;
        private static bool s_savePending;
        private static float s_movedAt;

        // A music volume the self-test tries: the player's own to put back, and when at the latest.
        private static float? s_trialOwnMusic;
        private static float s_trialUntil;

        /// <summary>The server's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool OodFound { get; private set; }

        public static bool AzuFound { get; private set; }

        public static string OodVersion { get; private set; }

        public static string AzuVersion { get; private set; }

        /// <summary>Why the patch stepped aside; null when its hooks are in (or OdinOnDemand is absent).</summary>
        public static string Problem { get; private set; }

        public static string State => OdinVolume.StateLine(On, OodFound, OodVersion, Problem, AzuFound, AzuVersion);

        private static bool Working => On && Problem == null;

        public static void Hook()
        {
            var ood = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == OodGuid);
            var azu = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == AzuGuid);
            OodFound = ood != null;
            AzuFound = azu != null;
            OodVersion = ood?.Metadata.Version.ToString();
            AzuVersion = azu?.Metadata.Version.ToString();
            var harmony = new Harmony(HarmonyId);
            if (OodFound)
            {
                try
                {
                    var player = TypeLookup.Loaded.Find("OdinOnDemand.MPlayer.BasePlayer");
                    var settings = TypeLookup.Loaded.Find("OdinOnDemand.MPlayer.PlayerSettings");
                    var ui = TypeLookup.Loaded.Find("OdinOnDemand.Utils.UI.UIController");
                    s_playerType = player;
                    s_ui = player != null ? AccessTools.Property(player, "UIController") : null;
                    var config = TypeLookup.Loaded.Find("OdinOnDemand.Utils.Config.OODConfig");
                    var update = player != null ? AccessTools.Method(player, "UpdateChecks") : null;
                    var toggle = ui != null ? AccessTools.Method(ui, "ToggleSettingsPanel") : null;
                    var moved = ui != null ? AccessTools.Method(ui, "OnMasterVolumeChanged") : null;
                    s_audio = player != null ? AccessTools.Property(player, "mAudio") : null;
                    s_settings = player != null ? AccessTools.Property(player, "PlayerSettings") : null;
                    s_type = settings != null ? AccessTools.Property(settings, "PlayerType") : null;
                    s_slider = ui != null ? AccessTools.Field(ui, "_masterVolumeSliderComponent") : null;
                    s_panel = ui != null ? AccessTools.Field(ui, "_settingsPanelObj") : null;
                    s_uiPlayer = ui != null ? AccessTools.Field(ui, "_basePlayer") : null;
                    s_masterFor = config != null ? AccessTools.Method(config, "MasterVolumeFor") : null;
                    s_screens = config != null ? AccessTools.Property(config, "MasterVolumeScreen") : null;
                    s_radios = config != null ? AccessTools.Property(config, "MasterVolumeMusicplayer") : null;
                    s_girdle = config != null ? AccessTools.Property(config, "MasterVolumeTransport") : null;
                    if (update == null || s_audio == null || s_settings == null || s_type == null)
                        Problem = "OdinOnDemand's volume update was not found (an update changed it)";
                    else if (toggle == null || moved == null || s_slider == null || s_panel == null || s_uiPlayer == null || s_masterFor == null || s_screens == null || s_radios == null || s_girdle == null)
                        Problem = "OdinOnDemand's master volume slider was not found (an update changed it)";
                    else
                    {
                        harmony.Patch(update, postfix: new HarmonyMethod(typeof(OdinVolumePatch), nameof(AfterUpdateChecks)) { priority = Priority.Last });
                        harmony.Patch(toggle, postfix: new HarmonyMethod(typeof(OdinVolumePatch), nameof(AfterToggleSettings)) { priority = Priority.Last });
                        harmony.Patch(moved, prefix: new HarmonyMethod(typeof(OdinVolumePatch), nameof(BeforeMasterMoved)) { priority = Priority.First });
                    }
                }
                catch (Exception e) { Problem = "OdinOnDemand could not be hooked: " + e.GetType().Name + " " + e.Message; }
            }
            if (AzuFound && Problem == null)
            {
                try
                {
                    var events = TypeLookup.Loaded.Find("UnityEngine.EventSystems.EventSystem");
                    var focus = events != null ? AccessTools.Method(events, "OnApplicationFocus") : null;
                    var azuPlugin = TypeLookup.Loaded.Find("AzuMiscPatches.AzuMiscPatchesPlugin");
                    s_azuMute = azuPlugin != null ? AccessTools.Field(azuPlugin, "MuteGameInBackground") : null;
                    if (focus == null || s_azuMute == null) Problem = "AzuMiscPatches' mute in background was not found (an update changed it)";
                    else harmony.Patch(focus, postfix: new HarmonyMethod(typeof(OdinVolumePatch), nameof(AfterFocus)) { priority = Priority.Last });
                }
                catch (Exception e) { Problem = "AzuMiscPatches could not be hooked: " + e.GetType().Name + " " + e.Message; }
            }
        }

        private static void AfterUpdateChecks(object __instance)
        {
            SaveIfDue(panelClosed: false);
            if (!Working) return;
            try
            {
                var audio = s_audio.GetValue(__instance, null) as AudioSource;
                AudioMixer mixer = audio != null && audio.outputAudioMixerGroup != null ? audio.outputAudioMixerGroup.audioMixer : null;
                if (mixer == null) return;
                float target = OdinVolume.Db(Master, Music);
                if (!mixer.GetFloat("MasterVolume", out float now) || Math.Abs(now - target) > 0.01f) mixer.SetFloat("MasterVolume", target);
            }
            catch (Exception) { }
        }

        private static void AfterToggleSettings(object __instance)
        {
            try
            {
                var panel = s_panel.GetValue(__instance) as GameObject;
                SaveIfDue(panelClosed: panel != null && !panel.activeSelf);
                if (!(s_slider.GetValue(__instance) is Slider slider)) return;
                if (Working) ShowMusic(slider);
                else if (OdinVolume.IsMusicSlider(slider.maxValue)) GiveBack(__instance, slider);
            }
            catch (Exception) { }
        }

        private static bool BeforeMasterMoved(object __instance, float vol)
        {
            if (s_settingSlider) return false;
            Slider slider = null;
            try
            {
                slider = s_slider.GetValue(__instance) as Slider;
                switch (OdinVolume.Moved(Working, slider != null && OdinVolume.IsMusicSlider(slider.maxValue)))
                {
                    case OdinVolume.SliderMove.SetMusic:
                        SetMusic(vol, save: true);
                        SetLabel(slider, OdinVolume.SliderLabel(vol));
                        return false;
                    case OdinVolume.SliderMove.GiveBack:
                        GiveBack(__instance, slider);
                        return false;
                    default:
                        return true;
                }
            }
            catch (Exception) { return slider == null || !OdinVolume.IsMusicSlider(slider.maxValue); }
        }

        private static void ShowMusic(Slider slider)
        {
            SetSlider(slider, OdinVolume.SliderMin, OdinVolume.SliderMax, Music);
            SetLabel(slider, OdinVolume.SliderLabel(Music));
        }

        private static void GiveBack(object ui, Slider slider)
        {
            object kind = s_type.GetValue(s_settings.GetValue(s_uiPlayer.GetValue(ui), null), null);
            float own = s_masterFor.Invoke(null, new[] { kind }) is ConfigEntry<float> entry ? entry.Value : 0f;
            SetSlider(slider, OdinVolume.OwnMin, OdinVolume.OwnMax, own);
            SetLabel(slider, OdinVolume.OwnLabel);
        }

        private static void SetSlider(Slider slider, float min, float max, float value)
        {
            s_settingSlider = true;
            try
            {
                slider.minValue = min;
                slider.maxValue = max;
                slider.SetValueWithoutNotify(value);
            }
            finally { s_settingSlider = false; }
        }

        /// <summary>Sets the slider's label, the one text in its row that is the volume's.</summary>
        private static void SetLabel(Slider slider, string value)
        {
            Text label = slider.transform.parent != null ? slider.transform.parent.GetComponentsInChildren<Text>(true).FirstOrDefault(t => OdinVolume.IsVolumeLabel(t.text)) : null;
            if (label != null && label.text != value) label.text = value;
        }

        /// <summary>The game's music volume, applied as its settings slider applies it, and saved once the slider rests.</summary>
        private static void SetMusic(float vol, bool save)
        {
            float music = Mathf.Clamp01(vol);
            PlatformPrefs.SetFloat("MusicVolume", music);
            MusicMan.m_masterMusicVolume = Master * music;
            if (!save) return;
            s_savePending = true;
            s_movedAt = Time.realtimeSinceStartup;
        }

        /// <summary>The self-test sets the music volume as the slider would, without saving it, for at most <paramref name="seconds"/>; <see cref="EndMusicTrial"/> or <see cref="TrialTick"/> puts the player's own back.</summary>
        public static void TryMusic(float music, float seconds)
        {
            if (!s_trialOwnMusic.HasValue) s_trialOwnMusic = Music;
            s_trialUntil = Time.realtimeSinceStartup + seconds;
            SetMusic(music, save: false);
        }

        public static void EndMusicTrial()
        {
            if (!s_trialOwnMusic.HasValue) return;
            float own = s_trialOwnMusic.Value;
            s_trialOwnMusic = null;
            SetMusic(own, save: false);
        }

        /// <summary>Every frame: a music trial whose seconds ran out ends, so a self-test cut short leaves the player's volume as it was.</summary>
        public static void TrialTick()
        {
            if (s_trialOwnMusic.HasValue && Time.realtimeSinceStartup >= s_trialUntil) EndMusicTrial();
        }

        /// <summary>Every OdinOnDemand player loaded on this game, with its mixer's level now (NaN when it has none).</summary>
        public static List<float> MixerLevels()
        {
            var levels = new List<float>();
            if (s_playerType == null || s_audio == null) return levels;
            foreach (var o in UnityEngine.Object.FindObjectsByType(s_playerType, FindObjectsSortMode.None))
            {
                var audio = s_audio.GetValue(o, null) as AudioSource;
                AudioMixer mixer = audio != null && audio.outputAudioMixerGroup != null ? audio.outputAudioMixerGroup.audioMixer : null;
                levels.Add(mixer != null && mixer.GetFloat("MasterVolume", out float db) ? db : float.NaN);
            }
            return levels;
        }

        /// <summary>Every cog panel OdinOnDemand built on this game, found through the players that hold them (a panel is no Unity object): its slider's range, value and label.</summary>
        public static List<(float Min, float Max, float Value, string Label)> Sliders()
        {
            var found = new List<(float, float, float, string)>();
            if (s_playerType == null || s_ui == null || s_slider == null) return found;
            foreach (var o in UnityEngine.Object.FindObjectsByType(s_playerType, FindObjectsSortMode.None))
            {
                object ui = s_ui.GetValue(o, null);
                if (ui == null || !(s_slider.GetValue(ui) is Slider slider) || slider == null) continue;
                var label = slider.transform.parent != null ? slider.transform.parent.GetComponentsInChildren<Text>(true).FirstOrDefault(t => OdinVolume.IsVolumeLabel(t.text)) : null;
                found.Add((slider.minValue, slider.maxValue, slider.value, label != null ? label.text : null));
            }
            return found;
        }

        private static void SaveIfDue(bool panelClosed)
        {
            if (!OdinVolume.SaveDue(s_savePending, Time.realtimeSinceStartup - s_movedAt, panelClosed)) return;
            s_savePending = false;
            try { PlatformPrefs.Save(); }
            catch (Exception) { }
        }

        private static void AfterFocus(bool hasFocus)
        {
            if (!Working) return;
            try
            {
                bool muteOn = (s_azuMute?.GetValue(null) as ConfigEntryBase)?.BoxedValue?.ToString() == "On";
                if (OdinVolume.RestoreListener(hasFocus, AudioListener.volume, PlayerPrefs.GetFloat("MasterVolume", 1f), muteOn)) AudioListener.volume = 1f;
            }
            catch (Exception) { }
        }

        /// <summary>OdinOnDemand's own master volumes as its config holds them; null before it is hooked.</summary>
        public static string OwnMasters
        {
            get
            {
                if (s_screens == null || s_radios == null || s_girdle == null) return null;
                try
                {
                    return s_screens.GetValue(null, null) is ConfigEntry<float> screens && s_radios.GetValue(null, null) is ConfigEntry<float> radios && s_girdle.GetValue(null, null) is ConfigEntry<float> girdle
                        ? OdinVolume.OwnMastersLine(screens.Value, radios.Value, girdle.Value)
                        : null;
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>The game's master and music volume, as Valheim reads them.</summary>
        public static float Master => PlatformPrefs.GetFloat("MasterVolume", 1f);

        public static float Music => PlatformPrefs.GetFloat("MusicVolume", 1f);
    }
}
