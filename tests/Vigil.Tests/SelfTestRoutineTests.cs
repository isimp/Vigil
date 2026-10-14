using System;
using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The self-test's routines exercise Vigil's features end to end. The full tier fetches one of
    /// the server's reports and checks it arrived whole, so it picks the smallest that cannot change
    /// meanwhile; it sends a small test incident through sharing and gets it back; and it judges
    /// each question to the server by whether it was answered, and how fast.
    /// </summary>
    public sealed class SelfTestRoutineTests
    {
        private static ReportCatalog.Entry E(string path, long size) =>
            new ReportCatalog.Entry { Path = path, Size = size, Kind = ReportCatalog.KindOf(path.Substring(path.LastIndexOf('/') + 1)) };

        [Fact]
        public void The_report_fetched_is_the_smallest_that_cannot_change_while_it_comes()
        {
            var list = new List<ReportCatalog.Entry>
            {
                E("Vigil-summary.txt", 100),
                E("Vigil-selftest-server.txt", 200),
                E("Vigil-incident-211504-drop.txt", 52000),
                E("Vigil-runs/20261013-220715/Vigil-load-163001.txt", 9000),
            };
            Assert.Equal("Vigil-runs/20261013-220715/Vigil-load-163001.txt", SelfTestRoutines.SmallestFixed(list).Path);
            Assert.Null(SelfTestRoutines.SmallestFixed(new[] { E("Vigil-summary.txt", 100) }));
        }

        [Fact]
        public void The_test_incident_is_a_shareable_report_that_says_what_it_is()
        {
            var at = new DateTime(2026, 10, 13, 23, 1, 2);
            string name = SelfTestRoutines.TestIncidentName(at);
            Assert.Equal("Vigil-incident-230102-selftest.txt", name);
            Assert.True(ShareQueue.Shareable(name));
            Assert.Contains("self-test", ReportCatalog.Headline("selftest", SelfTestRoutines.TestIncidentText(at).Split('\n')));
        }

        [Fact]
        public void A_question_to_the_server_passes_when_answered_soon_and_says_so_when_slow_or_unanswered()
        {
            Assert.Equal(SelfTestReport.Result.Pass, SelfTestRoutines.Judge(true, 0.3));
            Assert.Equal(SelfTestReport.Result.Warn, SelfTestRoutines.Judge(true, 6));
            Assert.Equal(SelfTestReport.Result.Fail, SelfTestRoutines.Judge(false, 0.3));
        }
    }
}
