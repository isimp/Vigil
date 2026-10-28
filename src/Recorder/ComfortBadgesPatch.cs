using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "ComfortMe redraws its badges only on a change" (<see cref="ComfortBadges"/>):
    /// a prefix and postfix on ComfortMe's <c>BuildHudBadge.Refresh</c>, which its postfix on
    /// <c>Hud.UpdatePieceList</c> calls on every frame the build menu is open. The prefix skips the
    /// redraw when the hud, the player, the snapshot object, whether the menu is open and the number
    /// of piece buttons are those of the last redraw. ComfortMe hands out a new snapshot object at
    /// least every 0.2 s and whenever the player moves, so a badge that follows the player's
    /// materials or known pieces follows them within that time, as the snapshot itself does. A
    /// prefix on <c>BuildHudBadge.OnPieceButtonsRebuilt</c> and ComfortMe's own config changes force
    /// the next redraw. The redraws that run are timed for the state line. The switch is this game's own.
    /// </summary>
    internal static class ComfortBadgesPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.comfortbadges";

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        public static string Version { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }
        public static long Ran { get; private set; }
        public static long Skipped { get; private set; }
        private static long s_ranTicks;

        public static string State => ComfortBadges.StateLine(On, Found, Version, Problem, Ran, Skipped, s_ranTicks * 1000.0 / Stopwatch.Frequency);

        private static Func<Hud, bool> s_menuOpen;
        private static AccessTools.FieldRef<BuildUi, List<BuildUiPieceButton>> s_buttons;
        private static ComfortBadges.Key s_last;
        private static bool s_hasLast, s_forced;

        public static void Hook()
        {
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == ComfortBadges.Guid);
            Found = info != null && info.Instance != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var asm = info.Instance.GetType().Assembly;
                var badge = asm.GetType("ComfortMe.BuildHudBadge");
                var hudUi = asm.GetType("ComfortMe.HudUi");
                var snapshot = asm.GetType("ComfortMe.ComfortSnapshot");
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var refresh = badge != null && snapshot != null ? badge.GetMethod("Refresh", any, null, new[] { typeof(Hud), snapshot }, null) : null;
                var rebuilt = badge != null && snapshot != null ? badge.GetMethod("OnPieceButtonsRebuilt", any, null, new[] { typeof(Hud), snapshot }, null) : null;
                var open = hudUi?.GetMethod("BuildMenuOpen", any, null, new[] { typeof(Hud) }, null);
                if (refresh == null || rebuilt == null || open == null || open.ReturnType != typeof(bool) || AccessTools.Field(typeof(BuildUi), "m_pieceButtons") == null)
                {
                    Problem = "its badge code changed (an update)";
                    return;
                }
                s_menuOpen = (Func<Hud, bool>)Delegate.CreateDelegate(typeof(Func<Hud, bool>), open);
                s_buttons = AccessTools.FieldRefAccess<BuildUi, List<BuildUiPieceButton>>("m_pieceButtons");
                info.Instance.Config.SettingChanged += (_, __) => s_forced = true;
                var h = new Harmony(HarmonyId);
                h.Patch(refresh, prefix: Hm(nameof(BeforeRefresh)), postfix: Hm(nameof(AfterRefresh)));
                h.Patch(rebuilt, prefix: Hm(nameof(BeforeRebuilt)));
            }
            catch (Exception e)
            {
                Problem = "ComfortMe could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static HarmonyMethod Hm(string name) => new HarmonyMethod(typeof(ComfortBadgesPatch), name);

        private static void BeforeRebuilt() => s_forced = true;

        // ComfortMe's parameters are taken by position as object: its snapshot type is internal to it.
        private static bool BeforeRefresh(object __0, object __1, ref long __state)
        {
            __state = 0;
            if (!On || Problem != null) return true;
            try
            {
                var hud = __0 as Hud;
                var ui = hud != null ? hud.m_buildUi : null;
                var buttons = ui != null ? s_buttons(ui) : null;
                var now = new ComfortBadges.Key(hud, Player.m_localPlayer, __1, s_menuOpen(hud), buttons != null ? buttons.Count : -1);
                if (ComfortBadges.Skip(s_last, now, s_hasLast, s_forced))
                {
                    Skipped++;
                    return false;
                }
                s_last = now;
                s_hasLast = true;
                s_forced = false;
                Ran++;
                __state = Stopwatch.GetTimestamp();
                return true;
            }
            catch (Exception e)
            {
                // Left to ComfortMe from here on; it redraws every frame as it always did.
                Problem = "the badge check failed: " + e.GetType().Name + " " + e.Message;
                return true;
            }
        }

        private static void AfterRefresh(long __state)
        {
            if (__state != 0) s_ranTicks += Stopwatch.GetTimestamp() - __state;
        }
    }
}
