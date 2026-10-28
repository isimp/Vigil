using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "ComfortMe clears its hover highlight only when one is lit" (<see cref="ComfortPlates"/>):
    /// a prefix and postfix on ComfortMe's <c>GroupHoverHighlight.HideAll</c>, which its postfix on
    /// <c>Hud.Update</c> calls on every frame outside its comfort tab and which looks up and switches
    /// off the highlight plate of every piece button. A postfix on <c>GroupHoverHighlight.SetLift</c>
    /// notes every plate lit, one on the game's <c>BuildUi.UpdatePieceButtons</c> every rebuild of the
    /// buttons; the clearing runs after either and is skipped otherwise. The lifts are noted with the
    /// switch off too, so switching it on mid-session starts right. The clearings that run are timed
    /// for the state line. The switch is this game's own.
    /// </summary>
    internal static class ComfortPlatesPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.comfortplates";

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        public static string Version { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }
        public static long Ran { get; private set; }
        public static long Skipped { get; private set; }
        private static long s_ranTicks;
        private static readonly ComfortPlates s_plates = new ComfortPlates();

        public static string State => ComfortPlates.StateLine(On, Found, Version, Problem, Ran, Skipped, s_ranTicks * 1000.0 / Stopwatch.Frequency);

        public static void Hook()
        {
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == ComfortBadges.Guid);
            Found = info != null && info.Instance != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var highlight = info.Instance.GetType().Assembly.GetType("ComfortMe.GroupHoverHighlight");
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var hideAll = highlight?.GetMethod("HideAll", any, null, Type.EmptyTypes, null);
                var setLift = highlight?.GetMethod("SetLift", any, null, new[] { typeof(BuildUiPieceButton), typeof(bool) }, null);
                var rebuild = AccessTools.DeclaredMethod(typeof(BuildUi), "UpdatePieceButtons");
                if (hideAll == null || setLift == null || rebuild == null)
                {
                    Problem = "its highlight code changed (an update)";
                    return;
                }
                var h = new Harmony(HarmonyId);
                h.Patch(setLift, postfix: Hm(nameof(AfterSetLift)));
                h.Patch(rebuild, postfix: Hm(nameof(AfterRebuild)));
                h.Patch(hideAll, prefix: Hm(nameof(BeforeHideAll)), postfix: Hm(nameof(AfterHideAll)));
            }
            catch (Exception e)
            {
                Problem = "ComfortMe could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static HarmonyMethod Hm(string name) => new HarmonyMethod(typeof(ComfortPlatesPatch), name);

        private static void AfterSetLift(bool __1) => s_plates.Lifted(__1);

        private static void AfterRebuild() => s_plates.ButtonsRebuilt();

        private static bool BeforeHideAll(ref long __state)
        {
            __state = 0;
            if (!s_plates.ClearingRuns(On && Problem == null))
            {
                Skipped++;
                return false;
            }
            if (On) Ran++;
            __state = Stopwatch.GetTimestamp();
            return true;
        }

        private static void AfterHideAll(long __state)
        {
            if (__state == 0) return;
            s_plates.Cleared();
            if (On) s_ranTicks += Stopwatch.GetTimestamp() - __state;
        }
    }
}
