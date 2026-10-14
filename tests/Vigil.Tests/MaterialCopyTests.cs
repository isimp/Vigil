using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Asking a renderer for its material makes it a copy of its own, and objects with copies can
    /// no longer be drawn together with others of the same kind. The census says how many objects
    /// around the player draw with copies and which component types sit on those objects but not on
    /// the others; the tracer names the code that made the copies and the mod it belongs to.
    /// </summary>
    public sealed class MaterialCopyTests
    {
        [Fact]
        public void The_census_counts_renderers_that_draw_with_a_copy_of_their_own()
        {
            var c = new Census();
            c.Add("woodwall", renderers: 2, shadowCasters: 2, lights: 0, particles: 0, audio: 0, copied: 2);
            c.Add("woodwall", renderers: 2, shadowCasters: 2, lights: 0, particles: 0, audio: 0, copied: 0);
            var lines = c.Format().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Assert.EndsWith("\town_materials\tmod", lines[0]);
            Assert.Equal("woodwall\t2\t4\t4\t0\t0\t0\t2\t", lines.Single(l => l.StartsWith("woodwall\t")));
        }

        [Fact]
        public void A_component_found_only_on_objects_with_copies_is_the_first_suspect()
        {
            var s = new CopySuspects();
            for (int i = 0; i < 40; i++) s.Add(copied: true, new[] { "WearNTear", "Piece", "MaterialVariation" });
            for (int i = 0; i < 60; i++) s.Add(copied: false, new[] { "WearNTear", "Piece" });
            var top = s.Ranked().First();
            Assert.Equal("MaterialVariation", top.Component);
            Assert.Equal(40, top.OnCopied);
            Assert.Equal(0, top.OnClean);
        }

        [Fact]
        public void The_component_that_sets_the_objects_apart_most_comes_before_a_weaker_one()
        {
            var s = new CopySuspects();
            for (int i = 0; i < 10; i++) s.Add(copied: true, i < 5 ? new[] { "Strong", "Weak" } : new[] { "Strong" });
            for (int i = 0; i < 10; i++) s.Add(copied: false, new string[0]);
            Assert.Equal(new[] { "Strong", "Weak" }, s.Ranked().Select(r => r.Component).ToArray());
        }

        [Fact]
        public void Components_on_every_object_are_not_suspects()
        {
            var s = new CopySuspects();
            for (int i = 0; i < 10; i++) s.Add(copied: true, new[] { "Piece" });
            for (int i = 0; i < 10; i++) s.Add(copied: false, new[] { "Piece" });
            Assert.DoesNotContain(s.Ranked(), r => r.Component == "Piece");
        }

        [Fact]
        public void The_suspect_report_says_how_many_objects_have_copies()
        {
            var s = new CopySuspects();
            s.Add(copied: true, new[] { "X" });
            s.Add(copied: false, new[] { "Y" });
            s.Add(copied: false, new[] { "Y" });
            Assert.Contains("1 of 3 objects", string.Join("\n", s.Report(10)));
        }

        [Fact]
        public void The_tracer_ranks_callers_by_copies_and_names_their_mod_and_objects()
        {
            var t = new CopyTrace();
            for (int i = 0; i < 5; i++) t.Add("MaterialVariation.CheckMaterial", "Valheim", "woodwall");
            for (int i = 0; i < 2; i++) t.Add("MaterialVariation.CheckMaterial", "Valheim", "iron_wall");
            t.Add("Foo.Bar", "Some Mod", "door_wood");
            var lines = t.Report(10);
            Assert.Equal(8, t.Total);
            int first = lines.FindIndex(l => l.Contains("MaterialVariation.CheckMaterial"));
            int second = lines.FindIndex(l => l.Contains("Foo.Bar"));
            Assert.True(first >= 0 && first < second);
            Assert.Contains(lines, l => l.Contains("7 copies") && l.Contains("Valheim"));
            Assert.Contains(lines, l => l.Contains("woodwall 5") && l.Contains("iron_wall 2"));
            Assert.Contains(lines, l => l.Contains("Some Mod"));
        }

        [Fact]
        public void The_tracer_keeps_a_bounded_number_of_callers()
        {
            var t = new CopyTrace(maxCallers: 3);
            for (int i = 0; i < 10; i++) t.Add("Caller" + i, "Mod", "obj");
            Assert.Equal(10, t.Total);
            Assert.Contains(t.Report(10), l => l.Contains("7 copies from other callers"));
        }
    }

    /// <summary>
    /// Each second of the timeline carries the graphics settings in force, so frame rates can be
    /// compared between settings at the same place.
    /// </summary>
    public sealed class GraphicsSignatureTests
    {
        [Fact]
        public void Settings_read_as_short_words_in_a_fixed_order()
        {
            string s = GraphicsSignature.Format(new[] { ("shadow", 1), ("plshadow", 2) }, new[] { ("aa", true), ("bloom", false) });
            Assert.Equal("shadow1 plshadow2 aa+ bloom-", s);
        }

        [Fact]
        public void The_same_settings_give_the_same_signature()
        {
            var ints = new[] { ("lod", 2) };
            var bools = new[] { ("ssao", true) };
            Assert.Equal(GraphicsSignature.Format(ints, bools), GraphicsSignature.Format(ints, bools));
        }
    }
}
