using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// During a deep capture, a patched game method that stalls one frame (opening the build menu,
    /// the inventory, a chest) is split into the game's code and the mods' share from then on, so
    /// repeating the action shows how much of the stall the mods add. The deep report also gives
    /// each method's worst single frame, which an average over the capture hides.
    /// </summary>
    public sealed class SpikeSplitTests
    {
        [Fact]
        public void A_patched_method_that_stalls_one_frame_is_split()
        {
            Assert.True(DeepSplit.SplitOnSpike(frameMs: 120, patched: true, split: false, dropped: false, spikeSplits: 0));
        }

        [Fact]
        public void A_short_frame_does_not_split()
        {
            Assert.False(DeepSplit.SplitOnSpike(frameMs: DeepSplit.SpikeMs - 0.1, patched: true, split: false, dropped: false, spikeSplits: 0));
            Assert.True(DeepSplit.SplitOnSpike(frameMs: DeepSplit.SpikeMs, patched: true, split: false, dropped: false, spikeSplits: 0));
        }

        [Fact]
        public void Only_methods_mods_patch_are_split_and_only_once()
        {
            Assert.False(DeepSplit.SplitOnSpike(200, patched: false, split: false, dropped: false, spikeSplits: 0));
            Assert.False(DeepSplit.SplitOnSpike(200, patched: true, split: true, dropped: false, spikeSplits: 0));
            Assert.False(DeepSplit.SplitOnSpike(200, patched: true, split: false, dropped: true, spikeSplits: 0));
        }

        [Fact]
        public void Spike_splits_are_capped_so_a_stuttering_session_cannot_rebuild_everything()
        {
            Assert.True(DeepSplit.SplitOnSpike(200, true, false, false, spikeSplits: DeepSplit.MaxSpikeSplits - 1));
            Assert.False(DeepSplit.SplitOnSpike(200, true, false, false, spikeSplits: DeepSplit.MaxSpikeSplits));
        }

        [Fact]
        public void A_stall_repeated_after_the_split_shows_the_game_and_mod_shares()
        {
            string t = DeepSplit.DescribeShareOfCalls(splitWholeTicks: 1000, originalTicks: 620, calls: 3);
            Assert.Contains("3 calls", t);
            Assert.Contains("game code 62%", t);
            Assert.Contains("prefixes and postfixes 38%", t);
            Assert.Equal("", DeepSplit.DescribeShareOfCalls(0, 0, 0));
        }

        [Fact]
        public void The_worst_single_frame_is_shown_when_it_stands_out_from_the_average()
        {
            Assert.Contains("worst frame 312.4 ms", DeepSplit.DescribeWorst(averageMs: 0.4, worstMs: 312.4));
            Assert.Equal("", DeepSplit.DescribeWorst(averageMs: 1.0, worstMs: 1.5));
            // a method that costs a lot every frame has no single stall to point at
            Assert.Equal("", DeepSplit.DescribeWorst(averageMs: 20, worstMs: 25));
        }
    }

    /// <summary>
    /// What each mod costs in memory and in drawing: loaded asset bundles are matched to the mod
    /// that ships them (an embedded resource or a file in its folder of the same name), and loaded
    /// assets and world objects are matched to bundles by name.
    /// </summary>
    public sealed class ModAttributionTests
    {
        private static BundleIndex Index()
        {
            var i = new BundleIndex();
            i.Add("odinskingdom", "OdinsKingdom", new[] { "Assets/OdinsKingdom/rae_brickstone_wall_4x2.prefab", "Assets/OdinsKingdom/Textures/Brick_D.png" });
            i.Add("ironstorage", "OdinsKingdom", new[] { "assets/storage/rae_iron_storage_big.prefab" });
            i.Add("furniture", "BalrondFurnitureReborn", new[] { "assets/furniture/brick_d.png", "assets/furniture/chair.prefab" });
            return i;
        }

        [Fact]
        public void A_bundle_belongs_to_the_mod_that_ships_a_resource_or_file_of_its_name()
        {
            var mods = new System.Collections.Generic.Dictionary<string, string[]>
            {
                { "OdinsKingdom", new[] { "OdinsKingdom.Assets.odinskingdom", "OdinsKingdom.dll" } },
                { "Seasonality", new[] { "Seasonality.assets.snowpack", "README.md" } },
                { "Other", new[] { "other.ironstorage.bundle" } },
            };
            Assert.Equal("OdinsKingdom", BundleIndex.OwnerOfBundle("odinskingdom", mods));
            Assert.Equal("Seasonality", BundleIndex.OwnerOfBundle("SnowPack", mods));
            Assert.Equal("Other", BundleIndex.OwnerOfBundle("ironstorage", mods));
            Assert.Null(BundleIndex.OwnerOfBundle("unknownbundle", mods));
        }

        [Fact]
        public void A_bundle_two_mods_ship_belongs_to_neither()
        {
            var mods = new System.Collections.Generic.Dictionary<string, string[]>
            {
                { "A", new[] { "A.Assets.shared" } },
                { "B", new[] { "shared" } },
            };
            Assert.Equal(BundleIndex.Ambiguous, BundleIndex.OwnerOfBundle("shared", mods));
        }

        [Fact]
        public void A_bundle_name_must_match_a_whole_part_of_a_file_name_not_a_piece_of_one()
        {
            var mods = new System.Collections.Generic.Dictionary<string, string[]> { { "OdinsKingdom", new[] { "OdinsKingdom.Assets.odinskingdom" } } };
            Assert.Null(BundleIndex.OwnerOfBundle("kingdom", mods));
        }

        [Fact]
        public void An_object_is_matched_to_its_mod_by_its_asset_name_ignoring_case_and_clone_suffixes()
        {
            var i = Index();
            Assert.Equal("OdinsKingdom", i.OwnerOf("rae_brickstone_wall_4x2"));
            Assert.Equal("OdinsKingdom", i.OwnerOf("rae_iron_storage_big(Clone)"));
            Assert.Equal("BalrondFurnitureReborn", i.OwnerOf("Chair"));
            Assert.Null(i.OwnerOf("stone_floor_2x2"));
        }

        [Fact]
        public void A_name_two_mods_ship_is_not_given_to_either()
        {
            Assert.Equal(BundleIndex.Ambiguous, Index().OwnerOf("Brick_D"));
        }

        [Fact]
        public void Memory_adds_up_per_mod_and_kind_with_the_biggest_mod_first()
        {
            var m = new ModMemory();
            m.Add("OdinsKingdom", "Texture", 40L << 20);
            m.Add("OdinsKingdom", "Mesh", 10L << 20);
            m.Add("Seasonality", "Texture", 5L << 20);
            m.Add(null, "Texture", 100L << 20);
            var lines = m.Report(10);
            int ok = lines.FindIndex(l => l.Contains("OdinsKingdom"));
            int se = lines.FindIndex(l => l.Contains("Seasonality"));
            Assert.True(ok >= 0 && ok < se);
            Assert.Contains(lines, l => l.Contains("OdinsKingdom") && l.Contains("50.0 MB") && l.Contains("Texture 40.0") && l.Contains("Mesh 10.0"));
            Assert.Contains(lines, l => l.Contains("not matched to a mod") && l.Contains("100.0 MB"));
        }

        [Fact]
        public void The_census_adds_up_per_mod()
        {
            var c = new Census();
            c.Add("rae_brickstone_wall_4x2", renderers: 21, shadowCasters: 21, lights: 0, particles: 0, audio: 0, copied: 21, owner: "OdinsKingdom");
            c.Add("rae_iron_storage_big", renderers: 12, shadowCasters: 12, lights: 0, particles: 0, audio: 0, copied: 12, owner: "OdinsKingdom");
            c.Add("chair", renderers: 4, shadowCasters: 4, lights: 0, particles: 0, audio: 0, copied: 0, owner: "BalrondFurnitureReborn");
            c.Add("stone_floor_2x2", renderers: 2, shadowCasters: 2, lights: 0, particles: 0, audio: 0, copied: 0, owner: null);
            var lines = c.FormatByOwner().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Assert.Equal("mod\tobjects\trenderers\tshadow_casters\tlights\tparticle_systems\tsounds\town_materials", lines[0]);
            Assert.Equal("OdinsKingdom\t2\t33\t33\t0\t0\t0\t33", lines[1]);
            Assert.StartsWith("BalrondFurnitureReborn\t1\t4\t", lines[2]);
            Assert.StartsWith("the game, or not matched to a mod\t1\t2\t", lines[3]);
        }
    }
}
