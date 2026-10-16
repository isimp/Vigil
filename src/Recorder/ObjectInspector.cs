using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Reads one world object for the inspector, and only reads: what it is and the mod its prefab
    /// comes from, whose game runs it, how the game keeps it, its size as the game sends it, every
    /// value it stores (the game keeps them per object id in <c>ZDOExtraData</c>), the scripts on it
    /// by mod, what its kind adds, and the game's own record of it (<see cref="ObjectRecord"/>). It
    /// also samples the object's change counters once a second, for how often it changed over the
    /// last 10 s. The mod is read once per object picked (it lists the mods' files and asset
    /// bundles); the rest whenever asked. The values' names come from <see cref="ValueNames"/>, read
    /// once in the background from the game's and the mods' assembly files (<see cref="ReadNames"/>);
    /// until they are in, only the game's own list of names is used.
    /// </summary>
    internal sealed class ObjectInspector
    {
        public const double WatchSeconds = 10;
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static ValueNames s_fieldNames;
        private static volatile ValueNames s_names;
        private static volatile bool s_reading;
        private static volatile string s_namesNote = "not read yet";

        private static readonly MethodInfo s_support = AccessTools.Method(typeof(WearNTear), "GetSupport");
        private static readonly MethodInfo s_maxSupport = AccessTools.Method(typeof(WearNTear), "GetMaxSupport");
        private static readonly FieldInfo s_haveRoof = AccessTools.Field(typeof(WearNTear), "m_haveRoof");

        private readonly List<(double At, uint Data, ushort Owner)> _samples = new List<(double At, uint Data, ushort Owner)>();
        private bool _modRead;
        private string _mod;

        /// <summary>What one reading found.</summary>
        internal sealed class Facts
        {
            public string Name, Prefab, Mod, RunBy, Kept;
            public int SizeBytes;
            public float Distance;
            public List<KeyValuePair<string, string>> Extras = new List<KeyValuePair<string, string>>();
            public List<StoredValue.Row> Values = new List<StoredValue.Row>();
            public List<ScriptsByMod.Entry> Scripts = new List<ScriptsByMod.Entry>();
            public List<KeyValuePair<string, string>> Record = new List<KeyValuePair<string, string>>();

            /// <summary>For a windmill: its wind, else null.</summary>
            public WindFacts Wind;

            /// <summary>The parts of objects with rules of their own (<see cref="ObjectParts"/>), and the rays a fermenter or beehive cast.</summary>
            public List<RulesPart> Parts = new List<RulesPart>();
            public ObjectParts.RaySet PartRays;
        }

        /// <summary>A windmill's wind as one reading found it, and its rays for drawing.</summary>
        internal sealed class WindFacts
        {
            public string Output, Cover, Open;
            public List<string[]> Blocked;
            public Vector3 Hub;
            public Vector3[] Ends;
            public bool[] Hit;
        }

        internal static WindFacts WindOf(GameObject go)
        {
            var mill = go != null ? go.GetComponentInParent<Windmill>() : null;
            if (mill == null || mill.m_propeller == null || EnvMan.instance == null) return null;
            var rays = ObjectParts.CastCover(mill.m_propeller.position, c => c.GetComponentInParent<Windmill>() == mill ? "the windmill itself" : null, out var set);
            var smelter = mill.GetComponent<Smelter>();
            return new WindFacts
            {
                Output = WindCheck.OutputLine(EnvMan.instance.GetWindIntensity(), rays.Count(r => r.Blocked), smelter != null ? smelter.m_secPerProduct : 0f),
                Cover = WindCheck.CoverLine(rays), Open = WindCheck.OpenLine(rays), Blocked = WindCheck.BlockedRows(rays), Hub = set.Hub, Ends = set.Ends, Hit = set.Hit,
            };
        }


        public ZDOID Id { get; private set; } = ZDOID.None;

        public bool Picked => Id != ZDOID.None;

        /// <summary>The object picked, or nothing when <paramref name="view"/> is null; the watch starts over.</summary>
        public void Pick(ZNetView view, double now)
        {
            Id = view != null && view.IsValid() ? view.GetZDO().m_uid : ZDOID.None;
            _samples.Clear();
            _modRead = false;
            _mod = null;
            Sample(now);
        }

        /// <summary>Notes the object's change counters; samples older than the watch fall away.</summary>
        public void Sample(double now)
        {
            var zdo = Picked && ZDOMan.instance != null ? ZDOMan.instance.GetZDO(Id) : null;
            if (zdo == null) return;
            _samples.Add((now, zdo.DataRevision, zdo.OwnerRevision));
            while (_samples.Count > 2 && now - _samples[1].At >= WatchSeconds) _samples.RemoveAt(0);
        }

        /// <summary>How often it changed between the oldest and newest sample, with what sending it takes at most.</summary>
        public ChangeRate Rate(int sizeBytes, out double seconds, out int ownerChanges)
        {
            seconds = 0;
            ownerChanges = 0;
            if (_samples.Count < 2) return ChangeRate.Of(0, 0, 0, sizeBytes);
            var first = _samples[0];
            var last = _samples[_samples.Count - 1];
            seconds = last.At - first.At;
            ownerChanges = Math.Max(0, last.Owner - first.Owner);
            return ChangeRate.Of(first.Data, last.Data, seconds, sizeBytes);
        }

        /// <summary>Reads the picked object; null when it is gone (unloaded or destroyed).</summary>
        public Facts Read(OwnershipView owners, Func<OwnerRole, string, double> pingOf, double pingLimitMs)
        {
            var zdo = Picked && ZDOMan.instance != null ? ZDOMan.instance.GetZDO(Id) : null;
            var go = Picked && ZNetScene.instance != null ? ZNetScene.instance.FindInstance(Id) : null;
            if (zdo == null || go == null) return null;
            var facts = new Facts
            {
                Name = OwnershipView.DisplayName(go),
                Prefab = Utils.GetPrefabName(go),
                Kept = ObjectKept.Describe(zdo.Persistent, zdo.Distant, zdo.Type.ToString()),
                SizeBytes = SizeOf(zdo),
                Distance = Player.m_localPlayer != null ? Vector3.Distance(Player.m_localPlayer.transform.position, go.transform.position) : float.NaN,
            };
            if (!_modRead)
            {
                _modRead = true;
                try { _mod = ModAssets.Index(out _).OwnerOf(facts.Prefab); } catch (Exception) { _mod = null; }
            }
            facts.Mod = _mod;
            var view = go.GetComponent<ZNetView>();
            facts.RunBy = view != null && view.IsValid() ? OwnershipLook.OwnerLine(view, owners, pingOf, pingLimitMs) : "";
            facts.Values = StoredValue.Order(Values(Id));
            facts.Record = Record(zdo);
            facts.Scripts = Scripts(go);
            Extras(go, zdo, facts.Extras);
            try { facts.Wind = WindOf(go); } catch (Exception) { facts.Wind = null; }
            try { facts.Parts = ObjectParts.Read(go, out facts.PartRays); } catch (Exception) { facts.Parts = new List<RulesPart>(); }
            return facts;
        }

        /// <summary>The object's size as the game sends it: everything it serialises.</summary>
        public static int SizeOf(ZDO zdo)
        {
            var package = new ZPackage();
            zdo.Serialize(package);
            return package.Size();
        }

        /// <summary>The names read from the game and the mods; null until a reading has ended.</summary>
        public static ValueNames Loaded => s_names;

        /// <summary>Whether the names from the mods are being read now.</summary>
        public static bool NamesReading => s_reading;

        /// <summary>How the last reading of the names went, in words.</summary>
        public static string NamesNote => s_namesNote;

        /// <summary>
        /// Reads the names again in the background: the game's list of names, then every text in the
        /// game's assemblies and in each mod's (its plugin assembly, Vigil's own included), hashed the
        /// game's way. The files are read, never loaded. Until it ends the names read before stay.
        /// </summary>
        public static void ReadNames()
        {
            if (s_reading) return;
            var fields = Fields();
            var files = new List<KeyValuePair<string, string>>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = a.GetName().Name;
                if (name.StartsWith("assembly_", StringComparison.Ordinal)) Add(files, ValueNames.Game, a);
            }
            foreach (var kv in PhaseTimer.ModAssemblies()) Add(files, kv.Value, kv.Key);
            Add(files, "Vigil", typeof(ObjectInspector).Assembly);

            s_reading = true;
            s_namesNote = "reading";
            var thread = new Thread(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int unreadable = 0;
                try
                {
                    var texts = new List<KeyValuePair<string, IEnumerable<string>>>();
                    foreach (var f in files)
                    {
                        List<string> read = null;
                        try { using (var stream = File.OpenRead(f.Value)) read = AssemblyTexts.Read(stream); } catch (Exception) { }
                        if (read == null) unreadable++;
                        else texts.Add(new KeyValuePair<string, IEnumerable<string>>(f.Key, read));
                    }
                    var names = ValueNames.Build(fields, texts);
                    s_names = names;
                    int mods = files.Select(f => f.Key).Where(k => k != ValueNames.Game).Distinct().Count();
                    s_namesNote = string.Format(Ci, "{0:N0} names from the game and {1} mods, read in {2:0.0} s{3}", names.Count, mods, watch.Elapsed.TotalSeconds,
                        unreadable > 0 ? "; " + unreadable + " files could not be read" : "");
                }
                catch (Exception e) { s_namesNote = "reading the names failed: " + e.GetType().Name; }
                finally { s_reading = false; }
            }) { IsBackground = true, Name = "Vigil value names" };
            thread.Start();
        }

        private static void Add(List<KeyValuePair<string, string>> files, string source, Assembly assembly)
        {
            string path;
            try { path = assembly.Location; } catch (Exception) { return; }
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) files.Add(new KeyValuePair<string, string>(source, path));
        }

        private static List<KeyValuePair<string, int>> Fields() =>
            typeof(ZDOVars).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(int))
                .Select(f => new KeyValuePair<string, int>(f.Name, (int)f.GetValue(null)))
                .ToList();

        /// <summary>The names to use now: the full ones once read (the first call starts the reading), else the game's list alone.</summary>
        private static ValueNames Names()
        {
            var names = s_names;
            if (names != null) return names;
            if (!s_reading && s_namesNote == "not read yet") ReadNames();
            return s_fieldNames ??= ValueNames.Build(Fields(), new KeyValuePair<string, IEnumerable<string>>[0]);
        }

        private static readonly FieldInfo s_connections = AccessTools.Field(typeof(ZDOExtraData), "s_connections");

        /// <summary>Where a creature came from: the spawn log's record, else what the game keeps about it (<see cref="SpawnOrigin"/>).</summary>
        internal static string Origin(GameObject go, ZDO zdo, Character character)
        {
            var facts = new SpawnOrigin.Facts
            {
                Record = SpawnWatch.Log.Find(zdo.m_uid.ToString()),
                Now = Time.realtimeSinceStartupAsDouble,
                EventCreature = zdo.GetBool(ZDOVars.s_eventCreature),
                DespawnInDay = zdo.GetBool(ZDOVars.s_despawnInDay),
                Tamed = character.IsTamed(),
                Offspring = go.GetComponent<Growup>() != null,
            };
            if (facts.Record == null && !facts.EventCreature)
            {
                var spawner = SpawnerOf(zdo.m_uid);
                if (spawner != ZDOID.None)
                {
                    facts.LinkedToSpawner = true;
                    var spawnerZdo = ZDOMan.instance.GetZDO(spawner);
                    var location = spawnerZdo != null ? Location.GetLocation(spawnerZdo.GetPosition()) : null;
                    facts.SpawnerLocation = location != null ? Utils.GetPrefabName(location.gameObject) : null;
                }
            }
            return SpawnOrigin.Describe(facts);
        }

        /// <summary>The fixed spawner that made this creature: the one whose spawn link points at it; none when no loaded one does.</summary>
        private static ZDOID SpawnerOf(ZDOID creature)
        {
            if (!(s_connections?.GetValue(null) is Dictionary<ZDOID, ZDOConnection> all)) return ZDOID.None;
            foreach (var kv in all)
            {
                if (kv.Value != null && kv.Value.m_type == ZDOExtraData.ConnectionType.Spawned && kv.Value.m_target == creature) return kv.Key;
            }
            return ZDOID.None;
        }

        /// <summary>The game's own record of the object: id, prefab number, owner, change counters, place and zone, turn, flags, link.</summary>
        private static List<KeyValuePair<string, string>> Record(ZDO zdo)
        {
            var pos = zdo.GetPosition();
            var zone = zdo.GetSector();
            var turn = zdo.GetRotation().eulerAngles;
            long owner = zdo.GetOwner();
            var link = zdo.GetConnectionType();
            byte linkBits = (byte)link;
            return ObjectRecord.Rows(new ObjectRecord.Input
            {
                Id = zdo.m_uid.ToString(),
                Prefab = zdo.GetPrefab(),
                Owner = owner,
                OwnerName = owner == 0 ? null : OwnershipCount.Name(owner, OwnershipView.PlayerNames(), ZNet.GetUID(), OwnershipView.ServerUid()),
                DataRevision = zdo.DataRevision,
                OwnerRevision = zdo.OwnerRevision,
                X = pos.x, Y = pos.y, Z = pos.z,
                ZoneX = zone.x, ZoneY = zone.y,
                Turn = new[] { turn.x, turn.y, turn.z },
                Persistent = zdo.Persistent,
                Distant = zdo.Distant,
                Type = zdo.Type.ToString(),
                LinkKind = link == ZDOExtraData.ConnectionType.None ? null
                    : ((ZDOExtraData.ConnectionType)(linkBits & 0x0F)).ToString() + ((linkBits & 0x10) != 0 ? ", target" : ""),
                LinkTo = link == ZDOExtraData.ConnectionType.None ? null : zdo.GetConnectionZDOID(link).ToString(),
            });
        }

        private static List<StoredValue.Row> Values(ZDOID id)
        {
            var rows = new List<StoredValue.Row>();
            Read<float>("s_floats", id, "float", v => v, rows);
            Read<int>("s_ints", id, "int", v => v, rows);
            Read<long>("s_longs", id, "long", v => v, rows);
            Read<string>("s_strings", id, "text", v => v, rows);
            Read<Vector3>("s_vec3", id, "position", v => new[] { v.x, v.y, v.z }, rows);
            Read<Quaternion>("s_quats", id, "rotation", v => new[] { v.eulerAngles.x, v.eulerAngles.y, v.eulerAngles.z }, rows);
            Read<byte[]>("s_byteArrays", id, "data", v => v, rows);
            return rows;
        }

        private static void Read<T>(string field, ZDOID id, string type, Func<T, object> shown, List<StoredValue.Row> rows)
        {
            if (!(typeof(ZDOExtraData).GetField(field, Hidden)?.GetValue(null) is Dictionary<ZDOID, BinarySearchDictionary<int, T>> all)) return;
            if (!all.TryGetValue(id, out var values) || values == null) return;
            var names = Names();
            foreach (var kv in values)
            {
                var name = names.Of(kv.Key);
                rows.Add(new StoredValue.Row(name.Text, name.NamedIn, type, StoredValue.Format(shown(kv.Value))));
            }
        }

        private static List<ScriptsByMod.Entry> Scripts(GameObject go)
        {
            var mods = PhaseTimer.ModAssemblies();
            var scripts = go.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(b => b != null)
                .Select(b => new KeyValuePair<string, string>(b.GetType().Name, mods.TryGetValue(b.GetType().Assembly, out string mod) ? mod : null));
            return ScriptsByMod.Group(scripts);
        }

        /// <summary>What the object's kind adds: a creature's health, stars and mind, a piece's health, support and builder, an item's stack, a container's load.</summary>
        private static void Extras(GameObject go, ZDO zdo, List<KeyValuePair<string, string>> extras)
        {
            void Add(string label, string value) => extras.Add(new KeyValuePair<string, string>(label, value));

            var character = go.GetComponent<Character>();
            if (character != null && !character.IsPlayer())
            {
                Add("Came from", Origin(go, zdo, character));
            }
            if (character != null)
            {
                Add("Health", string.Format(Ci, "{0:0.#} of {1:0.#}", character.GetHealth(), character.GetMaxHealth()));
                Add("Stars", Math.Max(0, character.GetLevel() - 1).ToString(Ci));
                Add("Faction", character.m_faction.ToString());
                Add("Tamed", character.IsTamed() ? "yes" : "no");
                var ai = character.GetBaseAI();
                if (ai != null)
                {
                    var target = ai.GetTargetCreature();
                    Add("Mind", string.Join(", ", new[]
                    {
                        ai.IsSleeping() ? "asleep" : "awake",
                        ai.IsAlerted() ? "alerted" : "calm",
                        ai.IsAggravated() ? "aggravated" : null,
                    }.Where(s => s != null).ToArray()));
                    Add("Target", target != null ? OwnershipView.DisplayName(target.gameObject) : "none");
                }
            }

            var wear = go.GetComponent<WearNTear>();
            if (wear != null)
            {
                Add("Health", string.Format(Ci, "{0:0}%", wear.GetHealthPercentage() * 100f));
                try
                {
                    float support = (float)s_support.Invoke(wear, null), max = (float)s_maxSupport.Invoke(wear, null);
                    Add("Support", string.Format(Ci, "{0:0} of {1:0}", support, max));
                }
                catch (Exception) { }
                try { Add("Roof", (bool)s_haveRoof.GetValue(wear) ? "under a roof" : "open sky"); } catch (Exception) { }
            }

            var piece = go.GetComponent<Piece>();
            if (piece != null)
            {
                long creator = piece.GetCreator();
                var builder = creator == 0 ? null : Player.GetAllPlayers().FirstOrDefault(p => p != null && p.GetPlayerID() == creator);
                Add("Built by", creator == 0 ? "the world (no player)" : builder != null ? builder.GetPlayerName() : "player " + creator.ToString(Ci) + " (not near)");
            }

            var item = go.GetComponent<ItemDrop>();
            if (item != null && item.m_itemData != null)
            {
                Add("Stack", item.m_itemData.m_stack.ToString(Ci));
                Add("Quality", item.m_itemData.m_quality.ToString(Ci));
                if (item.m_itemData.m_shared != null && item.m_itemData.m_shared.m_useDurability)
                    Add("Durability", string.Format(Ci, "{0:0} of {1:0}", item.m_itemData.m_durability, item.m_itemData.GetMaxDurability()));
                long dropped = zdo.GetLong(ZDOVars.s_spawnTime, 0L);
                if (dropped != 0 && ZNet.instance != null) Add("Dropped", ObjectAge.Describe(ZNet.instance.GetTime() - new DateTime(dropped)) + " (world time)");
            }

            var container = go.GetComponent<Container>();
            if (container != null && container.GetInventory() != null)
            {
                var inv = container.GetInventory();
                Add("Holds", string.Format(Ci, "{0} items in {1} slots", inv.NrOfItems(), inv.GetWidth() * inv.GetHeight()));
                Add("In use", container.IsInUse() ? "yes, by someone" : "no");
            }
        }
    }
}
