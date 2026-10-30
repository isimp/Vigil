using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "TargetPortal's pins follow their portal" (<see cref="PinFollowRules"/>).
    /// TargetPortal 1.2.7 keeps its portal pins by place and names each once, when it makes it, so a
    /// renamed portal kept its old name on the map and in the favourites list; it keeps favourites
    /// as the place a portal had when it was marked, so a moved portal lost its favourite. On, after
    /// each of TargetPortal's pin updates a pin whose portal has another tag takes the new one. A
    /// favourite moves with its portal: Vigil remembers each favourite portal by its world id (which
    /// a move does not change) in its own entry of the character's data, and every second, and
    /// before each pin update, rewrites the favourite of a portal standing elsewhere; TargetPortal's
    /// own favourites keep their form. The favourites list is then built again. TargetPortal's code
    /// is compared with the code this was written against (1.2.7),
    /// and any change makes the patch step aside. The switch is this game's own.
    /// </summary>
    internal static class TargetPortalPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.targetportal";
        public const string Guid = "org.bepinex.plugins.targetportal";
        private const string FavouritesKey = "TargetPortal Favorites";

        /// <summary>TargetPortal 1.2.7's Map.AddPortalPins, Map.FillFavorites and Map.ToggleFavoritePortal.</summary>
        private const ulong KnownCode = 0x0D118261F52AD2DFUL;

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        private static long s_renamed, s_moved;
        private static bool s_favouritesChanged;

        public static string State => PinFollowRules.TargetPortalLine(On, Found, Problem, s_renamed, s_moved);

        private static Action<string> s_log;
        private static Dictionary<Minimap.PinData, ZDO> s_activePins;
        private static Action s_fillFavourites;
        private static readonly AccessTools.FieldRef<Minimap, bool> s_pinUpdate = AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");

        /// <summary>Vigil's own entry in the character's data: the favourite portals by world id, which TargetPortal does not read.</summary>
        private const string BookKey = "Vigil TargetPortal favourite portals";
        private const float ResyncSeconds = 1f;
        private static FieldInfo s_knownPortals;
        private static Player s_bookFor;
        private static Dictionary<VehicleId, string> s_book = new Dictionary<VehicleId, string>();
        private static readonly Dictionary<VehicleId, string> s_now = new Dictionary<VehicleId, string>();
        private static float s_nextResync;

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == Guid);
            Found = info?.Instance != null;
            if (!Found) return;
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var map = info.Instance.GetType().Assembly.GetType("TargetPortal.Map");
                var add = map?.GetMethod("AddPortalPins", any);
                var fill = map?.GetMethod("FillFavorites", any);
                var toggle = map?.GetMethod("ToggleFavoritePortal", any);
                if (add == null || fill == null || toggle == null || CodeHash(new[] { add, fill, toggle }) != KnownCode)
                {
                    Problem = "TargetPortal's pin code changed (an update)";
                    return;
                }
                s_activePins = AccessTools.DeclaredField(map, "activePins")?.GetValue(null) as Dictionary<Minimap.PinData, ZDO>;
                s_fillFavourites = AccessTools.MethodDelegate<Action>(fill);
                // Every portal this game knows, refilled by TargetPortal every 5 s from the game's own portal list.
                s_knownPortals = AccessTools.DeclaredField(info.Instance.GetType(), "knownPortals");
                if (s_activePins == null || s_knownPortals == null || s_knownPortals.FieldType != typeof(HashSet<ZDO>))
                {
                    Problem = "TargetPortal's pin code changed (an update)";
                    return;
                }
                new Harmony(HarmonyId).Patch(add, prefix: new HarmonyMethod(typeof(TargetPortalPatch), nameof(BeforeAddPins)),
                    postfix: new HarmonyMethod(typeof(TargetPortalPatch), nameof(AfterAddPins)));
            }
            catch (Exception e)
            {
                Problem = "TargetPortal could not be hooked: " + e.GetType().Name + " " + e.Message;
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

        private static void StepAside(string why)
        {
            Problem = why;
            s_log?.Invoke("Vigil: the patch for TargetPortal's pins following their portal stepped aside: " + why);
        }

        /// <summary>The pin's portal while it is still that portal: the game recycles a removed object's record for a new one.</summary>
        private static bool Live(ZDO zdo) =>
            zdo != null && ZDOMan.instance != null && ZDOMan.instance.GetZDO(zdo.m_uid) == zdo && Game.instance != null && Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab());

        /// <summary>
        /// Every second: favourites follow their portals by the portals' world ids, also while
        /// TargetPortal has no pins (it drops them on leaving a portal unless its icons are shown).
        /// </summary>
        public static void Update()
        {
            if (!On || !Found || Problem != null) return;
            var player = Player.m_localPlayer;
            if (player == null || ZDOMan.instance == null) return;
            float now = Time.unscaledTime;
            if (now < s_nextResync) return;
            s_nextResync = now + ResyncSeconds;
            try
            {
                Resync(player);
                if (s_favouritesChanged && s_activePins.Count > 0) s_fillFavourites();
                s_favouritesChanged = false;
            }
            catch (Exception e)
            {
                StepAside("its look at the favourites failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// Moves each favourite whose remembered portal stands elsewhere now, and keeps the book of
        /// favourite portals (world id and place) in the character's data, beside TargetPortal's own.
        /// </summary>
        private static void Resync(Player player)
        {
            if (s_bookFor != player)
            {
                s_bookFor = player;
                s_book = PinFollowRules.ParseFavouriteBook(player.m_customData.TryGetValue(BookKey, out string kept) ? kept : null);
            }
            player.m_customData.TryGetValue(FavouritesKey, out string favs);
            if (string.IsNullOrEmpty(favs) && s_book.Count == 0) return;
            s_now.Clear();
            if (s_knownPortals.GetValue(null) is HashSet<ZDO> portals)
                foreach (var zdo in portals)
                    if (Live(zdo)) s_now[new VehicleId(zdo.m_uid.UserID, zdo.m_uid.ID)] = zdo.GetPosition().ToString();
            string next = PinFollowRules.FavouritesResync(favs, s_now, s_book, out int moved);
            if (!ReferenceEquals(next, favs))
            {
                player.m_customData[FavouritesKey] = next;
                s_moved += moved;
                s_favouritesChanged = true;
            }
            string book = PinFollowRules.FormatFavouriteBook(s_book);
            player.m_customData.TryGetValue(BookKey, out string stored);
            if (book.Length == 0) player.m_customData.Remove(BookKey);
            else if (book != stored) player.m_customData[BookKey] = book;
        }

        /// <summary>Before TargetPortal moves the pin of a moved portal (it keeps pins by place): the favourite of its old place goes to the new one.</summary>
        private static void BeforeAddPins()
        {
            if (!On || Problem != null) return;
            try
            {
                var player = Player.m_localPlayer;
                if (player == null || !player.m_customData.TryGetValue(FavouritesKey, out string saved)) return;
                string now = saved;
                foreach (var pair in s_activePins)
                {
                    if (!Live(pair.Value)) continue;
                    Vector3 to = pair.Value.GetPosition();
                    if (to == pair.Key.m_pos) continue;
                    string moved = PinFollowRules.FavouritesMoved(now, pair.Key.m_pos.ToString(), to.ToString());
                    if (ReferenceEquals(moved, now)) continue;
                    now = moved;
                    s_moved++;
                }
                if (!ReferenceEquals(now, saved))
                {
                    player.m_customData[FavouritesKey] = now;
                    s_favouritesChanged = true;
                }
                Resync(player);
            }
            catch (Exception e)
            {
                StepAside("its look at the favourites failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>After TargetPortal's pin update: each pin takes its portal's tag, and the favourites list is built again when a name or favourite changed.</summary>
        private static void AfterAddPins()
        {
            if (!On || Problem != null) return;
            try
            {
                bool changed = s_favouritesChanged, redraw = false;
                s_favouritesChanged = false;
                foreach (var pair in s_activePins)
                {
                    if (!Live(pair.Value)) continue;
                    string tag = pair.Value.GetString(ZDOVars.s_tag);
                    var pin = pair.Key;
                    if (!PinFollowRules.NameChanged(pin.m_name, tag)) continue;
                    pin.m_name = tag;
                    if (pin.m_NamePinData == null)
                    {
                        // The game gives a name object only to a pin named when it was added; the next redraw makes it.
                        if (tag.Length > 0) pin.m_NamePinData = new Minimap.PinNameData(pin);
                        redraw = true;
                    }
                    else if (pin.m_NamePinData.PinNameText != null) pin.m_NamePinData.PinNameText.text = Localization.instance.Localize(tag);
                    s_renamed++;
                    changed = true;
                }
                if (redraw && Minimap.instance != null) s_pinUpdate(Minimap.instance) = true;
                if (changed && Player.m_localPlayer != null) s_fillFavourites();
            }
            catch (Exception e)
            {
                StepAside("its renaming of the pins failed: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
