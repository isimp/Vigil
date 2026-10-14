using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// Where the player is and what is around, sampled once a second for the timeline, plus how
    /// many networked objects were created and destroyed (ZNetScene.CreateObject / Destroy).
    /// Every read is guarded: a missing manager simply leaves its value empty.
    /// </summary>
    internal sealed class WorldContext
    {
        private static int s_created, s_destroyed;
        private static readonly AccessTools.FieldRef<ZoneSystem, IDictionary> s_zones =
            SafeRef<ZoneSystem, IDictionary>("m_zones");
        private static readonly System.Reflection.FieldInfo s_itemList = AccessTools.Field(typeof(ItemDrop), "s_instances");
        private static readonly System.Reflection.FieldInfo s_pieceList = AccessTools.Field(typeof(WearNTear), "s_allInstances");

        public WorldContext(Harmony harmony)
        {
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }), "objects created",
                postfix: Patching.M(typeof(WorldContext), nameof(Created)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNetScene), "Destroy", new[] { typeof(GameObject) }), "objects destroyed",
                prefix: Patching.M(typeof(WorldContext), nameof(Destroyed)));
        }

        private static AccessTools.FieldRef<T, F> SafeRef<T, F>(string field)
        {
            try { return AccessTools.FieldRefAccess<T, F>(field); } catch (Exception) { return null; }
        }

        private static readonly HashSet<ZDOID> s_missingSeen = new HashSet<ZDOID>();
        private static readonly Dictionary<int, string> s_knownNames = new Dictionary<int, string>
        {
            // Prefabs of mods that were once in this pack; the game only logs the hash.
            { "rae_OdinHorse".GetStableHashCode(), "rae_OdinHorse (OdinHorse)" },
        };
        internal static string MissingPath;
        public static int MissingCount => s_missingSeen.Count;

        private static void Created(ZDO zdo, GameObject __result)
        {
            if (!ProbeState.Active) return;
            if (__result != null) { s_created++; return; }
            try
            {
                // The game could not build this object; when its prefab is unknown, note where it is, once.
                // Objects without a type are a load test's; the game never builds them on purpose.
                if (zdo == null || zdo.GetPrefab() == 0 || ZNetScene.instance == null || ZNetScene.instance.GetPrefab(zdo.GetPrefab()) != null) return;
                if (!s_missingSeen.Add(zdo.m_uid) || MissingPath == null) return;
                int hash = zdo.GetPrefab();
                var p = zdo.GetPosition();
                if (!System.IO.File.Exists(MissingPath)) System.IO.File.WriteAllText(MissingPath, "time\thash\tknown_as\tx\ty\tz\tzdo\towner\r\n");
                System.IO.File.AppendAllText(MissingPath, string.Join("\t", new[]
                {
                    DateTime.Now.ToString("HH:mm:ss"), hash.ToString(), s_knownNames.TryGetValue(hash, out var n) ? n : "",
                    Mathf.RoundToInt(p.x).ToString(), Mathf.RoundToInt(p.y).ToString(), Mathf.RoundToInt(p.z).ToString(),
                    zdo.m_uid.ToString(), zdo.GetOwner().ToString(),
                }) + "\r\n");
            }
            catch (Exception) { }
        }
        private static void Destroyed() { if (ProbeState.Active) s_destroyed++; }

        public int TakeCreated() { int n = s_created; s_created = 0; return n; }
        public int TakeDestroyed() { int n = s_destroyed; s_destroyed = 0; return n; }

        /// <summary>"in world", "teleporting", "loading", "menu" or "server".</summary>
        public static string Situation()
        {
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsDedicated()) return "server";
                var p = Player.m_localPlayer;
                if (p == null) return ZNet.instance != null ? "loading" : "menu";
                if (p.IsTeleporting()) return "teleporting";
                return "in world";
            }
            catch (Exception) { return "?"; }
        }

        public void Fill(Action<string, object> set)
        {
            Try(() => set("zdos", ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : (object)null));
            Try(() => set("instances", ZNetScene.instance != null ? ZNetScene.instance.NrOfInstances() : (object)null));
            Try(() => set("characters", Character.GetAllCharacters().Count));
            Try(() => set("ai", BaseAI.Instances.Count));
            Try(() => set("items", (s_itemList?.GetValue(null) as IList)?.Count));
            Try(() => set("pieces", (s_pieceList?.GetValue(null) as IList)?.Count));
            Try(() => set("ships", Ship.Instances.Count));
            Try(() => set("heightmaps", Heightmap.Instances.Count));
            Try(() => set("zones", ZoneSystem.instance != null && s_zones != null ? s_zones(ZoneSystem.instance).Count : (object)null));
            Try(() => set("peers", ZNet.instance != null ? ZNet.instance.GetPeers().Count : (object)null));

            var p = Player.m_localPlayer;
            if (p != null)
            {
                Try(() =>
                {
                    var pos = p.transform.position;
                    set("x", Mathf.RoundToInt(pos.x)); set("y", Mathf.RoundToInt(pos.y)); set("z", Mathf.RoundToInt(pos.z));
                });
                Try(() => set("biome", p.GetCurrentBiome().ToString()));
                Try(() => set("interior", p.InInterior()));
                Try(() => set("on_ship", p.GetStandingOnShip() != null));
                Try(() => set("speed", Math.Round(p.GetVelocity().magnitude, 1)));
            }
            Try(() => set("env", EnvMan.instance != null && EnvMan.instance.GetCurrentEnvironment() != null ? EnvMan.instance.GetCurrentEnvironment().m_name : null));
            Try(() => set("fog", EnvMan.instance != null ? Math.Round(RenderSettings.fogDensity, 4) : (object)null));
            Try(() => set("day", EnvMan.instance != null ? Math.Round(EnvMan.instance.GetDayFraction(), 2) : (object)null));
            Try(() =>
            {
                var ev = RandEventSystem.instance != null ? RandEventSystem.instance.GetActiveEvent() : null;
                set("event", ev != null ? ev.m_name : null);
            });
            Try(() => set("paused", Game.IsPaused()));
            Try(() => set("focused", Application.isFocused));
            Try(() => set("target_fps", Application.targetFrameRate));
            Try(() => set("vsync", QualitySettings.vSyncCount));
            Try(() => set("gfx", GraphicsState()));
            Try(() => set("ui", OpenUi()));
        }

        // The game's graphics settings in force, then what Unity actually runs with (a mod could
        // change those behind the settings): shadow distance, cascades and resolution, the level of
        // detail bias and the per-pixel light count.
        private static string GraphicsState()
        {
            var m = GraphicsSettingsManager.Instance;
            string game = "";
            if (m != null)
            {
                var s = m.ActiveSettings;
                game = Vigil.Core.GraphicsSignature.Format(
                    new[]
                    {
                        ("veg", (int)s.m_vegetation), ("lod", s.m_lod), ("lights", s.m_lights), ("shadow", s.m_shadowQuality),
                        ("plights", s.m_pointLights), ("plshadow", s.m_pointLightShadows), ("ssao", s.m_ssao), ("sim", s.m_simulationDistance),
                        ("cloth", s.m_clothQuality), ("res", s.m_target3DResolutionVertical >= 100000 ? 0 : s.m_target3DResolutionVertical),
                        ("fps", s.m_presentSettings.m_fpsLimit),
                    },
                    new[]
                    {
                        ("vsync", s.m_presentSettings.m_vsync), ("dshadow", s.m_distantShadows), ("tess", s.m_tesselation), ("bloom", s.m_bloom),
                        ("dof", s.m_depthOfField), ("mblur", s.m_motionBlur), ("chroma", s.m_chromaticAberration), ("sun", s.m_sunShafts),
                        ("softp", s.m_softParticles), ("aa", s.m_antiAliasing), ("aniso", s.m_anisotropicTextures),
                    }) + " | ";
            }
            return game + string.Format(System.Globalization.CultureInfo.InvariantCulture, "unity shd{0:0} c{1} {2} lodb{3:0.#} pix{4}",
                QualitySettings.shadowDistance, QualitySettings.shadowCascades, QualitySettings.shadowResolution, QualitySettings.lodBias, QualitySettings.pixelLightCount);
        }

        private static string OpenUi()
        {
            var open = new List<string>();
            try { if (InventoryGui.IsVisible()) open.Add("inventory"); } catch (Exception) { }
            try { if (Minimap.IsOpen()) open.Add("map"); } catch (Exception) { }
            try { if (Menu.IsVisible()) open.Add("menu"); } catch (Exception) { }
            try { if (global::Console.IsVisible()) open.Add("console"); } catch (Exception) { }
            try { if (Chat.instance != null && Chat.instance.HasFocus()) open.Add("chat"); } catch (Exception) { }
            try { if (TextInput.IsVisible()) open.Add("text"); } catch (Exception) { }
            try { if (StoreGui.IsVisible()) open.Add("store"); } catch (Exception) { }
            return string.Join("+", open.ToArray());
        }

        private static void Try(Action a)
        {
            try { a(); } catch (Exception) { }
        }
    }
}
