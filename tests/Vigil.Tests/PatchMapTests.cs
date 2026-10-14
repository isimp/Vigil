using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Each session writes which mod patches which game method and how. Comparing two sessions
    /// shows what a mod update added or removed before anyone notices a changed behaviour.
    /// </summary>
    public sealed class PatchMapTests
    {
        private static PatchRow Row(string method, string kind, string owner, int priority = 400, string patch = "P.Prefix", bool skips = false) =>
            new PatchRow { Method = method, Kind = kind, Owner = owner, Priority = priority, Patch = patch, CanSkip = skips };

        private static List<string> Lines(params PatchRow[] rows) => PatchMap.Format(rows).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        [Fact]
        public void The_map_is_sorted_and_has_a_header()
        {
            var lines = Lines(Row("Player.Update", "prefix", "ModB"), Row("Hud.Update", "postfix", "ModA"));
            Assert.Equal(PatchMap.Header, lines[0]);
            Assert.StartsWith("Hud.Update\t", lines[1]);
            Assert.StartsWith("Player.Update\t", lines[2]);
        }

        [Fact]
        public void A_skip_capable_prefix_is_marked()
        {
            var lines = Lines(Row("Inventory.AddItem", "prefix", "ModA", skips: true));
            Assert.EndsWith("\tyes", lines[1]);
        }

        [Fact]
        public void Two_identical_sessions_have_no_differences()
        {
            var a = Lines(Row("Hud.Update", "postfix", "ModA"));
            Assert.Empty(PatchMap.Diff(a, a));
        }

        [Fact]
        public void An_added_and_a_removed_patch_are_both_named()
        {
            var older = Lines(Row("Hud.Update", "postfix", "ModA"), Row("Player.Update", "transpiler", "ModB", patch: "T.Transpiler"));
            var newer = Lines(Row("Hud.Update", "postfix", "ModA"), Row("Inventory.AddItem", "prefix", "ModC", skips: true));
            var diff = PatchMap.Diff(older, newer);
            Assert.Contains(diff, d => d.StartsWith("added") && d.Contains("Inventory.AddItem") && d.Contains("ModC") && d.Contains("can skip"));
            Assert.Contains(diff, d => d.StartsWith("removed") && d.Contains("Player.Update") && d.Contains("transpiler") && d.Contains("ModB"));
            Assert.Equal(2, diff.Count);
        }

        [Fact]
        public void A_changed_priority_or_skip_ability_counts_as_changed()
        {
            var older = Lines(Row("Hud.Update", "prefix", "ModA", priority: 400));
            var newer = Lines(Row("Hud.Update", "prefix", "ModA", priority: 800, skips: true));
            var diff = PatchMap.Diff(older, newer);
            Assert.Single(diff);
            Assert.StartsWith("changed", diff[0]);
            Assert.Contains("400", diff[0]);
            Assert.Contains("800", diff[0]);
        }

        [Fact]
        public void A_prefix_that_newly_can_skip_the_original_counts_as_changed()
        {
            var older = Lines(Row("Inventory.AddItem", "prefix", "ModA", priority: 400, skips: false));
            var newer = Lines(Row("Inventory.AddItem", "prefix", "ModA", priority: 400, skips: true));
            var diff = PatchMap.Diff(older, newer);
            Assert.Single(diff);
            Assert.StartsWith("changed", diff[0]);
            Assert.Contains("can skip the original", diff[0]);
        }

        [Fact]
        public void Blank_lines_and_the_header_are_ignored()
        {
            var a = new List<string> { PatchMap.Header, "", "Hud.Update\tpostfix\tModA\t400\tP.Postfix\t" };
            var b = new List<string> { "Hud.Update\tpostfix\tModA\t400\tP.Postfix\t", "" };
            Assert.Empty(PatchMap.Diff(a, b));
        }

        [Fact]
        public void Tabs_and_line_breaks_inside_names_cannot_break_the_file()
        {
            var lines = Lines(Row("A.B", "prefix", "Mod\twith\ntab"));
            Assert.Equal(2, lines.Count(l => l.Length > 0));
            Assert.Equal(6, lines[1].Split('\t').Length);
        }
    }
}
