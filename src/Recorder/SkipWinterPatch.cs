using System;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Skip winter" (<see cref="SeasonSkip"/>): a postfix on Seasonality's
    /// next-season rule (<see cref="SeasonRule"/>) turns winter into spring while the server's switch is on.
    /// Seasonality's season type is not known to Vigil, so the result is taken as an object and
    /// written back by name. Right after hooking, the patch asks Seasonality for the season after fall
    /// and steps aside, with the reason, unless the answer is spring. On the server and players' games.
    /// </summary>
    internal static class SkipWinterPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.skipwinter";
        private const string Guid = "RustyMods.Seasonality";

        /// <summary>The server's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }

        public static string Version { get; private set; }

        /// <summary>Why the patch stepped aside; null when its hook is in and checked (or Seasonality is absent).</summary>
        public static string Problem { get; private set; }

        public static string State => SeasonSkip.StateLine(On, Found, Version, Problem);

        public static void Hook(Action<string> log)
        {
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == Guid);
            Found = info != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var rule = SeasonRule.Find(TypeLookup.Loaded.Find);
                if (rule == null)
                {
                    Problem = "Seasonality's next-season rule was not found (an update changed it)";
                    return;
                }
                new Harmony(HarmonyId).Patch(rule.Next, postfix: new HarmonyMethod(typeof(SkipWinterPatch), nameof(AfterNext)) { priority = Priority.Last });

                // The check: with the switch on for a moment, the season after fall must be spring.
                bool was = On;
                On = true;
                object after;
                try { after = rule.AfterFall(); }
                finally { On = was; }
                if (after?.ToString() != "Spring") Problem = "the hook did not take: fall still goes to " + after;
                else log?.Invoke("Vigil: the Skip winter patch is hooked into Seasonality " + Version + " and checked: fall goes to spring while the server's switch is on.");
            }
            catch (Exception e) { Problem = "Seasonality could not be hooked: " + e.GetType().Name + " " + e.Message; }
        }

        private static void AfterNext(ref object __result)
        {
            if (!On || __result == null) return;
            string name = __result.ToString();
            string instead = SeasonSkip.Next(name);
            if (instead != name) __result = Enum.Parse(__result.GetType(), instead);
        }
    }
}
