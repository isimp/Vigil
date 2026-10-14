using System;
using System.IO;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Vigil hooks timers into hundreds of methods. If one of those hooks ever crashes the
    /// game, the next launch must skip exactly that hook on its own, so a crash happens at most
    /// once and the rest of the recording keeps working.
    /// </summary>
    public sealed class InstrumentJournalTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "VigilJournal-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private InstrumentJournal Launch() => new InstrumentJournal(_dir);

        [Fact]
        public void AHookThatWasBeingInstalledWhenTheGameDiedIsSkippedNextLaunch()
        {
            using (var first = Launch())
            {
                first.Begin("ModA :: Foo.Update"); first.Done("ModA :: Foo.Update");
                first.Begin("ModB :: Bar.Update");
                // the game dies here
            }

            using (var second = Launch())
            {
                Assert.False(second.Allowed("ModB :: Bar.Update"));
                Assert.True(second.Allowed("ModA :: Foo.Update"));
                Assert.Contains("ModB :: Bar.Update", second.NewlySkipped);
            }
        }

        [Fact]
        public void ASkippedHookStaysSkippedInLaterLaunches()
        {
            using (var first = Launch()) first.Begin("ModB :: Bar.Update");
            using (var second = Launch()) { second.Begin("ModA :: Foo.Update"); second.Done("ModA :: Foo.Update"); second.MarkCleanExit(); }

            using (var third = Launch())
            {
                Assert.False(third.Allowed("ModB :: Bar.Update"));
                Assert.Empty(third.NewlySkipped);
            }
        }

        [Fact]
        public void HooksThatInstalledFineAreNeverSkipped()
        {
            using (var first = Launch())
            {
                first.Begin("a"); first.Done("a");
                first.Begin("b"); first.Done("b");
            }

            using (var second = Launch())
            {
                Assert.True(second.Allowed("a"));
                Assert.True(second.Allowed("b"));
            }
        }

        [Fact]
        public void ASessionThatEndedWithoutAQuitIsReportedOnce()
        {
            using (var first = Launch()) { }
            using (var second = Launch()) { Assert.False(second.PreviousEndedCleanly); second.MarkCleanExit(); }
            using (var third = Launch()) { Assert.True(third.PreviousEndedCleanly); }
        }

        [Fact]
        public void TheVeryFirstLaunchHasNothingToReport()
        {
            using (var j = Launch())
            {
                Assert.True(j.PreviousEndedCleanly);
                Assert.Empty(j.NewlySkipped);
            }
        }

        [Fact]
        public void AStateFolderThatCannotBeWrittenNeverStopsTheGame()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dir + "-file") ?? Path.GetTempPath());
            File.WriteAllText(_dir + "-file", "this is a file, not a folder");
            try
            {
                var ex = Record.Exception(() =>
                {
                    using (var j = new InstrumentJournal(_dir + "-file"))
                    {
                        j.Begin("x"); j.Done("x"); j.MarkCleanExit();
                        Assert.True(j.Allowed("x"));
                    }
                });
                Assert.Null(ex);
            }
            finally { File.Delete(_dir + "-file"); }
        }
    }
}
