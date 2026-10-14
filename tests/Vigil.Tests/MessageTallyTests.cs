using System;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The network-message report has to show which kind of message floods the connection
    /// (for example one status effect sent to one creature fifty times a second), both for the
    /// last few seconds and for the whole session, so two sessions can be compared.
    /// </summary>
    public sealed class MessageTallyTests
    {
        private static string[] Lines(string report) =>
            report.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        private static string LineOf(string report, string label) =>
            Lines(report).Single(l => l.Contains(label));

        [Fact]
        public void AWindowShowsHowOftenEachKindWasSentPerSecond()
        {
            var t = new MessageTally();
            for (int i = 0; i < 500; i++) t.Add("RPC_AddStatusEffect(SafeStatusEffect) -> Boar", 40);
            t.Add("RPC_Damage -> Greydwarf", 120);

            string report = t.EndWindow(10f, 10);

            string boar = LineOf(report, "RPC_AddStatusEffect(SafeStatusEffect) -> Boar");
            Assert.Contains("500", boar);
            Assert.Contains("50.0/s", boar);
            Assert.Contains("19.5 kB", boar);
        }

        [Fact]
        public void TheBusiestKindIsListedFirst()
        {
            var t = new MessageTally();
            t.Add("rare", 10);
            for (int i = 0; i < 30; i++) t.Add("busy", 10);
            for (int i = 0; i < 5; i++) t.Add("middle", 10);

            var lines = Lines(t.EndWindow(10f, 10));

            int busy = Array.FindIndex(lines, l => l.Contains("busy"));
            int middle = Array.FindIndex(lines, l => l.Contains("middle"));
            int rare = Array.FindIndex(lines, l => l.Contains("rare"));
            Assert.True(busy < middle && middle < rare);
        }

        [Fact]
        public void TrafficCanBeRankedByBytesSoAFewHugeMessagesOutrankManySmallOnes()
        {
            var t = new MessageTally(rankByBytes: true);
            for (int i = 0; i < 100; i++) t.Add("chatty", 10);
            for (int i = 0; i < 2; i++) t.Add("bulky", 50000);

            var lines = Lines(t.EndWindow(10f, 10));

            Assert.True(Array.FindIndex(lines, l => l.Contains("bulky")) < Array.FindIndex(lines, l => l.Contains("chatty")));
        }

        [Fact]
        public void TheWindowHeaderGivesTheTotalsForThatWindow()
        {
            var t = new MessageTally();
            for (int i = 0; i < 300; i++) t.Add("a", 100);
            for (int i = 0; i < 100; i++) t.Add("b", 100);

            string header = Lines(t.EndWindow(10f, 10)).First();

            Assert.Contains("400 messages", header);
            Assert.Contains("40.0/s", header);
            Assert.Contains("39.1 kB", header);
        }

        [Fact]
        public void BeyondTheListedKindsTheReportSaysHowManyMoreThereWere()
        {
            var t = new MessageTally();
            for (int k = 0; k < 15; k++) t.Add("RPC_Sample" + k, 10);

            string report = t.EndWindow(10f, 10);

            Assert.Equal(10, Lines(report).Count(l => l.Contains("RPC_Sample")));
            Assert.Contains("5 more kinds", report);
        }

        [Fact]
        public void EachWindowCountsOnlyItsOwnMessages()
        {
            var t = new MessageTally();
            for (int i = 0; i < 100; i++) t.Add("a", 10);
            t.EndWindow(10f, 10);
            for (int i = 0; i < 7; i++) t.Add("a", 10);

            string second = t.EndWindow(10f, 10);

            Assert.Contains("7 messages", Lines(second).First());
        }

        [Fact]
        public void TheSessionSummaryCoversEveryWindowNotJustTheLast()
        {
            var t = new MessageTally();
            for (int i = 0; i < 100; i++) t.Add("a", 10);
            t.EndWindow(10f, 10);
            for (int i = 0; i < 20; i++) t.Add("a", 10);
            t.EndWindow(10f, 10);

            string summary = t.Summary(20f, 10);

            string a = LineOf(summary, " a ");
            Assert.Contains("120", a);
            Assert.Contains("6.0/s", a);
        }

        [Fact]
        public void TheSessionSummaryIncludesMessagesOfAnUnfinishedWindow()
        {
            var t = new MessageTally();
            for (int i = 0; i < 100; i++) t.Add("a", 10);
            t.EndWindow(10f, 10);
            for (int i = 0; i < 3; i++) t.Add("late", 10);

            string summary = t.Summary(15f, 10);

            Assert.Contains("late", summary);
            Assert.Contains("103 messages", Lines(summary).First());
        }

        [Fact]
        public void ALagReportCanShowTheWindowSoFarWithoutDisturbingIt()
        {
            var t = new MessageTally();
            for (int i = 0; i < 40; i++) t.Add("a", 10);

            string peek = t.Peek(4f, 10);
            for (int i = 0; i < 60; i++) t.Add("a", 10);
            string window = t.EndWindow(10f, 10);

            Assert.Contains("40 messages", Lines(peek).First());
            Assert.Contains("10.0/s", Lines(peek).First());
            Assert.Contains("100 messages", Lines(window).First());
        }

        [Fact]
        public void AQuietWindowSaysSoInsteadOfListingNothing()
        {
            var t = new MessageTally();

            string report = t.EndWindow(10f, 10);

            Assert.Contains("0 messages", Lines(report).Single());
        }
    }
}
