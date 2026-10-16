using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "Skip winter": when Seasonality is about to start winter, it starts spring instead,
    /// so the seasons go spring, summer, fall, spring; every other change stays as it was.
    /// </summary>
    public sealed class SkipWinterTests
    {
        [Fact]
        public void Winter_coming_next_becomes_spring_and_the_others_stay()
        {
            Assert.Equal("Spring", SeasonSkip.Next("Winter"));
            Assert.Equal("Summer", SeasonSkip.Next("Summer"));
            Assert.Equal("Fall", SeasonSkip.Next("Fall"));
            Assert.Equal("Spring", SeasonSkip.Next("Spring"));
        }

        [Fact]
        public void The_state_says_off_working_stepped_aside_or_not_needed()
        {
            Assert.Equal("off: the server's switch", SeasonSkip.StateLine(false, true, "3.8.4", null));
            Assert.Equal("working · Seasonality 3.8.4 hooked, checked: fall now goes to spring", SeasonSkip.StateLine(true, true, "3.8.4", null));
            Assert.Equal("not needed: Seasonality is not installed", SeasonSkip.StateLine(true, false, null, null));
            Assert.Equal("stepped aside: the hook did not take: fall still goes to Winter", SeasonSkip.StateLine(true, true, "3.9.0", "the hook did not take: fall still goes to Winter"));
        }
    }
}
