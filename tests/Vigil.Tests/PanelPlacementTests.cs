using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The panel opens where it was left, on the tab last used, and always fully on screen, also
    /// after a change to a smaller screen or resolution. What it remembers lives in Vigil's own
    /// folder on this machine, not in the shared config.
    /// </summary>
    public sealed class PanelPlacementTests
    {
        [Fact]
        public void ThePanelOpensWhereItWasLeftOnTheSameTab()
        {
            var p = PanelPlacement.Parse("300,200,640,480,Network");
            var back = PanelPlacement.Parse(p.Format());
            Assert.Equal(300, back.X);
            Assert.Equal(200, back.Y);
            Assert.Equal(640, back.Width);
            Assert.Equal(480, back.Height);
            Assert.Equal("Network", back.Tab);
        }

        [Fact]
        public void APanelOffAShrunkenScreenIsBroughtBack()
        {
            var p = PanelPlacement.Parse("2400,1300,640,480,Overview").Clamped(1920, 1080);
            Assert.Equal(1920 - 640, p.X);
            Assert.Equal(1080 - 480, p.Y);
        }

        [Fact]
        public void APanelLargerThanTheScreenIsMadeToFit()
        {
            var p = PanelPlacement.Parse("0,0,3000,2000,Overview").Clamped(1280, 720);
            Assert.Equal(0, p.X);
            Assert.Equal(0, p.Y);
            Assert.Equal(1280, p.Width);
            Assert.Equal(720, p.Height);
        }

        [Fact]
        public void APanelTooSmallToUseGrowsToItsSmallestSize()
        {
            var p = PanelPlacement.Parse("10,10,50,40,Overview").Clamped(1920, 1080);
            Assert.Equal(PanelPlacement.MinWidth, p.Width);
            Assert.Equal(PanelPlacement.MinHeight, p.Height);
        }

        [Fact]
        public void DraggingTheCornerResizesThePanelAndLeavesItsTopLeftWhereItIs()
        {
            var p = PanelPlacement.Parse("100,80,640,480,Tools").Resized(900, 600, 1920, 1080);
            Assert.Equal(100, p.X);
            Assert.Equal(80, p.Y);
            Assert.Equal(900, p.Width);
            Assert.Equal(600, p.Height);
            Assert.Equal("Tools", p.Tab);
        }

        [Fact]
        public void TheCornerStopsAtTheSmallestUsableSize()
        {
            var p = PanelPlacement.Parse("100,80,640,480,Overview").Resized(100, -50, 1920, 1080);
            Assert.Equal(PanelPlacement.MinWidth, p.Width);
            Assert.Equal(PanelPlacement.MinHeight, p.Height);
        }

        [Fact]
        public void TheCornerStopsAtTheScreenEdgeRatherThanPushingThePanelAside()
        {
            var p = PanelPlacement.Parse("1200,700,640,320,Overview").Resized(1500, 900, 1920, 1080);
            Assert.Equal(1200, p.X);
            Assert.Equal(700, p.Y);
            Assert.Equal(1920 - 1200, p.Width);
            Assert.Equal(1080 - 700, p.Height);
        }

        [Fact]
        public void WithoutAnythingRememberedItOpensCentred()
        {
            var p = PanelPlacement.Parse(null).Clamped(1920, 1080);
            Assert.Equal((1920 - PanelPlacement.DefaultWidth) / 2, p.X);
            Assert.Equal((1080 - PanelPlacement.DefaultHeight) / 2, p.Y);
            Assert.Equal("Overview", p.Tab);
        }

        [Fact]
        public void ADamagedFileOpensItCentred()
        {
            var p = PanelPlacement.Parse("abc,,7").Clamped(1920, 1080);
            Assert.Equal(PanelPlacement.DefaultWidth, p.Width);
            Assert.Equal((1920 - PanelPlacement.DefaultWidth) / 2, p.X);
        }

        [Fact]
        public void FoldedCardsAndResultsStayFoldedTheNextTimeThePanelOpens()
        {
            var p = PanelPlacement.Parse("300,200,640,480,Tools");
            p.Folds.Add("card:Tools/Inspect");
            p.Folds.Add("result:load");
            var back = PanelPlacement.Parse(p.Format());
            Assert.Equal(new[] { "card:Tools/Inspect", "result:load" }, back.Folds.OrderBy(f => f, System.StringComparer.Ordinal).ToArray());
            Assert.Equal("Tools", back.Tab);
        }

        [Fact]
        public void APlaceRememberedBeforeFoldsExistedOpensWithNothingFolded()
        {
            var p = PanelPlacement.Parse("300,200,640,480,Network");
            Assert.Empty(p.Folds);
            Assert.Equal("Network", p.Tab);
        }

        [Fact]
        public void AFoldNamedWithTheLinesOwnSeparatorsComesBackAsItWas()
        {
            var p = PanelPlacement.Parse("300,200,640,480,World");
            p.Folds.Add("card:World/Spawns, marks|50%");
            var back = PanelPlacement.Parse(p.Format());
            Assert.Equal(300, back.X);
            Assert.Equal("World", back.Tab);
            Assert.Equal("card:World/Spawns, marks|50%", Assert.Single(back.Folds));
        }

        [Fact]
        public void BringingThePanelOnScreenOrResizingItKeepsWhatIsFolded()
        {
            var p = PanelPlacement.Parse("5000,200,640,480,Tools");
            p.Folds.Add("card:Tools/Load test");
            Assert.Contains("card:Tools/Load test", p.Clamped(1920, 1080).Folds);
            Assert.Contains("card:Tools/Load test", p.Clamped(1920, 1080).Resized(700, 500, 1920, 1080).Folds);
        }
    }
}
