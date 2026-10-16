using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "OdinOnDemand follows the game's volume" (<see cref="OdinVolume"/>). A postfix on
    /// OdinOnDemand's once-a-second <c>BasePlayer.UpdateChecks</c>, which sets the player's mixer to its
    /// own level, moves that level by the game's master and music volume; turning the patch off lets
    /// OdinOnDemand set its own level again the next second. A postfix on
    /// <c>EventSystem.OnApplicationFocus</c> after every other mod's puts the listener back at 1 when
    /// AzuMiscPatches gave it back at the master volume. Both find their targets by name and step aside,
    /// with the reason, when an update changed them. On a player's game only.
    /// </summary>
    internal static class OdinVolumePatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.odinvolume";
        private const string OodGuid = "com.valmedia.odinondemand", AzuGuid = "Azumatt.AzuMiscPatches";

        private static PropertyInfo s_audio, s_settings, s_type;
        private static MethodInfo s_masterFor;
        private static FieldInfo s_azuMute;

        /// <summary>The server's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool OodFound { get; private set; }

        public static bool AzuFound { get; private set; }

        public static string OodVersion { get; private set; }

        public static string AzuVersion { get; private set; }

        /// <summary>Why the patch stepped aside; null when its hooks are in (or OdinOnDemand is absent).</summary>
        public static string Problem { get; private set; }

        public static string State => OdinVolume.StateLine(On, OodFound, OodVersion, Problem, AzuFound, AzuVersion);

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
                    var player = AccessTools.TypeByName("OdinOnDemand.MPlayer.BasePlayer");
                    var settings = AccessTools.TypeByName("OdinOnDemand.MPlayer.PlayerSettings");
                    var update = player != null ? AccessTools.Method(player, "UpdateChecks") : null;
                    s_audio = player != null ? AccessTools.Property(player, "mAudio") : null;
                    s_settings = player != null ? AccessTools.Property(player, "PlayerSettings") : null;
                    s_type = settings != null ? AccessTools.Property(settings, "PlayerType") : null;
                    s_masterFor = AccessTools.Method("OdinOnDemand.Utils.Config.OODConfig:MasterVolumeFor");
                    if (update == null || s_audio == null || s_settings == null || s_type == null || s_masterFor == null)
                        Problem = "OdinOnDemand's volume update was not found (an update changed it)";
                    else harmony.Patch(update, postfix: new HarmonyMethod(typeof(OdinVolumePatch), nameof(AfterUpdateChecks)) { priority = Priority.Last });
                }
                catch (Exception e) { Problem = "OdinOnDemand could not be hooked: " + e.GetType().Name + " " + e.Message; }
            }
            if (AzuFound && Problem == null)
            {
                try
                {
                    var events = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
                    var focus = events != null ? AccessTools.Method(events, "OnApplicationFocus") : null;
                    var azuPlugin = AccessTools.TypeByName("AzuMiscPatches.AzuMiscPatchesPlugin");
                    s_azuMute = azuPlugin != null ? AccessTools.Field(azuPlugin, "MuteGameInBackground") : null;
                    if (focus == null || s_azuMute == null) Problem = "AzuMiscPatches' mute in background was not found (an update changed it)";
                    else harmony.Patch(focus, postfix: new HarmonyMethod(typeof(OdinVolumePatch), nameof(AfterFocus)) { priority = Priority.Last });
                }
                catch (Exception e) { Problem = "AzuMiscPatches could not be hooked: " + e.GetType().Name + " " + e.Message; }
            }
        }

        private static void AfterUpdateChecks(object __instance)
        {
            if (!On || Problem != null) return;
            try
            {
                var audio = s_audio.GetValue(__instance, null) as AudioSource;
                AudioMixer mixer = audio != null && audio.outputAudioMixerGroup != null ? audio.outputAudioMixerGroup.audioMixer : null;
                if (mixer == null) return;
                object kind = s_type.GetValue(s_settings.GetValue(__instance, null), null);
                if (!(s_masterFor.Invoke(null, new[] { kind }) is ConfigEntry<float> own)) return;
                float target = OdinVolume.Db(own.Value, Master, Music);
                if (!mixer.GetFloat("MasterVolume", out float now) || Math.Abs(now - target) > 0.01f) mixer.SetFloat("MasterVolume", target);
            }
            catch (Exception) { }
        }

        private static void AfterFocus(bool hasFocus)
        {
            if (!On || Problem != null) return;
            try
            {
                bool muteOn = (s_azuMute?.GetValue(null) as ConfigEntryBase)?.BoxedValue?.ToString() == "On";
                if (OdinVolume.RestoreListener(hasFocus, AudioListener.volume, PlayerPrefs.GetFloat("MasterVolume", 1f), muteOn)) AudioListener.volume = 1f;
            }
            catch (Exception) { }
        }

        /// <summary>The game's master and music volume, as Valheim reads them.</summary>
        public static float Master => PlatformPrefs.GetFloat("MasterVolume", 1f);

        public static float Music => PlatformPrefs.GetFloat("MusicVolume", 1f);
    }
}
