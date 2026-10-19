using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Vigil.Core;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace Vigil.Recorder
{
    /// <summary>
    /// On request only, since it walks thousands of objects in one go: a memory census of loaded
    /// textures, meshes and sounds by size, plus materials and shaders, plus the loaded asset bundles
    /// (Vigil-memory-*.txt). The census of what is loaded around the player is <see cref="CensusRun"/>.
    /// </summary>
    internal static class SceneCensus
    {
        public static string WriteMemory(string dir)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("Vigil memory census   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendFormat(ci, "Unity total used {0:0} MB, reserved {1:0} MB, Mono heap used {2:0} MB, graphics driver {3:0} MB\r\n",
                Profiler.GetTotalAllocatedMemoryLong() / 1048576.0, Profiler.GetTotalReservedMemoryLong() / 1048576.0,
                Profiler.GetMonoUsedSizeLong() / 1048576.0, Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576.0);
            sb.AppendLine("Sizes are Unity's own estimate per object (it reads 0 for sounds). Names usually tell the mod (prefixes such as _bal, OdinPlus or a mod's own words).");
            var index = ModAssets.Index(out var bundleLines);
            var perMod = new ModMemory();
            var kinds = new List<(string Kind, int Count, long Bytes)>();
            var largest = new List<(string Name, string Kind, long Bytes, string Owner)>();
            var sections = new StringBuilder();
            foreach (var type in new[] { typeof(Texture), typeof(Mesh), typeof(AudioClip), typeof(Material), typeof(Shader) })
            {
                var items = new List<KeyValuePair<string, long>>();
                foreach (var o in Resources.FindObjectsOfTypeAll(type))
                {
                    long size;
                    try { size = Profiler.GetRuntimeMemorySizeLong(o); } catch (Exception) { continue; }
                    items.Add(new KeyValuePair<string, long>(o.name, size));
                    perMod.Add(index.OwnerOf(o.name), type.Name, size);
                }
                kinds.Add((type.Name, items.Count, items.Sum(i => i.Value)));
                sections.AppendLine();
                sections.AppendFormat(ci, "== {0}: {1} objects, {2:0.0} MB ==\r\n", type.Name, items.Count, items.Sum(i => i.Value) / 1048576.0);
                foreach (var g in items.GroupBy(i => i.Key).Select(g => new { Name = g.Key, Count = g.Count(), Bytes = g.Sum(i => i.Value) })
                             .OrderByDescending(g => g.Bytes).Take(40))
                {
                    sections.AppendFormat(ci, "   {0,9:0.00} MB  {1,4}x  {2}{3}\r\n", g.Bytes / 1048576.0, g.Count, string.IsNullOrEmpty(g.Name) ? "(no name)" : g.Name,
                        index.OwnerOf(g.Name) is string owner ? "   [" + owner + "]" : "");
                    largest.Add((g.Name, type.Name, g.Bytes, index.OwnerOf(g.Name)));
                }
            }
            sb.AppendLine();
            sb.AppendLine("== By mod: loaded textures, meshes, sounds, materials and shaders whose names one mod's asset bundles hold (MB) ==");
            sb.AppendLine("   Matched by name, so assets a mod builds from the game's own, or names two mods share, count for no mod.");
            foreach (var l in perMod.Report(60)) sb.AppendLine(l);
            sb.Append(sections);
            sb.AppendLine();
            sb.AppendLine("== Loaded asset bundles: " + bundleLines.Count + " (name, the mod that ships it, assets in it) ==");
            foreach (var b in bundleLines) sb.AppendLine("   " + b);
            string file = "Vigil-memory-" + DateTime.Now.ToString("HHmmss") + ".txt";
            File.WriteAllText(Path.Combine(dir, file), sb.ToString());
            var result = InspectResult.Memory(DateTime.Now, kinds, perMod, largest, Profiler.GetTotalAllocatedMemoryLong(), Profiler.GetMonoUsedSizeLong());
            result.File = file;
            result.Path = Path.Combine(dir, file);
            ResultStore.Shared.Put(ResultStore.Memory, result);
            return "memory census written to " + file;
        }
    }
}
