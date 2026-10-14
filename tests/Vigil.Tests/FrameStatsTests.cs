using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// "Average fps" hides stutter. The session summary gives how long typical, bad and worst
    /// frames took, so a session that stutters reads differently from one that is merely slow.
    /// </summary>
    public sealed class FrameStatsTests
    {
        [Fact]
        public void TypicalBadAndWorstFramesAreReported()
        {
            var s = new FrameStats();
            for (int i = 0; i < 98; i++) s.Add(20);
            s.Add(100); s.Add(900);

            Assert.Equal(20, s.Percentile(50), 1);
            Assert.Equal(900, s.Max, 1);
            Assert.True(s.Percentile(99) >= 100);
        }

        [Fact]
        public void TheShareOfFramesAboveALimitIsCounted()
        {
            var s = new FrameStats();
            for (int i = 0; i < 90; i++) s.Add(16);
            for (int i = 0; i < 10; i++) s.Add(60);

            Assert.Equal(10, s.CountAbove(50));
            Assert.Equal(0.1, s.ShareAbove(50), 3);
        }

        [Fact]
        public void LongSessionsDoNotNeedMemoryForEveryFrame()
        {
            var s = new FrameStats();
            for (int i = 0; i < 2_000_000; i++) s.Add(i % 2 == 0 ? 10 : 30);

            Assert.Equal(2_000_000, s.Count);
            Assert.InRange(s.Percentile(50), 9, 31);
            Assert.Equal(30, s.Max, 1);
        }

        [Fact]
        public void NoFramesMeansNoNumbers()
        {
            var s = new FrameStats();

            Assert.Equal(0, s.Count);
            Assert.Equal(0, s.Percentile(99));
            Assert.Equal(0, s.ShareAbove(50));
        }

        [Fact]
        public void TheSummaryLineReadsAtAGlance()
        {
            var s = new FrameStats();
            for (int i = 0; i < 100; i++) s.Add(25);

            string line = s.Describe();

            Assert.Contains("median 25", line);
            Assert.Contains("40 fps", line);
        }
    }
}
