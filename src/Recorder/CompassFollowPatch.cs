using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "HUDCompass's pins follow their portal, cart or ship" (<see cref="PinFollowRules"/>).
    /// The game redraws its map pins only when something asks for it (walking, dragging, zooming,
    /// a pin added or removed), and HUDCompass 1.2.1 places its pins only in that redraw; it writes a
    /// pin's name tag and colour only when it makes the pin, and makes it again only after the pin
    /// was hidden. So a portal moved, renamed or linked while the player stands near kept its old
    /// place, name and colour until the pins were hidden once (TargetPortal does that on entering a
    /// portal). On, every quarter second each pin is compared with its object, by the same rules the
    /// redraw uses (the server's place for a far one while VehiclePinsFromServer is in use), and a
    /// redraw is asked for when one differs; after each redraw a pin whose name tag or portal colour
    /// is out of date is made again. HUDCompass's code is compared with the code this was written
    /// against (1.2.1), and any change makes the patch step aside. The switch is this game's own.
    /// </summary>
    internal static class CompassFollowPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.compassfollow";

        /// <summary>HUDCompass 1.2.1's DrawMarker, its map pin postfix and MarkerToPinData.</summary>
        private const ulong KnownCode = 0x0F71C38696676BFDUL;

        private const float CheckSeconds = 0.25f;

        // HUDCompass's MarkerGroup.
        private const int Ship = 0, Cart = 1, Portal = 2, ActivePortal = 3;

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        private static long s_redraws, s_rebuilt;

        public static string State => PinFollowRules.CompassLine(On, Found, Problem, s_redraws, s_rebuilt);

        private static Action<string> s_log;
        private static object s_dmm;
        private static FieldInfo s_markersField;
        private static Func<object, GameObject> s_marker, s_nametag;
        private static Func<object, string> s_label;
        private static Func<object, Vector3> s_position;
        private static Func<object, bool> s_active;
        private static Func<object, ZDOID> s_zdoid;
        private static Func<object, object> s_type;
        private static Func<object, int> s_group;
        private static Action<object> s_destroy;
        private static readonly AccessTools.FieldRef<Minimap, bool> s_pinUpdate = AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");
        private static float s_nextCheck;

        /// <summary>A pin's icon as it was made, with the link state its colour shows.</summary>
        private struct Made
        {
            public GameObject Icon;
            public bool Active;
        }

        private static readonly Dictionary<object, Made> s_made = new Dictionary<object, Made>();
        private static readonly List<object> s_sweep = new List<object>();

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == HudCompassPatch.Guid);
            Found = info?.Instance != null;
            if (!Found) return;
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var asm = info.Instance.GetType().Assembly;
                var main = asm.GetType("neobotics.ValheimMods.HUDCompass");
                var dmmType = asm.GetType("neobotics.ValheimMods.DynamicMapMarkers");
                var markerData = asm.GetType("neobotics.ValheimMods.MarkerData");
                var markerType = asm.GetType("neobotics.ValheimMods.MarkerType");
                var draw = dmmType?.GetMethod("DrawMarker", any);
                var mapPostfix = asm.GetType("neobotics.ValheimMods.DynamicMapMarkers+Minimap_UpdatePins_Patch")?.GetMethod("Minimap_UpdatePins_DMM_Postfix", any);
                var toPin = dmmType?.GetMethod("MarkerToPinData", any);
                var destroy = markerData?.GetMethod("Destroy", any);
                if (main == null || markerData == null || markerType == null || draw == null || mapPostfix == null || toPin == null || destroy == null
                    || CodeShape.Hash(new[] { draw, mapPostfix, toPin }) != KnownCode)
                {
                    Problem = "HUDCompass's map pin code changed (an update)";
                    return;
                }
                s_dmm = AccessTools.DeclaredField(main, "s_dmm")?.GetValue(null);
                s_markersField = AccessTools.DeclaredField(dmmType, "Markers");
                s_marker = Getter<GameObject>(markerData, "Marker");
                s_nametag = Getter<GameObject>(markerData, "Nametag");
                s_label = Getter<string>(markerData, "Label");
                s_position = Getter<Vector3>(markerData, "Position");
                s_active = Getter<bool>(markerData, "IsActivePortal");
                s_zdoid = Getter<ZDOID>(markerData, "Zdoid");
                s_type = Getter<object>(markerData, "Type");
                s_group = Getter<int>(markerType, "Group");
                s_destroy = OpenCall.Make<Action<object>>(destroy);
                if (s_dmm == null || s_markersField == null)
                {
                    Problem = "HUDCompass's map pin code changed (an update)";
                    return;
                }
                // Also runs when VehiclePinsFromServer takes the place of the postfix's own body.
                new Harmony(HarmonyId).Patch(mapPostfix, postfix: new HarmonyMethod(typeof(CompassFollowPatch), nameof(AfterMapPins)));
            }
            catch (Exception e)
            {
                Problem = "HUDCompass could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static Func<object, T> Getter<T>(Type type, string name)
        {
            var o = Expression.Parameter(typeof(object), "o");
            Expression member = Expression.PropertyOrField(Expression.Convert(o, type), name);
            return Expression.Lambda<Func<object, T>>(Expression.Convert(member, typeof(T)), o).Compile();
        }

        private static void StepAside(string why)
        {
            Problem = why;
            s_log?.Invoke("Vigil: the patch for HUDCompass's pins following their portal, cart or ship stepped aside: " + why);
        }

        /// <summary>Every frame: a quarter-second look whether a pin differs from its object; one difference asks the game for a redraw.</summary>
        public static void Update()
        {
            if (!On || !Found || Problem != null) return;
            var map = Minimap.instance;
            if (map == null || ZDOMan.instance == null || Player.m_localPlayer == null) return;
            float now = Time.unscaledTime;
            if (now < s_nextCheck) return;
            s_nextCheck = now + CheckSeconds;
            try
            {
                if (s_pinUpdate(map) || !(s_markersField.GetValue(s_dmm) is IDictionary markers)) return;
                foreach (object marker in markers.Values)
                {
                    if (!Differs(marker)) continue;
                    s_pinUpdate(map) = true;
                    s_redraws++;
                    return;
                }
            }
            catch (Exception e)
            {
                StepAside("its look at the pins failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>Whether the pin, as the last redraw left it, differs from its object as the redraw would place and name it now.</summary>
        private static bool Differs(object marker)
        {
            int group = s_group(s_type(marker));
            bool portal = group == Portal || group == ActivePortal;
            bool named = portal || group == Cart || group == Ship;
            var uid = s_zdoid(marker);
            var zdo = ZDOMan.instance.GetZDO(uid);
            string drawnLabel = s_label(marker);
            bool drawnActive = s_active(marker);
            Vector3 at;
            string label = drawnLabel;
            bool active = drawnActive;
            if (VehiclePinsPatch.ServerPlace(uid, zdo, out var told))
            {
                at = new Vector3(told.X, told.Y, told.Z);
                if (portal) active = told.Active;
                if (named) label = PinFollowRules.ServerLabel(drawnLabel, told.Label);
            }
            else if (zdo != null)
            {
                at = zdo.GetPosition();
                if (portal)
                {
                    active = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None;
                    label = zdo.GetString(ZDOVars.s_tag, drawnLabel);
                }
                else if (named) label = zdo.GetString(ZDOVars.s_text, drawnLabel);
            }
            else return false;
            var drawn = s_position(marker);
            return PinFollowRules.RedrawWanted(PinFollowRules.MovedOnMap(drawn.x, drawn.z, at.x, at.z), drawnLabel, label, drawnActive, active);
        }

        /// <summary>After HUDCompass's map pin update: a pin whose name tag or portal colour is out of date is made again at the next redraw.</summary>
        private static void AfterMapPins()
        {
            if (!On || Problem != null) return;
            try
            {
                if (!(s_markersField.GetValue(s_dmm) is IDictionary markers)) return;
                bool again = false;
                foreach (object marker in markers.Values)
                {
                    var icon = s_marker(marker);
                    if (icon == null) continue;
                    int group = s_group(s_type(marker));
                    bool portal = group == Portal || group == ActivePortal;
                    bool active = s_active(marker);
                    if (!s_made.TryGetValue(marker, out var made) || made.Icon != icon)
                    {
                        // A new icon takes the colour of the state it was made in.
                        s_made[marker] = new Made { Icon = icon, Active = active };
                        made = s_made[marker];
                    }
                    var tag = s_nametag(marker);
                    string shown = tag != null ? tag.GetComponentInChildren<TMP_Text>()?.text : null;
                    if (!PinFollowRules.Rebuild(tag != null, shown, s_label(marker), made.Active, active, portal)) continue;
                    s_destroy(marker);
                    s_made.Remove(marker);
                    s_rebuilt++;
                    again = true;
                }
                if (again && Minimap.instance != null) s_pinUpdate(Minimap.instance) = true;
                if (s_made.Count > markers.Count + 32) Sweep(markers);
            }
            catch (Exception e)
            {
                StepAside("its check after the map pins failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>Forgets the pins HUDCompass no longer has.</summary>
        private static void Sweep(IDictionary markers)
        {
            var live = new HashSet<object>(markers.Values.Cast<object>());
            s_sweep.Clear();
            foreach (var key in s_made.Keys)
                if (!live.Contains(key)) s_sweep.Add(key);
            foreach (var key in s_sweep) s_made.Remove(key);
            s_sweep.Clear();
        }
    }
}
