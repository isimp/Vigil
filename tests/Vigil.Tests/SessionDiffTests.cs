using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// "It felt fine yesterday" is answered by what changed since: every session records its mods
    /// and the state of each config file, and two sessions can be compared.
    /// </summary>
    public sealed class SessionDiffTests
    {
        private const string ModsA =
            "guid\tname\tversion\tfile\n" +
            "Searica.Valheim.SafetyStatus\tSafetyStatus\t1.3.2\tSearica-SafetyStatus/SafetyStatus.dll\n" +
            "Azumatt.AzuCraftyBoxes\tAzuCraftyBoxes\t1.8.27\tAzumatt-AzuCraftyBoxes/AzuCraftyBoxes.dll\n" +
            "old.mod\tOldMod\t1.0.0\tOld/Old.dll\n";

        private const string ModsB =
            "guid\tname\tversion\tfile\n" +
            "Searica.Valheim.SafetyStatus\tSafetyStatus\t1.3.3\tSearica-SafetyStatus/SafetyStatus.dll\n" +
            "Azumatt.AzuCraftyBoxes\tAzuCraftyBoxes\t1.8.27\tAzumatt-AzuCraftyBoxes/AzuCraftyBoxes.dll\n" +
            "new.mod\tNewMod\t0.1.0\tNew/New.dll\n";

        [Fact]
        public void AModWhoseVersionChangedIsListedWithBothVersions()
        {
            var d = SessionDiff.Mods(ModsA, ModsB);

            Assert.Contains(d, l => l.Contains("SafetyStatus") && l.Contains("1.3.2") && l.Contains("1.3.3"));
        }

        [Fact]
        public void AddedAndRemovedModsAreListed()
        {
            var d = SessionDiff.Mods(ModsA, ModsB);

            Assert.Contains(d, l => l.Contains("NewMod") && l.Contains("added"));
            Assert.Contains(d, l => l.Contains("OldMod") && l.Contains("removed"));
        }

        [Fact]
        public void UnchangedModsAreNotListed()
        {
            var d = SessionDiff.Mods(ModsA, ModsB);

            Assert.DoesNotContain(d, l => l.Contains("AzuCraftyBoxes"));
        }

        [Fact]
        public void IdenticalSessionsHaveNothingToReport()
        {
            Assert.Empty(SessionDiff.Mods(ModsA, ModsA));
        }

        [Fact]
        public void AConfigFileThatChangedIsListed()
        {
            string a = "file\tsize\thash\nAzumatt.AzuCraftyBoxes.yml\t9585\taaaa\nx.cfg\t10\tbbbb\n";
            string b = "file\tsize\thash\nAzumatt.AzuCraftyBoxes.yml\t9621\tcccc\nx.cfg\t10\tbbbb\ny.cfg\t3\tdddd\n";

            var d = SessionDiff.Configs(a, b);

            Assert.Contains(d, l => l.Contains("Azumatt.AzuCraftyBoxes.yml") && l.Contains("changed"));
            Assert.Contains(d, l => l.Contains("y.cfg") && l.Contains("added"));
            Assert.DoesNotContain(d, l => l.Contains("x.cfg"));
        }

        [Fact]
        public void BrokenOrEmptyListsDoNotStopTheComparison()
        {
            var d = SessionDiff.Mods("", "garbage line without tabs\n" + ModsB.Split('\n')[1]);

            Assert.Contains(d, l => l.Contains("SafetyStatus") && l.Contains("added"));
        }
    }
}
