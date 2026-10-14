using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// Which mod a loaded asset or world object comes from, through the asset bundles mods load:
    /// a bundle belongs to the mod that ships a resource or file of its name, and an object to the
    /// bundle holding an asset of its name. A name two mods ship is given to neither. Objects a mod
    /// builds from the game's own (clones, kitbashing) are not in any bundle and stay unmatched.
    /// </summary>
    public sealed class BundleIndex
    {
        public const string Ambiguous = "(several mods)";

        private readonly Dictionary<string, string> _owners = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <param name="bundle">The asset bundle's name.</param>
        /// <param name="modFiles">Per mod: its embedded resource names and the files in its folder.</param>
        /// <returns>The mod, <see cref="Ambiguous"/> when several match, or null.</returns>
        public static string OwnerOfBundle(string bundle, IDictionary<string, string[]> modFiles)
        {
            if (string.IsNullOrEmpty(bundle) || modFiles == null) return null;
            string b = bundle.ToLowerInvariant();
            var hits = modFiles.Where(kv => kv.Value != null && kv.Value.Any(n => Segments(n).Contains(b))).Select(kv => kv.Key).Distinct().ToList();
            return hits.Count == 0 ? null : hits.Count == 1 ? hits[0] : Ambiguous;
        }

        private static IEnumerable<string> Segments(string name) =>
            (name ?? "").ToLowerInvariant().Split(new[] { '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        public void Add(string bundle, string owner, IEnumerable<string> assetPaths)
        {
            if (owner == null || assetPaths == null) return;
            foreach (var p in assetPaths)
            {
                string name = Path.GetFileNameWithoutExtension(p ?? "").ToLowerInvariant();
                if (name.Length == 0) continue;
                _owners[name] = _owners.TryGetValue(name, out var was) && was != owner ? Ambiguous : owner;
            }
        }

        /// <summary>The mod whose bundle holds an asset of this object's name, or null.</summary>
        public string OwnerOf(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return null;
            string n = objectName;
            foreach (var suffix in new[] { "(Clone)", " (Instance)" })
                while (n.EndsWith(suffix, StringComparison.Ordinal)) n = n.Substring(0, n.Length - suffix.Length);
            return _owners.TryGetValue(n.Trim().ToLowerInvariant(), out var owner) ? owner : null;
        }

        public int Count => _owners.Count;
    }

    /// <summary>Loaded asset memory added up per mod and kind (textures, meshes, sounds ...), biggest mod first.</summary>
    public sealed class ModMemory
    {
        public const string Unmatched = "the game, or not matched to a mod";

        private readonly Dictionary<string, Dictionary<string, long>> _bytes = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

        public void Add(string owner, string kind, long bytes)
        {
            string o = owner ?? Unmatched;
            if (!_bytes.TryGetValue(o, out var kinds)) _bytes[o] = kinds = new Dictionary<string, long>(StringComparer.Ordinal);
            kinds[kind] = (kinds.TryGetValue(kind, out long b) ? b : 0) + bytes;
        }

        /// <summary>Each owner (the game's and unmatched assets under <see cref="Unmatched"/>) with its bytes per kind, most first, for the panel.</summary>
        public List<(string Owner, long Bytes, IReadOnlyDictionary<string, long> Kinds)> Totals() =>
            _bytes.OrderByDescending(k => k.Value.Values.Sum()).ThenBy(k => k.Key, StringComparer.Ordinal)
                .Select(k => (k.Key, k.Value.Values.Sum(), (IReadOnlyDictionary<string, long>)k.Value)).ToList();

        public List<string> Report(int top)
        {
            var ci = CultureInfo.InvariantCulture;
            Func<string, string> line = o =>
            {
                var kinds = _bytes[o];
                string parts = string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => k.Key + " " + (k.Value / 1048576.0).ToString("0.0", ci)).ToArray());
                return string.Format(ci, "   {0,-40} {1,8:0.0} MB   ({2})", o, kinds.Values.Sum() / 1048576.0, parts);
            };
            var lines = _bytes.Keys.Where(o => o != Unmatched).OrderByDescending(o => _bytes[o].Values.Sum()).ThenBy(o => o, StringComparer.Ordinal)
                .Take(top).Select(line).ToList();
            if (_bytes.ContainsKey(Unmatched)) lines.Add(line(Unmatched));
            return lines;
        }
    }
}
