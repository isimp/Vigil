using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// After a session, the worst seconds should be one command away: the timeline is read back,
    /// the seconds with the longest frames come first, and any column can be summarised.
    /// </summary>
    public sealed class TimelineReaderTests
    {
        private const string Tsv =
            "time\tsituation\tdt_max_ms\tping_ms\ttop_method\n" +
            "18:00:01\tin world\t40\t30\tZNet.Update\n" +
            "18:00:02\tin world\t1200\t35\tZoneSystem.Update\n" +
            "18:00:03\tloading\t9000\t\tGame.Update\n" +
            "18:00:04\tin world\t300\t400\tZNet.Update\n";

        [Fact]
        public void TheWorstSecondsInWorldComeFirst()
        {
            var t = TimelineReader.Parse(Tsv);

            var worst = t.Worst("dt_max_ms", 2, onlyWhere: ("situation", "in world"));

            Assert.Equal(new[] { "18:00:02", "18:00:04" }, worst.Select(r => r["time"]).ToArray());
        }

        [Fact]
        public void AnyColumnCanBeSummarised()
        {
            var t = TimelineReader.Parse(Tsv);

            var s = t.Stats("ping_ms");

            Assert.Equal(3, s.Count);
            Assert.Equal(400, s.Max, 1);
            Assert.Equal(155, s.Mean, 1);
        }

        [Fact]
        public void EmptyCellsAndTextAreSkippedInNumbers()
        {
            var t = TimelineReader.Parse(Tsv);

            Assert.Equal(0, t.Stats("top_method").Count);
            Assert.Equal(3, t.Stats("ping_ms").Count);
            Assert.Null(t.Rows[2]["ping_ms"]);
        }

        [Fact]
        public void AColumnThatIsNotThereGivesNothingRatherThanFailing()
        {
            var t = TimelineReader.Parse(Tsv);

            Assert.Equal(0, t.Stats("nope").Count);
            Assert.Empty(t.Worst("nope", 5));
        }

        [Fact]
        public void ShortOrBrokenLinesDoNotStopTheReading()
        {
            var t = TimelineReader.Parse(Tsv + "18:00:05\tin world\n\n");

            Assert.Equal(5, t.Rows.Count);
            Assert.Null(t.Rows.Last()["ping_ms"]);
        }
    }
}
