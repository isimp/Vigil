using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Before the server tooling stops a server with players on, it leaves a notice for the
    /// server's Vigil, which tells every player on screen that the server restarts and when:
    /// at once, then with 30 and with 10 seconds left. A notice that makes no sense is ignored.
    /// </summary>
    public sealed class RestartNoticeTests
    {
        [Fact]
        public void The_notice_the_tooling_leaves_is_read_with_its_time_and_reason()
        {
            var n = RestartNotice.Parse(RestartNotice.Format(60, "mod update"));
            Assert.Equal(60, n.Seconds);
            Assert.Equal("mod update", n.Reason);
        }

        [Fact]
        public void A_damaged_or_absurd_notice_is_ignored()
        {
            Assert.Null(RestartNotice.Parse(null));
            Assert.Null(RestartNotice.Parse("hello"));
            Assert.Null(RestartNotice.Parse("seconds=0"));
            Assert.Null(RestartNotice.Parse("seconds=-5"));
            Assert.Null(RestartNotice.Parse("seconds=99999"));
        }

        [Fact]
        public void A_notice_without_a_reason_still_counts()
        {
            Assert.Equal(45, RestartNotice.Parse("seconds=45").Seconds);
        }

        [Fact]
        public void Players_hear_of_it_with_30_and_with_10_seconds_left_once_each()
        {
            Assert.False(RestartNotice.Due(60, 59));
            Assert.True(RestartNotice.Due(30.4, 29.9));
            Assert.False(RestartNotice.Due(29.9, 29.0));
            Assert.True(RestartNotice.Due(10.2, 9.8));
            Assert.False(RestartNotice.Due(9.8, 8.8));
            Assert.True(RestartNotice.Due(31, 9));
        }

        [Fact]
        public void The_words_say_when_and_why()
        {
            Assert.Equal("The server restarts in 60 s (mod update)", RestartNotice.Message(60, "mod update"));
            Assert.Equal("The server restarts in 10 s", RestartNotice.Message(9.8, null));
        }
    }
}
