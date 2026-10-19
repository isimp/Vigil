using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>The results of the Tools tab's inspections, from the same numbers each writes to its file.</summary>
    internal static class InspectResult
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private const double Mb = 1048576.0;
        private static readonly string[] AssetHeaders = { "Asset", "Kind", "Mod", "Size" };
        private static readonly bool[] AssetRight = { false, false, false, true };
        private static readonly string[] TypeHeaders = { "Type", "Mod", "Objects", "Renderers", "Shadows", "Lights", "Copies" };
        private static readonly bool[] TypeRight = { false, false, true, true, true, true, true };
        private static readonly string[] SharedHeaders = { "Method", "Mods", "Which" };
        private static readonly bool[] SharedRight = { false, true, false };

        /// <summary>The name a bar gives the assets no mod's bundles hold.</summary>
        public const string NoMod = "The game, or no mod matched";

        public static Result Memory(DateTime at, IReadOnlyList<(string Kind, int Count, long Bytes)> kinds, ModMemory perMod,
            IReadOnlyList<(string Name, string Kind, long Bytes, string Owner)> largest, long unityUsedBytes, long monoUsedBytes)
        {
            var r = new Result("What fills memory", at) { Badge = Size(unityUsedBytes) + " in use", BadgeTone = ResultTone.Quiet };
            long Of(string kind) => kinds.Where(k => k.Kind == kind).Sum(k => k.Bytes);
            var tiles = r.Add(new TilesBlock()).Add("Unity in use", Size(unityUsedBytes)).Add("Textures", Size(Of("Texture"))).Add("Meshes", Size(Of("Mesh")));
            tiles.Add("Script heap", Size(monoUsedBytes));
            // The game's own share dwarfs every mod's, so it is a line of its own and the bars compare the mods.
            var bars = r.Add(new BarsBlock("Most memory, by mod"));
            foreach (var (owner, bytes, _) in perMod.Totals())
            {
                if (owner == ModMemory.Unmatched)
                {
                    bars.Note = NoMod + ": " + Size(bytes) + ", left out of the bars.";
                }
                else if (bars.Bars.Count < 8)
                {
                    bars.Add(owner, bytes, Size(bytes));
                }
            }

            var table = r.Add(new TableBlock("Largest single assets", AssetHeaders, AssetRight));
            foreach (var (name, kind, bytes, owner) in largest.OrderByDescending(a => a.Bytes).Take(30))
            {
                table.Rows.Add(new[] { string.IsNullOrEmpty(name) ? "(no name)" : name, kind, owner ?? "", Size(bytes) });
            }

            return r;
        }

        public static Result Census(DateTime at, Census census, float radius, IEnumerable<CensusFinding> findings = null)
        {
            var rows = census.Rows();
            var r = new Result("Count objects", at)
            {
                Badge = radius > 0 ? "within " + radius.ToString("0", Ci) + " m" : "everything loaded",
                BadgeTone = ResultTone.Quiet,
            };
            // The findings lead; the ones pointing at a change worth making are marked.
            foreach (var f in findings ?? Enumerable.Empty<CensusFinding>())
            {
                r.Add(new VerdictBlock(f.ActOn ? ResultTone.Caution : ResultTone.Quiet, f.Text));
            }

            r.Add(new TilesBlock())
                .Add("Objects", rows.Sum(x => x.Objects).ToString(Ci))
                .Add("Renderers", rows.Sum(x => x.Renderers).ToString(Ci))
                .Add("Shadow casters", rows.Sum(x => x.Shadows).ToString(Ci))
                .Add("Lights", rows.Sum(x => x.Lights).ToString(Ci))
                .Add("Material copies", rows.Sum(x => x.Copied).ToString(Ci));
            var types = r.Add(new BarsBlock("Most renderers, by type"));
            foreach (var x in rows.Where(x => x.Renderers > 0).Take(8))
            {
                types.Add(x.Prefab, x.Renderers, string.Format(Ci, "{0} renderers, {1} objects", x.Renderers, x.Objects));
            }

            var mods = r.Add(new BarsBlock("Renderers, by mod"));
            var game = rows.Where(x => x.Owner == null).ToList();
            if (game.Count > 0)
            {
                mods.Note = string.Format(Ci, "{0}: {1} renderers, {2} objects, left out of the bars.", NoMod, game.Sum(x => x.Renderers), game.Sum(x => x.Objects));
            }

            foreach (var g in rows.Where(x => x.Owner != null).GroupBy(x => x.Owner).Select(g => (Owner: g.Key, Renderers: g.Sum(x => x.Renderers), Objects: g.Sum(x => x.Objects)))
                         .Where(g => g.Renderers > 0).OrderByDescending(g => g.Renderers).ThenBy(g => g.Owner, StringComparer.Ordinal).Take(8))
            {
                mods.Add(g.Owner, g.Renderers, string.Format(Ci, "{0} renderers, {1} objects", g.Renderers, g.Objects));
            }

            var table = r.Add(new TableBlock("Every type", TypeHeaders, TypeRight));
            foreach (var x in rows.Take(80))
            {
                table.Rows.Add(new[]
                {
                    x.Prefab, x.Owner ?? "", x.Objects.ToString(Ci), x.Renderers.ToString(Ci), x.Shadows.ToString(Ci), x.Lights.ToString(Ci), x.Copied.ToString(Ci),
                });
            }

            return r;
        }

        public static Result Patches(DateTime at, IReadOnlyList<PatchRow> rows)
        {
            int methods = rows.Select(x => x.Method).Distinct(StringComparer.Ordinal).Count();
            var r = new Result("Harmony patch map", at) { Badge = rows.Count.ToString(Ci) + " patches", BadgeTone = ResultTone.Quiet };
            r.Add(new TilesBlock())
                .Add("Patches", rows.Count.ToString(Ci))
                .Add("Game methods", methods.ToString(Ci))
                .Add("Mods", rows.Select(x => x.Owner).Distinct(StringComparer.Ordinal).Count().ToString(Ci))
                .Add("Can skip the game's code", rows.Count(x => x.CanSkip).ToString(Ci));
            var bars = r.Add(new BarsBlock("Patches, by mod"));
            foreach (var g in rows.GroupBy(x => x.Owner ?? "?").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(10))
            {
                int n = g.Count();
                bars.Add(g.Key, n, string.Format(Ci, "{0} on {1} methods", n, g.Select(x => x.Method).Distinct(StringComparer.Ordinal).Count()));
            }

            var shared = r.Add(new TableBlock("Game methods several mods patch", SharedHeaders, SharedRight));
            foreach (var g in rows.GroupBy(x => x.Method).Select(g => (Method: g.Key, Owners: g.Select(x => x.Owner ?? "?").Distinct(StringComparer.Ordinal).OrderBy(o => o, StringComparer.Ordinal).ToList()))
                         .Where(g => g.Owners.Count >= 2).OrderByDescending(g => g.Owners.Count).ThenBy(g => g.Method, StringComparer.Ordinal).Take(40))
            {
                shared.Rows.Add(new[] { g.Method, g.Owners.Count.ToString(Ci), string.Join(", ", g.Owners) });
            }

            return r;
        }

        public static Result Trace(DateTime at, CopyTrace trace, string why)
        {
            var callers = trace.Callers();
            var r = new Result("Material copies", at) { Badge = trace.Total.ToString(Ci) + " copies", BadgeTone = trace.Total > 0 ? ResultTone.Caution : ResultTone.Good };
            r.Add(new VerdictBlock(trace.Total > 0 ? ResultTone.Caution : ResultTone.Good, trace.Total > 0
                ? string.Format(Ci, "{0} material copies made by {1} caller{2} ({3})", trace.Total, callers.Count, callers.Count == 1 ? "" : "s", why)
                : "No material copies were made while the trace ran (" + why + ")"));
            var bars = r.Add(new BarsBlock("Copies, by caller"));
            foreach (var (caller, owner, count, top) in callers.Take(10))
            {
                bars.Add(caller, count, string.Format(Ci, "{0}, {1}{2}", count, owner, top != null ? ", most on " + top : ""));
            }

            if (trace.Other > 0)
            {
                r.Add(new TextBlock(null, string.Format(Ci, "{0} copies came from further callers, not kept apart.", trace.Other)));
            }

            return r;
        }

        /// <summary>A size as people say it: "380 MB", "1.68 GB", "0.4 MB".</summary>
        public static string Size(long bytes)
        {
            double mb = bytes / Mb;
            return mb >= 1024 ? (mb / 1024).ToString("0.00", Ci) + " GB" : mb >= 10 ? mb.ToString("0", Ci) + " MB" : mb.ToString("0.0", Ci) + " MB";
        }
    }
}
