using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The check after a hold of config saves: each written file, read the way BepInEx reads it back,
    /// must hold the value of every setting its mod has.
    /// </summary>
    public sealed class CfgValuesTests
    {
        private const string File =
            "## Settings file was created by plugin OdinArchitect v1.9.3\n" +
            "## Plugin GUID: Raelaziel.OdinArchitect\n\n" +
            "[1 - General]\n\n" +
            "## Lock the config\n" +
            "# Setting type: Boolean\n" +
            "Lock Configuration = true\n\n" +
            "[Piece: Wall = Stone]\n\n" +
            "  Recipe =  Stone:4,Wood:2  \n" +
            "Formula = a = b\n";

        [Fact]
        public void A_file_is_read_as_BepInEx_reads_it_back()
        {
            var values = CfgValues.Parse(File.Split('\n'));

            Assert.Equal("true", values[CfgValues.Key("1 - General", "Lock Configuration")]);
            Assert.Equal("Stone:4,Wood:2", values[CfgValues.Key("Piece: Wall = Stone", "Recipe")]);
            Assert.Equal("a = b", values[CfgValues.Key("Piece: Wall = Stone", "Formula")]);
            Assert.Equal(3, values.Count);
        }

        [Fact]
        public void Settings_whose_file_value_differs_or_is_missing_are_named()
        {
            var values = CfgValues.Parse(File.Split('\n'));
            var live = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(CfgValues.Key("1 - General", "Lock Configuration"), "true"),
                new KeyValuePair<string, string>(CfgValues.Key("Piece: Wall = Stone", "Recipe"), "Stone:5"),
                new KeyValuePair<string, string>(CfgValues.Key("2 - Later", "New"), "1"),
            };

            var differences = CfgValues.Differences(values, live, 10);

            Assert.Equal(new[] { "[Piece: Wall = Stone] Recipe", "[2 - Later] New" }, differences);
            Assert.Single(CfgValues.Differences(values, live, 1));
        }
    }
}
