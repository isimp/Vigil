using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "What could spawn here": the spot under the crosshair, read the way the game's spawn check
    /// reads a spawn point (the ground straight below it, <c>ZoneSystem.GetGroundData</c>, and the
    /// rest of <c>SpawnSystem.IsSpawnPointGood</c>), and the entries that could spawn there as the game
    /// holds them now, mods' changes included: the biome lists of the spot's zone
    /// (<c>SpawnSystem.m_spawnLists</c>), the special areas' lists of its ground, and the raid's while
    /// a raid spawns. The count about is the game's own (<c>SpawnSystem.GetNrOfZDOInstances</c> over the
    /// zone and two zones around it). <see cref="SpawnRules"/> judges each. Only reads.
    /// </summary>
    internal sealed class SpawnSpotReader
    {
        /// <summary>How far the crosshair looks for ground.</summary>
        public const float Reach = 250f;

        private static readonly string[] Layers = { "terrain", "piece", "Default", "static_solid", "Default_small", "vehicle" };
        private static readonly FieldInfo s_instances = AccessTools.Field(typeof(SpawnSystem), "m_instances");

        private readonly List<ZDO> _zdos = new List<ZDO>();
        private int _mask = -1;

        public bool Picked { get; private set; }

        public Vector3 Point { get; private set; }

        private static string s_mods;
        private static bool s_modsRead;

        /// <summary>The mods with patches on the spawn system's methods, read once a session (mods patch at load); null for none.</summary>
        public static string Mods
        {
            get
            {
                if (!s_modsRead)
                {
                    s_modsRead = true;
                    s_mods = SpawnMods();
                }

                return s_mods;
            }
        }

        /// <summary>One reading of the spot: the spot, the answers, and who runs the spawns there.</summary>
        public sealed class Reading
        {
            public SpawnSpot Spot;
            public List<SpawnAnswer> Answers = new List<SpawnAnswer>();
            public int SwitchedOff;
            public float Distance;

            /// <summary>The zone's spawn system, whose owner's game runs the biome spawns there; null when none is loaded.</summary>
            public ZNetView System;
        }

        /// <summary>Takes the ground under the crosshair, as far as <see cref="Reach"/>, as the spot to read.</summary>
        public void Pick()
        {
            Picked = false;
            var camera = GameCamera.instance;
            if (camera == null || Player.m_localPlayer == null) return;
            if (_mask == -1) _mask = LayerMask.GetMask(Layers);
            if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, Reach, _mask)) return;
            Point = hit.point;
            Picked = true;
        }

        /// <summary>The spot as the game reads it now and the answer for each entry there.</summary>
        public Reading Read()
        {
            var zones = ZoneSystem.instance;
            if (!Picked || zones == null) return null;
            var p = Point;
            zones.GetGroundData(ref p, out var normal, out var biome, out var area, out var heightmap);
            var reading = new Reading
            {
                Distance = Player.m_localPlayer != null ? Vector3.Distance(Player.m_localPlayer.transform.position, p) : 0f,
                Spot = new SpawnSpot(),
            };
            if (heightmap == null || biome == Heightmap.Biome.None) return reading;

            var spot = reading.Spot;
            spot.Biome = (int)biome;
            spot.BiomeName = SpawnLog.BiomeName(biome.ToString());
            spot.Area = (int)area;
            spot.Blocked = zones.IsBlocked(p);
            spot.Ground = p.y;
            spot.NormalY = normal.y;
            spot.FromCentre = Utils.LengthXZ(p);
            spot.InsideBase = EffectArea.IsPointInsideArea(p, EffectArea.Type.PlayerBase) != null;
            spot.InForest = WorldGenerator.InForest(p);
            spot.OnLava = zones.IsLava(p);
            spot.OceanDepth = heightmap.GetOceanDepth(p);
            spot.Day = EnvMan.IsDay();
            spot.Night = EnvMan.IsNight();
            spot.Weather = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment()?.m_name : null;
            spot.KeySet = zones.GetGlobalKey;
            spot.EventHere = PersistentEventSystem.instance != null ? PersistentEventSystem.instance.GetActiveEvent(p)?.internalName : null;
            spot.SpawningOff = SpawnSystem.m_nospawn;
            spot.LeftOut = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var alt in heightmap.m_cornerAltBiomes.Where(a => a != null))
            {
                foreach (string name in alt.m_blockSpawnNames)
                {
                    if (!spot.LeftOut.ContainsKey(name)) spot.LeftOut[name] = alt.m_name;
                }
            }

            var system = SystemAt(p);
            reading.System = system != null ? system.GetComponent<ZNetView>() : null;
            _zdos.Clear();
            ZDOMan.instance?.FindSectorObjects(ZoneSystem.GetZone(system != null ? system.transform.position : p), SimulationDistance.OriginalDistance, _zdos);

            var entries = new List<SpawnEntryFacts>();
            var lists = system != null ? system.m_spawnLists : AnySystem()?.m_spawnLists;
            if (lists != null)
            {
                foreach (var list in lists.Where(l => l != null))
                {
                    foreach (var data in list.m_spawners) Add(entries, data, SpawnSource.Biome, null, p, spot);
                }
            }

            foreach (var alt in heightmap.m_cornerAltBiomes.Where(a => a != null))
            {
                foreach (var data in alt.m_spawn) Add(entries, data, SpawnSource.SpecialArea, alt.m_name, p, spot);
            }

            var raid = RandEventSystem.instance != null ? RandEventSystem.instance.GetCurrentSpawners() : null;
            if (raid != null)
            {
                string name = RandEventSystem.instance.GetActiveEvent()?.m_name ?? "a raid";
                foreach (var data in raid) Add(entries, data, SpawnSource.Raid, name, p, spot);
            }

            reading.Answers = SpawnRules.ForSpot(entries, spot, out reading.SwitchedOff);
            return reading;
        }

        private void Add(List<SpawnEntryFacts> entries, SpawnSystem.SpawnData data, SpawnSource source, string list, Vector3 p, SpawnSpot spot)
        {
            if (data == null || data.m_prefab == null) return;
            var e = new SpawnEntryFacts
            {
                Name = string.IsNullOrEmpty(data.m_name) ? data.m_prefab.name : data.m_name,
                Prefab = data.m_prefab.name,
                Creature = SpawnWatch.Name(data.m_prefab),
                Source = source,
                List = list,
                Enabled = data.m_enabled,
                Biome = (int)data.m_biome,
                Area = (int)data.m_biomeArea,
                Chance = data.m_spawnChance,
                MinAltitude = data.m_minAltitude,
                MaxAltitude = data.m_maxAltitude,
                MinTilt = data.m_minTilt,
                MaxTilt = data.m_maxTilt,
                MinFromCentre = data.m_minDistanceFromCenter,
                MaxFromCentre = data.m_maxDistanceFromCenter,
                RadiusMin = data.m_spawnRadiusMin,
                RadiusMax = data.m_spawnRadiusMax,
                MinOceanDepth = data.m_minOceanDepth,
                MaxOceanDepth = data.m_maxOceanDepth,
                CloseToPlayer = data.m_canSpawnCloseToPlayer,
                InsideBase = data.m_insidePlayerBase,
                InForest = data.m_inForest,
                OutsideForest = data.m_outsideForest,
                InLava = data.m_inLava,
                OutsideLava = data.m_outsideLava,
                AtDay = data.m_spawnAtDay,
                AtNight = data.m_spawnAtNight,
                GlobalKey = data.m_requiredGlobalKey,
                PersistentEvent = data.m_requiredPersistentEvent,
                Weathers = data.m_requiredEnvironments,
                MaxSpawned = data.m_maxSpawned,
                SpawnDistance = data.m_spawnDistance,
            };

            // Counted only for entries the spot's biome could take, as only those are listed.
            if (e.Enabled && (e.Biome & spot.Biome) != 0)
            {
                if (e.MaxSpawned > 0) e.Nearby = SpawnSystem.GetNrOfZDOInstances(data.m_prefab, _zdos, source == SpawnSource.Raid);
                if (e.SpawnDistance > 0f) e.NearestSame = Nearest(data.m_prefab, p);
            }

            entries.Add(e);
        }

        // As SpawnSystem.HaveInstanceInRange: creatures among the AI instances, anything else among
        // the objects tagged "spawned"; by name, across the ground (x and z).
        private static float Nearest(GameObject prefab, Vector3 p)
        {
            string name = prefab.name;
            float nearest = float.PositiveInfinity;
            if (prefab.GetComponent<BaseAI>() != null)
            {
                foreach (var ai in BaseAI.BaseAIInstances)
                {
                    if (ai != null && ai.gameObject.name.StartsWith(name, StringComparison.Ordinal)) nearest = Mathf.Min(nearest, Utils.DistanceXZ(ai.transform.position, p));
                }

                return nearest;
            }

            foreach (var go in GameObject.FindGameObjectsWithTag("spawned"))
            {
                if (go.name.StartsWith(name, StringComparison.Ordinal)) nearest = Mathf.Min(nearest, Utils.DistanceXZ(go.transform.position, p));
            }

            return nearest;
        }

        private static IEnumerable<SpawnSystem> Systems() =>
            (s_instances?.GetValue(null) as IEnumerable)?.OfType<SpawnSystem>().Where(s => s != null) ?? Enumerable.Empty<SpawnSystem>();

        // The spawn system of the spot's zone, which runs the spawns around players inside that zone.
        private static SpawnSystem SystemAt(Vector3 p)
        {
            var zone = ZoneSystem.GetZone(p);
            return Systems().FirstOrDefault(s => ZoneSystem.GetZone(s.transform.position).Equals(zone));
        }

        private static SpawnSystem AnySystem() => Systems().FirstOrDefault();

        private static string SpawnMods()
        {
            try
            {
                var owners = PatchWatch.Rows(m => m.DeclaringType == typeof(SpawnSystem)).Select(r => r.Owner).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                return owners.Length == 0 ? null : string.Join(", ", owners);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
