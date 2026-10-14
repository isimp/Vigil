using System;
using System.Collections.Generic;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>A networked world object as the cleanup sees it: its id, prefab hash and position.</summary>
    internal sealed class WorldObject
    {
        public WorldObject(long id, int hash, float x, float y, float z) { Id = id; Hash = hash; X = x; Y = y; Z = z; }

        public long Id { get; }
        public int Hash { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        /// <summary>Distance from the point given to <see cref="Orphans.Select"/>.</summary>
        public float Distance { get; internal set; }
    }

    /// <summary>Picks the objects whose prefab no installed mod provides.</summary>
    internal static class Orphans
    {
        /// <summary>
        /// Objects within <paramref name="radius"/> of the point whose prefab does not exist,
        /// nearest first; with <paramref name="onlyHash"/>, only objects of that prefab.
        /// </summary>
        public static List<WorldObject> Select(IEnumerable<WorldObject> objects, Func<int, bool> prefabExists,
            float x, float y, float z, float radius, int? onlyHash)
        {
            var picked = new List<WorldObject>();
            foreach (var o in objects)
            {
                if (prefabExists(o.Hash)) continue;
                if (onlyHash.HasValue && o.Hash != onlyHash.Value) continue;
                float dx = o.X - x, dy = o.Y - y, dz = o.Z - z;
                float d = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > radius) continue;
                o.Distance = d;
                picked.Add(o);
            }
            return picked.OrderBy(o => o.Distance).ToList();
        }
    }

    /// <summary>Names for prefabs of mods that were once installed; the game only logs their hash.</summary>
    internal static class KnownPrefabs
    {
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            { StableHash.Of("rae_OdinHorse"), "rae_OdinHorse (OdinHorse, removed)" },
        };

        public static string NameOf(int hash) => Names.TryGetValue(hash, out var n) ? n : "unknown prefab, hash " + hash;
    }

    /// <summary>The game's string hash (StringExtensionMethods.GetStableHashCode), so prefab names can be matched without the game.</summary>
    internal static class StableHash
    {
        public static int Of(string str)
        {
            unchecked
            {
                int a = 5381, b = a;
                for (int i = 0; i < str.Length && str[i] != '\0'; i += 2)
                {
                    a = ((a << 5) + a) ^ str[i];
                    if (i == str.Length - 1 || str[i + 1] == '\0') break;
                    b = ((b << 5) + b) ^ str[i + 1];
                }
                return a + b * 1566083941;
            }
        }
    }
}
