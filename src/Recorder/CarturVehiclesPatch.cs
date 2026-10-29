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
    /// The game patch "HUDCompass leaves your carts and boats to Cartur" (<see cref="CarturPinRules"/>).
    /// Cartur's Map Pins pins the carts and boats you built and keeps each pin where you last saw the
    /// thing; HUDCompass 1.2.1 pins every cart, boat and portal by its own rules, so your own cart had
    /// two pins that came and went apart. On, HUDCompass draws no pin, on the map or the compass, for
    /// a cart or boat Cartur draws a pin for (the same object, by its id); carts and boats Cartur does
    /// not pin, such as other players', keep HUDCompass's. Both mods' code is compared with the code
    /// this was written against (HUDCompass 1.2.1, Cartur 2.0.0), and any change makes the patch step
    /// aside. The switch is this game's own.
    /// </summary>
    internal static class CarturVehiclesPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.carturvehicles";
        public const string CarturGuid = "com.jekkle.valheim.carturmappins";

        /// <summary>HUDCompass 1.2.1's DrawMarker, its map pin postfix and MarkerToPinData.</summary>
        private const ulong KnownCompassCode = 0xA2263FC08E9D9063UL;

        /// <summary>Cartur 2.0.0's Trackers.Note and Trackers.Draw.</summary>
        private const ulong KnownCarturCode = 0xCCC2A8CC3E5041A9UL;

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        private static int s_leftLastFrame;

        public static string State => CarturPinRules.VehiclesLine(On, Found, Problem, s_leftLastFrame);

        private static Action<string> s_log;
        private static object s_dmm;
        private static FieldInfo s_markersField;
        private static List<Minimap.PinData> s_dynamicPins;
        private static Func<object, ZDOID> s_zdoid;
        private static Func<object, Vector3> s_position;
        private static Func<object, object> s_type;
        private static Func<object, int> s_group;
        private static Func<object, GameObject> s_marker, s_nametag;
        private static IDictionary s_entries;
        private static Func<object, Minimap.PinData> s_pinOf;

        private static readonly Dictionary<ZDOID, string> s_keys = new Dictionary<ZDOID, string>();
        private static readonly Dictionary<object, int> s_groups = new Dictionary<object, int>();
        private static readonly List<Vector3> s_left = new List<Vector3>();
        private static readonly Predicate<Minimap.PinData> s_isLeft = IsLeft;

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var compass = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == HudCompassPatch.Guid);
            var cartur = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == CarturGuid);
            Found = compass?.Instance != null && cartur?.Instance != null;
            if (!Found) return;
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var hud = compass.Instance.GetType().Assembly;
                var main = hud.GetType("neobotics.ValheimMods.HUDCompass");
                var dmmType = hud.GetType("neobotics.ValheimMods.DynamicMapMarkers");
                var mapPatch = hud.GetType("neobotics.ValheimMods.DynamicMapMarkers+Minimap_UpdatePins_Patch");
                var markerData = hud.GetType("neobotics.ValheimMods.MarkerData");
                var markerType = hud.GetType("neobotics.ValheimMods.MarkerType");
                var draw = dmmType?.GetMethod("DrawMarker", any);
                var mapPostfix = mapPatch?.GetMethod("Minimap_UpdatePins_DMM_Postfix", any);
                var toPin = dmmType?.GetMethod("MarkerToPinData", any);
                if (main == null || draw == null || mapPostfix == null || toPin == null || markerData == null || markerType == null
                    || CodeHash(new[] { draw, mapPostfix, toPin }) != KnownCompassCode)
                {
                    Problem = "HUDCompass's map pin code changed (an update)";
                    return;
                }
                var trackers = cartur.Instance.GetType().Assembly.GetType("CarturMapPins.Trackers");
                var note = trackers?.GetMethod("Note", any);
                var drawTracked = trackers?.GetMethod("Draw", any);
                var tracked = trackers?.GetNestedType("Tracked", BindingFlags.NonPublic);
                if (note == null || drawTracked == null || tracked == null || CodeHash(new[] { note, drawTracked }) != KnownCarturCode)
                {
                    Problem = "Cartur's tracker code changed (an update)";
                    return;
                }

                s_dmm = AccessTools.DeclaredField(main, "s_dmm")?.GetValue(null);
                s_markersField = AccessTools.DeclaredField(dmmType, "Markers");
                s_dynamicPins = s_dmm == null ? null : AccessTools.DeclaredField(dmmType, "PinData")?.GetValue(s_dmm) as List<Minimap.PinData>;
                s_zdoid = Getter<ZDOID>(markerData, "Zdoid");
                s_position = Getter<Vector3>(markerData, "Position");
                s_type = Getter<object>(markerData, "Type");
                s_group = Getter<int>(markerType, "Group");
                s_marker = Getter<GameObject>(markerData, "Marker");
                s_nametag = Getter<GameObject>(markerData, "Nametag");
                s_entries = AccessTools.DeclaredField(trackers, "Entries")?.GetValue(null) as IDictionary;
                // Tracked is a private class, read through Harmony's field access rather than a compiled expression.
                var pinField = AccessTools.DeclaredField(tracked, "Pin");
                if (pinField != null && pinField.FieldType == typeof(Minimap.PinData))
                {
                    var pinRef = AccessTools.FieldRefAccess<object, Minimap.PinData>(pinField);
                    s_pinOf = o => pinRef(o);
                }
                if (s_pinOf == null || s_dmm == null || s_markersField == null || s_dynamicPins == null || s_entries == null)
                {
                    Problem = "HUDCompass's or Cartur's code changed (an update)";
                    return;
                }
                var h = new Harmony(HarmonyId);
                // DrawMarker is static: its MarkerData is taken by position.
                h.Patch(draw, prefix: new HarmonyMethod(typeof(CarturVehiclesPatch), nameof(BeforeDraw)));
                h.Patch(mapPostfix, postfix: new HarmonyMethod(typeof(CarturVehiclesPatch), nameof(AfterMapPins)));
            }
            catch (Exception e)
            {
                Problem = "HUDCompass or Cartur could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        /// <summary>FNV-1a over the IL of each method in turn.</summary>
        private static ulong CodeHash(IEnumerable<MethodInfo> methods)
        {
            ulong h = 14695981039346656037UL;
            foreach (var m in methods)
                foreach (byte b in m.GetMethodBody().GetILAsByteArray())
                {
                    h ^= b;
                    h *= 1099511628211UL;
                }
            return h;
        }

        /// <summary>A compiled read of a property or field of a type this mod cannot name.</summary>
        private static Func<object, T> Getter<T>(Type type, string name)
        {
            var o = Expression.Parameter(typeof(object), "o");
            Expression member = Expression.PropertyOrField(Expression.Convert(o, type), name);
            return Expression.Lambda<Func<object, T>>(Expression.Convert(member, typeof(T)), o).Compile();
        }

        private static bool Active => On && Problem == null;

        /// <summary>Whether Cartur draws a pin for this marker's object, which then has no HUDCompass pin.</summary>
        private static bool LeftToCartur(object data)
        {
            var type = s_type(data);
            if (type == null) return false;
            if (!s_groups.TryGetValue(type, out int group))
            {
                group = s_group(type);
                s_groups[type] = group;
            }
            var id = s_zdoid(data);
            if (!s_keys.TryGetValue(id, out var key))
            {
                // Cartur keys its tracked things by the id as the game prints it.
                key = id.ToString();
                s_keys[id] = key;
            }
            var entry = s_entries[key];
            return CarturPinRules.LeftToCartur(group, entry != null && s_pinOf(entry) != null);
        }

        private static bool BeforeDraw(object __0)
        {
            if (!Active || __0 == null) return true;
            try
            {
                if (!LeftToCartur(__0)) return true;
                // HUDCompass destroys a marker's objects the same way when it is not drawn.
                var marker = s_marker(__0);
                if (marker != null) UnityEngine.Object.Destroy(marker);
                var tag = s_nametag(__0);
                if (tag != null) UnityEngine.Object.Destroy(tag);
                return false;
            }
            catch (Exception e)
            {
                StepAside("its map pin check failed: " + e.GetType().Name + " " + e.Message);
                return true;
            }
        }

        /// <summary>After HUDCompass lists its compass pins: the carts and boats Cartur pins come off the list.</summary>
        private static void AfterMapPins()
        {
            if (!Active) return;
            try
            {
                s_left.Clear();
                if (s_markersField.GetValue(s_dmm) is IDictionary markers)
                    foreach (object data in markers.Values)
                        if (data != null && LeftToCartur(data)) s_left.Add(s_position(data));
                s_leftLastFrame = s_left.Count;
                if (s_left.Count > 0) s_dynamicPins.RemoveAll(s_isLeft);
            }
            catch (Exception e)
            {
                StepAside("its compass pin check failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>A compass pin HUDCompass made from a marker left to Cartur: same position, a ship or cart pin.</summary>
        private static bool IsLeft(Minimap.PinData pin)
        {
            if (pin == null || (pin.m_name != "Cart" && pin.m_name != "Ship")) return false;
            foreach (var p in s_left)
                if (p == pin.m_pos) return true;
            return false;
        }

        private static void StepAside(string why)
        {
            Problem = why;
            s_log?.Invoke("Vigil: the patch leaving your carts and boats to Cartur stepped aside: " + why);
        }
    }
}
