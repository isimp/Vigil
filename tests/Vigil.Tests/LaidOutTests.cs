using System;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The panel lays a tab out in one pass and draws it in the next, and a click is handled during
    /// the first. A click that makes a section's badge appear (Compare, Show, Find) must not make the
    /// second pass draw a badge the first never laid out, which the game logs as an error and which
    /// leaves the frame half drawn. So the panel draws what the layout pass read, and shows the
    /// click's effect from the next layout pass on.
    /// </summary>
    public sealed class LaidOutTests
    {
        [Fact]
        public void ABadgeAClickMakesAppearWaitsForTheNextLayoutPass()
        {
            var view = new LaidOut<string>();
            string badge = null;
            Assert.Null(view.Read(layoutPass: true, () => badge));
            badge = "comparing";
            Assert.Null(view.Read(layoutPass: false, () => badge));
            Assert.Equal("comparing", view.Read(layoutPass: true, () => badge));
            Assert.Equal("comparing", view.Read(layoutPass: false, () => badge));
        }

        [Fact]
        public void ABadgeAClickTakesAwayStaysUntilTheNextLayoutPass()
        {
            var view = new LaidOut<string>();
            string badge = "shown";
            view.Read(layoutPass: true, () => badge);
            badge = null;
            Assert.Equal("shown", view.Read(layoutPass: false, () => badge));
            Assert.Null(view.Read(layoutPass: true, () => badge));
        }

        [Fact]
        public void EveryLayoutPassReadsAfresh()
        {
            var view = new LaidOut<string>();
            int reads = 0;
            view.Read(layoutPass: true, () => (++reads).ToString());
            view.Read(layoutPass: false, () => (++reads).ToString());
            view.Read(layoutPass: false, () => (++reads).ToString());
            Assert.Equal("2", view.Read(layoutPass: true, () => (++reads).ToString()));
            Assert.Equal(2, reads);
        }

        [Fact]
        public void APassBeforeAnyLayoutPassReadsWhatIsThere()
        {
            var view = new LaidOut<string>();
            Assert.Equal("on", view.Read(layoutPass: false, () => "on"));
        }

        [Fact]
        public void ALayoutPassWhoseReadFailsLeavesNoOlderViewBehind()
        {
            var view = new LaidOut<string>();
            view.Read(layoutPass: true, () => "old");
            Assert.Throws<InvalidOperationException>(() => view.Read(layoutPass: true, () => throw new InvalidOperationException("gone")));
            Assert.Equal("new", view.Read(layoutPass: false, () => "new"));
        }
    }
}
