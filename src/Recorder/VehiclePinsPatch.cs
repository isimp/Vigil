using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Carts, ships and portals where they really are" (<see cref="VehicleBook"/>,
    /// <see cref="VehiclePinRules"/>), switched on the server. A player's game hears only of objects
    /// near it and keeps a far one at the spot it last saw; HUDCompass 1.2.1 pins every cart, ship
    /// and portal it knows, from the server's list when a player joins and from the game after that,
    /// so a far object moved or built later is missing or in its old place. On, the server's Vigil
    /// keeps every cart, ship and portal of the world (found once by a look over all objects, then
    /// among the newly made ones), sends a joining player the whole list and every
    /// <see cref="VehiclePinRules.RefreshSeconds"/> s what was added, moved, renamed or removed;
    /// a changed portal is also sent whole, so every mod reading the game's copy sees it.
    /// The player's Vigil adds and removes HUDCompass's pins by it and draws HUDCompass's pins
    /// itself, by HUDCompass's rules, with a far object at the server's place and a loaded one at
    /// the game's own. HUDCompass's code is compared with the code this was written against (1.2.1),
    /// and any change makes the patch step aside on that game.
    /// </summary>
    internal static class VehiclePinsPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.vehiclepins";
        internal const string RpcList = "Vigil_VehiclePins", RpcAsk = "Vigil_VehiclePinsAsk";
        private const int Format = 1;

        /// <summary>HUDCompass 1.2.1's map pin postfix, DrawMarker, MarkerToPinData, AddMarkerFromServer, RemoveMarker, IsAttachedToPlayer, IsPortalType.</summary>
        private const ulong KnownCode = 0x486932119744CBDAUL;

        /// <summary>The server's switch, passed on by ServerSync.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        private static bool s_server;
        private static Action<string> s_log;
        private static ZRoutedRpc s_registeredOn;

        public static string State => s_server
            ? VehiclePinRules.ServerLine(On, true, Problem, s_index.Count, s_lastChanges)
            : VehiclePinRules.GameLine(On, Found, Problem, s_heard, s_fromServer.Count, s_farLastFrame);

        // ---- On the server ----

        private static readonly VehicleBook s_book = new VehicleBook();
        private static readonly HashSet<ZDOID> s_index = new HashSet<ZDOID>();
        private static readonly List<ZDOID> s_pending = new List<ZDOID>();
        private static readonly List<ZDOID> s_lost = new List<ZDOID>();
        private static readonly List<KeyValuePair<VehicleId, VehicleState>> s_changed = new List<KeyValuePair<VehicleId, VehicleState>>();
        private static readonly List<VehicleId> s_gone = new List<VehicleId>();
        private static readonly List<VehicleId> s_whole = new List<VehicleId>();
        /// <summary>Prefab to kind: true for a portal, false for a cart or ship.</summary>
        private static Dictionary<int, bool> s_kinds;
        private static bool s_scanned;
        private static float s_nextRefresh;
        private static int s_lastChanges;
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> s_objects = AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        // ---- On a player's game ----

        private static readonly Dictionary<ZDOID, VehicleState> s_fromServer = new Dictionary<ZDOID, VehicleState>();
        private static bool s_heard, s_asked;
        private static int s_farLastFrame;

        private static object s_dmm;
        private static FieldInfo s_markersField, s_typesField;
        private static List<Minimap.PinData> s_dynamicPins;
        private static Func<object, ZDOID> s_zdoid;
        private static Func<object, object> s_type;
        private static Func<object, Vector3> s_position;
        private static Action<object, Vector3> s_setPosition;
        private static Func<object, string> s_label;
        private static Action<object, string> s_setLabel;
        private static Action<object, bool> s_setActive;
        private static Func<object, bool> s_show;
        private static Func<object, Minimap.PinType> s_pinType;
        private static Func<object, int> s_group;
        private static Action<ZDOID, int, Vector3, long, bool, string> s_addFromServer;
        private static Action<ZDOID> s_remove;
        private static Action<object, float, RectTransform, RawImage, float, bool> s_draw;
        private static Action<object> s_destroy;
        private static Func<object, object, Minimap.PinData> s_toPin;
        private static Func<ZDO, bool> s_attached;
        private static ConfigEntry<bool> s_onCompass, s_onMap;
        private static readonly AccessTools.FieldRef<Minimap, bool[]> s_visibleTypes = AccessTools.FieldRefAccess<Minimap, bool[]>("m_visibleIconTypes");
        private static readonly Func<Minimap, Vector3, bool> s_isExplored = AccessTools.MethodDelegate<Func<Minimap, Vector3, bool>>(AccessTools.Method(typeof(Minimap), "IsExplored"));
        private static readonly Func<Minimap, Vector3, RawImage, bool> s_isPointVisible = AccessTools.MethodDelegate<Func<Minimap, Vector3, RawImage, bool>>(AccessTools.Method(typeof(Minimap), "IsPointVisible"));

        // HUDCompass's MarkerGroup.
        private const int Ship = 0, Cart = 1, Portal = 2, ActivePortal = 3;

        public static void Hook(bool server, Action<string> log)
        {
            s_server = server;
            s_log = log;
            var h = new Harmony(HarmonyId);
            if (server)
            {
                Found = true;
                try
                {
                    h.Patch(AccessTools.Method(typeof(ZDOMan), "CreateNewZDO", new[] { typeof(ZDOID), typeof(Vector3), typeof(int) }),
                        postfix: new HarmonyMethod(typeof(VehiclePinsPatch), nameof(AfterCreate)));
                }
                catch (Exception e)
                {
                    Problem = "the game's object code could not be hooked: " + e.GetType().Name + " " + e.Message;
                }
                return;
            }
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == HudCompassPatch.Guid);
            Found = info?.Instance != null;
            if (!Found) return;
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var asm = info.Instance.GetType().Assembly;
                var main = asm.GetType("neobotics.ValheimMods.HUDCompass");
                var dmmType = asm.GetType("neobotics.ValheimMods.DynamicMapMarkers");
                var mapPatch = asm.GetType("neobotics.ValheimMods.DynamicMapMarkers+Minimap_UpdatePins_Patch");
                var markerData = asm.GetType("neobotics.ValheimMods.MarkerData");
                var markerType = asm.GetType("neobotics.ValheimMods.MarkerType");
                var cfg = asm.GetType("neobotics.ValheimMods.Cfg");
                var names = new[] { "DrawMarker", "MarkerToPinData", "AddMarkerFromServer", "RemoveMarker", "IsAttachedToPlayer", "IsPortalType" };
                var methods = names.Select(n => dmmType?.GetMethod(n, any)).ToList();
                var postfix = mapPatch?.GetMethod("Minimap_UpdatePins_DMM_Postfix", any);
                if (main == null || postfix == null || markerData == null || markerType == null || cfg == null || methods.Any(m => m == null)
                    || CodeShape.Hash(new[] { postfix }.Concat(methods)) != KnownCode)
                {
                    Problem = "HUDCompass's map pin code changed (an update)";
                    return;
                }
                s_dmm = AccessTools.DeclaredField(main, "s_dmm")?.GetValue(null);
                s_markersField = AccessTools.DeclaredField(dmmType, "Markers");
                s_typesField = AccessTools.DeclaredField(dmmType, "MarkerTypes");
                s_dynamicPins = s_dmm == null ? null : AccessTools.DeclaredField(dmmType, "PinData")?.GetValue(s_dmm) as List<Minimap.PinData>;
                s_zdoid = Getter<ZDOID>(markerData, "Zdoid");
                s_type = Getter<object>(markerData, "Type");
                s_position = Getter<Vector3>(markerData, "Position");
                s_setPosition = Setter<Vector3>(markerData, "Position");
                s_label = Getter<string>(markerData, "Label");
                s_setLabel = Setter<string>(markerData, "Label");
                s_setActive = Setter<bool>(markerData, "IsActivePortal");
                s_show = Getter<bool>(markerType, "Show");
                s_pinType = Getter<Minimap.PinType>(markerType, "PinType");
                s_group = Getter<int>(markerType, "Group");
                s_draw = OpenCall.Make<Action<object, float, RectTransform, RawImage, float, bool>>(methods[0]);
                s_toPin = OpenCall.Make<Func<object, object, Minimap.PinData>>(methods[1]);
                s_addFromServer = OpenCall.Make<Action<ZDOID, int, Vector3, long, bool, string>>(methods[2]);
                s_remove = OpenCall.Make<Action<ZDOID>>(methods[3]);
                s_attached = OpenCall.Make<Func<ZDO, bool>>(methods[4]);
                s_destroy = OpenCall.Make<Action<object>>(markerData.GetMethod("Destroy", any));
                s_onCompass = Setting(cfg, "showDynamicPinsOnCompass");
                s_onMap = Setting(cfg, "showDynamicPinsOnMap");
                if (s_dmm == null || s_markersField == null || s_typesField == null || s_dynamicPins == null || s_onCompass == null || s_onMap == null)
                {
                    Problem = "HUDCompass's map pin code changed (an update)";
                    return;
                }
                // A static patch method: its arguments are taken by position.
                h.Patch(postfix, prefix: new HarmonyMethod(typeof(VehiclePinsPatch), nameof(BeforeMapPins)));
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

        private static Action<object, T> Setter<T>(Type type, string name)
        {
            var o = Expression.Parameter(typeof(object), "o");
            var v = Expression.Parameter(typeof(T), "v");
            var member = Expression.PropertyOrField(Expression.Convert(o, type), name);
            return Expression.Lambda<Action<object, T>>(Expression.Assign(member, Expression.Convert(v, member.Type)), o, v).Compile();
        }

        /// <summary>A HUDCompass setting: a plain config entry, or one inside its own wrapper.</summary>
        private static ConfigEntry<bool> Setting(Type cfg, string name)
        {
            object v = AccessTools.DeclaredField(cfg, name)?.GetValue(null);
            if (v is ConfigEntry<bool> e) return e;
            return v?.GetType().GetProperty("ConfigEntry")?.GetValue(v) as ConfigEntry<bool>;
        }

        private static void StepAside(string why)
        {
            Problem = why;
            s_log?.Invoke("Vigil: the patch for carts, ships and portals where they really are stepped aside: " + why);
        }

        /// <summary>Every frame: registers the messages on each new connection; the server refreshes, a player's game asks once for the whole list.</summary>
        public static void Update()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc != s_registeredOn)
            {
                s_registeredOn = rpc;
                Reset();
                if (rpc != null)
                {
                    rpc.Register<ZPackage>(RpcList, OnList);
                    rpc.Register<int>(RpcAsk, OnAsk);
                }
            }
            if (s_server && !On && s_scanned)
            {
                // New objects are not noted while off, so the list starts afresh when it is on again.
                Reset();
                return;
            }
            if (rpc == null || ZNet.instance == null || Problem != null || !On) return;
            if (s_server)
            {
                if (!ZNet.instance.IsServer() || ZDOMan.instance == null || ZNetScene.instance == null) return;
                float now = Time.realtimeSinceStartup;
                if (now < s_nextRefresh) return;
                s_nextRefresh = now + VehiclePinRules.RefreshSeconds;
                try
                {
                    Refresh();
                    s_lastChanges = s_changed.Count + s_gone.Count;
                    if (s_lastChanges > 0 && ZNet.instance.GetPeers().Count > 0)
                    {
                        rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcList, Pack(false, s_changed, s_gone));
                        SendWholePortals();
                    }
                }
                catch (Exception e)
                {
                    StepAside("its refresh failed: " + e.GetType().Name + " " + e.Message);
                }
                return;
            }
            if (!Found || s_asked || Player.m_localPlayer == null || ZNet.instance.IsServer()) return;
            var serverPeer = ZNet.instance.GetServerPeer();
            if (serverPeer == null || !serverPeer.IsReady()) return;
            s_asked = true;
            rpc.InvokeRoutedRPC(RpcAsk, Format);
        }

        private static void Reset()
        {
            s_book.Clear();
            s_index.Clear();
            s_pending.Clear();
            s_scanned = false;
            s_nextRefresh = 0f;
            s_fromServer.Clear();
            s_heard = false;
            s_asked = false;
        }

        // ---- Server ----

        private static void AfterCreate(ZDOID uid)
        {
            if (On && s_scanned) s_pending.Add(uid);
        }

        private static Dictionary<int, bool> Kinds()
        {
            if (s_kinds != null) return s_kinds;
            var kinds = new Dictionary<int, bool>();
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;
                bool portal = prefab.GetComponent<TeleportWorld>() != null;
                if (portal || prefab.GetComponent<Ship>() != null || prefab.GetComponent<Vagon>() != null)
                    kinds[prefab.name.GetStableHashCode()] = portal;
            }
            return s_kinds = kinds;
        }

        /// <summary>Looks at every cart, ship and portal of the world and notes what changed since the players were last told.</summary>
        private static void Refresh()
        {
            var kinds = Kinds();
            var man = ZDOMan.instance;
            if (!s_scanned)
            {
                long start = Stopwatch.GetTimestamp();
                foreach (var pair in s_objects(man))
                    if (kinds.ContainsKey(pair.Value.GetPrefab())) s_index.Add(pair.Key);
                s_scanned = true;
                s_log?.Invoke(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Vigil: {0} carts, ships and portals found among the world's objects in {1:0} ms",
                    s_index.Count, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency));
            }
            foreach (var uid in s_pending)
            {
                // Read now: a new object's kind arrives with its data, after it is made.
                var zdo = man.GetZDO(uid);
                if (zdo != null && kinds.ContainsKey(zdo.GetPrefab())) s_index.Add(uid);
            }
            s_pending.Clear();

            s_book.Begin();
            s_lost.Clear();
            foreach (var uid in s_index)
            {
                var zdo = man.GetZDO(uid);
                if (zdo == null || !kinds.TryGetValue(zdo.GetPrefab(), out bool portal))
                {
                    s_lost.Add(uid);
                    continue;
                }
                var p = zdo.GetPosition();
                string label = portal ? zdo.GetString(ZDOVars.s_tag) : zdo.GetString(ZDOVars.s_text);
                bool active = portal && zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None;
                s_book.See(Key(uid), new VehicleState(zdo.GetPrefab(), p.x, p.y, p.z, zdo.GetLong(ZDOVars.s_creator), active, label));
            }
            foreach (var uid in s_lost) s_index.Remove(uid);
            s_changed.Clear();
            s_gone.Clear();
            s_book.End(s_changed, s_gone);
        }

        /// <summary>Sends each changed portal whole to every player, so the game's own copy (which TargetPortal reads) has its new place, name and link.</summary>
        private static void SendWholePortals()
        {
            var kinds = Kinds();
            s_whole.Clear();
            VehiclePinRules.PortalsToSend(s_changed, prefab => kinds.TryGetValue(prefab, out bool portal) && portal, s_whole);
            foreach (var id in s_whole) ZDOMan.instance.ForceSendZDO(Uid(id));
        }

        private static void OnAsk(long sender, int format)
        {
            if (!s_server || !On || Problem != null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                if (!s_scanned)
                {
                    Refresh();
                    s_nextRefresh = Time.realtimeSinceStartup + VehiclePinRules.RefreshSeconds;
                    // What this first look found is in the whole list; the others hear of it at the next refresh.
                    if (s_changed.Count + s_gone.Count > 0 && ZNet.instance.GetPeers().Count > 1)
                        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcList, Pack(false, s_changed, s_gone));
                }
                var all = new List<KeyValuePair<VehicleId, VehicleState>>();
                s_book.All(all);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcList, Pack(true, all, new List<VehicleId>()));
            }
            catch (Exception e)
            {
                StepAside("its whole list failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static VehicleId Key(ZDOID uid) => new VehicleId(uid.UserID, uid.ID);

        private static ZDOID Uid(VehicleId id) => new ZDOID(id.User, id.Id);

        private static ZPackage Pack(bool full, List<KeyValuePair<VehicleId, VehicleState>> changed, List<VehicleId> gone)
        {
            var pkg = new ZPackage();
            pkg.Write(Format);
            pkg.Write(full);
            pkg.Write(changed.Count);
            foreach (var pair in changed)
            {
                var s = pair.Value;
                pkg.Write(Uid(pair.Key));
                pkg.Write(s.Prefab);
                pkg.Write(new Vector3(s.X, s.Y, s.Z));
                pkg.Write(s.Creator);
                pkg.Write(s.Active);
                pkg.Write(s.Label);
            }
            pkg.Write(gone.Count);
            foreach (var id in gone) pkg.Write(Uid(id));
            return pkg;
        }

        // ---- Player's game ----

        private static void OnList(long sender, ZPackage pkg)
        {
            if (s_server || !Found || Problem != null || ZNet.instance == null || ZNet.instance.IsServer()) return;
            try
            {
                if (pkg.ReadInt() != Format) return;
                bool full = pkg.ReadBool();
                var markers = s_markersField.GetValue(s_dmm) as IDictionary;
                var types = s_typesField.GetValue(s_dmm) as IDictionary;
                if (markers == null || types == null) return;
                if (full) s_fromServer.Clear();
                int n = pkg.ReadInt();
                for (int i = 0; i < n; i++)
                {
                    var uid = pkg.ReadZDOID();
                    int prefab = pkg.ReadInt();
                    var pos = pkg.ReadVector3();
                    long creator = pkg.ReadLong();
                    bool active = pkg.ReadBool();
                    string label = pkg.ReadString();
                    // Only the kinds HUDCompass has a pin for.
                    if (!types.Contains(prefab)) continue;
                    s_fromServer[uid] = new VehicleState(prefab, pos.x, pos.y, pos.z, creator, active, label);
                    if (!markers.Contains(uid))
                    {
                        object type = types[prefab];
                        string shown = label.Length > 0 ? label : Localization.instance.Localize(AccessTools.Property(type.GetType(), "Name").GetValue(type) as string ?? "");
                        s_addFromServer(uid, prefab, pos, creator, active, shown);
                    }
                }
                int gone = pkg.ReadInt();
                for (int i = 0; i < gone; i++)
                {
                    var uid = pkg.ReadZDOID();
                    s_fromServer.Remove(uid);
                    s_remove(uid);
                }
                if (full)
                {
                    var known = new List<VehicleId>();
                    foreach (object key in markers.Keys) known.Add(Key((ZDOID)key));
                    var onServer = new HashSet<VehicleId>(s_fromServer.Keys.Select(Key));
                    var drop = new List<VehicleId>();
                    VehiclePinRules.NotOnServer(known, onServer, drop);
                    foreach (var id in drop) s_remove(Uid(id));
                }
                s_heard = true;
            }
            catch (Exception e)
            {
                StepAside("a list from the server could not be read: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// Whether this game's map draws the object at the server's place and name, as the map pin
        /// loop below decides: while the list is in use, for an object this game has not loaded.
        /// </summary>
        public static bool ServerPlace(ZDOID uid, ZDO zdo, out VehicleState told)
        {
            told = default;
            if (s_server || !On || !s_heard || Problem != null) return false;
            bool loaded = zdo != null && ZNetScene.instance != null && ZNetScene.instance.FindInstance(zdo) != null;
            return VehiclePinRules.UseServer(zdo != null, loaded, s_fromServer.TryGetValue(uid, out told));
        }

        /// <summary>HUDCompass's map pin postfix, by its rules, with far objects at the server's place; HUDCompass's own runs until the server's list arrives.</summary>
        private static bool BeforeMapPins(Minimap __0, float __1)
        {
            if (!On || !s_heard || Problem != null) return true;
            try
            {
                MapPins(__1);
                return false;
            }
            catch (Exception e)
            {
                StepAside("its map pins failed: " + e.GetType().Name + " " + e.Message);
                return true;
            }
        }

        private static void MapPins(float largeZoom)
        {
            s_dynamicPins.Clear();
            s_farLastFrame = 0;
            var map = Minimap.instance;
            if (!(s_markersField.GetValue(s_dmm) is IDictionary markers) || markers.Count <= 0 || map?.m_largeRoot == null) return;
            bool large = map.m_largeRoot.activeSelf;
            RawImage image = large ? map.m_mapImageLarge : map.m_mapImageSmall;
            float size = large ? map.m_pinSizeLarge : map.m_pinSizeSmall;
            RectTransform parent = large ? map.m_pinRootLarge : map.m_pinRootSmall;
            bool[] visible = s_visibleTypes(map);
            bool onCompass = s_onCompass.Value, onMap = s_onMap.Value;
            var scene = ZNetScene.instance;
            foreach (object marker in markers.Values)
            {
                var type = s_type(marker);
                int group = s_group(type);
                bool portal = group == Portal || group == ActivePortal;
                var uid = s_zdoid(marker);
                var zdo = ZDOMan.instance.GetZDO(uid);
                bool loaded = zdo != null && scene != null && scene.FindInstance(zdo) != null;
                bool attached = false;
                if (VehiclePinRules.UseServer(zdo != null, loaded, s_fromServer.TryGetValue(uid, out var told)))
                {
                    s_setPosition(marker, new Vector3(told.X, told.Y, told.Z));
                    if (portal) s_setActive(marker, told.Active);
                    if ((portal || group == Cart || group == Ship) && told.Label.Length > 0) s_setLabel(marker, told.Label);
                    s_farLastFrame++;
                }
                else if (zdo != null)
                {
                    s_setPosition(marker, zdo.GetPosition());
                    attached = s_attached(zdo);
                    if (portal)
                    {
                        s_setActive(marker, zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None);
                        s_setLabel(marker, zdo.GetString(ZDOVars.s_tag, s_label(marker)));
                    }
                    if (group == Cart || group == Ship) s_setLabel(marker, zdo.GetString(ZDOVars.s_text, s_label(marker)));
                }
                Vector3 position = s_position(marker);
                bool shown = s_show(type) && visible[(int)s_pinType(type)] && !attached && s_isExplored(map, position);
                if (onCompass && shown)
                {
                    var pin = s_toPin(s_dmm, marker);
                    if (!s_dynamicPins.Contains(pin)) s_dynamicPins.Add(pin);
                }
                if (onMap && shown && s_isPointVisible(map, position, image)) s_draw(marker, size, parent, image, largeZoom, large);
                else s_destroy(marker);
            }
        }
    }
}
