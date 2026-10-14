using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// When something goes wrong a detailed report is written on its own. A burst of the same
    /// problem must not bury the disk in near-identical reports, but pressing the "lag now" key
    /// must always produce one.
    /// </summary>
    public sealed class IncidentPolicyTests
    {
        private static IncidentPolicy Policy(double cooldown = 30, int cap = 5) => new IncidentPolicy(cooldown, cap);

        [Fact]
        public void TheFirstProblemOfAKindIsWritten()
        {
            Assert.True(Policy().ShouldWrite("hitch", 100, manual: false));
        }

        [Fact]
        public void TheSameProblemRightAfterIsNotWrittenAgain()
        {
            var p = Policy(cooldown: 30);
            p.ShouldWrite("hitch", 100, false);

            Assert.False(p.ShouldWrite("hitch", 110, false));
        }

        [Fact]
        public void TheSameProblemIsWrittenAgainOnceTheQuietTimeHasPassed()
        {
            var p = Policy(cooldown: 30);
            p.ShouldWrite("hitch", 100, false);

            Assert.True(p.ShouldWrite("hitch", 131, false));
        }

        [Fact]
        public void DifferentProblemsDoNotHoldEachOtherBack()
        {
            var p = Policy(cooldown: 30);
            p.ShouldWrite("hitch", 100, false);

            Assert.True(p.ShouldWrite("server-silent", 101, false));
        }

        [Fact]
        public void AfterTheSessionLimitOnlyTheKeyStillWritesReports()
        {
            var p = Policy(cooldown: 0, cap: 2);
            p.ShouldWrite("a", 1, false);
            p.ShouldWrite("b", 2, false);

            Assert.False(p.ShouldWrite("c", 3, false));
            Assert.True(p.ShouldWrite("manual", 4, manual: true));
        }

        [Fact]
        public void TheKeyWorksEvenRightAfterItWasPressed()
        {
            var p = Policy(cooldown: 30);
            p.ShouldWrite("manual", 100, true);

            Assert.True(p.ShouldWrite("manual", 101, true));
        }

        [Fact]
        public void PressingTheKeyNeverUsesUpTheReportsWrittenOnTheirOwn()
        {
            // A benchmark marks many moments by hand; the problems after it must still get reports.
            var p = Policy(cooldown: 0, cap: 2);
            for (int i = 0; i < 10; i++) p.ShouldWrite("manual", i, true);

            Assert.True(p.ShouldWrite("hitch", 20, false));
            Assert.True(p.ShouldWrite("slow", 21, false));
            Assert.False(p.ShouldWrite("silent", 22, false));
        }

        [Fact]
        public void ProblemsThatWereNotWrittenAreStillCounted()
        {
            var p = Policy(cooldown: 30);
            p.ShouldWrite("hitch", 100, false);
            p.ShouldWrite("hitch", 101, false);
            p.ShouldWrite("hitch", 102, false);

            Assert.Equal(2, p.Suppressed("hitch"));
            Assert.Equal(3, p.Seen("hitch"));
            Assert.Equal(0, p.Suppressed("other"));
        }
    }
}
