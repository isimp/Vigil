using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Cartur's map search and hide key cover HUDCompass's pins" (<see cref="CarturPinRules"/>).
    /// Cartur's Map Pins fades a map pin whose name does not hold the text typed into its search on
    /// the large map and hides its label, and its hide key steps through all pins, only your own,
    /// only auto pins (2.1.0) and none. HUDCompass 1.2.1 draws its cart, ship and portal pins itself,
    /// not as map pins, so both passed them by. On, a postfix after HUDCompass's map pin update gives
    /// each of those pins the same strength: gone while the hide key shows none or only your own
    /// (nobody places them by hand, so they go with the auto pins), else faded when the search does
    /// not match its name (a portal's tag, a cart's or ship's name or type), through Cartur's own
    /// matching; its label shown or gone alike. A CanvasGroup on the pin carries it, so HUDCompass's
    /// colours stay as they are. The hide key's mode is read by its name, which means the same in
    /// 2.0 and 2.1.0 although the order changed. Both mods' code is compared with the code this was
    /// written against (HUDCompass 1.2.1, Cartur 2.0.0 to 2.1.0), and any change makes the patch
    /// step aside. The switch is this game's own.
    /// </summary>
    internal static class CarturMapPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.carturmap";

        /// <summary>HUDCompass 1.2.1's DrawMarker, its map pin postfix and MarkerToPinData.</summary>
        private const ulong KnownCompassCode = 0x0F71C38696676BFDUL;

        /// <summary>The code (<see cref="CodeShape"/>) of Cartur's Patch_Minimap_UpdatePins.Postfix (its search fades the map's pins), PinHiding.CycleHides and PinHiding.Step (its hide key): 2.0.0 and 2.0.1, then 2.1.0 (a fourth mode, only auto pins).</summary>
        private static readonly ulong[] KnownCarturCode = { 0xDC9C8AF80AB28234UL, 0x0ED0DB2241B986BBUL };

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        private static bool s_searching, s_changed;
        private static string s_mode;
        private static int s_matching, s_pins;

        public static string State => CarturPinRules.MapLine(On, Found, Problem, s_mode, s_searching, s_matching, s_pins);

        private static Action<string> s_log;
        private static object s_dmm;
        private static FieldInfo s_markersField;
        private static Func<object, GameObject> s_marker, s_nametag;
        private static Func<object, string> s_label;
        private static Func<bool> s_searchActive;
        private static Func<string, bool> s_searchMatches;
        private static Func<object> s_hideMode;

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var compass = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == HudCompassPatch.Guid);
            var cartur = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == HudCompassPatch.CarturGuid);
            Found = compass?.Instance != null && cartur?.Instance != null;
            if (!Found) return;
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var hud = compass.Instance.GetType().Assembly;
                var main = hud.GetType("neobotics.ValheimMods.HUDCompass");
                var dmmType = hud.GetType("neobotics.ValheimMods.DynamicMapMarkers");
                var markerData = hud.GetType("neobotics.ValheimMods.MarkerData");
                var draw = dmmType?.GetMethod("DrawMarker", any);
                var mapPostfix = hud.GetType("neobotics.ValheimMods.DynamicMapMarkers+Minimap_UpdatePins_Patch")?.GetMethod("Minimap_UpdatePins_DMM_Postfix", any);
                var toPin = dmmType?.GetMethod("MarkerToPinData", any);
                if (main == null || markerData == null || draw == null || mapPostfix == null || toPin == null
                    || CodeShape.Hash(new[] { draw, mapPostfix, toPin }) != KnownCompassCode)
                {
                    Problem = "HUDCompass's map pin code changed (an update)";
                    return;
                }
                var asm = cartur.Instance.GetType().Assembly;
                var fade = asm.GetType("CarturMapPins.Patch_Minimap_UpdatePins")?.GetMethod("Postfix", any);
                var filter = asm.GetType("CarturMapPins.PinFilter");
                var hiding = asm.GetType("CarturMapPins.PinHiding");
                var active = filter?.GetProperty("Active", any)?.GetGetMethod(true);
                var matches = filter?.GetMethod("Matches", any, null, new[] { typeof(string) }, null);
                var cycleHides = hiding?.GetMethod("CycleHides", any);
                var step = hiding?.GetMethod("Step", any);
                var cycle = hiding?.GetProperty("Cycle", any)?.GetGetMethod(true);
                if (fade == null || active == null || matches == null || cycleHides == null || step == null || cycle == null || !cycle.ReturnType.IsEnum
                    || !KnownCarturCode.Contains(CodeShape.Hash(new[] { fade, cycleHides, step })))
                {
                    Problem = "Cartur's search or hiding code changed (an update)";
                    return;
                }
                s_searchActive = AccessTools.MethodDelegate<Func<bool>>(active);
                s_searchMatches = AccessTools.MethodDelegate<Func<string, bool>>(matches);
                s_hideMode = OpenCall.Make<Func<object>>(cycle);
                s_dmm = AccessTools.DeclaredField(main, "s_dmm")?.GetValue(null);
                s_markersField = AccessTools.DeclaredField(dmmType, "Markers");
                s_marker = Getter<GameObject>(markerData, "Marker");
                s_nametag = Getter<GameObject>(markerData, "Nametag");
                s_label = Getter<string>(markerData, "Label");
                if (s_dmm == null || s_markersField == null)
                {
                    Problem = "HUDCompass's map pin code changed (an update)";
                    return;
                }
                new Harmony(HarmonyId).Patch(mapPostfix, postfix: new HarmonyMethod(typeof(CarturMapPatch), nameof(AfterMapPins)));
            }
            catch (Exception e)
            {
                Problem = "HUDCompass or Cartur could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static Func<object, T> Getter<T>(Type type, string name)
        {
            var o = Expression.Parameter(typeof(object), "o");
            Expression member = Expression.PropertyOrField(Expression.Convert(o, type), name);
            return Expression.Lambda<Func<object, T>>(Expression.Convert(member, typeof(T)), o).Compile();
        }

        /// <summary>After HUDCompass's map pin update: its pins take the hide key's and the search's strength; once neither applies they are shown in full again.</summary>
        private static void AfterMapPins()
        {
            if (Problem != null) return;
            try
            {
                string mode = On ? s_hideMode().ToString() : null;
                bool searching = On && s_searchActive();
                s_mode = mode;
                s_searching = searching;
                bool changes = CarturPinRules.KeyHides(mode) || searching;
                if (!changes && !s_changed) return;
                if (!(s_markersField.GetValue(s_dmm) is IDictionary markers)) return;
                int matching = 0, pins = 0;
                foreach (object marker in markers.Values)
                {
                    var icon = s_marker(marker);
                    if (icon == null) continue;
                    pins++;
                    bool match = !searching || s_searchMatches(s_label(marker));
                    if (match) matching++;
                    SetAlpha(icon, CarturPinRules.MapIconAlpha(mode, searching, match));
                    var tag = s_nametag(marker);
                    if (tag != null) SetAlpha(tag, CarturPinRules.MapLabelAlpha(mode, searching, match));
                }
                s_matching = matching;
                s_pins = pins;
                s_changed = changes;
            }
            catch (Exception e)
            {
                Problem = "its search or hiding check failed: " + e.GetType().Name + " " + e.Message;
                s_log?.Invoke("Vigil: the patch for Cartur's map search and hide key on HUDCompass's pins stepped aside: " + Problem);
            }
        }

        /// <summary>The pin's strength, on a CanvasGroup made only once it is needed.</summary>
        private static void SetAlpha(GameObject go, float alpha)
        {
            var group = go.GetComponent<CanvasGroup>();
            if (group == null)
            {
                if (alpha >= 1f) return;
                group = go.AddComponent<CanvasGroup>();
            }
            if (!Mathf.Approximately(group.alpha, alpha)) group.alpha = alpha;
        }
    }
}
