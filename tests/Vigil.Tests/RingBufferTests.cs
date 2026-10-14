using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The recorder keeps the recent past (the last few minutes of frames and seconds) so a report
    /// written when lag happens can show what led up to it, without memory growing over a session.
    /// </summary>
    public sealed class RingBufferTests
    {
        [Fact]
        public void TheRecentPastIsReadOldestFirst()
        {
            var r = new RingBuffer<int>(5);
            r.Add(1); r.Add(2); r.Add(3);

            Assert.Equal(new[] { 1, 2, 3 }, r.ToArray());
        }

        [Fact]
        public void WhenFullTheOldestEntriesMakeRoomForNewOnes()
        {
            var r = new RingBuffer<int>(3);
            for (int i = 1; i <= 7; i++) r.Add(i);

            Assert.Equal(new[] { 5, 6, 7 }, r.ToArray());
            Assert.Equal(3, r.Count);
        }

        [Fact]
        public void TheLastFewEntriesCanBeTakenOnTheirOwn()
        {
            var r = new RingBuffer<int>(10);
            for (int i = 1; i <= 12; i++) r.Add(i);

            Assert.Equal(new[] { 10, 11, 12 }, r.Last(3));
            Assert.Equal(r.ToArray(), r.Last(50));
        }

        [Fact]
        public void TheNewestEntryIsAtHand()
        {
            var r = new RingBuffer<string>(3);
            Assert.False(r.TryNewest(out _));
            r.Add("a"); r.Add("b");

            Assert.True(r.TryNewest(out var n));
            Assert.Equal("b", n);

            r.Add("c"); r.Add("d");
            Assert.True(r.TryNewest(out n));
            Assert.Equal("d", n);
        }

        [Fact]
        public void AnEmptyBufferHasNothingToShow()
        {
            var r = new RingBuffer<int>(4);

            Assert.Empty(r.ToArray());
            Assert.Empty(r.Last(2));
            Assert.Equal(0, r.Count);
        }

        [Fact]
        public void ClearingForgetsEverything()
        {
            var r = new RingBuffer<int>(3);
            r.Add(1); r.Add(2);
            r.Clear();
            r.Add(9);

            Assert.Equal(new[] { 9 }, r.ToArray().ToArray());
        }
    }
}
