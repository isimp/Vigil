using System;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "OdinOnDemand follows the game's volume". OdinOnDemand (1.3.0) plays through mixers
    /// of its own, and every second sets each mixer's exposed "MasterVolume" to its own level in dB, so
    /// Valheim's master and music volume, which Valheim applies inside its own mixers and to its music
    /// source, never reach it. The patch moves that level by 20 log10(master x music) dB, within the
    /// mixer's range. AzuMiscPatches' "Mute Game in Background" mutes the listener when the game loses
    /// focus and gives it back at the master volume, which then counts twice for Valheim's own sounds;
    /// the patch puts the listener back at 1, where Valheim keeps it.
    /// </summary>
    internal static class OdinVolume
    {
        public const float MinDb = -80f, MaxDb = 20f;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>OdinOnDemand's own level <paramref name="ownDb"/> moved by the game's master and music volume (each 0 to 1).</summary>
        public static float Db(float ownDb, float master, float music)
        {
            float factor = Clamp01(master) * Clamp01(music);
            return Math.Max(MinDb, Math.Min(MaxDb, ownDb + 20f * (float)Math.Log10(factor)));
        }

        /// <summary>"master 40 % × music 70 % = radios at 28 % of their own level".</summary>
        public static string NowLine(float master, float music) =>
            "master " + Percent(master) + " × music " + Percent(music) + " = radios at " + Percent(Clamp01(master) * Clamp01(music)) + " of their own level";

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
    }
}
