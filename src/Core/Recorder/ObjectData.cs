using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// What one stored value of a world object costs when the game sends the object (<c>ZDO.Serialize</c>):
    /// its name as a 4-byte number, then its value as the game writes it (a string as its length in
    /// 7-bit groups and its UTF-8 bytes, a byte array as a 4-byte length and its bytes).
    /// </summary>
    internal static class ValueSize
    {
        public const int Hash = 4;
        public const int Float = Hash + 4, Vec3 = Hash + 12, Quat = Hash + 16, Int = Hash + 4, Long = Hash + 8;

        public static int String(string s)
        {
            int bytes = s == null ? 0 : Encoding.UTF8.GetByteCount(s);
            int length = 1;
            for (int n = bytes; n >= 0x80; n >>= 7) length++;
            return Hash + length + bytes;
        }

        public static int Bytes(byte[] b) => Hash + 4 + (b?.Length ?? 0);
    }

    /// <summary>One kind of world object in a capture: how many objects, how often sent, how big each time, and each owner's share of that.</summary>
    internal sealed class PrefabRow
    {
        public string Prefab;
        public int Objects;
        public double SendsPerSecond, BytesEach, GameEach;
        public List<KeyValuePair<string, double>> Mods = new List<KeyValuePair<string, double>>();
    }

    /// <summary>One value whose changes made objects send again: its name, whose it is, which kinds it changed on, how often.</summary>
    internal sealed class ChangeRow
    {
        public string Value, Whose, On;
        public double PerSecond;
    }

    /// <summary>A capture's result: the kinds that sent the most bytes, the values that changed the most.</summary>
    internal sealed class ObjectDataView
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public double Seconds;
        public long Sends;
        public int Kinds;
        public List<PrefabRow> Prefabs = new List<PrefabRow>();
        public List<ChangeRow> Changes = new List<ChangeRow>();

        /// <summary>"Wolf ×27".</summary>
        public static string ObjectLabel(PrefabRow r) => r.Prefab + " ×" + r.Objects.ToString(Ci);

        /// <summary>"Spawn That 26 B · Seasonality 14 B · unknown 8 B", or "none".</summary>
        public static string ModsLine(PrefabRow r) =>
            r.Mods.Count == 0 ? "none" : string.Join(" · ", r.Mods.Select(m => m.Key + " " + Math.Round(m.Value).ToString("0", Ci) + " B").ToArray());
    }

    /// <summary>
    /// Counts a capture of what this game sends: every object sent, with its size and the bytes of
    /// its values that are not the game's, by owner (a mod's name, several mods', or
    /// <see cref="Unknown"/>); and every value that changed on an object this game runs, by name and
    /// owner (<see cref="Game"/> for the game's own, position and rotation included). The game's share
    /// is what the others leave: its own values, the object's prefab, flags and rotation.
    /// </summary>
    internal sealed class ObjectDataTally
    {
        public const string Game = "the game", Unknown = "unknown";

        private sealed class Kind
        {
            public long Sends, Bytes;
            public readonly HashSet<long> Objects = new HashSet<long>();
            public readonly Dictionary<string, long> ByOwner = new Dictionary<string, long>(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, Kind> _kinds = new Dictionary<string, Kind>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, int>> _changes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        /// <summary>One object sent: its kind, its id, its size, and the bytes of its values that are not the game's, by owner.</summary>
        public void Add(string prefab, long objectId, int bytes, IEnumerable<KeyValuePair<string, int>> notTheGames)
        {
            if (!_kinds.TryGetValue(prefab, out var k)) _kinds[prefab] = k = new Kind();
            k.Sends++;
            k.Bytes += bytes;
            k.Objects.Add(objectId);
            foreach (var kv in notTheGames)
            {
                k.ByOwner.TryGetValue(kv.Key, out long b);
                k.ByOwner[kv.Key] = b + kv.Value;
            }
        }

        /// <summary>A value changed on an object of this kind.</summary>
        public void Changed(string prefab, string value, string owner)
        {
            string key = value + "\n" + owner;
            if (!_changes.TryGetValue(key, out var on)) _changes[key] = on = new Dictionary<string, int>(StringComparer.Ordinal);
            on.TryGetValue(prefab, out int n);
            on[prefab] = n + 1;
        }

        public ObjectDataView View(double seconds, int prefabs = 8, int changes = 10)
        {
            double s = seconds > 0 ? seconds : 1;
            var view = new ObjectDataView { Seconds = seconds, Sends = _kinds.Values.Sum(k => k.Sends), Kinds = _kinds.Count };
            foreach (var kv in _kinds.OrderByDescending(kv => kv.Value.Bytes).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(prefabs))
            {
                var k = kv.Value;
                var row = new PrefabRow
                {
                    Prefab = kv.Key, Objects = k.Objects.Count, SendsPerSecond = k.Sends / s, BytesEach = k.Bytes / (double)k.Sends,
                    Mods = k.ByOwner.Where(o => o.Key != Unknown).OrderByDescending(o => o.Value).ThenBy(o => o.Key, StringComparer.Ordinal)
                        .Select(o => new KeyValuePair<string, double>(o.Key, o.Value / (double)k.Sends)).ToList(),
                };
                if (k.ByOwner.TryGetValue(Unknown, out long unknown)) row.Mods.Add(new KeyValuePair<string, double>(Unknown, unknown / (double)k.Sends));
                row.GameEach = row.BytesEach - row.Mods.Sum(m => m.Value);
                view.Prefabs.Add(row);
            }
            foreach (var kv in _changes.OrderByDescending(kv => kv.Value.Values.Sum()).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(changes))
            {
                var parts = kv.Key.Split('\n');
                view.Changes.Add(new ChangeRow
                {
                    Value = parts[0], Whose = parts[1], PerSecond = kv.Value.Values.Sum() / s,
                    On = string.Join(", ", kv.Value.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(2).Select(p => p.Key).ToArray())
                        + (kv.Value.Count > 2 ? " +" + (kv.Value.Count - 2).ToString(CultureInfo.InvariantCulture) : ""),
                });
            }
            return view;
        }
    }
}
