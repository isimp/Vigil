using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A server starting up, or a player joining, stalls and floods by design: the world loads,
    /// the joining player is sent the area around them while their own loading screen keeps them
    /// quiet. Problems seen in that time are not reported until things have settled.
    /// </summary>
    public sealed class SettlingTests
    {
        [Fact]
        public void SomethingThatJustStartedHasNotSettled()
        {
            var s = new Settling(90);
            s.Started("Bjorn", 100);

            Assert.False(s.Settled("Bjorn", 150));
        }

        [Fact]
        public void AfterTheQuietTimeItHasSettled()
        {
            var s = new Settling(90);
            s.Started("Bjorn", 100);

            Assert.True(s.Settled("Bjorn", 190));
        }

        [Fact]
        public void StartingAgainRestartsTheQuietTime()
        {
            var s = new Settling(90);
            s.Started("Bjorn", 100);
            s.Started("Bjorn", 500);

            Assert.False(s.Settled("Bjorn", 520));
        }

        [Fact]
        public void SomethingNeverSeenStartingCountsAsSettled()
        {
            Assert.True(new Settling(90).Settled("someone", 5));
        }

        [Fact]
        public void EachOneSettlesOnItsOwn()
        {
            var s = new Settling(90);
            s.Started("a", 0);
            s.Started("b", 80);

            Assert.True(s.Settled("a", 100));
            Assert.False(s.Settled("b", 100));
        }
    }
}
