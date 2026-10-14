using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Watches the spawns this game makes and keeps them in <see cref="Log"/>. Biome, special-area and
    /// raid spawns go through <c>SpawnSystem.Spawn</c>, spawner structures (nests) through
    /// <c>SpawnArea.SpawnOne</c>, fixed spawners (in locations) through <c>CreatureSpawner.Spawn</c>.
    /// A prefix placed after every other mod's notes what is about to spawn, or, when an earlier
    /// prefix already stopped it (<c>__runOriginal</c>), a cancelled spawn with the mods whose prefixes
    /// can do that; the creature's object is taken from the first <c>ZNetView.Awake</c> inside the
    /// call, and the postfix, after every other mod's, reads its level and place. Biome spawns run
    /// on the game of whoever's area it is, so only this game's areas are seen. Only reads.
    /// </summary>
    internal static class SpawnWatch
    {
        private const string HarmonyId = "isimp.Vigil.spawns";
        private static readonly FieldInfo s_heightmap = AccessTools.Field(typeof(SpawnSystem), "m_heightmap");
        private static readonly MethodInfo s_biomeSpawn = AccessTools.Method(typeof(SpawnSystem), "Spawn");
        private static readonly MethodInfo s_structureSpawn = AccessTools.Method(typeof(SpawnArea), "SpawnOne");
        private static readonly MethodInfo s_fixedSpawn = AccessTools.Method(typeof(CreatureSpawner), "Spawn");
        private static readonly MethodInfo s_viewAwake = AccessTools.Method(typeof(ZNetView), "Awake");
        private static readonly Dictionary<MethodBase, string> s_stoppers = new Dictionary<MethodBase, string>();

        private static SpawnRecord s_pending;
        private static ZNetView s_pendingView;

        public static SpawnLog Log { get; } = new SpawnLog();

        /// <summary>Why the hooks are not in; null once they are.</summary>
        public static string HookProblem { get; private set; } = "not put in yet";

        /// <summary>Called on the main thread for each spawn that appeared (not for cancelled ones).</summary>
        public static Action<SpawnRecord> Spawned;

        private static double Now => Time.realtimeSinceStartupAsDouble;

        public static void Hook()
        {
            try
            {
                if (s_biomeSpawn == null || s_structureSpawn == null || s_fixedSpawn == null || s_viewAwake == null)
                {
                    HookProblem = "a spawn method was not found";
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                var after = new HarmonyMethod(typeof(SpawnWatch), nameof(AfterSpawn)) { priority = Priority.Last };
                harmony.Patch(s_biomeSpawn, prefix: new HarmonyMethod(typeof(SpawnWatch), nameof(BeforeBiomeSpawn)) { priority = Priority.Last }, postfix: after);
                harmony.Patch(s_structureSpawn, prefix: new HarmonyMethod(typeof(SpawnWatch), nameof(BeforeStructureSpawn)) { priority = Priority.Last }, postfix: after);
                harmony.Patch(s_fixedSpawn, postfix: new HarmonyMethod(typeof(SpawnWatch), nameof(AfterFixedSpawn)) { priority = Priority.Last });
                harmony.Patch(s_viewAwake, postfix: new HarmonyMethod(typeof(SpawnWatch), nameof(AfterViewAwake)));
                HookProblem = null;
            }
            catch (Exception e)
            {
                HookProblem = e.GetType().Name + " " + e.Message;
            }
        }

        private static void BeforeBiomeSpawn(SpawnSystem __instance, SpawnSystem.SpawnData critter, Vector3 spawnPoint, bool eventSpawner, bool __runOriginal)
        {
            try
            {
                if (critter == null || critter.m_prefab == null) return;
                var record = new SpawnRecord
                {
                    At = Now,
                    Creature = Name(critter.m_prefab),
                    Entry = string.IsNullOrEmpty(critter.m_name) ? critter.m_prefab.name : critter.m_name,
                    NightOnly = !critter.m_spawnAtDay,
                    AllowedInBase = critter.m_insidePlayerBase,
                    X = spawnPoint.x, Y = spawnPoint.y, Z = spawnPoint.z,
                };
                if (eventSpawner)
                {
                    record.Source = SpawnSource.Raid;
                    record.Detail = RandEventSystem.instance?.GetActiveEvent()?.m_name ?? "a raid";
                }
                else
                {
                    var alt = (s_heightmap?.GetValue(__instance) as Heightmap)?.m_cornerAltBiomes?.FirstOrDefault(a => a != null && a.m_spawn != null && a.m_spawn.Contains(critter));
                    record.Source = alt != null ? SpawnSource.SpecialArea : SpawnSource.Biome;
                    record.Detail = alt != null ? alt.m_name : SpawnLog.BiomeName(WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(spawnPoint).ToString() : "?");
                }
                Begin(record, s_biomeSpawn, __runOriginal);
            }
            catch (Exception) { s_pending = null; }
        }

        private static void BeforeStructureSpawn(SpawnArea __instance, bool __runOriginal)
        {
            try
            {
                var p = __instance.transform.position;
                var record = new SpawnRecord
                {
                    At = Now,
                    Source = SpawnSource.Structure,
                    Detail = OwnershipView.DisplayName(__instance.gameObject),
                    Creature = "a creature",
                    X = p.x, Y = p.y, Z = p.z,
                };
                Begin(record, s_structureSpawn, __runOriginal);
            }
            catch (Exception) { s_pending = null; }
        }

        // Notes the spawn about to happen, or keeps it as cancelled when an earlier prefix stopped it.
        private static void Begin(SpawnRecord record, MethodBase method, bool runs)
        {
            s_pendingView = null;
            if (runs)
            {
                s_pending = record;
                return;
            }

            s_pending = null;
            record.Cancelled = true;
            record.CancelledBy = Stoppers(method);
            Log.Add(record);
        }

        private static void AfterViewAwake(ZNetView __instance)
        {
            if (s_pending == null || s_pendingView != null) return;
            if (__instance != null && __instance.GetZDO() != null) s_pendingView = __instance;
        }

        private static void AfterSpawn()
        {
            var record = s_pending;
            var view = s_pendingView;
            s_pending = null;
            s_pendingView = null;
            if (record == null || view == null) return;
            try { Finish(record, view); } catch (Exception) { }
        }

        private static void AfterFixedSpawn(CreatureSpawner __instance, ZNetView __result)
        {
            if (__result == null) return;
            try
            {
                var location = Location.GetLocation(__instance.transform.position);
                Finish(new SpawnRecord
                {
                    At = Now,
                    Source = SpawnSource.FixedSpawner,
                    Detail = location != null ? Utils.GetPrefabName(location.gameObject) : null,
                    AllowedInBase = __instance.m_spawnInPlayerBase,
                    NightOnly = !__instance.m_spawnAtDay,
                }, __result);
            }
            catch (Exception) { }
        }

        // The creature appeared: its id, name, level, place, distance from the player and base.
        private static void Finish(SpawnRecord record, ZNetView view)
        {
            if (view == null || !view.IsValid()) return;
            var go = view.gameObject;
            var p = go.transform.position;
            record.Id = view.GetZDO().m_uid.ToString();
            record.Creature = OwnershipView.DisplayName(go);
            record.Level = go.GetComponent<Character>()?.GetLevel() ?? 1;
            record.X = p.x;
            record.Y = p.y;
            record.Z = p.z;
            record.Distance = Player.m_localPlayer != null ? Vector3.Distance(Player.m_localPlayer.transform.position, p) : 0f;
            var area = EffectArea.IsPointInsideArea(p, EffectArea.Type.PlayerBase);
            record.InsideBase = area != null;
            if (area != null) NameBase(record, area);
            Log.Add(record);
            Spawned?.Invoke(record);
        }

        // The base a spawn appeared in: the piece whose base area it is, its builder (named when that
        // player is loaded here), and where the piece stands.
        private static void NameBase(SpawnRecord record, EffectArea area)
        {
            var piece = area.GetComponentInParent<Piece>();
            if (piece == null) return;
            var at = piece.transform.position;
            record.BasePiece = Localization.instance != null ? Localization.instance.Localize(piece.m_name) : piece.m_name;
            record.BaseBuilderId = piece.GetCreator();
            record.BaseX = at.x;
            record.BaseZ = at.z;
            if (record.BaseBuilderId == 0) return;
            foreach (var player in Player.GetAllPlayers())
            {
                if (player != null && player.GetPlayerID() == record.BaseBuilderId) record.BaseBuilder = player.GetPlayerName();
            }
        }

        internal static string Name(GameObject prefab)
        {
            var character = prefab.GetComponent<Character>();
            if (character != null && !string.IsNullOrEmpty(character.m_name) && Localization.instance != null) return Localization.instance.Localize(character.m_name);
            return prefab.name;
        }

        /// <summary>The mods whose prefixes on the method can stop it, other than Vigil; read once per method.</summary>
        private static string Stoppers(MethodBase method)
        {
            if (s_stoppers.TryGetValue(method, out string mods)) return mods;
            try
            {
                var names = PatchWatch.Rows(m => m == method).Where(r => r.Kind == "prefix" && r.CanSkip).Select(r => r.Owner).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                mods = names.Length == 0 ? null : string.Join(", ", names);
            }
            catch (Exception) { mods = null; }
            s_stoppers[method] = mods;
            return mods;
        }
    }
}
