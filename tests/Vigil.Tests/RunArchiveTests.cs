using System;
using System.IO;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Every launch starts fresh report files. What the previous session wrote must survive that,
    /// so two sessions (for example one per mod version) can be compared afterwards.
    /// </summary>
    public sealed class RunArchiveTests : IDisposable
    {
        private readonly string _dir;
        private string Runs => Path.Combine(_dir, "Vigil-runs");

        public RunArchiveTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VigilTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private void WriteRun(DateTime endedAt, params string[] names)
        {
            foreach (var n in names)
            {
                string p = Path.Combine(_dir, n);
                File.WriteAllText(p, "report " + n);
                File.SetLastWriteTime(p, endedAt);
            }
        }

        private void Launch(int keep = 10) => RunArchive.KeepPreviousRun(_dir, keep);

        private string[] RunFolders() =>
            Directory.Exists(Runs) ? Directory.GetDirectories(Runs).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray() : new string[0];

        [Fact]
        public void TheLastSessionsReportsAreStillReadableAfterTheNextLaunch()
        {
            WriteRun(new DateTime(2026, 10, 8, 18, 37, 1), "Vigil.txt", "Vigil-frames.txt", "Vigil-rpc.txt");

            Launch();

            var folder = Assert.Single(RunFolders());
            foreach (var n in new[] { "Vigil.txt", "Vigil-frames.txt", "Vigil-rpc.txt" })
            {
                Assert.Equal("report " + n, File.ReadAllText(Path.Combine(Runs, folder, n)));
                Assert.False(File.Exists(Path.Combine(_dir, n)), n + " should no longer sit where the new session writes");
            }
        }

        [Fact]
        public void TheSessionsTablesAndIncidentReportsAreKeptToo()
        {
            WriteRun(new DateTime(2026, 10, 8, 19, 0, 0), "Vigil-timeline.tsv", "Vigil-mods.tsv", "Vigil-incident-190000-hitch.txt");

            Launch();

            var folder = Assert.Single(RunFolders());
            Assert.True(File.Exists(Path.Combine(Runs, folder, "Vigil-timeline.tsv")));
            Assert.True(File.Exists(Path.Combine(Runs, folder, "Vigil-mods.tsv")));
            Assert.True(File.Exists(Path.Combine(Runs, folder, "Vigil-incident-190000-hitch.txt")));
        }

        [Fact]
        public void EachKeptSessionIsNamedByWhenItEndedSoTheyListInOrder()
        {
            WriteRun(new DateTime(2026, 10, 8, 17, 43, 17), "Vigil.txt");
            Launch();
            WriteRun(new DateTime(2026, 10, 8, 18, 37, 1), "Vigil.txt");
            Launch();

            Assert.Equal(new[] { "2026-10-08_17-43-17", "2026-10-08_18-37-01" }, RunFolders());
        }

        [Fact]
        public void TwoSessionsEndingInTheSameSecondAreBothKept()
        {
            var t = new DateTime(2026, 10, 8, 18, 0, 0);
            WriteRun(t, "Vigil.txt");
            Launch();
            WriteRun(t, "Vigil.txt");
            Launch();

            Assert.Equal(2, RunFolders().Length);
        }

        [Fact]
        public void BeyondTheSizeLimitTheOldestSessionsGoFirst()
        {
            for (int i = 0; i < 5; i++)
            {
                WriteRun(new DateTime(2026, 10, 1).AddHours(i), "Vigil-timeline.tsv");
                File.WriteAllBytes(Path.Combine(_dir, "Vigil-timeline.tsv"), new byte[1000]);
                File.SetLastWriteTime(Path.Combine(_dir, "Vigil-timeline.tsv"), new DateTime(2026, 10, 1).AddHours(i));
                RunArchive.KeepPreviousRun(_dir, keep: 20, maxBytes: 2500);
            }
            Assert.Equal(new[] { "2026-10-01_03-00-00", "2026-10-01_04-00-00" }, RunFolders());
        }

        [Fact]
        public void TheNewestSessionIsKeptEvenWhenItAloneIsOverTheSizeLimit()
        {
            WriteRun(new DateTime(2026, 10, 1), "Vigil-timeline.tsv");
            File.WriteAllBytes(Path.Combine(_dir, "Vigil-timeline.tsv"), new byte[5000]);
            RunArchive.KeepPreviousRun(_dir, keep: 20, maxBytes: 1000);
            Assert.Single(RunFolders());
        }

        [Fact]
        public void OnlyTheNewestSessionsAreKeptSoTheFolderDoesNotGrowForever()
        {
            for (int i = 0; i < 12; i++)
            {
                WriteRun(new DateTime(2026, 10, 1).AddHours(i), "Vigil.txt");
                Launch(keep: 10);
            }

            var left = RunFolders();
            Assert.Equal(10, left.Length);
            Assert.Equal("2026-10-01_02-00-00", left.First());
            Assert.Equal("2026-10-01_11-00-00", left.Last());
        }

        [Fact]
        public void FoldersSomeoneElsePutInTheArchiveAreNeverDeleted()
        {
            // Named so it would sort before every session folder, i.e. first in line for pruning.
            Directory.CreateDirectory(Path.Combine(Runs, "0000 my notes"));
            for (int i = 0; i < 4; i++)
            {
                WriteRun(new DateTime(2026, 10, 1).AddHours(i), "Vigil.txt");
                Launch(keep: 2);
            }

            Assert.True(Directory.Exists(Path.Combine(Runs, "0000 my notes")));
        }

        [Fact]
        public void TheFirstLaunchLeavesNoEmptyArchiveBehind()
        {
            Launch();

            Assert.False(Directory.Exists(Runs));
        }

        [Fact]
        public void OtherFilesBesideTheReportsAreLeftAlone()
        {
            WriteRun(new DateTime(2026, 10, 8, 18, 0, 0), "Vigil.txt", "LogOutput.log", "VigilNotes.md");

            Launch();

            Assert.True(File.Exists(Path.Combine(_dir, "LogOutput.log")));
            Assert.True(File.Exists(Path.Combine(_dir, "VigilNotes.md")));
        }

        [Fact]
        public void AReportThatCannotBeMovedDoesNotStopTheOthersOrTheGame()
        {
            WriteRun(new DateTime(2026, 10, 8, 18, 0, 0), "Vigil.txt", "Vigil-frames.txt");
            using (new FileStream(Path.Combine(_dir, "Vigil-frames.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Record.Exception(() => Launch());
                Assert.Null(ex);
            }

            var folder = Assert.Single(RunFolders());
            Assert.True(File.Exists(Path.Combine(Runs, folder, "Vigil.txt")));
        }
    }
}
