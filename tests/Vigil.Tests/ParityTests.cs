using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Any player compares their mods and configs with the server's: which mods differ in version,
    /// which the server has and they lack (and the other way round), and which config files hold
    /// other settings. A config counts as the same when its settings are, whatever its line
    /// endings, comments or descriptions.
    /// </summary>
    public sealed class ParityTests
    {
        private const string Cfg = "## Settings file was created by plugin X v1.0\n[General]\n\n## How far\n# Setting type: Single\nRadius = 40\nEnabled = true\n";

        [Fact]
        public void A_config_with_the_same_settings_matches_whatever_its_line_endings_and_comments()
        {
            string windows = Cfg.Replace("\n", "\r\n");
            string newerDescription = Cfg.Replace("## How far", "## How far around you, in metres").Replace("v1.0", "v1.1");
            Assert.Equal(Parity.Fingerprint("x.cfg", Cfg), Parity.Fingerprint("x.cfg", windows));
            Assert.Equal(Parity.Fingerprint("x.cfg", Cfg), Parity.Fingerprint("x.cfg", newerDescription));
        }

        [Fact]
        public void A_config_with_another_value_differs()
        {
            Assert.NotEqual(Parity.Fingerprint("x.cfg", Cfg), Parity.Fingerprint("x.cfg", Cfg.Replace("Radius = 40", "Radius = 60")));
            Assert.NotEqual(Parity.Fingerprint("x.yml", "a: 1\nb: 2\n"), Parity.Fingerprint("x.yml", "a: 1\nb: 3\n"));
            Assert.Equal(Parity.Fingerprint("x.yml", "a: 1\nb: 2\n"), Parity.Fingerprint("x.yml", "a: 1\r\nb: 2\r\n"));
        }

        [Fact]
        public void Mods_are_compared_by_their_id_and_version()
        {
            var mine = new[] { new Parity.Mod("a", "Alpha", "1.0"), new Parity.Mod("b", "Beta", "2.0"), new Parity.Mod("ui", "My UI", "1.0") };
            var theirs = new[] { new Parity.Mod("a", "Alpha", "1.0"), new Parity.Mod("b", "Beta", "2.1"), new Parity.Mod("srv", "Server Tool", "3.0") };
            var r = Parity.Compare(mine, theirs, new Dictionary<string, string>(), new Dictionary<string, string>());
            Assert.Equal(1, r.ModsSame);
            Assert.Equal("Beta", Assert.Single(r.VersionsDiffer).Name);
            Assert.Equal("2.0", r.VersionsDiffer[0].Mine);
            Assert.Equal("2.1", r.VersionsDiffer[0].Theirs);
            Assert.Equal("srv", Assert.Single(r.OnlyTheirs).Guid);
            Assert.Equal("ui", Assert.Single(r.OnlyMine).Guid);
        }

        [Fact]
        public void Configs_on_both_sides_are_compared_and_the_servers_extra_ones_named()
        {
            var mine = new Dictionary<string, string> { { "a.cfg", "1" }, { "B.cfg", "2" }, { "mine-only.cfg", "3" } };
            var theirs = new Dictionary<string, string> { { "a.cfg", "1" }, { "b.cfg", "9" }, { "server-only.yml", "4" } };
            var r = Parity.Compare(new Parity.Mod[0], new Parity.Mod[0], mine, theirs);
            Assert.Equal(1, r.ConfigsSame);
            Assert.Equal(new[] { "b.cfg" }, r.ConfigsDiffer.Select(c => c.ToLowerInvariant()).ToArray());
            Assert.Equal(new[] { "server-only.yml" }, r.ConfigsOnlyTheirs.ToArray());
            Assert.Equal(1, r.ConfigsOnlyMine);
        }

        [Fact]
        public void The_servers_list_arrives_as_it_sent_it()
        {
            var mods = new[] { new Parity.Mod("isimp.Vigil", "Vigil", "0.9.0"), new Parity.Mod("x.y", "X Y", "1.2.3") };
            var configs = new Dictionary<string, string> { { "isimp.Vigil.cfg", "abc" }, { "Seasonality/Seasonality.yml", "def" } };
            var sent = Parity.Format(mods, configs);
            var travelled = KeyValues.Decode(KeyValues.Encode(sent.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value))));
            var (backMods, backConfigs) = Parity.Parse(travelled);
            Assert.Equal(new[] { "isimp.Vigil", "x.y" }, backMods.Select(m => m.Guid).ToArray());
            Assert.Equal("1.2.3", backMods[1].Version);
            Assert.Equal("def", backConfigs["Seasonality/Seasonality.yml"]);
        }

        [Fact]
        public void The_panel_leads_with_mods_whose_versions_differ()
        {
            var r = Parity.Compare(new[] { new Parity.Mod("b", "Beta", "2.0") }, new[] { new Parity.Mod("b", "Beta", "2.1") },
                new Dictionary<string, string> { { "a.cfg", "1" } }, new Dictionary<string, string> { { "a.cfg", "2" } });
            var result = ParityResult.Build(r, new System.DateTime(2026, 10, 13, 23, 0, 0));
            Assert.Contains("1 mod", result.Badge);
            Assert.Equal(ResultTone.Caution, result.BadgeTone);
            var first = result.Blocks.OfType<TableBlock>().First();
            Assert.Equal(new[] { "Beta", "2.0", "2.1" }, first.Rows[0]);
            Assert.Contains(result.Blocks.OfType<TableBlock>(), t => t.Rows.Any(row => row[0] == "a.cfg"));
        }

        [Fact]
        public void Other_settings_with_the_same_mods_still_show_as_a_difference()
        {
            var r = Parity.Compare(new[] { new Parity.Mod("b", "Beta", "2.0") }, new[] { new Parity.Mod("b", "Beta", "2.0") },
                new Dictionary<string, string> { { "a.cfg", "1" } }, new Dictionary<string, string> { { "a.cfg", "2" } });
            var result = ParityResult.Build(r, new System.DateTime(2026, 10, 13, 23, 0, 0));
            Assert.Equal(ResultTone.Caution, result.BadgeTone);
            Assert.Contains("1 config", result.Badge);
        }

        [Fact]
        public void The_same_mods_and_settings_read_as_such()
        {
            var r = Parity.Compare(new[] { new Parity.Mod("b", "Beta", "2.0") }, new[] { new Parity.Mod("b", "Beta", "2.0") },
                new Dictionary<string, string> { { "a.cfg", "1" } }, new Dictionary<string, string> { { "a.cfg", "1" } });
            var result = ParityResult.Build(r, new System.DateTime(2026, 10, 13, 23, 0, 0));
            Assert.Equal(ResultTone.Good, result.BadgeTone);
        }
    }
}
