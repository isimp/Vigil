using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>A world object's id as the game keeps it: the player who made it and a number.</summary>
    internal readonly struct VehicleId : IEquatable<VehicleId>
    {
        public readonly long User;
        public readonly uint Id;

        public VehicleId(long user, uint id)
        {
            User = user;
            Id = id;
        }

        public bool Equals(VehicleId other) => User == other.User && Id == other.Id;

        public override bool Equals(object obj) => obj is VehicleId v && Equals(v);

        public override int GetHashCode()
        {
            unchecked
            {
                return (int)Id * 397 ^ User.GetHashCode();
            }
        }

        public override string ToString() => User.ToString(CultureInfo.InvariantCulture) + ":" + Id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>What a player's map needs of a cart, ship or portal: its kind, place, maker, name and, for a portal, whether it is linked.</summary>
    internal readonly struct VehicleState
    {
        public readonly int Prefab;
        public readonly float X, Y, Z;
        public readonly long Creator;
        public readonly bool Active;
        public readonly string Label;

        public VehicleState(int prefab, float x, float y, float z, long creator, bool active, string label)
        {
            Prefab = prefab;
            X = x;
            Y = y;
            Z = z;
            Creator = creator;
            Active = active;
            Label = label ?? "";
        }
    }

    /// <summary>
    /// On the server: the carts, ships and portals as the players were last told of them. Each
    /// refresh looks at every one; what is new, has moved at least <see cref="MoveMetres"/> from
    /// where the players were told it is, or has another name, maker or link is sent, and what was
    /// not seen is sent as gone.
    /// </summary>
    internal sealed class VehicleBook
    {
        public const float MoveMetres = 1f;

        private readonly Dictionary<VehicleId, VehicleState> _sent = new Dictionary<VehicleId, VehicleState>();
        private readonly Dictionary<VehicleId, VehicleState> _seen = new Dictionary<VehicleId, VehicleState>();

        public int Count => _sent.Count;

        public void Begin() => _seen.Clear();

        /// <summary>Forgets everything, for a new world or the switch going off.</summary>
        public void Clear()
        {
            _seen.Clear();
            _sent.Clear();
        }

        public void See(VehicleId id, VehicleState state) => _seen[id] = state;

        public void End(List<KeyValuePair<VehicleId, VehicleState>> changed, List<VehicleId> gone)
        {
            foreach (var pair in _seen)
            {
                if (_sent.TryGetValue(pair.Key, out var told) && Same(told, pair.Value)) continue;
                _sent[pair.Key] = pair.Value;
                changed.Add(pair);
            }
            foreach (var id in _sent.Keys)
                if (!_seen.ContainsKey(id)) gone.Add(id);
            foreach (var id in gone) _sent.Remove(id);
        }

        /// <summary>Everything as last sent, for a player who joins.</summary>
        public void All(List<KeyValuePair<VehicleId, VehicleState>> into) => into.AddRange(_sent);

        private static bool Same(VehicleState a, VehicleState b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz < MoveMetres * MoveMetres
                && a.Prefab == b.Prefab && a.Creator == b.Creator && a.Active == b.Active && a.Label == b.Label;
        }
    }

    /// <summary>How a player's game uses the server's list of carts, ships and portals.</summary>
    internal static class VehiclePinRules
    {
        /// <summary>Seconds between two refreshes the server sends.</summary>
        public const float RefreshSeconds = 5f;

        /// <summary>The server's place wins unless this game has the object loaded, where its own copy is current.</summary>
        public static bool UseServer(bool haveLocal, bool localLoaded, bool haveServer) => haveServer && !(haveLocal && localLoaded);

        /// <summary>On a whole list from the server: the pins this game has that the server does not know.</summary>
        public static void NotOnServer(IEnumerable<VehicleId> known, HashSet<VehicleId> full, List<VehicleId> drop)
        {
            foreach (var id in known)
                if (!full.Contains(id)) drop.Add(id);
        }

        /// <summary>
        /// The changed portals, which the server sends whole to every player: a player's game hears of a
        /// far portal only when it is built or the player joins, so mods reading the game's copy
        /// (TargetPortal's list and its teleport target) kept its old place and name. Carts and ships
        /// move all the time and are left to the list.
        /// </summary>
        public static void PortalsToSend(List<KeyValuePair<VehicleId, VehicleState>> changed, Func<int, bool> isPortal, List<VehicleId> into)
        {
            foreach (var pair in changed)
                if (isPortal(pair.Value.Prefab)) into.Add(pair.Key);
        }

        private const string Off = "off: the server's switch (Patches.VehiclePinsFromServer)";
        private const string Missing = "not needed: HUDCompass is not installed";

        public static string ServerLine(bool on, bool found, string problem, int objects, int lastChanges)
        {
            if (!on) return Off;
            if (!found) return Missing;
            if (problem != null) return "stepped aside: " + problem;
            return string.Format(CultureInfo.InvariantCulture, "working · {0} carts, ships and portals · {1} changes sent in the last refresh", objects, lastChanges);
        }

        public static string GameLine(bool on, bool found, string problem, bool heard, int objects, int farLastFrame)
        {
            if (!on) return Off;
            if (!found) return Missing;
            if (problem != null) return "stepped aside: " + problem;
            if (!heard) return "waiting: no list from the server yet (it needs Vigil 0.43.19 or later with the switch on)";
            return string.Format(CultureInfo.InvariantCulture, "working · {0} from the server's list · {1} far ones drawn at the server's place", objects, farLastFrame);
        }
    }
}
