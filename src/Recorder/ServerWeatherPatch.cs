using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The server's weather on this game (<see cref="ServerWeather"/>), on the server and on every
    /// player's game. A postfix on <c>EnvMan.GetEnvironmentOverride</c>, which gives the game's own
    /// overrides (a raid, a dungeon, the intro), puts the server's weather where it gives none. While a
    /// freeze holds: a prefix on <c>EnvMan.UpdateEnvironment</c> rolls for the frozen world second, so
    /// each biome keeps the weather it had then; around <c>EnvMan.FixedUpdate</c> the game's own
    /// time-of-day switch draws and judges the frozen hour (unless an admin drew another on this game),
    /// and is put back after (a finalizer, so also when the update throws), so nothing else sees it set; beds (<c>EnvMan.CalculateCanSleep</c>) and
    /// the server's check that everyone sleeps (<c>Game.UpdateSleeping</c>) go by the world's real
    /// clock; and when the server skips to the morning (<c>EnvMan.SkipToMorning</c>), the freeze ends.
    /// Every target is found by name; when one is missing, nothing is hooked and the reason is kept.
    /// The self-test tries a weather on this game alone (<see cref="Try"/>): the hooks follow the
    /// trial instead of the server's until it ends, or until its seconds run out on their own.
    /// </summary>
    internal static class ServerWeatherPatch
    {
        private const string HarmonyId = "isimp.Vigil.serverweather";

        private static MethodInfo s_select;
        private static FieldInfo s_afternoon, s_night;
        private static bool s_borrowedTime;
        private static ServerWeather s_trial;
        private static float s_trialUntil;

        /// <summary>What the server holds, kept current by the config entry.</summary>
        public static ServerWeather State = ServerWeather.None;

        /// <summary>On the server: called when everyone slept through a night while a freeze held.</summary>
        public static Action SleptThrough;

        /// <summary>What this game follows: a self-test's trial while it lasts, else the server's.</summary>
        public static ServerWeather Current => s_trial != null && Time.realtimeSinceStartup < s_trialUntil ? s_trial : State;

        /// <summary>This game alone follows <paramref name="trial"/> for at most <paramref name="seconds"/>.</summary>
        public static void Try(ServerWeather trial, float seconds)
        {
            s_trial = trial;
            s_trialUntil = Time.realtimeSinceStartup + seconds;
        }

        public static void EndTrial() => s_trial = null;

        /// <summary>Why the hooks did not go in; null when they are in.</summary>
        public static string Problem { get; private set; } = "not put in yet";

        public static void Hook()
        {
            try
            {
                var env = typeof(EnvMan);
                var overrideOf = AccessTools.Method(env, "GetEnvironmentOverride");
                var update = AccessTools.Method(env, "UpdateEnvironment");
                var fixedUpdate = AccessTools.Method(env, "FixedUpdate");
                var canSleep = AccessTools.Method(env, "CalculateCanSleep");
                var morning = AccessTools.Method(env, "SkipToMorning");
                var sleeping = AccessTools.Method(typeof(Game), "UpdateSleeping");
                s_select = AccessTools.Method(env, "SelectWeightedEnvironment");
                s_afternoon = AccessTools.Field(env, "s_isAfternoon");
                s_night = AccessTools.Field(env, "s_isNight");
                var missing = new List<string>();
                if (overrideOf == null) missing.Add("EnvMan.GetEnvironmentOverride");
                if (update == null) missing.Add("EnvMan.UpdateEnvironment");
                if (fixedUpdate == null) missing.Add("EnvMan.FixedUpdate");
                if (canSleep == null) missing.Add("EnvMan.CalculateCanSleep");
                if (morning == null) missing.Add("EnvMan.SkipToMorning");
                if (sleeping == null) missing.Add("Game.UpdateSleeping");
                if (s_afternoon == null || s_night == null) missing.Add("EnvMan's day and night flags");
                if (missing.Count > 0)
                {
                    Problem = "not found (an update changed it): " + string.Join(", ", missing.ToArray());
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(overrideOf, postfix: new HarmonyMethod(typeof(ServerWeatherPatch), nameof(AfterOverride)));
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(ServerWeatherPatch), nameof(BeforeUpdateEnvironment)));
                harmony.Patch(fixedUpdate, prefix: new HarmonyMethod(typeof(ServerWeatherPatch), nameof(BeforeFixedUpdate)), finalizer: new HarmonyMethod(typeof(ServerWeatherPatch), nameof(AfterFixedUpdate)));
                harmony.Patch(canSleep, postfix: new HarmonyMethod(typeof(ServerWeatherPatch), nameof(AfterCanSleep)));
                harmony.Patch(sleeping, prefix: new HarmonyMethod(typeof(ServerWeatherPatch), nameof(BeforeUpdateSleeping)), finalizer: new HarmonyMethod(typeof(ServerWeatherPatch), nameof(AfterUpdateSleeping)));
                harmony.Patch(morning, postfix: new HarmonyMethod(typeof(ServerWeatherPatch), nameof(AfterSkipToMorning)));
                Problem = null;
            }
            catch (Exception e)
            {
                Problem = e.GetType().Name + " " + e.Message;
            }
        }

        private static bool Holding => Current.HeldAt.HasValue;

        private static void AfterOverride(ref string __result)
        {
            var w = Current;
            if (w.Weather != null) __result = w.WeatherFor(__result);
        }

        private static void BeforeUpdateEnvironment(ref long sec)
        {
            if (Holding) sec = Current.RollSecond(sec);
        }

        private static void BeforeFixedUpdate(EnvMan __instance)
        {
            s_borrowedTime = false;
            if (!Holding || __instance.m_debugTimeOfDay) return;
            __instance.m_debugTimeOfDay = true;
            __instance.m_debugTime = Current.HeldDayFraction(__instance.m_dayLengthSec) ?? 0f;
            s_borrowedTime = true;
        }

        private static void AfterFixedUpdate(EnvMan __instance)
        {
            if (!s_borrowedTime) return;
            __instance.m_debugTimeOfDay = false;
            s_borrowedTime = false;
        }

        private static void AfterCanSleep(EnvMan __instance, ref bool __result)
        {
            if (!Holding || ZNet.instance == null) return;
            double now = ZNet.instance.GetTimeSeconds();
            bool rested = Player.m_localPlayer == null || now > Player.m_localPlayer.m_wakeupTime + __instance.m_sleepCooldownSeconds;
            __result = DayClock.SleepTime(DayClock.Fraction(now, __instance.m_dayLengthSec)) && rested;
        }

        // The server's sleep check reads the day and night flags the frozen hour set; for it, they are the real clock's.
        private static void BeforeUpdateSleeping(out bool[] __state)
        {
            __state = null;
            if (!Holding || EnvMan.instance == null || ZNet.instance == null) return;
            __state = new[] { (bool)s_afternoon.GetValue(null), (bool)s_night.GetValue(null) };
            float real = DayClock.Fraction(ZNet.instance.GetTimeSeconds(), EnvMan.instance.m_dayLengthSec);
            s_afternoon.SetValue(null, real >= 0.5f && real <= 0.75f);
            s_night.SetValue(null, real <= 0.25f || real >= 0.75f);
        }

        private static void AfterUpdateSleeping(bool[] __state)
        {
            if (__state == null) return;
            s_afternoon.SetValue(null, __state[0]);
            s_night.SetValue(null, __state[1]);
        }

        private static void AfterSkipToMorning()
        {
            if (State.HeldAt.HasValue && ZNet.instance != null && ZNet.instance.IsServer()) SleptThrough?.Invoke();
        }

        /// <summary>What the game's own roll would give now where this game's camera is, the server's weather aside; null when it cannot tell.</summary>
        public static string RollNow(EnvMan env, object biome)
        {
            if (s_select == null || env == null || biome == null || ZNet.instance == null) return null;
            var camera = Utils.GetMainCamera();
            if (camera == null) return null;
            var list = env.GetAvailableEnvironments((BiomeSector)biome);
            if (list == null || list.Count == 0) return null;
            var saved = UnityEngine.Random.state;
            try
            {
                long period = WeatherRoll.Period((long)ZNet.instance.GetTimeSeconds(), env.m_environmentDuration);
                UnityEngine.Random.InitState((int)period);
                var picked = s_select.Invoke(env, new object[] { list }) as EnvSetup;
                Vector3 p = camera.transform.position;
                // asked with the same coordinates the game asks them with
                bool ashlands = WorldGenerator.IsAshlands(p.x, p.z), deepNorth = WorldGenerator.IsDeepnorth(p.x, p.y);
                foreach (var entry in list)
                {
                    if ((entry.m_ashlandsOverride && ashlands) || (entry.m_deepnorthOverride && deepNorth)) picked = entry.m_env;
                }
                return picked?.m_name;
            }
            catch (Exception) { return null; }
            finally { UnityEngine.Random.state = saved; }
        }
    }
}
