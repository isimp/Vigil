using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The HUD line in a corner of the screen shows at a glance how the game runs: frame rate, frame
    /// time, ping, data waiting to go out, machine memory and, when known, the server's frame rate.
    /// A value turns amber as it nears the limit that writes an incident report and red at it, so
    /// the line warns before and when a report is written. A value not measured is left out.
    /// </summary>
    public sealed class HudLineTests
    {
        private static readonly HudLine.Limits Limits = new HudLine.Limits { SlowFps = 20, PingMs = 400, QueueKb = 64, MemoryLoadPct = 95 };

        private static HudLine.Values Healthy() => new HudLine.Values
        {
            Fps = 58, FrameAvgMs = 17.2, FrameMaxMs = 31, PingMs = 31, QueueKb = 1.2, MemLoadPct = 80, ServerFps = 30, Recording = true,
        };

        private static string Text(HudLine.Values v) => string.Join(" ", HudLine.Build(v, Limits).Select(s => s.Text));

        private static HudLine.Level LevelOf(HudLine.Values v, string startsWith) =>
            HudLine.Build(v, Limits).First(s => s.Text.StartsWith(startsWith, System.StringComparison.Ordinal)).Level;

        [Fact]
        public void AHealthyGameShowsItsNumbersWithoutWarnings()
        {
            Assert.Equal("58 fps 17 ms ping 31 queue 1.2 kB mem 80% server 30 fps REC", Text(Healthy()));
            Assert.All(HudLine.Build(Healthy(), Limits), s => Assert.Equal(HudLine.Level.Fine, s.Level));
        }

        [Fact]
        public void AFrameRateBelowTheSlowLimitIsRedAndNearItAmber()
        {
            var v = Healthy();
            v.Fps = 18;
            Assert.Equal(HudLine.Level.Bad, LevelOf(v, "18 fps"));
            v.Fps = 25;
            Assert.Equal(HudLine.Level.Warn, LevelOf(v, "25 fps"));
            v.Fps = 31;
            Assert.Equal(HudLine.Level.Fine, LevelOf(v, "31 fps"));
        }

        [Fact]
        public void APingAtTheReportLimitIsRedAndPastHalfOfItAmber()
        {
            var v = Healthy();
            v.PingMs = 400;
            Assert.Equal(HudLine.Level.Bad, LevelOf(v, "ping"));
            v.PingMs = 220;
            Assert.Equal(HudLine.Level.Warn, LevelOf(v, "ping"));
        }

        [Fact]
        public void DataPilingUpToTheReportLimitIsRed()
        {
            var v = Healthy();
            v.QueueKb = 70;
            Assert.Equal(HudLine.Level.Bad, LevelOf(v, "queue"));
            v.QueueKb = 40;
            Assert.Equal(HudLine.Level.Warn, LevelOf(v, "queue"));
        }

        [Fact]
        public void MachineMemoryAtTheReportLimitIsRedAndJustBelowItAmber()
        {
            var v = Healthy();
            v.MemLoadPct = 96;
            Assert.Equal(HudLine.Level.Bad, LevelOf(v, "mem"));
            v.MemLoadPct = 91;
            Assert.Equal(HudLine.Level.Warn, LevelOf(v, "mem"));
        }

        [Fact]
        public void ASlowServerIsAmberAndAVerySlowOneRed()
        {
            var v = Healthy();
            v.ServerFps = 22;
            Assert.Equal(HudLine.Level.Warn, LevelOf(v, "server"));
            v.ServerFps = 12;
            Assert.Equal(HudLine.Level.Bad, LevelOf(v, "server"));
        }

        [Fact]
        public void AValueNotMeasuredIsLeftOut()
        {
            var v = Healthy();
            v.PingMs = double.NaN;
            v.ServerFps = double.NaN;
            v.QueueKb = double.NaN;
            Assert.Equal("58 fps 17 ms mem 80% REC", Text(v));
        }

        [Fact]
        public void NotRecordingIsShownInAmber()
        {
            var v = Healthy();
            v.Recording = false;
            Assert.EndsWith("paused", Text(v));
            Assert.Equal(HudLine.Level.Warn, LevelOf(v, "paused"));
        }

        [Fact]
        public void TheLineFitsInOneShortRow()
        {
            var v = new HudLine.Values { Fps = 144, FrameAvgMs = 6.9, PingMs = 1234, QueueKb = 123.4, MemLoadPct = 99, ServerFps = 30, Recording = true };
            Assert.True(Text(v).Length <= 70, Text(v));
        }
    }
}
