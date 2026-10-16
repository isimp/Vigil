using System;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "OdinOnDemand follows the game's volume". OdinOnDemand (1.3.0) plays through mixers
    /// of its own, and every second sets each mixer's exposed "MasterVolume" to its own master volume in
    /// dB, so Valheim's master and music volume, which Valheim applies inside its own mixers and to its
    /// music source, never reach it. The patch sets that level to 20 log10(master x music) dB, within the
    /// mixer's range, and OdinOnDemand's master volume slider becomes the game's music slider: it shows
    /// the music volume on the game's 0 to 100 % scale, and moving it sets the music volume. OdinOnDemand's
    /// own master volumes stay in its config, unused, for when the patch is off. AzuMiscPatches' "Mute
    /// Game in Background" mutes the listener when the game loses focus and gives it back at the master
    /// volume, which then counts twice for Valheim's own sounds; the patch puts the listener back at 1,
    /// where Valheim keeps it.
    /// </summary>
    internal static class OdinVolume
    {
        /// <summary>The mixer's floor: silence.</summary>
        public const float MinDb = -80f;

        /// <summary>The slider's range as the game's music slider, and as OdinOnDemand's own master volume in dB.</summary>
        public const float SliderMin = 0f, SliderMax = 1f, OwnMin = -15f, OwnMax = 15f;

        /// <summary>OdinOnDemand's own label for the slider.</summary>
        public const string OwnLabel = "Master Volume";

        /// <summary>How long the slider rests before the music volume is saved.</summary>
        public const float SaveAfterSeconds = 1f;

        private const string MusicLabel = "Music Volume ";
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>What moving OdinOnDemand's master volume slider does.</summary>
        public enum SliderMove
        {
            /// <summary>The slider is the music slider: it sets the game's music volume.</summary>
            SetMusic,

            /// <summary>The patch went off while the slider was the music slider: it becomes OdinOnDemand's own again, nothing is written.</summary>
            GiveBack,

            /// <summary>The slider is OdinOnDemand's own: OdinOnDemand sets its master volume, as without the patch.</summary>
            OodsOwn,
        }

        /// <summary>The mixer level for the game's master and music volume (each 0 to 1).</summary>
        public static float Db(float master, float music)
        {
            float factor = Clamp01(master) * Clamp01(music);
            return Math.Max(MinDb, 20f * (float)Math.Log10(factor));
        }

        /// <summary>The music volume the self-test tries: a quarter away from the player's, towards the middle.</summary>
        public static float TrialMusic(float music)
        {
            float m = Clamp01(music);
            return m > 0.5f ? m - 0.25f : m + 0.25f;
        }

        /// <summary>Whether a mixer's level is the one the game's master and music volume call for.</summary>
        public static bool MixerAgrees(float mixerDb, float master, float music) => Math.Abs(mixerDb - Db(master, music)) <= 0.05f;

        /// <summary>Whether a slider with this top is the music slider (the game's 0 to 1), not OdinOnDemand's own (to 15 dB).</summary>
        public static bool IsMusicSlider(float maxValue) => maxValue <= SliderMax + 0.001f;

        public static SliderMove Moved(bool working, bool musicSlider) =>
            !musicSlider ? SliderMove.OodsOwn : working ? SliderMove.SetMusic : SliderMove.GiveBack;

        /// <summary>"Music Volume 70 %", the slider's label while it is the music slider.</summary>
        public static string SliderLabel(float music) => MusicLabel + Percent(music);

        /// <summary>Whether a label is the slider's, OdinOnDemand's own or the music one.</summary>
        public static bool IsVolumeLabel(string text) =>
            text != null && (text == OwnLabel || text.StartsWith(MusicLabel, StringComparison.Ordinal));

        /// <summary>Whether the music volume set from the slider is saved now: the slider rested <see cref="SaveAfterSeconds"/>, or its panel closed.</summary>
        public static bool SaveDue(bool pending, float sinceMoved, bool panelClosed) =>
            pending && (panelClosed || sinceMoved >= SaveAfterSeconds);

        /// <summary>"master 40 % × music 70 % = radios at 28 % of their own slider".</summary>
        public static string NowLine(float master, float music) =>
            "master " + Percent(master) + " × music " + Percent(music) + " = radios at " + Percent(Clamp01(master) * Clamp01(music)) + " of their own slider";

        /// <summary>OdinOnDemand's own master volumes, in dB, as left in its config.</summary>
        public static string OwnMastersLine(float screensDb, float radiosDb, float girdleDb) =>
            "screens " + Signed(screensDb) + ", radios " + Signed(radiosDb) + ", girdle " + Signed(girdleDb) +
            ": unused while the patch is on, left in OdinOnDemand's config for when it is off";

        /// <summary>Whether the listener holds Azu's restore (the master volume, not 1) right after the game got focus back.</summary>
        public static bool RestoreListener(bool hasFocus, float listener, float masterPref, bool azuMuteOn) =>
            hasFocus && azuMuteOn && Math.Abs(listener - masterPref) < 0.001f && Math.Abs(listener - 1f) >= 0.001f;

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool oodFound, string oodVersion, string problem, bool azuFound, string azuVersion)
        {
            if (!on) return "off: the server's switch";
            if (!oodFound) return "not needed: OdinOnDemand is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return "working · OdinOnDemand " + oodVersion + " hooked · " + (azuFound ? "AzuMiscPatches " + azuVersion + " hooked" : "AzuMiscPatches not installed, nothing to give back");
        }

        private static float Clamp01(float v) => Math.Max(0f, Math.Min(1f, v));

        private static string Percent(float v) => Math.Round(Clamp01(v) * 100f).ToString("0", Ci) + " %";

        private static string Signed(float db)
        {
            double r = Math.Round(db, 1, MidpointRounding.AwayFromZero);
            if (r == 0) return "0 dB";
            return (r > 0 ? "+" : "−") + Math.Abs(r).ToString("0.#", Ci) + " dB";
        }
    }
}
