using System;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Each tool's result shows in the panel under the tool: headline numbers, the biggest items as
    /// bars, and the detail as a table, from the same numbers the tool writes to its file.
    /// </summary>
    public sealed class ToolResultTests
    {
        private static readonly DateTime At = new DateTime(2026, 10, 13, 14, 9, 0);
        private const long Mb = 1048576;

        [Fact]
        public void Memory_shows_the_totals_and_the_mods_that_use_the_most_first()
        {
            var perMod = new ModMemory();
            perMod.Add(null, "Texture", 1200 * Mb);
            perMod.Add("OdinsKingdom", "Texture", 300 * Mb);
            perMod.Add("OdinsKingdom", "Mesh", 80 * Mb);
            perMod.Add("Seasonality", "Texture", 220 * Mb);
            var kinds = new List<(string, int, long)> { ("Texture", 900, 1720 * Mb), ("Mesh", 400, 80 * Mb) };
            var largest = new List<(string, string, long, string)> { ("atlas", "Texture", 64 * Mb, null) };
            var r = InspectResult.Memory(At, kinds, perMod, largest, 2900 * Mb, 600 * Mb);
            var tiles = r.Blocks.OfType<TilesBlock>().Single().Tiles;
            Assert.Contains(new KeyValuePair<string, string>("Textures", "1.68 GB"), tiles);
            Assert.Contains(new KeyValuePair<string, string>("Meshes", "80 MB"), tiles);
            var byMod = r.Blocks.OfType<BarsBlock>().Single();
            Assert.Equal(new[] { "OdinsKingdom", "Seasonality" }, byMod.Bars.Select(b => b.Label).ToArray());
            Assert.Equal("380 MB", byMod.Bars[0].Text);
            Assert.Contains("The game, or no mod matched", byMod.Note);
            Assert.Contains("1.17 GB", byMod.Note);
            Assert.Single(r.Blocks.OfType<TableBlock>().Single().Rows);
        }

        [Fact]
        public void The_mods_bars_compare_mods_with_each_other_not_with_the_games_own_share()
        {
            var c = new Census();
            for (int i = 0; i < 50; i++) c.Add("piece_wall", 1, 1, 0, 0, 0, 0, null);
            c.Add("chest_big", 6, 6, 0, 0, 0, 0, "Chests");
            c.Add("torch", 2, 0, 1, 1, 0, 1, "Torches");
            var r = InspectResult.Census(At, c, 60);
            var byMod = r.Blocks.OfType<BarsBlock>().Skip(1).First();
            Assert.Equal(new[] { "Chests", "Torches" }, byMod.Bars.Select(b => b.Label).ToArray());
            Assert.Contains("50 renderers", byMod.Note);
        }

        [Fact]
        public void The_census_adds_up_every_object_and_lists_the_types_with_most_renderers_first()
        {
            var c = new Census();
            c.Add("piece_wall", 2, 2, 0, 0, 0, 0, null);
            c.Add("piece_wall", 2, 2, 0, 0, 0, 0, null);
            c.Add("torch", 1, 0, 1, 1, 0, 1, "Torches");
            c.Add("chest_big", 6, 6, 0, 0, 0, 0, "Chests");
            var r = InspectResult.Census(At, c, 60);
            var tiles = r.Blocks.OfType<TilesBlock>().Single().Tiles;
            Assert.Contains(new KeyValuePair<string, string>("Objects", "4"), tiles);
            Assert.Contains(new KeyValuePair<string, string>("Renderers", "11"), tiles);
            Assert.Contains(new KeyValuePair<string, string>("Material copies", "1"), tiles);
            var types = r.Blocks.OfType<BarsBlock>().First().Bars;
            Assert.Equal(new[] { "chest_big", "piece_wall", "torch" }, types.Select(b => b.Label).ToArray());
            Assert.Equal("within 60 m", r.Badge);
        }

        [Fact]
        public void The_patch_map_counts_patches_per_mod_and_names_methods_several_mods_patch()
        {
            var rows = new List<PatchRow>
            {
                new PatchRow { Method = "Player.Update", Kind = "postfix", Owner = "A" },
                new PatchRow { Method = "Player.Update", Kind = "prefix", Owner = "B" },
                new PatchRow { Method = "Humanoid.Pickup", Kind = "prefix", Owner = "A" },
                new PatchRow { Method = "Smelter.UpdateSmelter", Kind = "postfix", Owner = "A" },
            };
            var r = InspectResult.Patches(At, rows);
            var bars = r.Blocks.OfType<BarsBlock>().Single().Bars;
            Assert.Equal("A", bars[0].Label);
            Assert.Equal(3, bars[0].Value);
            var shared = r.Blocks.OfType<TableBlock>().Single();
            Assert.Single(shared.Rows);
            Assert.Equal("Player.Update", shared.Rows[0][0]);
            Assert.Equal("A, B", shared.Rows[0][2]);
        }

        [Fact]
        public void The_material_trace_ranks_callers_by_their_copies()
        {
            var t = new CopyTrace();
            for (int i = 0; i < 3; i++) t.Add("Fireplace.Start", "Torches", "torch");
            t.Add("Piece.Awake", "Builder", "wall");
            var r = InspectResult.Trace(At, t, "time is up");
            var bars = r.Blocks.OfType<BarsBlock>().Single().Bars;
            Assert.Equal(new[] { "Fireplace.Start", "Piece.Awake" }, bars.Select(b => b.Label).ToArray());
            Assert.Contains("Torches", bars[0].Text);
            Assert.Equal("4 copies", r.Badge);
        }

        [Fact]
        public void A_deep_capture_splits_each_method_into_the_games_part_and_the_mods_part()
        {
            var rows = new List<CaptureResult.DeepRow>
            {
                new CaptureResult.DeepRow("Player.Update", "A, B", 0.40, 1, 3, 0.25),
                new CaptureResult.DeepRow("Smelter.UpdateSmelter", "C", 0.90, 12, 45, double.NaN),
            };
            var r = CaptureResult.Deep(At, 1200, "asked for", rows);
            var methods = r.Blocks.OfType<BarsBlock>().First();
            Assert.Equal("Smelter.UpdateSmelter", methods.Bars[0].Label);
            Assert.True(double.IsNaN(methods.Bars[0].Part));
            Assert.Equal(0.10, methods.Bars[1].Part, 3);
            Assert.NotNull(methods.PartName);
            var mods = r.Blocks.OfType<BarsBlock>().Skip(1).First().Bars;
            Assert.Equal(new[] { "C", "A", "B" }, mods.Select(b => b.Label).ToArray());
            Assert.Single(r.Blocks.OfType<TableBlock>().Single().Rows);
        }

        [Fact]
        public void A_drop_shows_how_the_connection_ended_and_each_seconds_longest_pause_and_waiting_data()
        {
            var samples = new List<DropAnalysis.Sample>
            {
                new DropAnalysis.Sample { At = 0, InKbs = 12, QueueKb = 0, MaxGapMs = 60 },
                new DropAnalysis.Sample { At = 1, InKbs = 0, QueueKb = 30, MaxGapMs = 1000 },
            };
            var d = DropAnalysis.Read("D'Arcy", false, samples, DropAnalysis.Ending.Timeout, 30, null);
            var r = CaptureResult.Drop(At, d, samples);
            Assert.Equal("line died", r.Badge);
            Assert.Equal(d.Headline, r.Summary);
            var strips = r.Blocks.OfType<StripBlock>().ToList();
            Assert.Equal(new[] { 60.0, 1000.0 }, strips[0].Values);
            Assert.Equal(new[] { 0.0, 30.0 }, strips[1].Values);
        }

        [Fact]
        public void An_incident_shows_what_happened_its_worst_frame_and_each_seconds_worst_frame()
        {
            var f = new FrameBreakdown { DtMs = 1100 };
            f.Phases.Add(new Timed("ZNet.Update", 40));
            f.Mods.Add(new Timed("PlantEasily", 600));
            var seconds = new List<double> { 20, 30, 1100, 25 };
            var r = CaptureResult.Incident(At, "hitch", "a 1100 ms frame (limit 250 ms)", f, seconds);
            Assert.Contains("a 1100 ms frame", r.Blocks.OfType<VerdictBlock>().First().Text);
            Assert.Equal(new[] { "PlantEasily", "ZNet.Update" }, r.Blocks.OfType<BarsBlock>().Single().Bars.Select(b => b.Label).ToArray());
            var strip = r.Blocks.OfType<StripBlock>().Single();
            Assert.Equal(seconds, strip.Values);
            Assert.Equal("hitch", r.Badge);
            Assert.Equal("a 1100 ms frame (limit 250 ms)", r.Summary);
        }
    }
}
