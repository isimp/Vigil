using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// On a player's game: the weather and light as the game has them now, and the changes Vigil
    /// makes to them. Each game picks and draws its own weather (<c>EnvMan</c>), so every change here
    /// is this game's only. The weather, the wind and the time of day drawn use the game's own debug
    /// switches (<c>m_debugEnv</c>, <c>SetDebugWind</c>, <c>m_debugTimeOfDay</c>), which a world's
    /// unloading clears; the fog has none, so a postfix on <c>EnvMan.SetEnv</c>, which draws the
    /// weather's fog every fixed step, puts the fog an admin set, or caps it at the fog limiter
    /// (<see cref="WeatherControl.FogDrawn"/>).
    /// </summary>
    internal static class WeatherWatch
    {
        private const string HarmonyId = "isimp.Vigil.weather";
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly FieldInfo s_currentBiome = AccessTools.Field(typeof(EnvMan), "m_currentBiome");
        private static readonly HashSet<string> LightTypes = new HashSet<string> { "EnvMan", "EnvSetup", "EnvZone", "CameraEffects", "ParticleMist", "Mister", "MistEmitter" };

        /// <summary>Fog an admin set; null for the weather's own.</summary>
        public static float? SetFog { get; set; }

        /// <summary>The fog limiter's cap; null for off.</summary>
        public static float? Limit { get; set; }

        /// <summary>Whether the cap is the server's (it wins while set) rather than the player's own.</summary>
        public static bool LimitFromServer { get; set; }

        /// <summary>The weather Vigil put into the game's debug weather; null when it did not.</summary>
        public static string SetWeather { get; set; }

        /// <summary>The fog the weather itself drew in the last fixed step, before Vigil's change.</summary>
        public static float WeatherFog { get; private set; } = float.NaN;

        /// <summary>Why the fog hook could not go in; null when it is in.</summary>
        public static string HookProblem { get; private set; } = "not put in yet";

        public static void Hook()
        {
            try
            {
                var setEnv = AccessTools.Method(typeof(EnvMan), "SetEnv");
                if (setEnv == null) { HookProblem = "EnvMan.SetEnv not found"; return; }
                new Harmony(HarmonyId).Patch(setEnv, postfix: new HarmonyMethod(typeof(WeatherWatch), nameof(AfterSetEnv)));
                HookProblem = null;
            }
            catch (Exception e)
            {
                HookProblem = e.GetType().Name + " " + e.Message;
            }
        }

        private static void AfterSetEnv()
        {
            float weather = RenderSettings.fogDensity;
            WeatherFog = weather;
            if (SetFog == null && Limit == null) return;
            float drawn = (float)WeatherControl.FogDrawn(weather, SetFog, Limit);
            if (!Mathf.Approximately(drawn, weather)) RenderSettings.fogDensity = drawn;
        }

        /// <summary>The world unloaded: what was set for it goes with it (the limiter stays).</summary>
        public static void WorldLeft()
        {
            SetFog = null;
            SetWeather = null;
            WeatherFog = float.NaN;
        }

        // ------------------------------------------------------------------ reading

        /// <summary>What one reading found.</summary>
        internal sealed class State
        {
            public string Weather, Biome, Day, Time, Wind, OnYou, FogColour;
            public WeatherCause.Cause Cause;
            public long Period, NextRoll, Duration;
            public float FogDrawn, FogWeather;
            public List<WeatherRoll.Entry> List = new List<WeatherRoll.Entry>();
            public List<KeyValuePair<string, string>> Mods = new List<KeyValuePair<string, string>>();
            public List<KeyValuePair<string, bool>> Post = new List<KeyValuePair<string, bool>>();
            public string Quality, Gpu, Api, SetWind, SetTime;
        }

        /// <summary>The weather and light now; null outside a world.</summary>
        public static State Read(bool withMods)
        {
            var env = EnvMan.instance;
            if (env == null || ZNet.instance == null) return null;
            long sec = (long)ZNet.instance.GetTimeSeconds();
            var s = new State
            {
                Weather = env.GetCurrentEnvironment()?.m_name ?? "none",
                Biome = env.GetCurrentBiome().ToString(),
                Duration = env.m_environmentDuration,
                Period = WeatherRoll.Period(sec, env.m_environmentDuration),
                NextRoll = WeatherRoll.SecondsToNext(sec, env.m_environmentDuration),
                FogDrawn = RenderSettings.fogDensity,
                FogWeather = WeatherFog,
                FogColour = "#" + ColorUtility.ToHtmlStringRGB(RenderSettings.fogColor),
            };
            s.Cause = WeatherCause.Of(Sources(env), s.Biome, s.Period, s.NextRoll);
            float day = env.GetDayFraction();
            s.Day = env.GetDay().ToString(Ci);
            s.Time = string.Format(Ci, "{0:00}:{1:00}", (int)(day * 24f), (int)(day * 24f * 60f) % 60);
            var dir = env.GetWindDir();
            float heading = (Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + 360f) % 360f;
            s.Wind = string.Format(Ci, "{0:0}%, heading {1:0}°", env.GetWindIntensity() * 100f, heading);
            var on = new List<string>();
            if (EnvMan.IsWet()) on.Add("wet");
            if (EnvMan.IsCold()) on.Add("cold");
            if (EnvMan.IsFreezing()) on.Add("freezing");
            s.OnYou = on.Count == 0 ? "dry and mild" : string.Join(", ", on.ToArray());
            s.SetWind = env.m_debugWind ? string.Format(Ci, "{0:0}° at {1:0}%", env.m_debugWindAngle, env.m_debugWindIntensity * 100f) : null;
            s.SetTime = env.m_debugTimeOfDay ? string.Format(Ci, "{0:00}:{1:00}", (int)(env.m_debugTime * 24f), (int)(env.m_debugTime * 24f * 60f) % 60) : null;
            try
            {
                var biome = s_currentBiome?.GetValue(env);
                var list = biome != null ? env.GetAvailableEnvironments((BiomeSector)biome) : null;
                if (list != null) s.List = WeatherRoll.Chances(list.Select(e => new KeyValuePair<string, float>(e.m_env != null ? e.m_env.m_name : e.m_environment, e.m_weight)), s.Weather);
            }
            catch (Exception) { }
            if (withMods)
            {
                try
                {
                    s.Mods = PatchWatch.Rows(m => m.DeclaringType != null && (LightTypes.Contains(m.DeclaringType.Name) || m.Name.IndexOf("Env", StringComparison.Ordinal) >= 0
                            || m.Name.IndexOf("Fog", StringComparison.Ordinal) >= 0 || m.Name.IndexOf("Weather", StringComparison.Ordinal) >= 0 || m.Name == "GetBiomeColor"))
                        .GroupBy(r => r.Owner)
                        .Select(g => new KeyValuePair<string, string>(g.Key, string.Join(", ", g.Select(r => r.Method + " (" + r.Kind + ")").Distinct().ToArray())))
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                }
                catch (Exception) { }
            }
            try
            {
                var g = GraphicsSettingsManager.Instance.ActiveSettings;
                s.Post.Add(new KeyValuePair<string, bool>("bloom", g.m_bloom));
                s.Post.Add(new KeyValuePair<string, bool>("dof", g.m_depthOfField));
                s.Post.Add(new KeyValuePair<string, bool>("sun_shafts", g.m_sunShafts));
                s.Post.Add(new KeyValuePair<string, bool>("ssao", g.m_ssao != 0));
                s.Post.Add(new KeyValuePair<string, bool>("aa", g.m_antiAliasing));
                s.Post.Add(new KeyValuePair<string, bool>("motion_blur", g.m_motionBlur));
                s.Post.Add(new KeyValuePair<string, bool>("chromatic", g.m_chromaticAberration));
            }
            catch (Exception) { }
            s.Quality = QualitySettings.names.Length > QualitySettings.GetQualityLevel() ? QualitySettings.names[QualitySettings.GetQualityLevel()] : QualitySettings.GetQualityLevel().ToString(Ci);
            s.Gpu = SystemInfo.graphicsDeviceName;
            s.Api = SystemInfo.graphicsDeviceType.ToString();
            return s;
        }

        /// <summary>What each of the game's weather overrides holds, in the order the game asks them.</summary>
        private static WeatherSources Sources(EnvMan env)
        {
            var s = new WeatherSources { DebugEnv = env.m_debugEnv, IntroEnv = env.m_introEnvironment };
            s.SetByVigil = !string.IsNullOrEmpty(SetWeather) && string.Equals(env.m_debugEnv, SetWeather, StringComparison.Ordinal);
            try { s.InIntro = Player.m_localPlayer != null && Player.m_localPlayer.InIntro(); } catch (Exception) { }
            try
            {
                s.EventEnv = RandEventSystem.instance?.GetEnvOverride();
                s.EventName = RandEventSystem.instance?.GetActiveEvent()?.m_name;
            }
            catch (Exception) { }
            try
            {
                if (Player.m_localPlayer != null && WorldGenerator.instance != null)
                    s.AreaEnv = WorldGenerator.instance.GetBiomeSector(Player.m_localPlayer.transform.position).AltBiomes
                        .Select(a => a.m_forceEnvironment).FirstOrDefault(e => !string.IsNullOrEmpty(e));
            }
            catch (Exception) { }
            try { s.WorldEventEnv = PersistentEventSystem.instance?.GetEnvironmentOverride(); } catch (Exception) { }
            try { s.ZoneEnv = EnvZone.GetEnvironment(); } catch (Exception) { }
            return s;
        }

        /// <summary>The names of every weather the game knows, mods' included.</summary>
        public static List<string> Known() =>
            EnvMan.instance != null ? EnvMan.instance.m_environments.Where(e => e != null).Select(e => e.m_name).ToList() : new List<string>();

        /// <summary>This game's weather and light as an environment report.</summary>
        public static EnvironmentReport Report(State s, string player)
        {
            var r = new EnvironmentReport();
            void Put(string key, string value) { if (value != null) r.Values[key] = value; }
            Put("player", player);
            Put("written", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Ci));
            Put("day", s.Day);
            Put("time", s.Time);
            Put("biome", s.Biome);
            Put("weather", s.Weather);
            Put("cause", s.Cause?.Words);
            Put("period", s.Period.ToString(Ci));
            Put("list", string.Join("; ", s.List.Select(e => e.Name + " " + e.Weight.ToString("0.##", Ci)).ToArray()));
            Put("fog_drawn", s.FogDrawn.ToString("0.000", Ci));
            Put("fog_weather", float.IsNaN(s.FogWeather) ? null : s.FogWeather.ToString("0.000", Ci));
            Put("fog_limit", Limit.HasValue ? Limit.Value.ToString("0.000", Ci) + (LimitFromServer ? " (the server's)" : "") : "off");
            Put("fog_colour", s.FogColour);
            Put("wind", s.Wind);
            Put("on_you", s.OnYou);
            Put("set", SetSummary(s));
            Put("mods", s.Mods.Count == 0 ? "none" : string.Join("; ", s.Mods.Select(m => m.Key + ": " + m.Value).ToArray()));
            foreach (var p in s.Post) Put(p.Key, p.Value ? "on" : "off");
            Put("quality", s.Quality);
            Put("gpu", s.Gpu);
            Put("api", s.Api);
            return r;
        }

        /// <summary>What an admin set on this game, in words; "nothing" when nothing.</summary>
        public static string SetSummary(State s)
        {
            var parts = new List<string>();
            if (s.Cause != null && (s.Cause.Kind == WeatherCauseKind.SetInVigil || s.Cause.Kind == WeatherCauseKind.DevCommand)) parts.Add("weather " + s.Weather);
            if (SetFog.HasValue) parts.Add("fog " + SetFog.Value.ToString("0.000", Ci));
            if (s.SetWind != null) parts.Add("wind " + s.SetWind);
            if (s.SetTime != null) parts.Add("time " + s.SetTime);
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray());
        }
    }
}
