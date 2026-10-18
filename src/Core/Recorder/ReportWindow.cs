namespace Vigil.Core
{
    /// <summary>
    /// When the 10 s report window and the session summary end. They go by the real clock, while
    /// the game tells a frame's length only on the frame after it, which is when a hitch report is
    /// taken; so one due during a long frame waits that one frame, and the report still holds what
    /// the long frame handled instead of a window just begun.
    /// </summary>
    internal static class ReportWindow
    {
        /// <summary>Whether a window running <paramref name="spanSeconds"/> ends in this frame, which took <paramref name="frameMs"/> by the real clock.</summary>
        public static bool CloseNow(float spanSeconds, float windowSeconds, float frameMs, float longFrameMs) =>
            spanSeconds >= windowSeconds && frameMs < longFrameMs;
    }
}
