using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Some problems are states that last (memory nearly full, unsent data piling up). Such a state
    /// is reported when it begins, not every few seconds while it lasts; once it has properly ended
    /// and comes back, that is news again.
    /// </summary>
    public sealed class ConditionGateTests
    {
        [Fact]
        public void AStateIsReportedWhenItBegins()
        {
            var g = new ConditionGate(clearSeconds: 30);

            Assert.True(g.Update("memory", true, 10));
        }

        [Fact]
        public void AStateThatLastsIsNotReportedAgain()
        {
            var g = new ConditionGate(30);
            g.Update("memory", true, 10);

            for (int t = 11; t < 300; t++) Assert.False(g.Update("memory", true, t));
        }

        [Fact]
        public void AStateThatEndedProperlyAndReturnsIsReportedAgain()
        {
            var g = new ConditionGate(30);
            g.Update("memory", true, 10);
            g.Update("memory", false, 11);
            g.Update("memory", false, 45);

            Assert.True(g.Update("memory", true, 46));
        }

        [Fact]
        public void ABriefDipDoesNotCountAsANewBeginning()
        {
            var g = new ConditionGate(30);
            g.Update("memory", true, 10);
            g.Update("memory", false, 11);

            Assert.False(g.Update("memory", true, 20));
        }

        [Fact]
        public void ABriefDipAfterALongStretchIsStillBrief()
        {
            var g = new ConditionGate(30);
            for (int t = 100; t <= 200; t++) g.Update("memory", true, t);
            g.Update("memory", false, 201);

            Assert.False(g.Update("memory", true, 205));
        }

        [Fact]
        public void DifferentStatesAreTrackedApart()
        {
            var g = new ConditionGate(30);
            g.Update("memory", true, 10);

            Assert.True(g.Update("send-queue", true, 11));
        }

        [Fact]
        public void HowLongAStateHasLastedIsKnown()
        {
            var g = new ConditionGate(30);
            g.Update("memory", true, 10);
            g.Update("memory", true, 70);

            Assert.Equal(60, g.ActiveFor("memory", 70), 1);
            Assert.Equal(0, g.ActiveFor("send-queue", 70), 1);
        }
    }
}
