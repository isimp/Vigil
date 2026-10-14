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
    /// On request only, since both walk thousands of objects in one go:
    /// a census of the world objects loaded around the player per object type with what they ask
    /// of the renderer (Vigil-census-*.tsv), and a memory census of loaded textures, meshes,
    /// and sounds by size, plus materials and shaders, plus the loaded asset bundles (Vigil-memory-*.txt).
    /// </summary>
    internal static class SceneCensus
    {
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        public static string WriteScene(string dir, float radius)
        {
            if (ZNetScene.instance == null) return "no world loaded";
            Vector3? center = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (Vector3?)null;
            var census = new Census();
            var suspects = new CopySuspects();
            var index = ModAssets.Index(out _);
            foreach (var view in s_instances(ZNetScene.instance).Values.ToList())
            {
                if (view == null) continue;
                var go = view.gameObject;
                if (center.HasValue && radius > 0 && Vector3.Distance(center.Value, go.transform.position) > radius) continue;
                int renderers = 0, shadows = 0, copied = 0;
                foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                {
                    if (!r.enabled) continue;
                    renderers++;
                    if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) shadows++;
                    if (HasCopy(r)) copied++;
                }
                int lights = go.GetComponentsInChildren<Light>(false).Count(l => l.enabled);
                int particles = go.GetComponentsInChildren<ParticleSystem>(false).Count(p => p.isPlaying);
                int audio = go.GetComponentsInChildren<AudioSource>(false).Count(a => a.isPlaying);
                string prefab = Utils.GetPrefabName(go);
                census.Add(prefab, renderers, shadows, lights, particles, audio, copied, index.OwnerOf(prefab));
                // inactive parts too (a piece's worn and broken models), for which code made the copies
                if (renderers > 0)
                    suspects.Add(go.GetComponentsInChildren<Renderer>(true).Any(HasCopy),
                        go.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null).Select(b => b.GetType().FullName));
            }
            string stamp = DateTime.Now.ToString("HHmmss");
            string file = "Vigil-census-" + stamp + ".tsv";
            File.WriteAllText(Path.Combine(dir, file), census.Format());
            string mods = "Vigil-census-" + stamp + "-mods.tsv";
            File.WriteAllText(Path.Combine(dir, mods), census.FormatByOwner());
            string copies = "Vigil-census-" + stamp + "-materials.txt";
            var lines = new List<string>
            {
                "Material copies among the objects in the census " + file + ". A renderer asked for its material(s) draws with copies",
                "of its own, which cannot be drawn together with others of their kind. vigil trace materials names the code that made them.",
                "",
            };
            lines.AddRange(suspects.Report(25));
            File.WriteAllLines(Path.Combine(dir, copies), lines.ToArray());
            var result = InspectResult.Census(DateTime.Now, census, center.HasValue ? radius : 0);
            result.File = file;
            result.Path = Path.Combine(dir, file);
            ResultStore.Shared.Put(ResultStore.Census, result);
            return "census of loaded objects" + (center.HasValue && radius > 0 ? " within " + radius.ToString("0", CultureInfo.InvariantCulture) + " m" : "") + " written to " + file + ", per mod " + mods + ", material copies " + copies;
        }

        // A material made for this renderer alone: Unity names it "<material> (Instance)". Reading
        // sharedMaterials makes no copies.
        private static bool HasCopy(Renderer r)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && m.name.EndsWith(" (Instance)", StringComparison.Ordinal)) return true;
            return false;
        }

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
