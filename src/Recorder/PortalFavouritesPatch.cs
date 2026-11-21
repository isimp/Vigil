using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "AdjustablePortals: favourite portals" (<see cref="PortalFavourites"/>).
    /// AdjustablePortals builds its destination list in <c>PortalListPicker.Rebuild</c>, one row a
    /// destination from <c>AddRow</c>, in the order of its <c>destinations</c> field; a prefix puts the
    /// favourites first, a postfix on <c>AddRow</c> gives each row a star that marks or unmarks it,
    /// and a postfix on <c>Rebuild</c> puts in the headings. Its map picker (<c>PortalMapPicker.Open</c>
    /// and <c>Close</c>) adds a pin per destination; while it is open a right click on one marks or
    /// unmarks it, favourite pins carry a star, and a panel beside the map lists every portal as its
    /// list does, a filter over the favourites and all other portals, a click on a row travelling there
    /// through AdjustablePortals' own <c>PortalTargeting.TravelTo</c>.
    /// Everything is drawn with AdjustablePortals' own <c>PickerUI</c> and the build menu's
    /// favourite star. AdjustablePortals' picker code is compared with 0.7.0's and 0.8.0's, which
    /// are the same, and any change makes the patch step aside. The switch is this game's own.
    /// </summary>
    internal static class PortalFavouritesPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.portalfavourites";

        /// <summary>
        /// AdjustablePortals 0.7.0's and 0.8.0's PortalListPicker Build, Rebuild, AddRow;
        /// PortalMapPicker Open, Close, PickAt; PickerUI CreatePanel, AddText, AddButton, NewLayoutRow,
        /// AddTextField, NewRect.
        /// </summary>
        private const ulong KnownCode = 0xBE6790BEB9108F48UL;

        private const string StarName = "VigilFavouriteStar";
        private const float StarSize = 20f, StarIndent = 26f, HeadingHeight = 24f;
        private const float SideWidth = 340f, SideRow = 32f, SideTop = 52f, SideFilter = 30f, SideListHeight = 400f, SideFoot = 50f, GripWidth = 22f, FocusWidth = 30f, ClearWidth = 30f;
        // Rows are real objects made again on every keystroke in the filter, as in AdjustablePortals' list; past this the filter narrows it.
        private const int SideMaxRows = 300;
        private static readonly Color Orange = new Color(1f, 0.631f, 0.235f, 1f);
        private static readonly Color StarOff = new Color(0.48f, 0.4f, 0.31f, 0.85f);

        /// <summary>This game's switch for the favourites, kept current by the config entry.</summary>
        public static bool On;

        /// <summary>
        /// This game's switch for "AdjustablePortals' map shows only the portals": while its map
        /// picker is open, every game pin but its own and HUDCompass's cart, ship and portal markers
        /// are hidden, after HUDCompass and Cartur's Map Pins have drawn them, and shown again as it
        /// closes. Scry's marks step aside by themselves.
        /// </summary>
        public static bool OnlyPortals;

        public static bool Found { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        private static long s_toggled, s_trips, s_portalMaps;

        public static string State => PortalFavourites.Line(On, Found, Problem, s_toggled, s_trips);
        public static string OnlyPortalsState => PortalFavourites.OnlyPortalsLine(OnlyPortals, Found, Problem, s_portalMaps);

        /// <summary>HUDCompass's markers (its DynamicMapMarkers.Markers, each a MarkerData with a Marker and a Nametag); none without HUDCompass.</summary>
        private static object s_compassDmm;
        private static FieldInfo s_compassMarkers;
        private static Func<object, GameObject> s_compassIcon, s_compassTag;
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> s_gamePins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
        /// <summary>The game pins' icons hidden while the map picker is open, shown again as it closes.</summary>
        private static readonly List<GameObject> s_hidden = new List<GameObject>();
        /// <summary>Whether anything was hidden for the open map picker, HUDCompass's markers included.</summary>
        private static bool s_hidAny;

        private static Action<string> s_log;
        private static FieldInfo s_listCurrent, s_listDestinations, s_listFilter, s_listContent, s_listWidth, s_mapPins, s_mapSelecting;
        private static FieldInfo s_destId, s_destName, s_destPosition;
        private static MethodInfo s_rebuild, s_createPanel, s_addText, s_addButton, s_newLayoutRow, s_travelTo, s_screenToWorld;
        private static PropertyInfo s_pinRadius;
        private static readonly AccessTools.FieldRef<Minimap, bool> s_pinUpdate = AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");

        /// <summary>The favourites, read from the character's data for <see cref="s_bookFor"/>.</summary>
        private static List<VehicleId> s_favourites = new List<VehicleId>();
        private static Player s_bookFor;

        /// <summary>The list picker the list's own order was taken from, and that order.</summary>
        private static object s_listFor;
        private static readonly List<object> s_listOrder = new List<object>();
        /// <summary>The rows of the list's current build, in order, and whether each is a favourite.</summary>
        private static readonly List<GameObject> s_rows = new List<GameObject>();
        private static readonly List<bool> s_rowFavourite = new List<bool>();

        /// <summary>The map picker's destinations, and the panel of favourites beside the map.</summary>
        private static readonly List<object> s_mapDestinations = new List<object>();
        private static GameObject s_side;
        private static InputField s_filter;
        private static RectTransform s_sideContent;
        private static float s_sideWidth;
        /// <summary>Where the player last left the panel; the left edge until moved.</summary>
        private static Vector2? s_sidePosition;
        /// <summary>The favourite rows shown, top down, and their portals, for a drag to find where it drops.</summary>
        private static readonly List<RectTransform> s_favRows = new List<RectTransform>();
        private static readonly List<VehicleId> s_favRowIds = new List<VehicleId>();
        private static Sprite s_focusSprite;
        /// <summary>The camera the panel's canvas draws with; none for a canvas drawn over the screen.</summary>
        private static Camera s_sideCamera;
        /// <summary>Jotunn's window drag, its GUIManager and the scroll view it makes, as AdjustablePortals' list uses them.</summary>
        private static Type s_dragWindow;
        private static PropertyInfo s_guiInstance;
        private static MethodInfo s_createScrollView;
        private static FieldInfo s_handleColors;
        private static object s_firstShown;
        private static MethodInfo s_addTextField, s_newRect, s_blockInput;
        private static FieldInfo s_destDistance;
        private static PropertyInfo s_destDistanceText;
        private static Sprite s_star;
        private static bool s_starLooked;

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == PortalFavourites.AdjustablePortalsGuid);
            Found = info?.Instance != null;
            if (!Found) return;
            try
            {
                var asm = info.Instance.GetType().Assembly;
                const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var list = asm.GetType("AdjustablePortals.ui.PortalListPicker");
                var map = asm.GetType("AdjustablePortals.ui.PortalMapPicker");
                var ui = asm.GetType("AdjustablePortals.ui.PickerUI");
                var targeting = asm.GetType("AdjustablePortals.modules.PortalTargeting");
                var destination = targeting?.GetNestedType("Destination", any);
                MethodInfo Method(Type t, string name) => t?.GetMethods(any).SingleOrDefault(m => m.Name == name && m.DeclaringType == t);
                var build = Method(list, "Build");
                s_rebuild = Method(list, "Rebuild");
                var addRow = Method(list, "AddRow");
                var open = Method(map, "Open");
                var close = Method(map, "Close");
                var pickAt = Method(map, "PickAt");
                s_createPanel = Method(ui, "CreatePanel");
                s_addText = Method(ui, "AddText");
                s_addButton = Method(ui, "AddButton");
                s_newLayoutRow = Method(ui, "NewLayoutRow");
                s_addTextField = Method(ui, "AddTextField");
                s_newRect = Method(ui, "NewRect");
                s_travelTo = Method(targeting, "TravelTo");
                var code = new MethodBase[] { build, s_rebuild, addRow, open, close, pickAt, s_createPanel, s_addText, s_addButton, s_newLayoutRow, s_addTextField, s_newRect };
                if (code.Any(m => m == null) || s_travelTo == null || destination == null || CodeShape.Hash(code) != KnownCode)
                {
                    Problem = "AdjustablePortals' picker code changed (an update)";
                    return;
                }
                s_listCurrent = AccessTools.DeclaredField(list, "current");
                s_listDestinations = AccessTools.DeclaredField(list, "destinations");
                s_listFilter = AccessTools.DeclaredField(list, "filter");
                s_listContent = AccessTools.DeclaredField(list, "content");
                s_listWidth = AccessTools.DeclaredField(list, "contentWidth");
                s_mapPins = AccessTools.DeclaredField(map, "pins");
                s_mapSelecting = AccessTools.DeclaredField(map, "selecting");
                s_destId = AccessTools.DeclaredField(destination, "Id");
                s_destName = AccessTools.DeclaredField(destination, "Name");
                s_destPosition = AccessTools.DeclaredField(destination, "Position");
                s_destDistance = AccessTools.DeclaredField(destination, "Distance");
                s_destDistanceText = AccessTools.DeclaredProperty(destination, "DistanceText");
                // Jotunn's input block, as AdjustablePortals' own list holds it: no walking and no map keys while the filter takes the keys.
                var gui = Type.GetType("Jotunn.Managers.GUIManager, Jotunn");
                s_blockInput = gui?.GetMethod("BlockInput", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(bool) }, null);
                s_guiInstance = gui?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                s_createScrollView = gui?.GetMethod("CreateScrollView", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(Transform), typeof(bool), typeof(bool), typeof(float), typeof(float), typeof(ColorBlock), typeof(Color), typeof(float), typeof(float) }, null);
                s_handleColors = gui?.GetField("ValheimScrollbarHandleColorBlock", BindingFlags.Instance | BindingFlags.Public);
                s_dragWindow = Type.GetType("Jotunn.GUI.DragWindowCntrl, Jotunn");
                s_screenToWorld = AccessTools.DeclaredMethod(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });
                s_pinRadius = AccessTools.DeclaredProperty(typeof(Minimap), "PinInteractRadius");
                if (new object[] { s_listCurrent, s_listDestinations, s_listFilter, s_listContent, s_listWidth, s_mapPins, s_mapSelecting, s_destId, s_destName, s_destPosition, s_destDistance, s_destDistanceText, s_blockInput, s_guiInstance, s_createScrollView, s_handleColors, s_screenToWorld, s_pinRadius }.Any(m => m == null)
                    || s_destId.FieldType != typeof(ZDOID) || s_mapSelecting.FieldType != typeof(bool))
                {
                    Problem = "AdjustablePortals' picker code changed (an update)";
                    return;
                }
                var h = new Harmony(HarmonyId);
                h.Patch(s_rebuild, prefix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(BeforeRebuild)), postfix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(AfterRebuild)));
                h.Patch(addRow, postfix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(AfterAddRow)));
                h.Patch(open, postfix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(AfterMapOpen)));
                h.Patch(close, prefix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(BeforeMapClose)));
                // Last: HUDCompass draws its markers, and switches them on, in its own postfix here.
                h.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "UpdatePins"), postfix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(AfterUpdatePins)) { priority = Priority.Last });
                FindCompassMarkers();
                h.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "RemovePinUnderPointer"), prefix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(BeforeRightClick)));
                // The map zooms by the game's wheel value whatever the pointer is over; over the panel it is the list's.
                h.Patch(AccessTools.DeclaredMethod(typeof(ZInput), "Internal_GetMouseScrollWheel"), postfix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(AfterWheel)));
            }
            catch (Exception e)
            {
                Problem = "AdjustablePortals could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        /// <summary>HUDCompass's markers, read by name; left alone where HUDCompass is missing or its names changed.</summary>
        private static void FindCompassMarkers()
        {
            var compass = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == HudCompassPatch.Guid);
            if (compass?.Instance == null) return;
            try
            {
                var hud = compass.Instance.GetType().Assembly;
                var dmmType = hud.GetType("neobotics.ValheimMods.DynamicMapMarkers");
                var markerData = hud.GetType("neobotics.ValheimMods.MarkerData");
                s_compassDmm = AccessTools.DeclaredField(hud.GetType("neobotics.ValheimMods.HUDCompass"), "s_dmm")?.GetValue(null);
                s_compassMarkers = dmmType != null ? AccessTools.DeclaredField(dmmType, "Markers") : null;
                if (s_compassDmm == null || s_compassMarkers == null || markerData == null) throw new InvalidOperationException("its marker list is not where 1.2.1 keeps it");
                s_compassIcon = Getter(markerData, "Marker");
                s_compassTag = Getter(markerData, "Nametag");
            }
            catch (Exception e)
            {
                s_compassDmm = null;
                s_log?.Invoke("Vigil: AdjustablePortals' map leaves HUDCompass's markers as they are: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static Func<object, GameObject> Getter(Type type, string name)
        {
            var o = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
            var member = System.Linq.Expressions.Expression.PropertyOrField(System.Linq.Expressions.Expression.Convert(o, type), name);
            return System.Linq.Expressions.Expression.Lambda<Func<object, GameObject>>(System.Linq.Expressions.Expression.Convert(member, typeof(GameObject)), o).Compile();
        }

        private static void StepAside(string why)
        {
            Problem = why;
            DestroySide();
            ShowHidden();
            s_log?.Invoke("Vigil: the patch for AdjustablePortals' favourite portals stepped aside: " + why);
        }

        private static bool Ready => Found && Problem == null;
        private static bool Working => On && Ready;

        private static VehicleId IdOf(object destination)
        {
            var id = (ZDOID)s_destId.GetValue(destination);
            return new VehicleId(id.UserID, id.ID);
        }

        private static string NameOf(object destination) => s_destName.GetValue(destination) as string ?? "";

        /// <summary>The favourites of the local player, read once a character; a character with none kept yet takes TargetPortal's (<see cref="PortalFavourites.Seed"/>).</summary>
        private static bool Book(IEnumerable<object> destinations)
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;
            if (s_bookFor == player) return true;
            s_bookFor = player;
            if (player.m_customData.TryGetValue(PortalFavourites.BookKey, out string kept))
            {
                s_favourites = PortalFavourites.ParseBook(kept);
                return true;
            }
            player.m_customData.TryGetValue(PortalFavourites.TargetPortalBookKey, out string targetBook);
            player.m_customData.TryGetValue(PortalFavourites.TargetPortalFavouritesKey, out string targetOwn);
            s_favourites = PortalFavourites.Seed(targetBook, targetOwn, destinations.Select(d => (IdOf(d), ((Vector3)s_destPosition.GetValue(d)).ToString())));
            // Kept even when empty, so TargetPortal's favourites are taken only the first time.
            player.m_customData[PortalFavourites.BookKey] = PortalFavourites.FormatBook(s_favourites);
            return true;
        }

        private static void Toggle(object destination)
        {
            if (Player.m_localPlayer == null || Player.m_localPlayer != s_bookFor) return;
            PortalFavourites.Toggle(s_favourites, IdOf(destination));
            Player.m_localPlayer.m_customData[PortalFavourites.BookKey] = PortalFavourites.FormatBook(s_favourites);
            s_toggled++;
        }

        /// <summary>Before the list is built: its own order taken once a picker, then the favourites first.</summary>
        private static void BeforeRebuild(object __instance)
        {
            s_rows.Clear();
            s_rowFavourite.Clear();
            if (!Working) return;
            try
            {
                if (!(s_listDestinations.GetValue(__instance) is IList destinations)) return;
                if (!ReferenceEquals(s_listFor, __instance))
                {
                    s_listFor = __instance;
                    s_listOrder.Clear();
                    foreach (var d in destinations) s_listOrder.Add(d);
                }
                if (!Book(s_listOrder)) return;
                var ordered = PortalFavourites.FavouritesFirst(s_listOrder, d => s_favourites.IndexOf(IdOf(d)));
                destinations.Clear();
                foreach (var d in ordered) destinations.Add(d);
            }
            catch (Exception e)
            {
                StepAside("its ordering of the list failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>Each row gets a star at its left, its name moved over to make room.</summary>
        private static void AfterAddRow(object __instance, object __0, Button __result)
        {
            if (!Working || __result == null || __0 == null || s_bookFor == null) return;
            try
            {
                var row = __result.transform.parent;
                bool favourite = s_favourites.Contains(IdOf(__0));
                // The button carries its own empty label from Jotunn; the name is the text AddRow wrote.
                string shown = NameOf(__0);
                var name = __result.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.text == shown);
                if (name != null)
                {
                    var rt = name.rectTransform;
                    rt.anchoredPosition += new Vector2(StarIndent, 0f);
                    rt.sizeDelta -= new Vector2(StarIndent, 0f);
                }
                float height = ((RectTransform)__result.transform).sizeDelta.y;
                var destination = __0;
                var picker = __instance;
                Star(row, 8f, (height - StarSize) / 2f, favourite, () =>
                {
                    Toggle(destination);
                    if (ReferenceEquals(s_listCurrent.GetValue(null), picker))
                        s_rebuild.Invoke(picker, new object[] { (s_listFilter.GetValue(picker) as InputField)?.text ?? "" });
                });
                s_rows.Add(row.gameObject);
                s_rowFavourite.Add(favourite);
            }
            catch (Exception e)
            {
                StepAside("its star on a row failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>After the list is built: the headings over the favourites and the rest.</summary>
        private static void AfterRebuild(object __instance)
        {
            if (!Working || s_rows.Count == 0) return;
            try
            {
                var (favourites, others) = PortalFavourites.HeadingsAt(s_rowFavourite);
                var content = s_listContent.GetValue(__instance) as Transform;
                float width = (float)s_listWidth.GetValue(__instance);
                if (content == null) return;
                if (favourites >= 0) Heading(content, width, "Favourites", s_rows[favourites]);
                if (others >= 0) Heading(content, width, "All portals", s_rows[others]);
            }
            catch (Exception e)
            {
                StepAside("its headings failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static void Heading(Transform content, float width, string words, GameObject before)
        {
            var row = (GameObject)s_newLayoutRow.Invoke(null, new object[] { content, width, HeadingHeight });
            s_addText.Invoke(null, new object[] { row.transform, 10f, 0f, width - 20f, HeadingHeight, words, 14, TextAnchor.LowerLeft, (Color?)Orange });
            row.transform.SetSiblingIndex(before.transform.GetSiblingIndex());
        }

        /// <summary>A star button at (x, y) from the top left of <paramref name="parent"/>: lit for a favourite.</summary>
        private static void Star(Transform parent, float x, float y, bool lit, Action click)
        {
            var go = new GameObject(StarName, typeof(RectTransform), typeof(Image), typeof(Button)) { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(StarSize, StarSize);
            rt.anchoredPosition = new Vector2(x, -y);
            var image = go.GetComponent<Image>();
            image.sprite = StarSprite();
            image.color = lit ? Orange : StarOff;
            image.preserveAspect = true;
            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            // A controller moves through the rows, not the stars.
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => click());
        }

        /// <summary>The build menu's favourite star; none where the game has none to give.</summary>
        private static Sprite StarSprite()
        {
            if (s_starLooked) return s_star;
            s_starLooked = true;
            try
            {
                var menu = Resources.FindObjectsOfTypeAll<BuildUi>().FirstOrDefault();
                var prefab = menu != null ? AccessTools.FieldRefAccess<BuildUi, GameObject>("m_pieceButtonPrefab")(menu) : null;
                var button = prefab != null ? prefab.GetComponent<BuildUiPieceButton>() : null;
                var star = button != null ? AccessTools.FieldRefAccess<BuildUiPieceButton, Image>("m_favoriteStar")(button) : null;
                s_star = star != null ? star.sprite : null;
            }
            catch (Exception)
            {
                s_star = null;
            }
            return s_star;
        }

        /// <summary>The map picker opened: its destinations kept and the panel of favourites put beside the map.</summary>
        private static void AfterMapOpen(bool __result, object __1)
        {
            if (!__result || !Ready) return;
            if (OnlyPortals) s_portalMaps++;
            if (!On) return;
            try
            {
                s_mapDestinations.Clear();
                if (__1 is IList destinations) foreach (var d in destinations) s_mapDestinations.Add(d);
                if (!Book(s_mapDestinations)) return;
                BuildSide();
            }
            catch (Exception e)
            {
                StepAside("its panel beside the map failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static void BeforeMapClose()
        {
            DestroySide();
            s_mapDestinations.Clear();
            ShowHidden();
        }

        /// <summary>The pins hidden for the map picker shown again; HUDCompass and the pins' names are drawn again by the next pin update.</summary>
        private static void ShowHidden()
        {
            bool any = s_hidAny;
            s_hidAny = false;
            foreach (var go in s_hidden) if (go != null) go.SetActive(true);
            s_hidden.Clear();
            if (any && Minimap.instance != null) s_pinUpdate(Minimap.instance) = true;
        }

        private static void DestroySide()
        {
            if (s_side != null)
            {
                // The panel opens again where the player left it, for as long as the game runs.
                s_sidePosition = ((RectTransform)s_side.transform).anchoredPosition;
                UnityEngine.Object.Destroy(s_side);
            }
            s_side = null;
            s_filter = null;
            s_sideContent = null;
            s_firstShown = null;
            s_favRows.Clear();
            s_favRowIds.Clear();
        }

        /// <summary>
        /// The panel at the map's left, as AdjustablePortals' own list: a filter, then in a scrolling
        /// list the favourites in the player's order and all other portals by name, each row with its
        /// star, its name, how far it is and a button that centres the map on it; a click on a row
        /// travels there, a favourite's grip drags it to another place. The panel moves by dragging.
        /// </summary>
        private static void BuildSide()
        {
            DestroySide();
            var map = Minimap.instance;
            if (map == null || map.m_largeRoot == null) return;
            float height =SideTop + SideFilter + 10f + SideListHeight + SideFoot;
            s_side = (GameObject)s_createPanel.Invoke(null, new object[] { map.m_largeRoot.transform, SideWidth, height, "Choose a destination" });
            var rt = (RectTransform)s_side.transform;
            // Centred pivot: Jotunn's window drag keeps a window on screen by its half size.
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = s_sidePosition ?? new Vector2(40f + SideWidth / 2f, 0f);
            s_side.transform.SetAsLastSibling();
            if (s_dragWindow != null) s_side.AddComponent(s_dragWindow);
            float inner = SideWidth - 32f;
            var canvas = s_side.GetComponentInParent<Canvas>();
            s_sideCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            s_filter = (InputField)s_addTextField.Invoke(null, new object[] { s_side.transform, 16f, SideTop, inner - ClearWidth - 4f, SideFilter, "Filter..." });
            var clear = (Button)s_addButton.Invoke(null, new object[] { s_side.transform, 16f + inner - ClearWidth, SideTop, ClearWidth, SideFilter, "X", new UnityAction(ClearFilter) });
            clear.navigation = new Navigation { mode = Navigation.Mode.None };
            s_filter.onValueChanged.AddListener(_ => FillSide());
            s_filter.onEndEdit.AddListener(_ => FilterDone());
            s_filter.gameObject.AddComponent<FilterFocus>();
            var holder = (RectTransform)s_newRect.Invoke(null, new object[] { "VigilPortalList", s_side.transform, 16f, SideTop + SideFilter + 10f, inner, SideListHeight });
            var gui = s_guiInstance.GetValue(null);
            var handles = (ColorBlock)s_handleColors.GetValue(gui);
            var view = (GameObject)s_createScrollView.Invoke(gui, new object[] { holder, false, true, 8f, 4f, handles, new Color(0f, 0f, 0f, 0.5f), inner, SideListHeight });
            s_sideContent = view.transform.Find("Scroll View/Viewport/Content") as RectTransform;
            var scroll = view.GetComponentInChildren<ScrollRect>();
            if (scroll != null && scroll.viewport != null) scroll.viewport.gameObject.AddComponent<ListWheel>().Scroll = scroll;
            // The scroll bar and its border take this much of the width, as in AdjustablePortals' list.
            s_sideWidth = inner - 16f;
            s_addText.Invoke(null, new object[] { s_side.transform, 16f, height - SideFoot + 2f, inner, SideFoot - 8f, "Star or right click a portal to mark it; drag a favourite by its grip to reorder.", 13, TextAnchor.MiddleLeft, (Color?)Orange });
            FillSide();
        }

        /// <summary>The rows of the list, laid out again for the filter's text and the favourites.</summary>
        private static void FillSide()
        {
            if (s_sideContent == null) return;
            foreach (Transform child in s_sideContent)
            {
                // Hidden first: destruction waits for the end of the frame and the layout would go on placing them.
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
            s_favRows.Clear();
            s_favRowIds.Clear();
            var names = s_mapDestinations.Select(NameOf).ToList();
            var rank = s_mapDestinations.Select(d => s_favourites.IndexOf(IdOf(d))).ToList();
            var distance = s_mapDestinations.Select(d => (float)s_destDistance.GetValue(d)).ToList();
            var list = PortalFavourites.SideList(names, rank, distance, s_filter != null ? s_filter.text : "", SideMaxRows);
            s_firstShown = null;
            if (list.Favourites.Count > 0)
            {
                SideNote("Favourites", HeadingHeight, TextAnchor.LowerLeft, Orange);
                foreach (int i in list.Favourites) SideRowFor(s_mapDestinations[i], names[i], true);
            }
            if (list.Others.Count > 0)
            {
                if (list.Favourites.Count > 0) SideNote("All portals", HeadingHeight, TextAnchor.LowerLeft, Orange);
                foreach (int i in list.Others) SideRowFor(s_mapDestinations[i], names[i], false);
            }
            if (list.Favourites.Count + list.Others.Count == 0) SideNote("No portals match.", SideRow, TextAnchor.MiddleLeft, null);
            else if (list.More > 0) SideNote("... " + list.More + " more, type to narrow the list.", SideRow, TextAnchor.MiddleLeft, null);
        }

        private static void SideNote(string words, float height, TextAnchor anchor, Color? colour)
        {
            var row = (GameObject)s_newLayoutRow.Invoke(null, new object[] { s_sideContent, s_sideWidth, height });
            s_addText.Invoke(null, new object[] { row.transform, 10f, 0f, s_sideWidth - 20f, height, words, 14, anchor, colour });
        }

        /// <summary>One portal's row: a grip for a favourite, its star, a button with its name and distance that travels there, and the focus button.</summary>
        private static void SideRowFor(object destination, string name, bool favourite)
        {
            if (s_firstShown == null) s_firstShown = destination;
            var row = (GameObject)s_newLayoutRow.Invoke(null, new object[] { s_sideContent, s_sideWidth, SideRow });
            float height = SideRow - 4f, left = GripWidth, right = s_sideWidth - FocusWidth;
            var button = (Button)s_addButton.Invoke(null, new object[] { row.transform, left, 0f, right - left - 4f, height, "", new UnityAction(() => Travel(destination)) });
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            float buttonWidth = right - left - 4f, nameLeft = StarIndent + 10f, distanceWidth = 76f;
            var nameText = (Text)s_addText.Invoke(null, new object[] { button.transform, nameLeft, 0f, buttonWidth - nameLeft - distanceWidth - 6f, height, name, 16, TextAnchor.MiddleLeft, null });
            bool cut = Shorten(nameText, buttonWidth - nameLeft - distanceWidth - 6f);
            var distanceText = (Text)s_addText.Invoke(null, new object[] { button.transform, buttonWidth - distanceWidth - 6f, 0f, distanceWidth, height, s_destDistanceText.GetValue(destination) as string ?? "", 14, TextAnchor.MiddleRight, (Color?)Orange });
            if (cut)
            {
                var reveal = button.gameObject.AddComponent<NameReveal>();
                reveal.Name = nameText;
                reveal.Distance = distanceText;
                reveal.Full = name;
                reveal.FullWidth = buttonWidth - nameLeft - 8f;
            }
            Star(row.transform, left + 6f, (height - StarSize) / 2f, favourite, () =>
            {
                Toggle(destination);
                FillSide();
                if (Minimap.instance != null) s_pinUpdate(Minimap.instance) = true;
            });
            FocusButton(row.transform, right, height, destination);
            if (!favourite) return;
            var id = IdOf(destination);
            Grip(row.transform, height, id);
            s_favRows.Add((RectTransform)row.transform);
            s_favRowIds.Add(id);
        }

        /// <summary>Cuts a name that would not fit its box down to what fits, ending in "..."; true when it was cut.</summary>
        private static bool Shorten(Text text, float width)
        {
            if (text == null || string.IsNullOrEmpty(text.text)) return false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            string full = text.text;
            if (text.preferredWidth <= width) return false;
            for (int keep = full.Length - 1; keep > 0; keep--)
            {
                text.text = full.Substring(0, keep).TrimEnd() + "...";
                if (text.preferredWidth <= width) return true;
            }
            return true;
        }

        /// <summary>
        /// A row whose name was cut shows it whole while the pointer is over it: the distance steps
        /// aside, the name takes its room, and a name longer still is drawn smaller until it fits.
        /// Nothing opens over the panel.
        /// </summary>
        private sealed class NameReveal : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public Text Name, Distance;
            public string Full;
            public float FullWidth;
            private string _cut;
            private float _cutWidth;
            private bool _shown;

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (_shown || Name == null) return;
                _shown = true;
                _cut = Name.text;
                _cutWidth = Name.rectTransform.sizeDelta.x;
                if (Distance != null) Distance.enabled = false;
                Name.rectTransform.sizeDelta = new Vector2(FullWidth, Name.rectTransform.sizeDelta.y);
                Name.text = Full;
                Name.horizontalOverflow = HorizontalWrapMode.Wrap;
                Name.resizeTextMinSize = 10;
                Name.resizeTextMaxSize = Name.fontSize;
                Name.resizeTextForBestFit = true;
            }

            public void OnPointerExit(PointerEventData eventData) => Hide();

            private void OnDisable() => Hide();

            private void Hide()
            {
                if (!_shown || Name == null) return;
                _shown = false;
                Name.resizeTextForBestFit = false;
                Name.horizontalOverflow = HorizontalWrapMode.Overflow;
                Name.text = _cut;
                Name.rectTransform.sizeDelta = new Vector2(_cutWidth, Name.rectTransform.sizeDelta.y);
                if (Distance != null) Distance.enabled = true;
            }
        }

        /// <summary>The button at a row's right end: the map centres on the portal, which stays to be picked.</summary>
        private static void FocusButton(Transform row, float x, float height, object destination)
        {
            var go = new GameObject("VigilPortalFocus", typeof(RectTransform), typeof(Image), typeof(Button)) { layer = row.gameObject.layer };
            go.transform.SetParent(row, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(FocusWidth - 4f, height);
            rt.anchoredPosition = new Vector2(x + 2f, 0f);
            var image = go.GetComponent<Image>();
            image.sprite = FocusSprite();
            image.color = s_focusSprite != null ? new Color(0.85f, 0.75f, 0.55f, 1f) : new Color(0f, 0f, 0f, 0.3f);
            image.preserveAspect = true;
            var button = go.GetComponent<Button>();
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => Focus(destination));
        }

        private static void Focus(object destination)
        {
            var map = Minimap.instance;
            if (!Working || map == null) return;
            map.ShowPointOnMap((Vector3)s_destPosition.GetValue(destination));
            s_pinUpdate(map) = true;
        }

        /// <summary>The game's own ping pin picture, for the focus button; none where the map has none.</summary>
        private static Sprite FocusSprite()
        {
            if (s_focusSprite == null && Minimap.instance != null) s_focusSprite = Minimap.instance.m_icons.Find(s => s.m_name == Minimap.PinType.Ping).m_icon;
            return s_focusSprite;
        }

        /// <summary>The grip at a favourite's left: three short bars over a hit area that starts the drag.</summary>
        private static void Grip(Transform row, float height, VehicleId id)
        {
            var go = new GameObject("VigilPortalGrip", typeof(RectTransform), typeof(Image)) { layer = row.gameObject.layer };
            go.transform.SetParent(row, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(GripWidth - 2f, height);
            rt.anchoredPosition = Vector2.zero;
            // Clear but hit: the whole strip takes the drag, not only the bars.
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            for (int i = 0; i < 3; i++)
            {
                var bar = new GameObject("Bar", typeof(RectTransform), typeof(Image)) { layer = go.layer };
                bar.transform.SetParent(go.transform, false);
                var b = (RectTransform)bar.transform;
                b.anchorMin = b.anchorMax = b.pivot = new Vector2(0.5f, 0.5f);
                b.sizeDelta = new Vector2(10f, 2f);
                b.anchoredPosition = new Vector2(0f, (i - 1) * 4f);
                var image = bar.GetComponent<Image>();
                image.color = new Color(0.79f, 0.71f, 0.54f, 1f);
                image.raycastTarget = false;
            }
            go.AddComponent<GripDrag>().Id = id;
        }

        /// <summary>Where a dragged favourite would drop now, by the pointer against the favourite rows' middles.</summary>
        private static int DropIndex(PointerEventData eventData)
        {
            if (s_sideContent == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(s_sideContent, eventData.position, eventData.pressEventCamera, out var local)) return -1;
            var middles = s_favRows.Select(r => -r.localPosition.y + r.rect.height / 2f).ToList();
            return PortalFavourites.InsertAt(middles, -local.y);
        }

        /// <summary>Drags a favourite to another place among the favourites: a line shows where it drops.</summary>
        private sealed class GripDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            public VehicleId Id;
            private RectTransform _line;

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (s_sideContent == null) return;
                var go = new GameObject("VigilDropLine", typeof(RectTransform), typeof(Image), typeof(LayoutElement)) { layer = gameObject.layer };
                go.transform.SetParent(s_sideContent, false);
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                var image = go.GetComponent<Image>();
                image.color = Orange;
                image.raycastTarget = false;
                _line = (RectTransform)go.transform;
                _line.anchorMin = _line.anchorMax = new Vector2(0f, 1f);
                _line.pivot = new Vector2(0f, 0.5f);
                _line.sizeDelta = new Vector2(s_sideWidth, 2f);
                OnDrag(eventData);
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (_line == null || s_favRows.Count == 0) return;
                int at = DropIndex(eventData);
                if (at < 0) return;
                var row = at < s_favRows.Count ? s_favRows[at] : s_favRows[s_favRows.Count - 1];
                float down = -row.localPosition.y + (at < s_favRows.Count ? 0f : row.rect.height);
                _line.localPosition = new Vector3(row.localPosition.x, -down, 0f);
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                if (_line != null) Destroy(_line.gameObject);
                _line = null;
                int at = DropIndex(eventData);
                if (at < 0 || Player.m_localPlayer == null || Player.m_localPlayer != s_bookFor) return;
                VehicleId? before = at < s_favRowIds.Count ? s_favRowIds[at] : (VehicleId?)null;
                PortalFavourites.MoveBefore(s_favourites, Id, before);
                Player.m_localPlayer.m_customData[PortalFavourites.BookKey] = PortalFavourites.FormatBook(s_favourites);
                FillSide();
            }

            private void OnDestroy()
            {
                if (_line != null) Destroy(_line.gameObject);
            }
        }

        /// <summary>The filter emptied, which lists every portal again, and the keys given back.</summary>
        private static void ClearFilter()
        {
            if (s_filter == null) return;
            s_filter.text = "";
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == s_filter.gameObject) EventSystem.current.SetSelectedGameObject(null);
        }

        /// <summary>Whether the pointer stands over the panel beside the map.</summary>
        private static bool PointerOverSide() =>
            s_side != null && RectTransformUtility.RectangleContainsScreenPoint((RectTransform)s_side.transform, ZInput.pointerPosition, s_sideCamera);

        /// <summary>The game's wheel value, none for the map while the pointer is over the panel (<see cref="PortalFavourites.MapTakesWheel"/>).</summary>
        private static void AfterWheel(ref float __result)
        {
            if (__result == 0f || s_side == null) return;
            try
            {
                if (!PortalFavourites.MapTakesWheel(PointerOverSide())) __result = 0f;
            }
            catch (Exception)
            {
                // The wheel goes on as the game reads it.
            }
        }

        /// <summary>
        /// The list's wheel: <see cref="PortalFavourites.RowsPerNotch"/> rows a notch by its sign
        /// (<see cref="PortalFavourites.ScrollTo"/>). It sits on the viewport, so a wheel over the rows
        /// reaches it before the ScrollRect, whose step follows the small size the input reports.
        /// </summary>
        private sealed class ListWheel : MonoBehaviour, IScrollHandler
        {
            public ScrollRect Scroll;

            public void OnScroll(PointerEventData eventData)
            {
                if (Scroll == null || Scroll.content == null || Scroll.viewport == null) return;
                var content = Scroll.content;
                Scroll.StopMovement();
                float y = PortalFavourites.ScrollTo(content.anchoredPosition.y, eventData.scrollDelta.y, content.rect.height, Scroll.viewport.rect.height, SideRow);
                content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
            }
        }

        /// <summary>Enter in the filter travels to the first portal it shows, as in AdjustablePortals' own list; any end of typing gives the keys back.</summary>
        private static void FilterDone()
        {
            bool enter = ZInput.GetKeyDown(KeyCode.Return) || ZInput.GetKeyDown(KeyCode.KeypadEnter);
            var first = s_firstShown;
            if (EventSystem.current != null && s_filter != null && EventSystem.current.currentSelectedGameObject == s_filter.gameObject)
                EventSystem.current.SetSelectedGameObject(null);
            if (enter && first != null) Travel(first);
        }

        /// <summary>Holds Jotunn's input block for as long as the filter is selected, so typing neither walks the player out of the portal nor works the map's keys.</summary>
        private sealed class FilterFocus : MonoBehaviour, ISelectHandler, IDeselectHandler
        {
            private bool _held;

            public void OnSelect(BaseEventData eventData)
            {
                if (_held) return;
                _held = true;
                Block(true);
            }

            public void OnDeselect(BaseEventData eventData) => Release();

            private void OnDisable() => Release();

            private void OnDestroy() => Release();

            private void Release()
            {
                if (!_held) return;
                _held = false;
                Block(false);
            }

            private static void Block(bool on)
            {
                try
                {
                    s_blockInput?.Invoke(null, new object[] { on });
                }
                catch (Exception)
                {
                    // A failed block leaves the keys as they were.
                }
            }
        }

        private static void Travel(object destination)
        {
            if (!Working) return;
            s_trips++;
            s_travelTo.Invoke(null, new object[] { destination });
        }

        private static bool Selecting => Ready && (bool)s_mapSelecting.GetValue(null);

        /// <summary>
        /// While the map picker is open, after every other pin drawing: the other pins hidden where
        /// only the portals are to show, and a star on each favourite's pin, put on again whenever
        /// the game makes the pin anew.
        /// </summary>
        private static void AfterUpdatePins(Minimap __instance)
        {
            if ((!On && !OnlyPortals) || !Selecting) return;
            try
            {
                if (!(s_mapPins.GetValue(null) is IDictionary pins)) return;
                if (OnlyPortals) HideOthers(__instance, pins);
                if (!On || s_side == null) return;
                foreach (DictionaryEntry pair in pins)
                {
                    var element = ((Minimap.PinData)pair.Key).m_uiElement;
                    if (element == null) continue;
                    bool lit = s_favourites.Contains(IdOf(pair.Value));
                    var star = element.Find(StarName);
                    if (lit && star == null) PinStar(element);
                    else if (!lit && star != null) UnityEngine.Object.Destroy(star.gameObject);
                }
            }
            catch (Exception e)
            {
                StepAside("its stars on the map failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>Every game pin the map picker did not put there, its name too, and HUDCompass's markers, hidden (<see cref="PortalFavourites.PinShown"/>).</summary>
        private static void HideOthers(Minimap map, IDictionary offered)
        {
            foreach (var pin in s_gamePins(map))
            {
                if (PortalFavourites.PinShown(true, offered.Contains(pin))) continue;
                var icon = pin.m_uiElement != null ? pin.m_uiElement.gameObject : null;
                if (icon != null && icon.activeSelf)
                {
                    icon.SetActive(false);
                    s_hidden.Add(icon);
                    s_hidAny = true;
                }
                // The game sets a name's state on each update by the zoom, so it is hidden again each time.
                var name = pin.m_NamePinData?.PinNameGameObject;
                if (name != null && name.activeSelf)
                {
                    name.SetActive(false);
                    s_hidAny = true;
                }
            }
            if (s_compassDmm == null || !(s_compassMarkers.GetValue(s_compassDmm) is IDictionary markers)) return;
            foreach (object marker in markers.Values)
            {
                // HUDCompass switches its markers on, or off by its own settings, on its next drawing.
                var icon = s_compassIcon(marker);
                if (icon != null && icon.activeSelf)
                {
                    icon.SetActive(false);
                    s_hidAny = true;
                }
                var tag = s_compassTag(marker);
                if (tag != null && tag.activeSelf)
                {
                    tag.SetActive(false);
                    s_hidAny = true;
                }
            }
        }

        private static void PinStar(RectTransform element)
        {
            var go = new GameObject(StarName, typeof(RectTransform), typeof(Image)) { layer = element.gameObject.layer };
            go.transform.SetParent(element, false);
            var rt = (RectTransform)go.transform;
            // The star sits on the pin's top right corner, half its size.
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = element.rect.size * 0.55f;
            rt.anchoredPosition = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.sprite = StarSprite();
            image.color = Orange;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        /// <summary>A right click on the map picker's pin marks or unmarks it, the pin AdjustablePortals' own click would take.</summary>
        private static void BeforeRightClick(Minimap __instance)
        {
            if (s_side == null || !Selecting) return;
            try
            {
                if (!(s_mapPins.GetValue(null) is IDictionary pins) || pins.Count == 0) return;
                var at = (Vector3)s_screenToWorld.Invoke(__instance, new object[] { (Vector3)ZInput.pointerPosition });
                float radius = (float)s_pinRadius.GetValue(__instance);
                var entries = PortalFavourites.Entries(pins);
                int hit = PortalFavourites.NearestWithin(entries.Select(e => (((Minimap.PinData)e.Key).m_pos.x, ((Minimap.PinData)e.Key).m_pos.z)).ToList(), at.x, at.z, radius);
                if (hit < 0) return;
                Toggle(entries[hit].Value);
                FillSide();
                s_pinUpdate(__instance) = true;
            }
            catch (Exception e)
            {
                StepAside("its right click on the map failed: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
