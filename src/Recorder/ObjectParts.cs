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
    /// The inspector's parts for objects that follow rules of their own (<see cref="ObjectRules"/>):
    /// a fermenter, a beehive, a crop, a smelter or kiln, a fire, and a creature being tamed or bred,
    /// read the way the game reads them (assembly_valheim 1.0.20), private values by reflection. Also
    /// the game's 17 cover rays from any point, for the windmill, the fermenter, the beehive and shelter.
    /// Only reads.
    /// </summary>
    internal static class ObjectParts
    {
        /// <summary>The rays one reading cast, for drawing.</summary>
        internal sealed class RaySet
        {
            public Vector3 Hub;
            public Vector3[] Ends;
            public bool[] Hit;
        }

        // The game's 17 cover rays (Cover.CreateCoverRays), used as they are: the game does not normalise them.
        private static readonly Vector3[] CoverRays =
        {
            new Vector3(0f, 1f, 0f), new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f),
            new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f), new Vector3(-1f, 0f, 1f), new Vector3(1f, 0f, 1f),
            new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 1f), new Vector3(0f, 1f, -1f),
            new Vector3(-1f, 1f, -1f), new Vector3(1f, 1f, -1f), new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f),
        };

        private static int s_coverMask = -1, s_roofMask = -1, s_spaceMask = -1;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        private static readonly MethodInfo s_fermenterStatus = AccessTools.Method(typeof(Fermenter), "GetStatus");
        private static readonly MethodInfo s_fermenterContent = AccessTools.Method(typeof(Fermenter), "GetContentName");
        private static readonly MethodInfo s_fermenterTime = AccessTools.Method(typeof(Fermenter), "GetFermentationTime");
        private static readonly MethodInfo s_honeyLevel = AccessTools.Method(typeof(Beehive), "GetHoneyLevel");
        private static readonly FieldInfo s_plantSeed = AccessTools.Field(typeof(Plant), "m_seed");
        private static readonly MethodInfo s_smelterFuel = AccessTools.Method(typeof(Smelter), "GetFuel");
        private static readonly MethodInfo s_smelterQueue = AccessTools.Method(typeof(Smelter), "GetQueueSize");
        private static readonly FieldInfo s_smelterSmoke = AccessTools.Field(typeof(Smelter), "m_blockedSmoke");
        private static readonly FieldInfo s_smelterRoof = AccessTools.Field(typeof(Smelter), "m_haveRoof");
        private static readonly FieldInfo s_fireWet = AccessTools.Field(typeof(Fireplace), "m_wet");
        private static readonly FieldInfo s_fireBlocked = AccessTools.Field(typeof(Fireplace), "m_blocked");
        private static readonly FieldInfo s_fireSolidMask = AccessTools.Field(typeof(Fireplace), "m_solidRayMask");
        private static readonly MethodInfo s_tameLeft = AccessTools.Method(typeof(Tameable), "GetRemainingTime");
        private static readonly FieldInfo s_offspring = AccessTools.Field(typeof(Procreation), "m_offspringPrefab");

        /// <summary>The cover rays from <paramref name="hub"/> as the game casts them, with what each blocked one hits.</summary>
        public static List<WindCheck.Ray> CastCover(Vector3 hub, Func<Collider, string> blocker, out RaySet set)
        {
            if (s_coverMask == -1) s_coverMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            var rays = new List<WindCheck.Ray>();
            set = new RaySet { Hub = hub, Ends = new Vector3[CoverRays.Length], Hit = new bool[CoverRays.Length] };
            for (int i = 0; i < CoverRays.Length; i++)
            {
                var v = CoverRays[i];
                var start = hub + v * 0.5f;
                bool blocked = Physics.Raycast(start, v, out var hit, WindCheck.RayMetres, s_coverMask);
                set.Hit[i] = blocked;
                set.Ends[i] = blocked ? hit.point : start + v.normalized * WindCheck.RayMetres;
                rays.Add(new WindCheck.Ray
                {
                    Direction = WindCheck.Directions[i], Blocked = blocked, By = blocked ? (blocker?.Invoke(hit.collider) ?? NameOf(hit.collider)) : null,
                    Metres = blocked ? hit.distance + v.magnitude * 0.5f : WindCheck.RayMetres,
                });
            }
            return rays;
        }

        /// <summary>What a collider is, in words: terrain, the object it belongs to, else its own name.</summary>
        public static string NameOf(Collider collider)
        {
            if (collider == null) return null;
            if (collider.GetComponent<Heightmap>() != null) return "terrain";
            var view = collider.GetComponentInParent<ZNetView>();
            return view != null ? OwnershipView.DisplayName(view.gameObject) : collider.gameObject.name;
        }

        /// <summary>The parts the object's kind calls for, and the rays to draw for a fermenter or beehive (else null).</summary>
        public static List<RulesPart> Read(GameObject go, out RaySet rays)
        {
            RaySet found = null;
            var parts = new List<RulesPart>();
            rays = null;
            if (go == null) return parts;
            Try(parts, () => Fermenter(go, ref found));
            Try(parts, () => Bees(go, ref found));
            Try(parts, () => Crop(go));
            Try(parts, () => Smelter(go));
            Try(parts, () => Fire(go));
            Try(parts, () => Taming(go));
            Try(parts, () => Breeding(go));
            rays = found;
            return parts;
        }

        private static void Try(List<RulesPart> parts, Func<RulesPart> read)
        {
            try
            {
                var part = read();
                if (part != null) parts.Add(part);
            }
            catch (Exception) { }
        }

        private static RulesPart Fermenter(GameObject go, ref RaySet rays)
        {
            var f = go.GetComponentInParent<Fermenter>();
            if (f == null || f.m_roofCheckPoint == null) return null;
            var point = f.m_roofCheckPoint.position;
            var cast = CastCover(point, null, out var set);
            rays = set;
            string status = s_fermenterStatus?.Invoke(f, null)?.ToString();
            var state = status == "Ready" ? FermenterState.Ready : status == "Empty" ? FermenterState.Empty : FermenterState.Fermenting;
            string content = s_fermenterContent?.Invoke(f, null) as string;
            double elapsed = s_fermenterTime != null ? Convert.ToDouble(s_fermenterTime.Invoke(f, null), Ci) : 0;
            return ObjectRules.Fermenting(new FermenterFacts
            {
                Content = Localization.instance != null && content != null ? Localization.instance.Localize(content) : content,
                State = state, Elapsed = elapsed, Duration = f.m_fermentationDuration, Blocked = cast.Count(r => r.Blocked),
                Roof = Cover.IsUnderRoof(point), Open = WindCheck.OpenLine(cast),
            });
        }

        private static RulesPart Bees(GameObject go, ref RaySet rays)
        {
            var b = go.GetComponentInParent<Beehive>();
            if (b == null) return null;
            var zdo = b.GetComponent<ZNetView>()?.GetZDO();
            int blocked = 0;
            if (b.m_coverPoint != null)
            {
                var cast = CastCover(b.m_coverPoint.position, null, out var set);
                rays = set;
                blocked = cast.Count(r => r.Blocked);
            }
            var part = ObjectRules.Bees(new BeehiveFacts
            {
                Blocked = blocked, MaxCover = b.m_maxCover, InBiome = (Heightmap.FindBiome(b.transform.position) & b.m_biome) != 0,
                Biome = SpawnLog.BiomeName(b.m_biome.ToString()), DaylightOnly = b.m_effectOnlyInDaylight, Daylight = EnvMan.IsDaylight(),
                Honey = s_honeyLevel != null ? (int)s_honeyLevel.Invoke(b, null) : 0, MaxHoney = b.m_maxHoney, SecondsPerHoney = b.m_secPerUnit,
                Product = zdo != null ? zdo.GetFloat(ZDOVars.s_product, 0f) : 0f,
            });
            return part;
        }

        private static RulesPart Crop(GameObject go)
        {
            var plant = go.GetComponentInParent<Plant>();
            if (plant == null || ZNet.instance == null) return null;
            var zdo = plant.GetComponent<ZNetView>()?.GetZDO();
            if (zdo == null) return null;
            var pos = plant.transform.position;
            if (s_roofMask == -1) s_roofMask = LayerMask.GetMask("Default", "static_solid", "piece");
            if (s_spaceMask == -1) s_spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
            string sky = Physics.Raycast(pos, Vector3.up, out var roof, 100f, s_roofMask) ? NameOf(roof.collider) + " (" + roof.distance.ToString("0", Ci) + " m above)" : null;
            var inWay = new List<string>();
            foreach (var c in Physics.OverlapSphere(pos, plant.m_growRadius, s_spaceMask))
            {
                var other = c.GetComponent<Plant>();
                if (other == plant) continue;
                float d = Vector3.Distance(pos, c.ClosestPoint(pos));
                if (other == null) inWay.Add(NameOf(c) + " (" + d.ToString("0.0", Ci) + " m)");
                else if (other.GetStatus() == Plant.Status.Healthy) inWay.Add(OwnershipView.DisplayName(other.gameObject) + ", healthy (" + d.ToString("0.0", Ci) + " m)");
            }
            var now = ZNet.instance.GetTime();
            double since = (now - new DateTime(zdo.GetLong(ZDOVars.s_plantTime, now.Ticks))).TotalSeconds;
            return ObjectRules.Growing(new PlantFacts
            {
                Status = plant.GetStatus().ToString(), Kind = OwnershipView.DisplayName(plant.gameObject), SkyBlocker = sky, InWay = inWay.Distinct().ToList(),
                GrowSeconds = GrowTime(plant), MinSeconds = plant.m_growTime, MaxSeconds = plant.m_growTimeMax, Since = since, DestroyIfCantGrow = plant.m_destroyIfCantGrow,
            });
        }

        // As Plant.GetGrowTime: the plant's own seed picks a time between its two limits, the shared random state put back after.
        private static double GrowTime(Plant plant)
        {
            int seed = s_plantSeed != null ? (int)s_plantSeed.GetValue(plant) : 0;
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            float value = UnityEngine.Random.value;
            UnityEngine.Random.state = state;
            return Mathf.Lerp(plant.m_growTime, plant.m_growTimeMax, value);
        }

        private static RulesPart Smelter(GameObject go)
        {
            var s = go.GetComponentInParent<Smelter>();
            if (s == null) return null;
            bool smoke = s_smelterSmoke != null && (bool)s_smelterSmoke.GetValue(s);
            return ObjectRules.Smelting(new SmelterFacts
            {
                Fuel = s_smelterFuel != null ? (float)s_smelterFuel.Invoke(s, null) : 0f, MaxFuel = s.m_maxFuel,
                Queued = s_smelterQueue != null ? (int)s_smelterQueue.Invoke(s, null) : 0, MaxOre = s.m_maxOre,
                RequiresRoof = s.m_requiresRoof, HaveRoof = s_smelterRoof != null && (bool)s_smelterRoof.GetValue(s),
                SmokeBlocked = smoke, VentBlocker = smoke ? VentBlocker(s.m_smokeSpawner) : null, HasSmoke = s.m_smokeSpawner != null,
            });
        }

        // What sits in the smoke's way: the first thing the spawner's own test sphere hits.
        private static string VentBlocker(SmokeSpawner spawner)
        {
            if (spawner == null) return null;
            var p = spawner.transform.position;
            var hit = Physics.OverlapSphere(p, spawner.m_testRadius, spawner.m_testMask.value).FirstOrDefault();
            return hit == null ? null : NameOf(hit) + " (" + Vector3.Distance(p, hit.ClosestPoint(p)).ToString("0.0", Ci) + " m)";
        }

        private static RulesPart Fire(GameObject go)
        {
            var f = go.GetComponentInParent<Fireplace>();
            if (f == null || EnvMan.instance == null) return null;
            var zdo = f.GetComponent<ZNetView>()?.GetZDO();
            var pos = f.transform.position;
            string wet = null, blocked = null;
            if (s_fireWet != null && (bool)s_fireWet.GetValue(f))
            {
                Cover.GetCoverForPoint(pos + Vector3.up * f.m_coverCheckOffset, out float cover, out bool roof);
                wet = EnvMan.IsWet() && !roof ? "rain and no roof" : "strong wind and cover " + Math.Round(cover * 100).ToString("0", Ci) + " %, it needs 70 %";
            }
            if (s_fireBlocked != null && (bool)s_fireBlocked.GetValue(f))
            {
                int mask = s_fireSolidMask != null ? (int)s_fireSolidMask.GetValue(null) : 0;
                if (Heightmap.GetHeight(pos, out float height) && height > pos.y + f.m_checkTerrainOffset) blocked = "terrain above it";
                else if (mask != 0 && Physics.Raycast(pos + Vector3.up * f.m_coverCheckOffset, Vector3.up, out var hit, 0.5f, mask)) blocked = "something solid just above it: " + NameOf(hit.collider);
                else blocked = "its smoke is blocked" + (VentBlocker(f.m_smokeSpawner) is string by ? " by " + by : "");
            }
            return ObjectRules.Burning(new FireFacts
            {
                Fuel = zdo != null ? zdo.GetFloat(ZDOVars.s_fuel, 0f) : 0f, MaxFuel = f.m_maxFuel, InfiniteFuel = f.m_infiniteFuel, WetReason = wet, BlockedReason = blocked,
            });
        }

        private static RulesPart Taming(GameObject go)
        {
            var t = go.GetComponentInParent<Tameable>();
            if (t == null) return null;
            var ai = t.GetComponent<MonsterAI>();
            return ObjectRules.Taming(new TameFacts
            {
                Tamed = t.IsTamed(), Hungry = t.IsHungry(), Alerted = ai != null && ai.IsAlerted(),
                Remaining = s_tameLeft != null ? Convert.ToDouble(s_tameLeft.Invoke(t, null), Ci) : 0, Total = t.m_tamingTime,
            });
        }

        private static RulesPart Breeding(GameObject go)
        {
            var p = go.GetComponentInParent<Procreation>();
            var t = go.GetComponentInParent<Tameable>();
            var zdo = p != null ? p.GetComponent<ZNetView>()?.GetZDO() : null;
            if (p == null || zdo == null || ZNetScene.instance == null || ZNet.instance == null) return null;
            var mine = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            var young = s_offspring?.GetValue(p) as GameObject;
            var pos = p.transform.position;
            int count = (mine != null ? SpawnSystem.GetNrOfInstances(mine, pos, p.m_totalCheckRange) : 0) + (young != null ? SpawnSystem.GetNrOfInstances(young, pos, p.m_totalCheckRange) : 0);
            var partnerKind = p.m_seperatePartner != null ? p.m_seperatePartner : mine;
            int partners = partnerKind != null ? SpawnSystem.GetNrOfInstances(partnerKind, pos, p.m_partnerCheckRange, false, true) : 0;
            bool partner = p.m_noPartnerOffspring != null || (p.m_seperatePartner != null ? partners >= 1 : partners >= 2);
            long pregnant = zdo.GetLong(ZDOVars.s_pregnant, 0L);
            double left = pregnant != 0 ? p.m_pregnancyDuration - (ZNet.instance.GetTime() - new DateTime(pregnant)).TotalSeconds : 0;
            var ai = p.GetComponent<BaseAI>();
            return ObjectRules.Breeding(new BreedFacts
            {
                Tamed = t != null && t.IsTamed(), Pregnant = pregnant != 0, PregnantLeft = left, PartnerNear = partner, Hungry = t != null && t.IsHungry(),
                Alerted = ai != null && ai.IsAlerted(), Love = p.GetLovePoints(), Required = p.m_requiredLovePoints, Count = count, Max = p.m_maxCreatures, SkipShare = p.m_pregnancyChance,
            });
        }
    }
}
