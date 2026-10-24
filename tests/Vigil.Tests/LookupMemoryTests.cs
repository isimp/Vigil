using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vigil.Core;
using Vigil.Startup;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Failed assembly lookups during the start: when a mod asks for an assembly that is not installed
    /// (a check for an optional mod), BepInEx searches every folder under the plugins folder for it,
    /// on every ask. While the game starts, a search that found nothing is answered from memory the
    /// next time the same assembly is asked for in the same folder.
    /// </summary>
    public sealed class LookupMemoryTests
    {
        private static readonly string Plugins = Path.Combine(Path.GetTempPath(), "BepInEx", "plugins");
        private static readonly string Core = Path.Combine(Path.GetTempPath(), "BepInEx", "core");

        [Fact]
        public void An_assembly_not_found_in_a_folder_is_answered_from_memory_the_next_time()
        {
            var m = new LookupMemory();
            m.Begin();
            Assert.False(m.KnownMissing("EpicLoot", Plugins));

            m.Missing("EpicLoot", Plugins);

            Assert.True(m.KnownMissing("EpicLoot", Plugins));
            Assert.True(m.KnownMissing("epicloot", Plugins));
            Assert.True(m.KnownMissing("EpicLoot", Plugins));
            var r = m.End("the menu showed");
            Assert.Equal(3, r.Answered);
            Assert.Equal(1, r.Assemblies);
        }

        [Fact]
        public void The_same_assembly_in_another_folder_is_searched()
        {
            var m = new LookupMemory();
            m.Begin();
            m.Missing("EpicLoot", Plugins);

            Assert.False(m.KnownMissing("EpicLoot", Core));
            Assert.False(m.KnownMissing("CLLC", Plugins));
        }

        [Fact]
        public void Nothing_is_remembered_before_the_start_or_after_it()
        {
            var m = new LookupMemory();
            m.Missing("EpicLoot", Plugins);
            Assert.False(m.KnownMissing("EpicLoot", Plugins));

            m.Begin();
            Assert.False(m.KnownMissing("EpicLoot", Plugins));
            m.Missing("EpicLoot", Plugins);
            m.End("the menu showed");

            Assert.False(m.KnownMissing("EpicLoot", Plugins));
            m.Missing("CLLC", Plugins);
            Assert.False(m.KnownMissing("CLLC", Plugins));
        }

        [Fact]
        public void The_report_says_how_many_searches_memory_answered_and_for_which_assemblies()
        {
            var m = new LookupMemory();
            m.Begin();
            m.Missing("EpicLoot", Plugins);
            for (int i = 0; i < 36; i++) m.KnownMissing("EpicLoot", Plugins);
            m.Missing("CLLC", Plugins);
            m.KnownMissing("CLLC", Plugins);
            m.KnownMissing("CLLC", Plugins);

            var r = m.End("the menu showed");

            Assert.Equal("Assembly lookups: 38 failed searches answered from memory until the menu showed, for 2 assemblies not installed: EpicLoot 36, CLLC 2", r.Line());
            var back = LookupResult.Parse(r.EventText());
            Assert.Equal(new[] { 38, 2 }, new[] { back.Answered, back.Assemblies });
            Assert.Equal(new[] { "EpicLoot", "CLLC" }, back.Most.Select(x => x.Key).ToArray());
            Assert.Equal(r.Line(), back.Line());
            Assert.Null(LookupResult.Parse("not a result"));
        }

        [Fact]
        public void A_start_with_nothing_answered_has_no_line()
        {
            var m = new LookupMemory();
            m.Begin();
            m.Missing("EpicLoot", Plugins);

            Assert.Null(m.End("the menu showed").Line());
        }
    }
}
