using System;
using System.Collections;
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
    /// The game patch "OdinOnDemand plays from where its speakers stand" (<see cref="OdinSpeakers"/>).
    /// A prefix on OdinOnDemand's <c>BasePlayer.UpdateSpeakerOutput</c>, which places the radio's sound
    /// from its saved speaker links, puts each linked speaker loaded here at its place now, and saves
    /// the new places in the radio's data when this game holds that data. That method runs only when a
    /// link changes or the radio's data is read, so a postfix on EasyRelocate's
    /// <c>RelocateApplier.ApplyVisual</c>, which moves a piece on the mover's game and on every game
    /// that has it loaded, runs it again for each radio linked to a moved speaker and for a moved radio;
    /// a postfix on <c>SpeakerComponent.OnEnable</c> does the same when a speaker loads somewhere else
    /// than its radio has it. A moved radio's own volume, which OdinOnDemand keeps under the radio's
    /// place, goes to the new place. OdinOnDemand's code is compared with 1.3.0's and EasyRelocate's
    /// with 1.1.1's; a change makes the part that relies on it step aside. The switch is this game's own.
    /// </summary>
    internal static class OdinSpeakersPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.odinspeakers";
        private const string OodGuid = "com.valmedia.odinondemand", RelocateGuid = "ModdedWolf.EasyRelocate";

        /// <summary>OdinOnDemand 1.3.0's BasePlayer.UpdateSpeakerOutput, FindSpeakerLink, LoadSpeakerLinks and the LocalVolumeKey getter.</summary>
        private const ulong KnownCode = 0xD149A8B0606B84CAUL;

        /// <summary>EasyRelocate 1.1.1's RelocateApplier.ApplyVisual.</summary>
        private const ulong KnownMoveCode = 0x682D0C02E69697C5UL;

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        public static bool RelocateFound { get; private set; }
        public static string OodVersion { get; private set; }

        /// <summary>Why the patch stepped aside; null while it is in (or OdinOnDemand is absent).</summary>
        public static string Problem { get; private set; }

        /// <summary>Why moves are not followed at once; the rest of the patch works without them.</summary>
        public static string MoveProblem { get; private set; }

        public static string State => OdinSpeakers.StateLine(On, Found, OodVersion, Problem, RelocateFound, MoveProblem, s_followed, s_carried);

        private static bool Working => On && Problem == null;

        private static long s_followed, s_carried;
        private static Action<string> s_log;
        private static Type s_playerType, s_speakerType;
        private static FieldInfo s_links, s_linkGuid, s_linkPosition;
        private static ConstructorInfo s_newLink;
        private static PropertyInfo s_speakerGuid, s_volumeKey;
        private static MethodInfo s_update, s_compress;
        private static IDictionary s_players;
        private static IList s_speakers;

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var ood = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == OodGuid);
            Found = ood?.Instance != null;
            OodVersion = ood?.Metadata.Version.ToString();
            if (!Found) return;
            var harmony = new Harmony(HarmonyId);
            try
            {
                var asm = ood.Instance.GetType().Assembly;
                s_playerType = asm.GetType("OdinOnDemand.MPlayer.BasePlayer");
                s_speakerType = asm.GetType("OdinOnDemand.Components.SpeakerComponent");
                var link = asm.GetType("OdinOnDemand.Components.SpeakerLink");
                var helper = asm.GetType("OdinOnDemand.Components.SpeakerHelper");
                var lists = asm.GetType("OdinOnDemand.Utils.Net.ComponentLists");
                s_update = s_playerType != null ? AccessTools.DeclaredMethod(s_playerType, "UpdateSpeakerOutput") : null;
                var find = s_playerType != null ? AccessTools.DeclaredMethod(s_playerType, "FindSpeakerLink") : null;
                var load = s_playerType != null ? AccessTools.DeclaredMethod(s_playerType, "LoadSpeakerLinks") : null;
                s_volumeKey = s_playerType != null ? AccessTools.DeclaredProperty(s_playerType, "LocalVolumeKey") : null;
                s_links = s_playerType != null ? AccessTools.DeclaredField(s_playerType, "speakerLinks") : null;
                s_linkGuid = link != null ? AccessTools.DeclaredField(link, "Guid") : null;
                s_linkPosition = link != null ? AccessTools.DeclaredField(link, "Position") : null;
                s_newLink = link != null ? AccessTools.DeclaredConstructor(link, new[] { typeof(string), typeof(Vector3) }) : null;
                s_compress = helper != null ? AccessTools.DeclaredMethod(helper, "CompressSpeakerLinks") : null;
                s_speakerGuid = s_speakerType != null ? AccessTools.DeclaredProperty(s_speakerType, "mGUID") : null;
                var enabled = s_speakerType != null ? AccessTools.DeclaredMethod(s_speakerType, "OnEnable") : null;
                s_players = lists != null ? AccessTools.DeclaredField(lists, "MediaComponentLists")?.GetValue(null) as IDictionary : null;
                s_speakers = lists != null ? AccessTools.DeclaredField(lists, "SpeakerComponentList")?.GetValue(null) as IList : null;
                if (s_update == null || find == null || load == null || s_volumeKey?.GetGetMethod(true) == null || CodeShape.Hash(new MethodBase[] { s_update, find, load, s_volumeKey.GetGetMethod(true) }) != KnownCode)
                {
                    Problem = "OdinOnDemand's speaker code changed (an update)";
                    return;
                }
                if (s_links == null || s_linkGuid == null || s_linkPosition == null || s_newLink == null || s_compress == null || s_speakerGuid == null || enabled == null || s_players == null || s_speakers == null)
                {
                    Problem = "OdinOnDemand's speaker links were not found (an update changed them)";
                    return;
                }
                harmony.Patch(s_update, prefix: new HarmonyMethod(typeof(OdinSpeakersPatch), nameof(BeforeOutput)));
                harmony.Patch(enabled, postfix: new HarmonyMethod(typeof(OdinSpeakersPatch), nameof(AfterSpeakerEnabled)));
            }
            catch (Exception e)
            {
                Problem = "OdinOnDemand could not be hooked: " + e.GetType().Name + " " + e.Message;
                return;
            }
            var relocate = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == RelocateGuid);
            RelocateFound = relocate?.Instance != null;
            if (!RelocateFound) return;
            try
            {
                var applier = relocate.Instance.GetType().Assembly.GetType("EasyRelocate.RelocateApplier");
                var visual = applier != null ? AccessTools.DeclaredMethod(applier, "ApplyVisual") : null;
                if (visual == null || CodeShape.Hash(new MethodBase[] { visual }) != KnownMoveCode)
                {
                    MoveProblem = "EasyRelocate's move code changed (an update)";
                    return;
                }
                harmony.Patch(visual, prefix: new HarmonyMethod(typeof(OdinSpeakersPatch), nameof(BeforeMove)),
                    postfix: new HarmonyMethod(typeof(OdinSpeakersPatch), nameof(AfterMove)));
            }
            catch (Exception e) { MoveProblem = "EasyRelocate could not be hooked: " + e.GetType().Name + " " + e.Message; }
        }

        private static void StepAside(string why)
        {
            Problem = why;
            s_log?.Invoke("Vigil: the patch for OdinOnDemand playing from where its speakers stand stepped aside: " + why);
        }

        /// <summary>Before OdinOnDemand places the radio's sound: each linked speaker loaded here at its place now.</summary>
        private static void BeforeOutput(object __instance)
        {
            if (!Working) return;
            try
            {
                if (!(s_links.GetValue(__instance) is IList links) || links.Count == 0) return;
                var saved = Spots(links);
                var now = OdinSpeakers.Follow(saved, LoadedSpeakers());
                if (now == null) return;
                for (int i = 0; i < now.Length; i++)
                {
                    if (!OdinSpeakers.Moved(saved[i], now[i])) continue;
                    links[i] = s_newLink.Invoke(new object[] { now[i].Guid, new Vector3(now[i].X, now[i].Y, now[i].Z) });
                    s_followed++;
                }
                // The radio's data holds the links for every game; only the game that holds it writes it, as OdinOnDemand does.
                var zdo = (__instance as Component)?.GetComponentInChildren<ZNetView>()?.GetZDO();
                if (zdo != null && zdo.IsOwner()) zdo.Set("speakers", (byte[])s_compress.Invoke(null, new object[] { links }));
            }
            catch (Exception e) { StepAside("its look at the speakers failed: " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>A speaker loaded somewhere else than its radios have it: they place their sound again.</summary>
        private static void AfterSpeakerEnabled(Component __instance)
        {
            if (!Working || __instance == null) return;
            try { Redo(Spot(__instance), null); }
            catch (Exception e) { StepAside("its look at a loaded speaker failed: " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>Before EasyRelocate moves a piece: a radio's volume key at its old place.</summary>
        private static void BeforeMove(ZNetView __0, out string __state)
        {
            __state = null;
            if (!Working || __0 == null) return;
            try
            {
                var player = __0.GetComponentInChildren(s_playerType);
                if (player != null) __state = s_volumeKey.GetValue(player, null) as string;
            }
            catch (Exception) { }
        }

        /// <summary>After EasyRelocate moved a piece: a moved speaker's radios and a moved radio place their sound again.</summary>
        private static void AfterMove(ZNetView __0, string __state)
        {
            if (!Working || __0 == null) return;
            try
            {
                var speaker = __0.GetComponentInChildren(s_speakerType);
                var player = __0.GetComponentInChildren(s_playerType);
                if (player != null) CarryVolume(__state, s_volumeKey.GetValue(player, null) as string);
                Redo(speaker != null ? Spot(speaker) : (SpeakerSpot?)null, player);
            }
            catch (Exception e) { StepAside("its look at a moved piece failed: " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>Places the sound again of every radio that plays this speaker from elsewhere, and of this radio.</summary>
        private static void Redo(SpeakerSpot? speaker, Component moved)
        {
            var redo = new List<object>();
            if (moved != null) redo.Add(moved);
            if (speaker.HasValue)
                foreach (IList list in s_players.Values)
                    foreach (var player in list)
                        if (player is Component c && c != null && !redo.Contains(player) && s_links.GetValue(player) is IList links && OdinSpeakers.Stale(Spots(links), speaker.Value))
                            redo.Add(player);
            foreach (var player in redo) s_update.Invoke(player, null);
        }

        private static void CarryVolume(string oldKey, string newKey)
        {
            if (!OdinSpeakers.CarryVolume(oldKey, newKey, oldKey != null && PlayerPrefs.HasKey(oldKey))) return;
            PlayerPrefs.SetFloat(newKey, PlayerPrefs.GetFloat(oldKey));
            PlayerPrefs.DeleteKey(oldKey);
            string unmuted = ".unmuted";
            if (PlayerPrefs.HasKey(oldKey + unmuted))
            {
                PlayerPrefs.SetFloat(newKey + unmuted, PlayerPrefs.GetFloat(oldKey + unmuted));
                PlayerPrefs.DeleteKey(oldKey + unmuted);
            }
            s_carried++;
        }

        private static List<SpeakerSpot> Spots(IList links)
        {
            var spots = new List<SpeakerSpot>(links.Count);
            foreach (var link in links)
            {
                var p = (Vector3)s_linkPosition.GetValue(link);
                spots.Add(new SpeakerSpot(s_linkGuid.GetValue(link) as string, p.x, p.y, p.z));
            }
            return spots;
        }

        private static List<SpeakerSpot> LoadedSpeakers()
        {
            var spots = new List<SpeakerSpot>(s_speakers.Count);
            foreach (var s in s_speakers)
                if (s is Component c && c != null) spots.Add(Spot(c));
            return spots;
        }

        private static SpeakerSpot Spot(Component speaker)
        {
            var p = speaker.transform.position;
            return new SpeakerSpot(s_speakerGuid.GetValue(speaker, null) as string, p.x, p.y, p.z);
        }
    }
}
