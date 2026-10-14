using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The panel's load test is a short form: which test, for the player simulation how many
    /// players and how they move, and two options. Starting it runs the same console command as
    /// typing it, so the panel and the console behave alike.
    /// </summary>
    public sealed class LoadFormTests
    {
        [Fact]
        public void EachTestRunsItsConsoleCommand()
        {
            Assert.Equal("load", LoadForm.Command(LoadForm.Test.BothWays, "16", "stay", false, false));
            Assert.Equal("load up", LoadForm.Command(LoadForm.Test.Upload, "16", "stay", false, false));
            Assert.Equal("load down", LoadForm.Command(LoadForm.Test.Download, "16", "stay", false, false));
            Assert.Equal("load players 16 travel", LoadForm.Command(LoadForm.Test.Players, "16", "travel", false, false));
        }

        [Fact]
        public void TheOptionsAreAddedToAnyTest()
        {
            Assert.Equal("load up force deep", LoadForm.Command(LoadForm.Test.Upload, "16", "stay", true, true));
            Assert.Equal("load players 8 spread deep", LoadForm.Command(LoadForm.Test.Players, "8", "spread", false, true));
        }

        [Fact]
        public void APlayerCountThatIsNoNumberIsLeftToTheConsolesDefault()
        {
            Assert.Equal("load players stay", LoadForm.Command(LoadForm.Test.Players, "lots", "stay", false, false));
            Assert.Equal("load players stay", LoadForm.Command(LoadForm.Test.Players, " ", "stay", false, false));
        }

        [Fact]
        public void TheTestsAreNamedInTheFormsOrder()
        {
            Assert.Equal(new[] { "Both ways", "Upload", "Download", "Simulated players" }, LoadForm.Names);
        }
    }
}
