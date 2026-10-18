using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The game tells a frame's length on the frame after it, so a hitch report is taken one frame
    /// after the long frame. The 10 s report window and the session summary end on the real clock;
    /// due during a long frame, they wait that one frame, so the report still holds what the long
    /// frame handled (a player's join messages, say) instead of a window just begun.
    /// </summary>
    public sealed class ReportWindowTests
    {
        [Fact]
        public void A_window_due_in_a_long_frame_waits_one_frame_for_the_hitch_report()
        {
            Assert.False(ReportWindow.CloseNow(spanSeconds: 10.3f, windowSeconds: 10f, frameMs: 1630f, longFrameMs: 250f));
            Assert.True(ReportWindow.CloseNow(spanSeconds: 10.34f, windowSeconds: 10f, frameMs: 33f, longFrameMs: 250f));
        }

        [Fact]
        public void A_window_closes_once_due_and_not_before()
        {
            Assert.True(ReportWindow.CloseNow(10f, 10f, 33f, 250f));
            Assert.False(ReportWindow.CloseNow(9.9f, 10f, 33f, 250f));
        }

        [Fact]
        public void A_frame_as_long_as_the_hitch_limit_counts_as_long()
        {
            Assert.True(ReportWindow.CloseNow(10f, 10f, 249f, 250f));
            Assert.False(ReportWindow.CloseNow(10f, 10f, 250f, 250f));
        }
    }
}
