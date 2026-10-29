using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Vigil.Core;
using Object = UnityEngine.Object;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "HUDCompass keeps its pin icons" (<see cref="CompassPinBook{T}"/>): a prefix on
    /// HUDCompass's postfix on <c>Hud.Update</c>, which every frame puts the map's pins on the compass.
    /// HUDCompass 1.2.1 names each icon after its pin's position as text and finds it by that name,
    /// for every pin of a shown type on the map, and gives a moving pin a new icon object each frame.
    /// On, the prefix does that frame's work itself by HUDCompass's rules, keeping icons by a position
    /// key, reusing those whose pin is gone, and calling HUDCompass's own checks for combat, mist, fog
    /// and the crossed-off mark; HUDCompass's postfix is skipped for that frame. Whenever something it
    /// needs is missing it leaves the frame to HUDCompass. Its code is compared with the code this
    /// was written against (1.2.1), and any change makes the patch step aside. Off, HUDCompass's
    /// postfix is timed for the state line. The switch is this game's own.
    /// A second switch, "the compass follows Cartur's map", works inside this frame when Cartur's Map
    /// Pins is installed: the map pins Cartur hides (its hidden names, its hide key) are left off the
    /// compass, and pins take the colour Cartur gives them on the map (<see cref="CarturPinRules"/>).
    /// Cartur's colouring code is compared with 2.0.0's, and any change makes that part step aside.
    /// </summary>
    internal static class HudCompassPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.hudcompass";
        public const string Guid = "neobotics.valheim_mod.hudcompass";
        public const string CarturGuid = "com.jekkle.valheim.carturmappins";

        /// <summary>The IL of the compass postfix and the pin rules in HUDCompass 1.2.1.</summary>
        private const ulong KnownCode = 0x31CF98BBE3A684B3UL;

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        public static string Version { get; private set; }
        /// <summary>Why the patch stepped aside; null while it is in.</summary>
        public static string Problem { get; private set; }

        private static long s_frames, s_ticks, s_made, s_reused, s_theirFrames, s_theirTicks;
        private static int s_pins;

        public static string State => CompassPinRules.StateLine(On, Found, Version, Problem, s_frames, Ms(s_ticks), s_pins, s_made, s_reused, s_theirFrames, Ms(s_theirTicks));

        /// <summary>Cartur 2.0.0's Patch_Minimap_UpdatePins.Postfix, where it colours the map's pins.</summary>
        private const ulong KnownCarturColours = 0xA506A78BD2002009UL;

        /// <summary>This game's switch for following Cartur's map, kept current by the config entry.</summary>
        public static bool FollowCartur;

        /// <summary>Why following Cartur stepped aside; null while it is in.</summary>
        public static string FollowProblem { get; private set; }

        private static bool s_carturFound;
        private static int s_hiddenLast, s_colouredLast, s_frameHidden, s_frameColoured;

        public static string FollowState => CarturPinRules.FollowLine(FollowCartur, Found && s_carturFound, On && Problem == null, FollowProblem, s_hiddenLast, s_colouredLast);

        private delegate bool TintFn(Minimap.PinType type, out Color colour);

        // Cartur's own answers.
        private static Func<Minimap.PinData, bool> s_carturHidden, s_carturKeyHides;
        private static Func<Vector3, Color?> s_carturStyle;
        private static TintFn s_carturTint;
        private static Func<Minimap.PinType> s_carturLooted;
        private static bool s_frameFollow;
        private static Minimap.PinType s_frameLooted;

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private sealed class Icon
        {
            public GameObject Go;
            public RectTransform Rt;
            public Image Img;
        }

        private static readonly CompassPinBook<Icon> s_book = new CompassPinBook<Icon>();
        private static readonly List<Icon> s_released = new List<Icon>();
        private static readonly List<Icon> s_destroy = new List<Icon>();
        private static readonly Dictionary<Sprite, string> s_iconNames = new Dictionary<Sprite, string>();
        private static readonly Dictionary<int, string> s_typeNames = new Dictionary<int, string>();
        private static Action<string> s_log;

        // HUDCompass's own state, found once.
        private static FieldInfo s_instanceField;
        private static AccessTools.FieldRef<GameObject> s_parent, s_compass, s_pinsRoot, s_centerMark;
        private static AccessTools.FieldRef<Sprite> s_spriteCompass, s_spriteMask;
        private static AccessTools.FieldRef<long> s_localPlayerId;
        private static List<string> s_ignoredNames, s_ignoredPrefixes, s_ignoredSuffixes, s_ignoredTypes, s_alwaysVisible;
        private static Dictionary<string, string> s_typeAlias;
        private static object s_dmm;
        private static List<Minimap.PinData> s_dynamicPins;
        private static AccessTools.FieldRef<object, Sprite> s_checkedSprite;
        private static Func<Player, bool> s_isAttacking, s_isInMist;
        private static Func<bool> s_isTooFoggy;
        private static Action<GameObject, Color, bool, float> s_setChecked;
        private static Type s_cfg;

        // HUDCompass's settings, found on its first frame (its config is bound by then).
        private static bool s_settingsRead;
        private static ConfigEntry<bool> s_enable, s_showHide, s_indoors, s_running, s_combat, s_mist, s_fog, s_mapOpen, s_playerDirection, s_centerShown, s_dynamicColors, s_dynamicOnCompass;
        private static ConfigEntry<Color> s_colorCompass, s_colorPins, s_colorCenter, s_shipColor, s_cartColor, s_portalColor, s_activePortalColor;
        private static ConfigEntry<float> s_scale, s_pinScaleMin, s_pinScale;
        private static ConfigEntry<int> s_yOffset, s_distanceMin, s_distanceMax;
        private static ConfigEntryBase s_playerPinBehavior;
        private static int s_playerPinMode;

        // The game's own state.
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> s_mapPins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
        private static readonly AccessTools.FieldRef<Minimap, Dictionary<Vector3, Minimap.PinData>> s_locationPins = AccessTools.FieldRefAccess<Minimap, Dictionary<Vector3, Minimap.PinData>>("m_locationPins");
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> s_playerPins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_playerPins");
        private static readonly AccessTools.FieldRef<Minimap, Minimap.PinData> s_deathPin = AccessTools.FieldRefAccess<Minimap, Minimap.PinData>("m_deathPin");
        private static readonly AccessTools.FieldRef<Minimap, bool[]> s_visibleTypes = AccessTools.FieldRefAccess<Minimap, bool[]>("m_visibleIconTypes");
        private static readonly AccessTools.FieldRef<List<GuiScaler>> s_scalers = AccessTools.StaticFieldRefAccess<List<GuiScaler>>(AccessTools.DeclaredField(typeof(GuiScaler), "m_scalers"));
        private static readonly AccessTools.FieldRef<GuiScaler, CanvasScaler> s_canvasScaler = AccessTools.FieldRefAccess<GuiScaler, CanvasScaler>("m_canvasScaler");

        // What the frame works with, cached while the same HUD lasts.
        private static GameObject s_cachedCompass, s_cachedPinsRoot;
        private static RectTransform s_compassRt, s_parentRt;
        private static Image s_compassImg, s_centerImg;
        private static GameObject s_cachedCenter;
        private static CanvasScaler s_loadingScaler;
        private static bool s_adopted;

        // One frame's values for the pin loop.
        private static Transform s_playerT, s_cameraT;
        private static Vector3 s_playerPos;
        private static bool[] s_frameVisible;
        private static bool s_framePlayerDirection, s_frameDynamicColors;
        private static float s_frameHalfWidth, s_frameFade, s_framePinScaleMin, s_framePinScale;
        private static int s_frameMin, s_frameMax;
        private static Color s_framePinColor;
        private static long s_frameLocalId;
        private static Sprite s_frameChecked;
        private static int s_framePins;

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == Guid);
            Found = info != null && info.Instance != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var asm = info.Instance.GetType().Assembly;
                var main = asm.GetType("neobotics.ValheimMods.HUDCompass");
                var patch = asm.GetType("neobotics.ValheimMods.HUDCompass+HudUpdateCompassPatch");
                var dmmType = asm.GetType("neobotics.ValheimMods.DynamicMapMarkers");
                s_cfg = asm.GetType("neobotics.ValheimMods.Cfg");
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var update = patch?.GetMethod("Hud_Update_Prefix", any, null, new[] { typeof(Hud) }, null);
                if (main == null || update == null || dmmType == null || s_cfg == null)
                {
                    Problem = "its compass code changed (an update)";
                    return;
                }
                var rules = new[] { "MatchType", "MatchTypeOrName", "MatchNameOrWildcard", "IsInBounds" }.Select(n => main.GetMethod(n, any)).ToList();
                if (rules.Any(m => m == null) || CodeHash(new[] { update }.Concat(rules)) != KnownCode)
                {
                    Problem = "its compass code changed (an update)";
                    return;
                }

                s_instanceField = AccessTools.DeclaredField(main, "s_instance");
                s_parent = Static<GameObject>(main, "s_objectParent");
                s_compass = Static<GameObject>(main, "s_objectCompass");
                s_pinsRoot = Static<GameObject>(main, "s_objectPins");
                s_centerMark = Static<GameObject>(main, "s_objectCenterMark");
                s_spriteCompass = Static<Sprite>(main, "s_spriteCompass");
                s_spriteMask = Static<Sprite>(main, "s_spriteMask");
                s_localPlayerId = Static<long>(main, "s_localPlayerID");
                s_ignoredNames = Value<List<string>>(main, "s_ignoredNames");
                s_ignoredPrefixes = Value<List<string>>(main, "s_ignoredWildcardPrefixes");
                s_ignoredSuffixes = Value<List<string>>(main, "s_ignoredWildcardSuffixes");
                s_ignoredTypes = Value<List<string>>(main, "s_ignoredTypes");
                s_alwaysVisible = Value<List<string>>(main, "s_alwaysVisible");
                s_typeAlias = Value<Dictionary<string, string>>(main, "s_typeAlias");
                s_dmm = AccessTools.DeclaredField(main, "s_dmm")?.GetValue(null);
                s_dynamicPins = s_dmm == null ? null : AccessTools.DeclaredField(dmmType, "PinData")?.GetValue(s_dmm) as List<Minimap.PinData>;
                s_checkedSprite = AccessTools.FieldRefAccess<Sprite>(dmmType, "CheckedMarkerSprite");
                s_isAttacking = AccessTools.MethodDelegate<Func<Player, bool>>(main.GetMethod("IsAttacking", any));
                s_isInMist = AccessTools.MethodDelegate<Func<Player, bool>>(main.GetMethod("IsInMist", any));
                s_isTooFoggy = AccessTools.MethodDelegate<Func<bool>>(main.GetMethod("IsTooFoggy", any));
                s_setChecked = AccessTools.MethodDelegate<Action<GameObject, Color, bool, float>>(main.GetMethod("SetCheckedOverlay", any));
                if (s_instanceField == null || s_ignoredNames == null || s_ignoredPrefixes == null || s_ignoredSuffixes == null || s_ignoredTypes == null
                    || s_alwaysVisible == null || s_typeAlias == null || s_dynamicPins == null)
                {
                    Problem = "its compass code changed (an update)";
                    return;
                }
                var h = new Harmony(HarmonyId);
                h.Patch(update, prefix: new HarmonyMethod(typeof(HudCompassPatch), nameof(BeforeUpdate)), postfix: new HarmonyMethod(typeof(HudCompassPatch), nameof(AfterUpdate)));
            }
            catch (Exception e)
            {
                Problem = "HUDCompass could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
            HookCartur();
        }

        /// <summary>Finds Cartur's answers for hiding and colouring a map pin.</summary>
        private static void HookCartur()
        {
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == CarturGuid);
            s_carturFound = info?.Instance != null;
            if (!s_carturFound) return;
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var asm = info.Instance.GetType().Assembly;
                var colours = asm.GetType("CarturMapPins.Patch_Minimap_UpdatePins")?.GetMethod("Postfix", any);
                var hiding = asm.GetType("CarturMapPins.PinHiding");
                var styles = asm.GetType("CarturMapPins.PinStyles");
                var placer = asm.GetType("CarturMapPins.PinPlacer");
                var isHidden = hiding?.GetMethod("IsHidden", any);
                var keyHides = hiding?.GetMethod("CycleHides", any);
                var styleFor = styles?.GetMethod("For", any);
                var colourFor = styles?.GetMethod("ColourFor", any);
                var tintFor = styles?.GetMethod("TintFor", any);
                var looted = placer?.GetMethod("LootedChestType", any);
                if (colours == null || isHidden == null || keyHides == null || styleFor == null || colourFor == null || tintFor == null || looted == null
                    || colourFor.ReturnType != typeof(Color?) || CodeHash(new[] { colours }) != KnownCarturColours)
                {
                    FollowProblem = "Cartur's colour code changed (an update)";
                    return;
                }
                s_carturHidden = AccessTools.MethodDelegate<Func<Minimap.PinData, bool>>(isHidden);
                s_carturKeyHides = AccessTools.MethodDelegate<Func<Minimap.PinData, bool>>(keyHides);
                s_carturTint = AccessTools.MethodDelegate<TintFn>(tintFor);
                s_carturLooted = AccessTools.MethodDelegate<Func<Minimap.PinType>>(looted);
                // PinStyles.For returns Cartur's own Style, which only its ColourFor reads.
                var pos = System.Linq.Expressions.Expression.Parameter(typeof(Vector3), "pos");
                s_carturStyle = System.Linq.Expressions.Expression.Lambda<Func<Vector3, Color?>>(
                    System.Linq.Expressions.Expression.Call(colourFor, System.Linq.Expressions.Expression.Call(styleFor, pos)), pos).Compile();
            }
            catch (Exception e)
            {
                FollowProblem = "Cartur could not be hooked: " + e.GetType().Name + " " + e.Message;
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

        private static AccessTools.FieldRef<F> Static<F>(Type type, string name) => AccessTools.StaticFieldRefAccess<F>(AccessTools.DeclaredField(type, name));

        private static F Value<F>(Type type, string name) where F : class => AccessTools.DeclaredField(type, name)?.GetValue(null) as F;

        /// <summary>A HUDCompass setting: a plain config entry, or one inside its own wrapper.</summary>
        private static ConfigEntry<T> Setting<T>(string name) => SettingBase(name) as ConfigEntry<T>;

        private static ConfigEntryBase SettingBase(string name)
        {
            object v = AccessTools.DeclaredField(s_cfg, name)?.GetValue(null);
            if (v is ConfigEntryBase e) return e;
            return v?.GetType().GetProperty("ConfigEntry")?.GetValue(v) as ConfigEntryBase;
        }

        private static bool ReadSettings()
        {
            s_enable = Setting<bool>("enableCompass");
            s_showHide = Setting<bool>("showHideCompass");
            s_indoors = Setting<bool>("hideCompassIndoors");
            s_running = Setting<bool>("hideCompassWhileRunning");
            s_combat = Setting<bool>("hideCompassInCombat");
            s_mist = Setting<bool>("hideCompassInMist");
            s_fog = Setting<bool>("hideCompassInFog");
            s_mapOpen = Setting<bool>("hideCompassWhileMapOpen");
            s_playerDirection = Setting<bool>("compassUsePlayerDirection");
            s_centerShown = Setting<bool>("compassShowCenterMark");
            s_dynamicColors = Setting<bool>("useDynamicColorsOnCompass");
            s_dynamicOnCompass = Setting<bool>("showDynamicPinsOnCompass");
            s_colorCompass = Setting<Color>("colorCompass");
            s_colorPins = Setting<Color>("colorPins");
            s_colorCenter = Setting<Color>("colorCenterMark");
            s_shipColor = Setting<Color>("shipColor");
            s_cartColor = Setting<Color>("cartColor");
            s_portalColor = Setting<Color>("portalColor");
            s_activePortalColor = Setting<Color>("activePortalColor");
            s_scale = Setting<float>("compassScale");
            s_pinScaleMin = Setting<float>("scalePinsMin");
            s_pinScale = Setting<float>("scalePins");
            s_yOffset = Setting<int>("compassYOffset");
            s_distanceMin = Setting<int>("distancePinsMin");
            s_distanceMax = Setting<int>("distancePinsMax");
            s_playerPinBehavior = SettingBase("playerPinBehavior");
            object[] all = { s_enable, s_showHide, s_indoors, s_running, s_combat, s_mist, s_fog, s_mapOpen, s_playerDirection, s_centerShown, s_dynamicColors, s_dynamicOnCompass,
                s_colorCompass, s_colorPins, s_colorCenter, s_shipColor, s_cartColor, s_portalColor, s_activePortalColor, s_scale, s_pinScaleMin, s_pinScale,
                s_yOffset, s_distanceMin, s_distanceMax, s_playerPinBehavior };
            if (all.Any(x => x == null)) return false;
            s_playerPinMode = Convert.ToInt32(s_playerPinBehavior.BoxedValue);
            s_playerPinBehavior.ConfigFile.SettingChanged += (_, __) => s_playerPinMode = Convert.ToInt32(s_playerPinBehavior.BoxedValue);
            return true;
        }

        private static bool BeforeUpdate(ref long __state)
        {
            __state = 0;
            if (!On || Problem != null)
            {
                s_adopted = false;
                __state = Stopwatch.GetTimestamp();
                return true;
            }
            if (!s_settingsRead)
            {
                s_settingsRead = true;
                if (!ReadSettings())
                {
                    StepAside("a HUDCompass setting is missing (an update)");
                    return true;
                }
            }
            var player = Player.m_localPlayer;
            var plugin = s_instanceField.GetValue(null) as Object;
            if (plugin == null || !s_enable.Value || player == null || s_parent() == null || s_compass() == null || s_pinsRoot() == null
                || Minimap.instance == null || GameCamera.instance == null || Hud.instance == null)
            {
                // HUDCompass returns early or fails here itself; its own code keeps that.
                s_adopted = false;
                return true;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                Frame(player);
            }
            catch (Exception e)
            {
                StepAside("its compass frame failed: " + e.GetType().Name + " " + e.Message);
                return true;
            }
            s_frames++;
            s_ticks += Stopwatch.GetTimestamp() - start;
            return false;
        }

        private static void AfterUpdate(long __state)
        {
            if (__state == 0) return;
            s_theirFrames++;
            s_theirTicks += Stopwatch.GetTimestamp() - __state;
        }

        private static void StepAside(string why)
        {
            Problem = why;
            s_adopted = false;
            s_log?.Invoke("Vigil: the HUDCompass pin patch stepped aside: " + why);
        }

        /// <summary>HUDCompass's compass frame, by its rules.</summary>
        private static void Frame(Player player)
        {
            var map = Minimap.instance;
            bool show = s_showHide.Value
                && (!s_indoors.Value || !player.InInterior())
                && (!s_running.Value || !player.IsRunning())
                && (!s_combat.Value || !s_isAttacking(player))
                && (!s_mist.Value || !s_isInMist(player))
                && (!s_fog.Value || !s_isTooFoggy())
                && (!s_mapOpen.Value || !map.m_largeRoot.activeSelf)
                && Hud.instance.IsVisible();
            var parent = s_parent();
            if (parent.activeInHierarchy != show) parent.SetActive(show);
            if (!show) return;
            if (s_spriteCompass() == null || s_spriteMask() == null) return;

            Follow(parent);

            bool playerDirection = s_playerDirection.Value;
            float angle = !playerDirection ? GameCamera.instance.transform.eulerAngles.y : player.transform.eulerAngles.y;
            if (angle > 180f) angle -= 360f;
            angle *= -Mathf.PI / 180f;
            Rect rect = s_compassImg.sprite.rect;
            float scaleFactor = 1f;
            var scaler = LoadingScaler();
            if (scaler != null) scaleFactor = scaler.scaleFactor;
            s_compassRt.localPosition = Vector3.right * (rect.width / 2f) * angle / (Mathf.PI * 2f) - new Vector3(rect.width * 0.125f, 0f, 0f);
            s_compassImg.color = s_colorCompass.Value;
            s_parentRt.localScale = Vector3.one * s_scale.Value;
            int halfHeight = Mathf.RoundToInt(rect.height * scaleFactor * s_scale.Value / 2f);
            s_parentRt.position = new Vector3(Screen.width / 2, Screen.height - halfHeight - s_yOffset.Value, 0f);

            var center = s_centerMark();
            if (center != null)
            {
                if (center != s_cachedCenter)
                {
                    s_cachedCenter = center;
                    s_centerImg = center.GetComponent<Image>();
                }
                bool centerShown = s_centerShown.Value;
                if (centerShown) s_centerImg.color = s_colorCenter.Value;
                if (center.activeInHierarchy != centerShown) center.SetActive(centerShown);
            }

            s_playerT = player.transform;
            s_playerPos = s_playerT.position;
            s_cameraT = GameCamera.instance.transform;
            s_frameVisible = s_visibleTypes(map);
            s_framePlayerDirection = playerDirection;
            s_frameDynamicColors = s_dynamicColors.Value;
            s_frameHalfWidth = rect.width / 2f;
            s_framePinScaleMin = s_pinScaleMin.Value;
            s_framePinScale = s_pinScale.Value;
            s_frameMin = s_distanceMin.Value;
            s_frameMax = s_distanceMax.Value;
            s_frameFade = 1f - s_framePinScaleMin > 0f ? s_frameMax / (1f - s_framePinScaleMin) : 0f;
            s_framePinColor = s_colorPins.Value;
            s_frameLocalId = s_localPlayerId();
            s_frameChecked = s_checkedSprite(s_dmm);
            s_framePins = 0;
            s_frameFollow = FollowCartur && s_carturFound && FollowProblem == null;
            s_frameHidden = 0;
            s_frameColoured = 0;
            if (s_frameFollow) s_frameLooted = s_carturLooted();

            s_book.BeginFrame();
            foreach (var pin in s_mapPins(map)) Visit(pin, true);
            foreach (var pin in s_locationPins(map).Values) Visit(pin, false);
            if (s_playerPinMode != PlayerPinForceOff) foreach (var pin in s_playerPins(map)) Visit(pin, false);
            var death = s_deathPin(map);
            if (death != null) Visit(death, false);
            if (s_dynamicOnCompass.Value) foreach (var pin in s_dynamicPins) Visit(pin, false);
            s_hiddenLast = s_frameHidden;
            s_colouredLast = s_frameColoured;
            s_released.Clear();
            s_destroy.Clear();
            s_book.EndFrame(s_released, s_destroy);
            foreach (var icon in s_released)
                if (icon.Go != null && icon.Go.activeSelf) icon.Go.SetActive(false);
            foreach (var icon in s_destroy)
                if (icon.Go != null) Object.Destroy(icon.Go);
            s_pins = s_framePins;
        }

        /// <summary>HUDCompass's PlayerPinBehavior.ForceOff.</summary>
        private const int PlayerPinForceOff = 1;

        /// <summary>Caches the HUD's parts; a new HUD, or a frame HUDCompass drew itself, starts the icons afresh.</summary>
        private static void Follow(GameObject parent)
        {
            var compass = s_compass();
            if (compass != s_cachedCompass)
            {
                s_cachedCompass = compass;
                s_compassImg = compass.GetComponent<Image>();
                s_compassRt = compass.GetComponent<RectTransform>();
            }
            if (s_parentRt == null || s_parentRt.gameObject != parent) s_parentRt = parent.GetComponent<RectTransform>();
            var root = s_pinsRoot();
            if (root == s_cachedPinsRoot && s_adopted) return;
            s_cachedPinsRoot = root;
            s_adopted = true;
            s_book.Forget();
            // HUDCompass's own icons, or this patch's from before a frame HUDCompass drew: the icons start afresh.
            foreach (Transform child in root.transform)
            {
                child.gameObject.SetActive(false);
                Object.Destroy(child.gameObject);
            }
        }

        private static CanvasScaler LoadingScaler()
        {
            if (s_loadingScaler != null) return s_loadingScaler;
            var scalers = s_scalers();
            if (scalers == null) return null;
            foreach (var s in scalers)
            {
                var cs = s_canvasScaler(s);
                if (cs.name == "LoadingGUI")
                {
                    s_loadingScaler = cs;
                    break;
                }
            }
            return s_loadingScaler;
        }

        private static void Visit(Minimap.PinData pin, bool fromMapPins)
        {
            if (!s_frameVisible[(int)pin.m_type]) return;
            s_framePins++;
            var key = PinKey.Of(pin.m_pos.x, pin.m_pos.y, pin.m_pos.z);
            bool have = s_book.TryGet(key, out var icon);
            float distance = Vector3.Distance(s_playerPos, pin.m_pos);
            string iconName = IconName(pin.m_icon);
            string typeName = TypeName(pin.m_type);
            bool always = CompassPinRules.TypeOrNameMatches(s_alwaysVisible, iconName, typeName, pin.m_name, s_typeAlias);
            bool ignoredType = CompassPinRules.TypeMatches(s_ignoredTypes, iconName, typeName, s_typeAlias);
            bool ignoredName = CompassPinRules.NameIgnored(s_ignoredNames, s_ignoredPrefixes, s_ignoredSuffixes, pin.m_name);
            bool shown = !ignoredType && !ignoredName && (always || CompassPinRules.InBounds(distance, s_frameMin, s_frameMax));
            if (shown && s_frameFollow && fromMapPins && CarturHides(pin))
            {
                shown = false;
                s_frameHidden++;
            }
            if (have && icon.Go.activeSelf != shown) icon.Go.SetActive(shown);
            if (!shown) return;

            Vector3 local = !s_framePlayerDirection ? s_cameraT.InverseTransformPoint(pin.m_pos) : s_playerT.InverseTransformPoint(pin.m_pos);
            float angle = Mathf.Atan2(local.x, local.z);
            if (!have)
            {
                icon = s_book.Place(key, MakeIcon, out bool reused);
                if (reused)
                {
                    s_reused++;
                    Reuse(icon);
                }
                else s_made++;
            }
            float size = always ? 1f : (s_framePinScaleMin < 1f ? (s_frameFade - distance) / s_frameFade : 1f);
            icon.Rt.localScale = Vector3.one * size * 0.5f * s_framePinScale;
            Color color = s_framePinColor;
            if (s_frameDynamicColors)
            {
                switch (pin.m_name)
                {
                    case "Ship": color = s_shipColor.Value; break;
                    case "Cart": color = s_cartColor.Value; break;
                    case "Portal": color = s_portalColor.Value; break;
                    case "ActivePortal": color = s_activePortalColor.Value; break;
                }
            }
            if (s_frameFollow && fromMapPins) color = CarturColour(pin, color);
            if (pin.m_ownerID != 0L && pin.m_ownerID != s_frameLocalId) color = new Color(color.r * 0.7f, color.g * 0.7f, color.b * 0.7f, color.a * 0.8f);
            icon.Img.color = color;
            icon.Img.sprite = pin.m_icon;
            icon.Rt.localPosition = Vector3.right * s_frameHalfWidth * angle / (Mathf.PI * 2f);
            if (s_frameChecked != null) s_setChecked(icon.Go, Color.red, pin.m_checked, color.a);
        }

        /// <summary>Whether Cartur leaves this map pin off its map; a failure there ends following Cartur for the session.</summary>
        private static bool CarturHides(Minimap.PinData pin)
        {
            try
            {
                return CarturPinRules.HiddenOnCompass(true, s_carturHidden(pin), s_carturKeyHides(pin));
            }
            catch (Exception e)
            {
                StepAsideFromCartur("its hiding check failed: " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>The colour Cartur gives this map pin, from the compass's own.</summary>
        private static Color CarturColour(Minimap.PinData pin, Color compass)
        {
            try
            {
                Color? style = s_carturStyle(pin.m_pos);
                Relocate.Rgba? tint = null;
                if (s_carturTint(pin.m_type, out var t)) tint = Rgba(t);
                bool looted = CarturPinRules.Looted((int)s_frameLooted, (int)pin.m_type, (int)Minimap.PinType.None);
                var c = CarturPinRules.Colour(Rgba(compass), style.HasValue ? Rgba(style.Value) : (Relocate.Rgba?)null, tint, pin.m_checked, looted);
                var result = new Color(c.R, c.G, c.B, c.A);
                if (result != compass) s_frameColoured++;
                return result;
            }
            catch (Exception e)
            {
                StepAsideFromCartur("its colour check failed: " + e.GetType().Name + " " + e.Message);
                return compass;
            }
        }

        private static Relocate.Rgba Rgba(Color c) => new Relocate.Rgba(c.r, c.g, c.b, c.a);

        private static void StepAsideFromCartur(string why)
        {
            FollowProblem = why;
            s_frameFollow = false;
            s_log?.Invoke("Vigil: the compass stopped following Cartur's map: " + why);
        }

        private static Icon MakeIcon()
        {
            var go = new GameObject("Vigil compass pin");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(s_cachedPinsRoot.transform, false);
            rt.anchoredPosition = Vector2.zero;
            return new Icon { Go = go, Rt = rt, Img = go.AddComponent<Image>() };
        }

        /// <summary>An icon back in use is shown last, as a new one would be, without the crossed-off mark of its last pin.</summary>
        private static void Reuse(Icon icon)
        {
            icon.Go.SetActive(true);
            icon.Rt.SetAsLastSibling();
            var mark = icon.Rt.Find("checked");
            if (mark != null)
            {
                // HUDCompass makes the mark again, coloured for this pin; renamed so it does not find this one meanwhile.
                mark.name = "checked (old)";
                Object.Destroy(mark.gameObject);
            }
        }

        private static string IconName(Sprite sprite)
        {
            if ((object)sprite == null) return null;
            if (!s_iconNames.TryGetValue(sprite, out var name))
            {
                name = sprite.name;
                s_iconNames[sprite] = name;
            }
            return name;
        }

        private static string TypeName(Minimap.PinType type)
        {
            int k = (int)type;
            if (!s_typeNames.TryGetValue(k, out var name))
            {
                name = type.ToString();
                s_typeNames[k] = name;
            }
            return name;
        }
    }
}
