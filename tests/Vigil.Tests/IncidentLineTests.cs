using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The panel lists this session's incidents as rows: when, what kind, and what happened, with
    /// the report's file kept apart so it does not crowd the row.
    /// </summary>
    public sealed class IncidentLineTests
    {
        [Fact]
        public void AnIncidentIsShownAsItsTimeKindAndWhatHappened()
        {
            var i = IncidentLine.Parse("10:05:10 hitch: a 1171 ms frame (limit 250 ms) (Vigil-incident-100510-hitch.txt)");
            Assert.Equal("10:05:10", i.Time);
            Assert.Equal("hitch", i.Kind);
            Assert.Equal("a 1171 ms frame (limit 250 ms)", i.Detail);
            Assert.Equal("Vigil-incident-100510-hitch.txt", i.File);
        }

        [Fact]
        public void AKindWithADashIsKeptWhole()
        {
            var i = IncidentLine.Parse("09:59:58 log-flood: 299 log lines in one second (Vigil-incident-095958-log-flood.txt)");
            Assert.Equal("log-flood", i.Kind);
            Assert.Equal("299 log lines in one second", i.Detail);
        }

        [Fact]
        public void ALineOfAnotherShapeIsShownWhole()
        {
            var i = IncidentLine.Parse("something else");
            Assert.Equal("", i.Time);
            Assert.Equal("", i.Kind);
            Assert.Equal("something else", i.Detail);
            Assert.Equal("", i.File);
        }

        [Fact]
        public void ALineThatStartsWithoutATimeIsShownWholeEvenWithAKindInIt()
        {
            var i = IncidentLine.Parse("the server said hitch: a long frame (Vigil-incident-1.txt)");
            Assert.Equal("", i.Time);
            Assert.Equal("", i.Kind);
            Assert.Equal("the server said hitch: a long frame (Vigil-incident-1.txt)", i.Detail);
        }
    }
}
