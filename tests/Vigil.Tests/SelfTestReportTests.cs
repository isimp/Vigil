using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// "vigil selftest" checks Vigil against the running game, every area its own part: whether its
    /// hooks went in, the game members it reads are there, Steam's numbers can be read, the server
    /// answers. The report says at the top how it went, lists what failed first with the reason,
    /// and names the area of each check, so a game update that broke one thing is found at once.
    /// </summary>
    public sealed class SelfTestReportTests
    {
        private static List<SelfTestReport.Check> Mixed() => new List<SelfTestReport.Check>
        {
            new SelfTestReport.Check("Recorder", "hooks installed", SelfTestReport.Result.Pass, "141 of 141"),
            new SelfTestReport.Check("Recorder", "Steam status per player", SelfTestReport.Result.Fail, "0 of 2 readable: the connection is a BufferingSocket"),
            new SelfTestReport.Check("Recorder", "exceptions counted", SelfTestReport.Result.Info, "this runtime does not report them"),
            new SelfTestReport.Check("Spawn marks", "mark shader", SelfTestReport.Result.Pass, "Sprites/Default"),
            new SelfTestReport.Check("Vigil", "server's Vigil", SelfTestReport.Result.Warn, "waiting for an answer"),
        };

        [Fact]
        public void TheTopLineSaysHowItWent()
        {
            var lines = SelfTestReport.Lines("this game", Mixed());
            Assert.Equal("Self-test of this game: 1 failed, 1 warning, 2 passed, 1 for information", lines[0]);
        }

        [Fact]
        public void WhatFailedComesFirstWithItsReason()
        {
            var lines = SelfTestReport.Lines("this game", Mixed());
            Assert.Equal("FAIL  Recorder: Steam status per player - 0 of 2 readable: the connection is a BufferingSocket", lines[1]);
            Assert.Equal("WARN  Vigil: server's Vigil - waiting for an answer", lines[2]);
        }

        [Fact]
        public void PassedChecksFollowInTheirAreasOrder()
        {
            var lines = SelfTestReport.Lines("this game", Mixed());
            Assert.Equal("ok    Recorder: hooks installed - 141 of 141", lines[3]);
            Assert.Equal("ok    Spawn marks: mark shader - Sprites/Default", lines[4]);
            Assert.Equal("info  Recorder: exceptions counted - this runtime does not report them", lines[5]);
        }

        [Fact]
        public void AllPassedSaysSo()
        {
            var lines = SelfTestReport.Lines("the server", new List<SelfTestReport.Check>
            {
                new SelfTestReport.Check("Recorder", "hooks installed", SelfTestReport.Result.Pass, null),
            });
            Assert.Equal("Self-test of the server: all 1 passed", lines[0]);
            Assert.Equal("ok    Recorder: hooks installed", lines[1]);
        }

        [Fact]
        public void NothingCheckedIsNotTakenForSuccess()
        {
            var lines = SelfTestReport.Lines("this game", new List<SelfTestReport.Check>());
            Assert.Equal("Self-test of this game: nothing was checked", lines[0]);
        }

        [Fact]
        public void TheWorstResultIsKnown()
        {
            Assert.Equal(SelfTestReport.Result.Fail, SelfTestReport.Worst(Mixed()));
            Assert.Equal(SelfTestReport.Result.Pass, SelfTestReport.Worst(new List<SelfTestReport.Check>()));
        }

        [Fact]
        public void ThePanelShowsEachCheckOfAReportWithItsResultAreaAndDetail()
        {
            var shown = SelfTestReport.Read(SelfTestReport.Lines("the server", Mixed()));
            Assert.Equal("1 failed, 1 warning, 2 passed, 1 for information", shown.Summary);
            Assert.Equal(SelfTestReport.Result.Fail, shown.Worst);
            Assert.Equal(5, shown.Checks.Count);
            var first = shown.Checks[0];
            Assert.Equal(SelfTestReport.Result.Fail, first.Outcome);
            Assert.Equal("Recorder", first.Area);
            Assert.Equal("Steam status per player", first.What);
            Assert.Equal("0 of 2 readable: the connection is a BufferingSocket", first.Detail);
            var last = shown.Checks[4];
            Assert.Equal(SelfTestReport.Result.Info, last.Outcome);
            Assert.Equal("exceptions counted", last.What);
        }

        [Fact]
        public void ACheckWithoutADetailHasNone()
        {
            var shown = SelfTestReport.Read(SelfTestReport.Lines("this game", new List<SelfTestReport.Check>
            {
                new SelfTestReport.Check("Vigil", "HUD line", SelfTestReport.Result.Pass, null),
            }));
            Assert.Equal("all 1 passed", shown.Summary);
            Assert.Equal(SelfTestReport.Result.Pass, shown.Worst);
            Assert.Equal("HUD line", shown.Checks[0].What);
            Assert.Null(shown.Checks[0].Detail);
        }

        [Fact]
        public void ADetailWithADashInItIsKeptWhole()
        {
            var shown = SelfTestReport.Read(SelfTestReport.Lines("this game", new List<SelfTestReport.Check>
            {
                new SelfTestReport.Check("Recorder", "hooks put in", SelfTestReport.Result.Warn, "2 not: Smelter - UpdateSmelter; Player - Update"),
            }));
            Assert.Equal("hooks put in", shown.Checks[0].What);
            Assert.Equal("2 not: Smelter - UpdateSmelter; Player - Update", shown.Checks[0].Detail);
        }

        [Fact]
        public void AReportThatCannotBeReadIsShownAsItsLines()
        {
            var shown = SelfTestReport.Read(new List<string> { "failed", "something odd" });
            Assert.Equal("failed", shown.Summary);
            Assert.Equal(SelfTestReport.Result.Info, shown.Checks[0].Outcome);
            Assert.Equal("something odd", shown.Checks[0].What);
            Assert.Equal(SelfTestReport.Result.Warn, shown.Worst);
        }
    }
}
