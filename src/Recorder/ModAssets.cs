using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using Vigil.Core;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// Which loaded asset bundle comes from which mod, and so which loaded asset or world object:
    /// a bundle belongs to the plugin that embeds a resource or keeps a file of the bundle's name
    /// in its own folder (<see cref="BundleIndex.OwnerOfBundle"/>). On request only: it lists every
    /// plugin's resources and files and every bundle's asset names.
    /// </summary>
    internal static class ModAssets
    {
        public static BundleIndex Index(out List<string> bundleLines)
        {
            bundleLines = new List<string>();
            var modFiles = new Dictionary<string, string[]>();
            string pluginRoot = Normalize(Paths.PluginPath);
            foreach (var p in Chainloader.PluginInfos.Values)
            {
                if (p?.Instance == null) continue;
                var names = new List<string>();
                try { names.AddRange(p.Instance.GetType().Assembly.GetManifestResourceNames()); } catch (Exception) { }
                try
                {
                    // A plugin directly in the plugins folder has no folder of its own to read.
                    string folder = Path.GetDirectoryName(p.Location);
                    if (!string.IsNullOrEmpty(folder) && Normalize(folder) != pluginRoot)
                        names.AddRange(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
                }
                catch (Exception) { }
                modFiles[p.Metadata.Name] = names.ToArray();
            }
            var index = new BundleIndex();
            foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (b == null) continue;
                string owner = BundleIndex.OwnerOfBundle(b.name, modFiles);
                string[] assets;
                try { assets = b.isStreamedSceneAssetBundle ? b.GetAllScenePaths() : b.GetAllAssetNames(); }
                catch (Exception) { assets = new string[0]; }
                if (owner != null && owner != BundleIndex.Ambiguous) index.Add(b.name, owner, assets);
                bundleLines.Add(b.name + "   " + (owner ?? "no mod matched (the game's own, or a name no mod file carries)") + "   " + assets.Length + " assets");
            }
            bundleLines.Sort(StringComparer.Ordinal);
            return index;
        }

        private static string Normalize(string path) =>
            (path ?? "").Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }
}
