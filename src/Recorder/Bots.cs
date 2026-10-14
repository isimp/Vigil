using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vigil.Core;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Vigil.Recorder
{
    /// <summary>
    /// Simulated players for "vigil load players", on the server. Each one is a connection the
    /// server treats as a player: it is in the player list, gets the world updates for the area
    /// around it and every routed message, through a link of a fixed rate (BotSocket). It also
    /// sends the server its own updates the way a player's game does, through the same code that
    /// takes in real players' updates (ZDOMan.RPC_ZDOData): its character 20 times a second, and
    /// a number of objects it keeps changing, as a player's game does with the creatures around
    /// it. Those objects have no type and are not kept: the world save never holds them, and the
    /// game removes them when the simulated player is disconnected.
    /// They differ from players in these ways, each a prefix that only touches simulated players.
    /// The server makes no terrain or locations for them (ZoneSystem.CreateGhostZones), so the
    /// world never grows because of a test. They take over no real objects (ZDOMan.ReleaseNearbyZDOS),
    /// so no creature or ship is left without a game running it. The permit list finds them
    /// on it (ZNet.ListContainsId, in memory only), and they are never kicked (ZNet.InternalKick). They have no character
    /// object, so raids and sleeping ignore them. They skip the join handshake, so the mods' work
    /// on a join is not part of the test.
    /// </summary>
    internal sealed class Bots
    {
        public const double CharacterHz = 20, ObjectHz = 5, WalkSpeed = 1.5, RunSpeed = 7, TeleportSeconds = 20;
        public const int CharacterBytes = 120, ObjectBytes = 40;
        private const int MarkBot = 3;
        private static readonly int s_markHash = "vigil_load".GetStableHashCode();
        private static readonly int s_dataHash = "vigil_data".GetStableHashCode();

        private sealed class Bot
        {
            public ZNetPeer Peer;
            public BotSocket Socket;
            public string Name;
            public long Uid;
            public BotPath Path;
            public Vector3 Home;
            public float Y;
            public ZDOID Character;
            public readonly List<ZDOID> Objects = new List<ZDOID>();
            public readonly List<Vector3> Offsets = new List<Vector3>();
            public uint Revision;
            public double NextCharacter, ObjectBudget, NextTeleport;
            public int NextObject, Spot;
            public readonly ArrivalTracker Arrival = new ArrivalTracker();
            public long BytesAtSecond;
        }

        private static readonly HashSet<long> s_uids = new HashSet<long>();
        private static readonly HashSet<ZNetPeer> s_peers = new HashSet<ZNetPeer>();
        private static readonly HashSet<string> s_names = new HashSet<string>();
        private static readonly AccessTools.FieldRef<ZNet, List<ZNetPeer>> s_znetPeers = AccessTools.FieldRefAccess<ZNet, List<ZNetPeer>>("m_peers");
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static readonly MethodInfo s_zdoDataMethod = AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData");
        private static readonly MethodInfo s_sendPlayerList = AccessTools.Method(typeof(ZNet), "SendPlayerList");
        private static readonly FieldInfo s_zdoPeers = AccessTools.Field(typeof(ZDOMan), "m_peers");

        public static bool IsBot(ZNetPeer p) => p != null && s_peers.Contains(p);
        public static int Live => s_peers.Count;

        private readonly List<Bot> _bots = new List<Bot>();
        private readonly string _pattern;
        private readonly int _objects;
        private readonly double _linkKbps;
        private readonly Vector3 _origin;
        private readonly SimulationDistance _sim;
        private readonly Harmony _harmony = new Harmony("isimp.Vigil.bots");
        private readonly System.Random _rng = new System.Random();
        private Action<ZDOMan, ZRpc, ZPackage> _zdoData;
        private List<Vector3> _spots = new List<Vector3>();
        private ZPackage _characterData, _objectData;
        private int _target, _serial, _updates;
        public readonly List<string> Notes = new List<string>();

        public Bots(string pattern, int objectsPerPlayer, double linkKbps, Vector3 origin, SimulationDistance sim)
        {
            _pattern = pattern; _objects = Math.Max(0, Math.Min(100, objectsPerPlayer)); _linkKbps = Math.Max(16, linkKbps); _origin = origin; _sim = sim;
        }

        public int Count => _bots.Count;

        /// <summary>Puts the safety prefixes in and prepares the updates; the reason when the simulation cannot run safely.</summary>
        public string Start()
        {
            if (ZNet.instance == null || ZDOMan.instance == null || ZRoutedRpc.instance == null) return "the world is not running";
            if (s_zdoDataMethod == null || s_znetPeers == null || s_sectors == null) return "this game version is not what the simulation was built for";
            try { _zdoData = (Action<ZDOMan, ZRpc, ZPackage>)Delegate.CreateDelegate(typeof(Action<ZDOMan, ZRpc, ZPackage>), s_zdoDataMethod); }
            catch (Exception e) { return "cannot feed the server simulated updates: " + e.GetType().Name; }
            bool ok = Patching.Apply(_harmony, AccessTools.Method(typeof(ZoneSystem), "CreateGhostZones"), "no terrain for simulated players",
                    prefix: Patching.M(typeof(Bots), nameof(GhostPrefix)))
                && Patching.Apply(_harmony, AccessTools.Method(typeof(ZDOMan), "ReleaseNearbyZDOS"), "no objects taken over by simulated players",
                    prefix: Patching.M(typeof(Bots), nameof(ReleasePrefix)))
                && Patching.Apply(_harmony, AccessTools.Method(typeof(ZNet), "ListContainsId"), "simulated players permitted",
                    prefix: Patching.M(typeof(Bots), nameof(PermitPrefix)))
                && Patching.Apply(_harmony, AccessTools.Method(typeof(ZNet), "InternalKick", new[] { typeof(ZNetPeer) }), "simulated players not kicked",
                    prefix: Patching.M(typeof(Bots), nameof(KickPrefix)));
            if (!ok)
            {
                try { _harmony.UnpatchSelf(); } catch (Exception) { }
                return "a safety patch could not be put in (" + Patching.Problems.LastOrDefault() + ")";
            }
            _characterData = Template(CharacterBytes);
            _objectData = Template(ObjectBytes);
            _spots = BusyPlaces(LoadPlan.MaxPlayers);
            if (_pattern != "stay" && _spots.Count == 0) Notes.Add("No busy places were found away from the world's center; the simulated players stayed near you.");
            return null;
        }

        /// <summary>The data part of an object update, written by the game's own code from an object that is not in the world.</summary>
        private static ZPackage Template(int bytes)
        {
            var zdo = new ZDO { m_uid = new ZDOID(-7_000_000_000L - bytes, 1) };
            zdo.SetPrefab(0);
            ZDOExtraData.Set(zdo.m_uid, s_markHash, MarkBot);
            var data = new byte[bytes];
            new System.Random(bytes).NextBytes(data);
            ZDOExtraData.Set(zdo.m_uid, s_dataHash, data);
            var pkg = new ZPackage();
            zdo.Serialize(pkg);
            try { ZDOExtraData.Release(zdo, zdo.m_uid); } catch (Exception) { }
            return pkg;
        }

        /// <summary>The places holding the most world objects (the bases), apart and away from the world's center.</summary>
        private static List<Vector3> BusyPlaces(int count)
        {
            var sectors = s_sectors(ZDOMan.instance);
            var zones = new List<BusySpots.Zone>();
            var heights = new Dictionary<long, float>();
            for (int i = 0; i < sectors.Length; i++)
            {
                var list = sectors[i];
                if (list == null || list.Count < 20) continue;
                var z = ZoneSystem.IndexToSector((uint)i);
                zones.Add(new BusySpots.Zone(z.x, z.y, list.Count));
                heights[((long)z.x << 32) ^ (uint)z.y] = list[0].GetPosition().y;
            }
            return BusySpots.Pick(zones, count, minZoneGap: 3, minMetersFromCenter: LoadAdmission.CenterGuardMeters, zoneSize: ZoneSystem.c_ZoneSize)
                .Select(z =>
                {
                    var p = ZoneSystem.GetZonePos(new Vector2s(z.X, z.Z));
                    heights.TryGetValue(((long)z.X << 32) ^ (uint)z.Z, out float y);
                    return new Vector3(p.x, y, p.z);
                }).ToList();
        }

        public void SetTarget(int count) { _target = Math.Max(0, Math.Min(LoadPlan.MaxPlayers, count)); }

        /// <summary>Each frame: adds or removes one simulated player towards the target, moves them, sends their updates.</summary>
        public void Step(double now, double dt)
        {
            var peers = s_znetPeers(ZNet.instance);
            foreach (var gone in _bots.Where(b => !peers.Contains(b.Peer)).ToList())
            {
                Notes.Add(gone.Name + " was disconnected by the game during the test.");
                Remove(gone);
            }
            if (_bots.Count < _target) Add(now);
            else if (_bots.Count > _target) Remove(_bots[_bots.Count - 1]);
            foreach (var b in _bots) Move(b, now, dt);
            foreach (var b in _bots) Upload(b, now, dt);
        }

        private Vector3 Ring(Vector3 center, float min, float max)
        {
            var dir = Random.insideUnitCircle.normalized;
            float r = Random.Range(min, max);
            return center + new Vector3(dir.x * r, 0, dir.y * r);
        }

        private Vector3 SpotFor(int i) => _spots.Count > 0 ? _spots[i % _spots.Count] : Ring(_origin, 8, 25);

        private void Add(double now)
        {
            long uid;
            do { uid = ((long)_rng.Next() << 32) | (uint)_rng.Next(); }
            while (uid == 0 || s_uids.Contains(uid) || ZNet.instance.GetPeer(uid) != null || uid == ZDOMan.GetSessionID());
            var b = new Bot { Uid = uid, Name = "Vigil player " + (++_serial) };
            b.Socket = new BotSocket(b.Name, _linkKbps);
            b.Peer = new ZNetPeer(b.Socket, server: false) { m_uid = uid, m_playerName = b.Name, m_simulationDistance = _sim };
            b.Spot = _bots.Count;
            Vector3 pos = _pattern == "spread" || _pattern == "teleport" ? Ring(SpotFor(b.Spot), 2, 6) : Ring(_origin, 8, 25);
            b.Home = pos;
            b.Y = pos.y;
            b.Path = new BotPath(pos.x, pos.z, _pattern == "travel" ? RunSpeed : WalkSpeed);
            b.Peer.m_refPos = pos;
            b.Character = new ZDOID(uid, 1);
            for (int i = 0; i < _objects; i++)
            {
                b.Objects.Add(new ZDOID(uid, (uint)(i + 2)));
                var off = Random.insideUnitCircle * 10f;
                b.Offsets.Add(new Vector3(off.x, 0, off.y));
            }
            b.NextTeleport = now + TeleportSeconds * (1 + _bots.Count / (double)Math.Max(1, _target));
            s_uids.Add(uid);
            s_names.Add(b.Name);
            s_peers.Add(b.Peer);
            s_znetPeers(ZNet.instance).Add(b.Peer);
            ZDOMan.instance.AddPeer(b.Peer);
            ZRoutedRpc.instance.AddPeer(b.Peer);
            try { s_sendPlayerList?.Invoke(ZNet.instance, null); } catch (Exception) { }
            b.BytesAtSecond = b.Socket.TotalBytes;
            b.Arrival.Arrive(now);
            _bots.Add(b);
        }

        private void Remove(Bot b)
        {
            // The game's own disconnect: it also removes the simulated player's objects.
            try { if (ZNet.instance != null && s_znetPeers(ZNet.instance).Contains(b.Peer)) ZNet.instance.Disconnect(b.Peer); } catch (Exception) { }
            s_peers.Remove(b.Peer);
            s_uids.Remove(b.Uid);
            s_names.Remove(b.Name);
            _bots.Remove(b);
        }

        private void Move(Bot b, double now, double dt)
        {
            if (b.Peer.m_rpc == null || !b.Socket.IsConnected()) return;
            if (_pattern == "teleport" && now >= b.NextTeleport && _spots.Count > 1)
            {
                b.Spot = (b.Spot + 1 + _rng.Next(_spots.Count - 1)) % _spots.Count;
                var to = Ring(_spots[b.Spot], 2, 6);
                b.Home = to;
                b.Y = to.y;
                b.Path = new BotPath(to.x, to.z, WalkSpeed);
                b.NextTeleport = now + TeleportSeconds;
                b.Arrival.Arrive(now);
            }
            if (b.Path.Step(dt))
            {
                Vector3 next = _pattern == "travel"
                    ? (_spots.Count > 0 && _rng.NextDouble() < 0.5 ? Ring(_spots[_rng.Next(Math.Min(_spots.Count, 8))], 2, 10) : Ring(_origin, 20, 300))
                    : Ring(b.Home, 0, 8);
                b.Path.Target(next.x, next.z);
            }
            b.Peer.m_refPos = new Vector3((float)b.Path.X, b.Y, (float)b.Path.Z);
        }

        private void Upload(Bot b, double now, double dt)
        {
            if (!b.Socket.IsConnected()) return;
            bool character = now >= b.NextCharacter;
            if (character) b.NextCharacter = now + 1 / CharacterHz;
            b.ObjectBudget = Math.Min(b.ObjectBudget + _objects * ObjectHz * dt, _objects);
            int objects = _objects > 0 ? (int)b.ObjectBudget : 0;
            b.ObjectBudget -= objects;
            if (!character && objects == 0) return;
            var pos = b.Peer.m_refPos;
            var pkg = new ZPackage();
            pkg.Write(0);
            if (character) Entry(pkg, b, b.Character, pos, _characterData);
            for (int i = 0; i < objects; i++)
            {
                int k = b.NextObject++ % b.Objects.Count;
                Entry(pkg, b, b.Objects[k], pos + b.Offsets[k], _objectData);
            }
            pkg.Write(ZDOID.None);
            pkg.SetPos(0);
            _zdoData(ZDOMan.instance, b.Peer.m_rpc, pkg);
            _updates += (character ? 1 : 0) + objects;
        }

        private static void Entry(ZPackage pkg, Bot b, ZDOID id, Vector3 pos, ZPackage data)
        {
            pkg.Write(id);
            pkg.Write((ushort)1);
            pkg.Write(++b.Revision);
            pkg.Write(b.Uid);
            pkg.Write(pos);
            pkg.Write(data);
        }

        /// <summary>The second's numbers for all simulated players together; finished arrivals go into <paramref name="arrivals"/>.</summary>
        public Dictionary<string, double> Second(double now, double span, List<double> arrivals)
        {
            var known = KnownObjects();
            long down = 0, queued = 0, objects = 0;
            foreach (var b in _bots)
            {
                long bytes = b.Socket.TotalBytes - b.BytesAtSecond;
                b.BytesAtSecond = b.Socket.TotalBytes;
                down += bytes;
                queued += b.Socket.Queued;
                if (known.TryGetValue(b.Peer, out int k)) objects += k;
                var done = b.Arrival.Second(now, bytes / 1024.0 / span, b.Socket.Queued / 1024.0, _linkKbps);
                if (done.HasValue) arrivals.Add(done.Value);
            }
            int n = _bots.Count;
            var d = new Dictionary<string, double>
            {
                [LoadMetrics.Bots] = n,
                [LoadMetrics.BotDown] = down / 1024.0 / span,
                [LoadMetrics.BotQueue] = n > 0 ? queued / 1024.0 / n : double.NaN,
                [LoadMetrics.BotKnown] = n > 0 ? (double)objects / n : double.NaN,
                [LoadMetrics.BotUpdates] = _updates / span,
            };
            _updates = 0;
            return d;
        }

        private static Dictionary<ZNetPeer, int> KnownObjects()
        {
            var d = new Dictionary<ZNetPeer, int>();
            try
            {
                if (ZDOMan.instance == null || !(s_zdoPeers?.GetValue(ZDOMan.instance) is IList peers)) return d;
                foreach (var zp in peers)
                {
                    var t = zp.GetType();
                    if (t.GetField("m_peer")?.GetValue(zp) is ZNetPeer np && s_peers.Contains(np) && t.GetField("m_zdos")?.GetValue(zp) is ICollection zdos)
                        d[np] = zdos.Count;
                }
            }
            catch (Exception) { }
            return d;
        }

        /// <summary>Disconnects every simulated player and takes the safety prefixes out again.</summary>
        public void Stop()
        {
            foreach (var b in _bots.ToList()) Remove(b);
            // The game wrote each simulated player into the world's player history when it joined.
            try { RecorderModule.RemoveBotHistory(); } catch (Exception) { }
            _target = 0;
            try { _harmony.UnpatchSelf(); } catch (Exception) { }
        }

        // ---- the three differences from a real player (prefixes; true lets the game's code run)

        private static bool GhostPrefix(Vector3 refPoint)
        {
            foreach (var p in s_peers) if (p.m_refPos == refPoint) return false;
            return true;
        }

        private static bool ReleasePrefix(long uid) => !s_uids.Contains(uid);

        // The permit list check (every few seconds, for every player) finds simulated players on
        // the list, so it neither kicks them nor logs that it would. Only the permit list: the
        // admin and ban lists still answer as they would. Nothing is written to permittedlist.txt.
        private static readonly AccessTools.FieldRef<ZNet, SyncedList> s_permitted = AccessTools.FieldRefAccess<ZNet, SyncedList>("m_permittedList");

        private static bool PermitPrefix(ZNet __instance, SyncedList list, string idString, ref bool __result)
        {
            if (idString == null || !s_names.Contains(idString) || list == null || list != s_permitted(__instance)) return true;
            __result = true;
            return false;
        }

        // The last line of defence: a simulated player is never kicked.
        private static bool KickPrefix(ZNetPeer peer) => !s_peers.Contains(peer);
    }
}
