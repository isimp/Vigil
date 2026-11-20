using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
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
    /// unmarks it, favourite pins carry a star, and a panel beside the map lists the favourites, a
    /// click on one travelling there through AdjustablePortals' own <c>PortalTargeting.TravelTo</c>.
    /// Everything is drawn with AdjustablePortals' own <c>PickerUI</c> and the build menu's
    /// favourite star. AdjustablePortals' picker code is compared with 0.7.0's and 0.8.0's, which
    /// are the same, and any change makes the patch step aside. The switch is this game's own.
    /// </summary>
    internal static class PortalFavouritesPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.portalfavourites";

        /// <summary>
        /// AdjustablePortals 0.7.0's and 0.8.0's PortalListPicker Build, Rebuild, AddRow;
        /// PortalMapPicker Open, Close, PickAt; PickerUI CreatePanel, AddText, AddButton, NewLayoutRow.
        /// </summary>
        private const ulong KnownCode = 0x013033726379EFEDUL;

        private const string StarName = "VigilFavouriteStar";
        private const float StarSize = 20f, StarIndent = 26f, HeadingHeight = 24f;
        private const float SideWidth = 250f, SideRow = 32f, SideTop = 52f, SideFoot = 44f;
        private const int SideMax = 12;
        private static readonly Color Orange = new Color(1f, 0.631f, 0.235f, 1f);
        private static readonly Color StarOff = new Color(0.48f, 0.4f, 0.31f, 0.85f);

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        private static long s_toggled, s_trips;

        public static string State => PortalFavourites.Line(On, Found, Problem, s_toggled, s_trips);

        private static Action<string> s_log;
        private static FieldInfo s_listCurrent, s_listDestinations, s_listFilter, s_listContent, s_listWidth, s_mapPins, s_mapSelecting;
        private static FieldInfo s_destId, s_destName, s_destPosition;
        private static MethodInfo s_rebuild, s_createPanel, s_addText, s_addButton, s_newLayoutRow, s_travelTo, s_screenToWorld;
        private static PropertyInfo s_pinRadius;
        private static readonly AccessTools.FieldRef<Minimap, bool> s_pinUpdate = AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");

        /// <summary>The favourites, read from the character's data for <see cref="s_bookFor"/>.</summary>
        private static HashSet<VehicleId> s_favourites = new HashSet<VehicleId>();
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
                s_travelTo = Method(targeting, "TravelTo");
                var code = new MethodBase[] { build, s_rebuild, addRow, open, close, pickAt, s_createPanel, s_addText, s_addButton, s_newLayoutRow };
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
                s_screenToWorld = AccessTools.DeclaredMethod(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });
                s_pinRadius = AccessTools.DeclaredProperty(typeof(Minimap), "PinInteractRadius");
                if (new object[] { s_listCurrent, s_listDestinations, s_listFilter, s_listContent, s_listWidth, s_mapPins, s_mapSelecting, s_destId, s_destName, s_destPosition, s_screenToWorld, s_pinRadius }.Any(m => m == null)
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
                h.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "UpdatePins"), postfix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(AfterUpdatePins)));
                h.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "RemovePinUnderPointer"), prefix: new HarmonyMethod(typeof(PortalFavouritesPatch), nameof(BeforeRightClick)));
            }
            catch (Exception e)
            {
                Problem = "AdjustablePortals could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static void StepAside(string why)
        {
            Problem = why;
            DestroySide();
            s_log?.Invoke("Vigil: the patch for AdjustablePortals' favourite portals stepped aside: " + why);
        }

        private static bool Working => On && Found && Problem == null;

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
                var ordered = PortalFavourites.FavouritesFirst(s_listOrder, d => s_favourites.Contains(IdOf(d)));
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
            if (!Working || !__result) return;
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
        }

        private static void DestroySide()
        {
            if (s_side != null) UnityEngine.Object.Destroy(s_side);
            s_side = null;
        }

        /// <summary>The panel of favourites at the map's left: one button a favourite, by name, and how to mark one.</summary>
        private static void BuildSide()
        {
            DestroySide();
            var map = Minimap.instance;
            if (map == null || map.m_largeRoot == null) return;
            var names = s_mapDestinations.Select(NameOf).ToList();
            var favourite = s_mapDestinations.Select(d => s_favourites.Contains(IdOf(d))).ToList();
            var rows = PortalFavourites.SideRows(names, favourite, SideMax, out int more);
            int lines = Math.Max(1, rows.Count) + (more > 0 ? 1 : 0);
            float height = SideTop + lines * SideRow + SideFoot;
            s_side = (GameObject)s_createPanel.Invoke(null, new object[] { map.m_largeRoot.transform, SideWidth, height, "Favourites" });
            var rt = (RectTransform)s_side.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(40f, 0f);
            s_side.transform.SetAsLastSibling();
            float inner = SideWidth - 32f, y = SideTop;
            foreach (int i in rows)
            {
                var destination = s_mapDestinations[i];
                s_addButton.Invoke(null, new object[] { s_side.transform, 16f, y, inner, SideRow - 4f, names[i], new UnityAction(() => Travel(destination)) });
                y += SideRow;
            }
            if (rows.Count == 0)
            {
                s_addText.Invoke(null, new object[] { s_side.transform, 16f, y, inner, SideRow, "No favourites yet.", 14, TextAnchor.MiddleLeft, null });
                y += SideRow;
            }
            if (more > 0)
            {
                s_addText.Invoke(null, new object[] { s_side.transform, 16f, y, inner, SideRow, "... " + more + " more on the map", 13, TextAnchor.MiddleLeft, null });
                y += SideRow;
            }
            s_addText.Invoke(null, new object[] { s_side.transform, 16f, y + 6f, inner, SideFoot - 12f, "Right click a portal to mark it.", 13, TextAnchor.MiddleLeft, (Color?)Orange });
        }

        private static void Travel(object destination)
        {
            if (!Working) return;
            s_trips++;
            s_travelTo.Invoke(null, new object[] { destination });
        }

        private static bool Selecting => Working && (bool)s_mapSelecting.GetValue(null);

        /// <summary>While the map picker is open: a star on each favourite's pin, put on again whenever the game makes the pin anew.</summary>
        private static void AfterUpdatePins()
        {
            if (s_side == null || !Selecting) return;
            try
            {
                if (!(s_mapPins.GetValue(null) is IDictionary pins)) return;
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
                var entries = pins.Cast<DictionaryEntry>().ToList();
                int hit = PortalFavourites.NearestWithin(entries.Select(e => (((Minimap.PinData)e.Key).m_pos.x, ((Minimap.PinData)e.Key).m_pos.z)).ToList(), at.x, at.z, radius);
                if (hit < 0) return;
                Toggle(entries[hit].Value);
                BuildSide();
                s_pinUpdate(__instance) = true;
            }
            catch (Exception e)
            {
                StepAside("its right click on the map failed: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
